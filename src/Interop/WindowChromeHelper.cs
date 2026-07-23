using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using static TaskbarMusic.Interop.NativeMethods;

namespace TaskbarMusic.Interop;

/// <summary>
/// Turns a plain WPF window into a floating Fluent-style overlay:
/// never steals focus, hidden from Alt-Tab, real acrylic blur-behind,
/// and Windows 11 rounded corners.
/// </summary>
internal static class WindowChromeHelper
{
    public static void MakeOverlay(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;

        // No focus stealing + not in Alt-Tab.
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        ex |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);

        // Do NOT let DWM round/border the window: it draws a 1px outline, and we
        // already clip our content to a rounded shape ourselves. This is what kills
        // the faint outer border the user saw.
        int noRound = (int)DWM_WINDOW_CORNER_PREFERENCE.DONOTROUND;
        DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, ref noRound, sizeof(int));
        int noBorder = unchecked((int)DWMWA_COLOR_NONE);
        DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));
    }

    /// <summary>Force the window back to the top of the z-order without activating it.</summary>
    public static void ReassertTopmost(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// Enable the real Windows acrylic blur-behind. tintColor is AARRGGBB.
    /// Falls back silently (window keeps its translucent WPF brush) if the OS
    /// rejects the call.
    /// </summary>
    public static void EnableAcrylic(Window w, Color tint, byte opacity)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;

        // GradientColor is AABBGGRR.
        uint gradient = ((uint)opacity << 24) | ((uint)tint.B << 16) | ((uint)tint.G << 8) | tint.R;

        var accent = new AccentPolicy
        {
            AccentState = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
            AccentFlags = 2, // draw all borders
            GradientColor = gradient,
            AnimationId = 0,
        };

        int size = Marshal.SizeOf(accent);
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = ptr,
                SizeOfData = size,
            };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
