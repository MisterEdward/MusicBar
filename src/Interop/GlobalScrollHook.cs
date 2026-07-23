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
    private readonly Action<int> _onScroll; // notches, marshalled to UI thread
    private readonly LowLevelMouseProc _proc; // keep alive against GC
    private IntPtr _hook = IntPtr.Zero;

    public GlobalScrollHook(Window target, Action<int> onScrollNotches)
    {
        _target = target;
        _onScroll = onScrollNotches;
        _proc = HookProc;
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
            if (nCode >= 0 && (int)wParam == WM_MOUSEWHEEL)
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
        var hwnd = new WindowInteropHelper(_target).Handle;
        if (hwnd == IntPtr.Zero) return false;
        // Both the hook point and GetWindowRect are in physical screen pixels,
        // so no DPI conversion is needed.
        if (!GetWindowRect(hwnd, out RECT r)) return false;
        return pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom;
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
