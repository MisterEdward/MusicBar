using System.Runtime.InteropServices;
using static TaskbarMusic.Interop.NativeMethods;

namespace TaskbarMusic.Interop;

/// <summary>
/// Finds the taskbar for whichever monitor a screen point is on. The actual
/// shell taskbar window supplies the edge (including auto-hide and secondary
/// monitors); the work area supplies a fallback and the visible thickness.
/// All coordinates are physical pixels.
/// </summary>
internal static class TaskbarHelper
{
    private static readonly HashSet<string> TaskbarClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
    };

    public static bool TryGetTaskbar(POINT screenPt, out TaskbarGeometryResult taskbar)
    {
        taskbar = default;
        IntPtr hMon = MonitorFromPoint(screenPt, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hMon, ref mi)) return false;

        PixelRect monitor = ToPixelRect(mi.rcMonitor);
        PixelRect workArea = ToPixelRect(mi.rcWork);
        PixelRect? shellRect = TryFindTaskbarWindow(
            hMon, mi.rcMonitor, out IntPtr taskbarHwnd, out RECT nativeRect, out TaskbarEdge? shellEdge)
            ? ToPixelRect(nativeRect)
            : null;

        int fallbackThickness = 48;
        if (taskbarHwnd != IntPtr.Zero)
        {
            try
            {
                uint dpi = GetDpiForWindow(taskbarHwnd);
                if (dpi > 0) fallbackThickness = (int)Math.Round(48 * dpi / 96.0);
            }
            catch (EntryPointNotFoundException)
            {
                // Windows 10 1607+ provides this. The app's minimum supported
                // build does too, but retaining 96-DPI fallback is harmless.
            }
        }

        return TaskbarGeometry.TryResolve(
            monitor, workArea, shellRect, fallbackThickness, out taskbar, shellEdge);
    }

    private static bool TryFindTaskbarWindow(
        IntPtr monitor,
        RECT monitorRect,
        out IntPtr foundHwnd,
        out RECT foundRect,
        out TaskbarEdge? foundEdge)
    {
        // For auto-hide, the HWND can sit mostly beyond its owning monitor and
        // MonitorFromWindow(...NEAREST) may therefore select an adjacent display.
        // ABM_GETAUTOHIDEBAREX asks Explorer for the owner by monitor + edge.
        foreach ((uint nativeEdge, TaskbarEdge edge) in AutoHideEdges)
        {
            var data = new APPBARDATA
            {
                cbSize = Marshal.SizeOf<APPBARDATA>(),
                uEdge = nativeEdge,
                rc = monitorRect,
            };
            UIntPtr answer = SHAppBarMessage(ABM_GETAUTOHIDEBAREX, ref data);
            if (answer == UIntPtr.Zero) continue;

            IntPtr hwnd = new(unchecked((long)answer.ToUInt64()));
            var autoHideClass = new System.Text.StringBuilder(64);
            if (GetClassName(hwnd, autoHideClass, autoHideClass.Capacity) <= 0 ||
                !TaskbarClasses.Contains(autoHideClass.ToString()) ||
                !GetWindowRect(hwnd, out RECT autoHideRect))
            {
                continue;
            }

            foundHwnd = hwnd;
            foundRect = autoHideRect;
            foundEdge = edge;
            return true;
        }

        IntPtr match = IntPtr.Zero;
        RECT rect = default;
        var className = new System.Text.StringBuilder(64);

        EnumWindows((hwnd, _) =>
        {
            className.Clear();
            if (GetClassName(hwnd, className, className.Capacity) <= 0 ||
                !TaskbarClasses.Contains(className.ToString()) ||
                MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) != monitor ||
                !GetWindowRect(hwnd, out RECT candidate))
            {
                return true;
            }

            match = hwnd;
            rect = candidate;
            return false;
        }, IntPtr.Zero);

        foundHwnd = match;
        foundRect = rect;
        foundEdge = null;
        return match != IntPtr.Zero;
    }

    private static readonly (uint NativeEdge, TaskbarEdge Edge)[] AutoHideEdges =
    [
        (ABE_LEFT, TaskbarEdge.Left),
        (ABE_TOP, TaskbarEdge.Top),
        (ABE_RIGHT, TaskbarEdge.Right),
        (ABE_BOTTOM, TaskbarEdge.Bottom),
    ];

    private static PixelRect ToPixelRect(RECT rect) =>
        new(rect.Left, rect.Top, rect.Right, rect.Bottom);
}
