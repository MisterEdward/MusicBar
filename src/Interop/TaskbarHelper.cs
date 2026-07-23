using System.Runtime.InteropServices;
using static TaskbarMusic.Interop.NativeMethods;

namespace TaskbarMusic.Interop;

/// <summary>
/// Finds the taskbar strip for whichever monitor a screen point is on, by
/// diffing the monitor bounds against its work area. Works on any edge and on
/// secondary monitors; falls back to a ~bottom strip if the taskbar auto-hides.
/// All rects are physical pixels.
/// </summary>
internal static class TaskbarHelper
{
    public static bool TryGetTaskbar(POINT screenPt, out RECT strip)
    {
        strip = default;
        IntPtr hMon = MonitorFromPoint(screenPt, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hMon, ref mi)) return false;

        RECT m = mi.rcMonitor, w = mi.rcWork;
        int bottom = m.Bottom - w.Bottom;
        int top = w.Top - m.Top;
        int left = w.Left - m.Left;
        int right = m.Right - w.Right;
        int max = Math.Max(Math.Max(bottom, top), Math.Max(left, right));

        if (max <= 0)
        {
            // Auto-hidden / no taskbar reported: assume a bottom bar.
            int h = Math.Max(40, (m.Bottom - m.Top) / 22);
            strip = new RECT { Left = m.Left, Top = m.Bottom - h, Right = m.Right, Bottom = m.Bottom };
            return true;
        }

        if (max == bottom) strip = new RECT { Left = m.Left, Top = w.Bottom, Right = m.Right, Bottom = m.Bottom };
        else if (max == top) strip = new RECT { Left = m.Left, Top = m.Top, Right = m.Right, Bottom = w.Top };
        else if (max == left) strip = new RECT { Left = m.Left, Top = m.Top, Right = w.Left, Bottom = m.Bottom };
        else strip = new RECT { Left = w.Right, Top = m.Top, Right = m.Right, Bottom = m.Bottom };
        return true;
    }
}
