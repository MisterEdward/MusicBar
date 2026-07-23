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

        var sb = new StringBuilder(256);
        GetClassName(fg, sb, sb.Capacity);
        if (ShellClasses.Contains(sb.ToString())) return false;

        // Belt-and-suspenders: any full-screen-looking window owned by explorer.exe
        // is shell chrome (Task View, Alt-Tab, taskbar), never a real fullscreen app —
        // even if Microsoft renames a class we don't know about. This is what made the
        // pill vanish on a taskbar/Task-View click ("need one more click to bring back").
        if (IsExplorerOwned(fg)) return false;

        // Distinguish "maximised" from "real fullscreen" by WINDOW STYLE, not
        // showCmd: a normal/maximised window keeps a title bar (WS_CAPTION); a
        // fullscreen app (game, fullscreen video, borderless) removes it. Using
        // showCmd wrongly excluded fullscreen apps that keep a maximised window
        // state (e.g. fullscreen video launched from a maximised browser), so the
        // pill didn't hide. The style test still excludes a plain maximised window
        // (which has a caption) even on an auto-hide-taskbar monitor.
        int style = GetWindowLong(fg, GWL_STYLE);
        if ((style & WS_CAPTION) == WS_CAPTION) return false;

        if (MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST) !=
            MonitorFromWindow(pillHwnd, MONITOR_DEFAULTTONEAREST)) return false;

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST), ref mi)) return false;
        if (!GetWindowRect(fg, out RECT r)) return false;

        const int tol = 2;
        RECT m = mi.rcMonitor;
        return r.Left <= m.Left + tol && r.Top <= m.Top + tol &&
               r.Right >= m.Right - tol && r.Bottom >= m.Bottom - tol;
    }

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
