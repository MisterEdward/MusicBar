using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using static TaskbarMusic.Interop.NativeMethods;

namespace TaskbarMusic.Interop;

/// <summary>
/// True when the foreground window is a real fullscreen app (covers an entire
/// monitor) on the SAME monitor as the pill. Maximised windows that leave the
/// taskbar visible don't count; the desktop and shell windows are ignored.
/// </summary>
internal static class FullscreenDetector
{
    private static readonly HashSet<string> ShellClasses = new()
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "MultitaskingViewFrame",        // Task View overlay (taskbar button / Win+Tab)
        "TaskSwitcherWnd",              // Alt+Tab switcher
        "XamlExplorerHostIslandWindow", // newer Win11 shell surfaces (Start/Search/Widgets/...)
    };

    public static bool ForegroundFullscreenOnSameMonitor(IntPtr pillHwnd)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == GetShellWindow() || fg == pillHwnd) return false;
        if (!IsWindowVisible(fg) || IsIconic(fg) || IsCloaked(fg)) return false;

        var sb = new StringBuilder(256);
        GetClassName(fg, sb, sb.Capacity);
        if (ShellClasses.Contains(sb.ToString())) return false;

        // Belt-and-suspenders: any full-screen-looking window owned by explorer.exe
        // is shell chrome (Task View, Alt-Tab, taskbar), never a real fullscreen app —
        // even if Microsoft renames a class we don't know about. This is what made the
        // pill vanish on a taskbar/Task-View click ("need one more click to bring back").
        if (IsExplorerOwned(fg)) return false;

        IntPtr foregroundMonitor = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
        if (foregroundMonitor != MonitorFromWindow(pillHwnd, MONITOR_DEFAULTTONEAREST)) return false;

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(foregroundMonitor, ref mi)) return false;
        if (!GetWindowRect(fg, out RECT r)) return false;

        var window = new PixelRect(r.Left, r.Top, r.Right, r.Bottom);
        RECT m = mi.rcMonitor;
        var monitor = new PixelRect(m.Left, m.Top, m.Right, m.Bottom);
        if (!FullscreenGeometry.CoversMonitor(window, monitor)) return false;

        // A normal maximised window retains WS_CAPTION. Geometry remains the
        // primary test: borderless games and browser/video fullscreen modes often
        // retain a maximised show state, so WINDOWPLACEMENT alone is unreliable.
        int style = GetWindowLong(fg, GWL_STYLE);
        return (style & WS_CAPTION) != WS_CAPTION;
    }

    private static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED,
            out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static bool IsExplorerOwned(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return false;
            using var p = Process.GetProcessById((int)pid);
            return string.Equals(p.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false; // fail open: never suppress a genuine fullscreen app over this
        }
    }
}
