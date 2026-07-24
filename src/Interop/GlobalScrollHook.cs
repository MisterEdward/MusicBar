using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using static TaskbarMusic.Interop.NativeMethods;

namespace TaskbarMusic.Interop;

/// <summary>
/// A system-wide low-level mouse hook that fires when the wheel is scrolled
/// while the cursor is anywhere over <paramref name="target"/> — regardless of
/// which window is focused or which monitor it's on. Returns the notch delta
/// (+ up / - down). Swallows the event so the window underneath doesn't scroll.
/// </summary>
internal sealed class GlobalScrollHook : IDisposable
{
    private readonly Window _target;
    private readonly FrameworkElement _visibleSurface;
    private readonly Action<int> _onScroll; // notches, marshalled to UI thread
    private readonly LowLevelMouseProc _proc; // keep alive against GC
    private IntPtr _hook = IntPtr.Zero;
    private int _enabled = 1;

    public GlobalScrollHook(Window target, FrameworkElement visibleSurface, Action<int> onScrollNotches)
    {
        _target = target;
        _visibleSurface = visibleSurface;
        _onScroll = onScrollNotches;
        _proc = HookProc;
    }

    public bool IsEnabled
    {
        get => Volatile.Read(ref _enabled) != 0;
        set => Volatile.Write(ref _enabled, value ? 1 : 0);
    }

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        // WH_MOUSE_LL is a global hook but is dispatched on the installing
        // thread's message loop, so no separate module is required.
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Never let a managed exception cross the native hook boundary.
        try
        {
            if (nCode >= 0 && (int)wParam == WM_MOUSEWHEEL && IsEnabled)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (CursorIsOverTarget(data.pt))
                {
                    short delta = (short)((data.mouseData >> 16) & 0xFFFF);
                    int notches = delta / 120;
                    if (notches != 0)
                    {
                        _target.Dispatcher.BeginInvoke(() => _onScroll(notches));
                        return (IntPtr)1; // handled — don't scroll the app underneath
                    }
                }
            }
        }
        catch (Exception ex)
        {
            App.LogException("GlobalScrollHook.HookProc", ex);
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private bool CursorIsOverTarget(POINT pt)
    {
        if (!_target.IsVisible || _target.WindowState == WindowState.Minimized ||
            _target.Opacity <= 0.01 || !_visibleSurface.IsVisible ||
            _visibleSurface.ActualWidth <= 0 || _visibleSurface.ActualHeight <= 0)
        {
            return false;
        }

        if (PresentationSource.FromVisual(_visibleSurface)?.CompositionTarget is null)
            return false;

        var hwnd = new WindowInteropHelper(_target).Handle;
        if (hwnd == IntPtr.Zero) return false;

        // Reject the part clipped at a mixed-DPI monitor seam.
        IntPtr targetMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (MonitorFromPoint(pt, MONITOR_DEFAULTTONULL) != targetMonitor) return false;

        // PointToScreen includes the element's current render transform, so the
        // wheel area follows the visible pill even during shrink/bounce animations.
        Point topLeft = _visibleSurface.PointToScreen(new Point(0, 0));
        Point bottomRight = _visibleSurface.PointToScreen(
            new Point(_visibleSurface.ActualWidth, _visibleSurface.ActualHeight));
        var bounds = new PixelRect(
            (int)Math.Floor(Math.Min(topLeft.X, bottomRight.X)),
            (int)Math.Floor(Math.Min(topLeft.Y, bottomRight.Y)),
            (int)Math.Ceiling(Math.Max(topLeft.X, bottomRight.X)),
            (int)Math.Ceiling(Math.Max(topLeft.Y, bottomRight.Y)));

        // The transparent corners of the stadium are not interactive.
        return RoundedRectHitTest.Contains(bounds, pt.X, pt.Y, bounds.Height / 2.0);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
