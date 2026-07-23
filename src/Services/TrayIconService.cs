using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarMusic.Services;

/// <summary>
/// System-tray icon + menu. A safety net: reach Start-with-Windows, Lock, Reset
/// and Exit even if the pill is dragged somewhere awkward. Fires plain events;
/// the window owns the actual behaviour and pushes checkbox state back via
/// <see cref="SetStates"/>.
/// </summary>
internal sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly Icon _iconImage;
    private readonly TrayMessageWindow _messageWindow = new();
    private readonly ToolStripMenuItem _startup, _lock, _cat;
    private bool _disposed;

    public event Action? StartupToggled, LockToggled, CatToggled, ResetRequested, ExitRequested;

    public TrayIconService()
    {
        _startup = Item("Start with Windows", () => StartupToggled?.Invoke(), check: true);
        _lock = Item("Lock position", () => LockToggled?.Invoke(), check: true);
        _cat = Item("Idle cat", () => CatToggled?.Invoke(), check: true);
        var reset = Item("Reset position", () => ResetRequested?.Invoke());
        var exit = Item("Exit", () => ExitRequested?.Invoke());

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _startup, _lock, _cat, new ToolStripSeparator(), reset, new ToolStripSeparator(), exit,
        });

        _iconImage = BuildIcon();
        _icon = new NotifyIcon
        {
            Text = "Taskbar Music",
            Visible = true,
            Icon = _iconImage,
            ContextMenuStrip = _menu,
        };
        // Left-click should also open the menu (default only opens on right-click).
        _icon.MouseClick += OnMouseClick;
    }

    public void SetStates(bool startup, bool locked, bool cat)
    {
        _startup.Checked = startup;
        _lock.Checked = locked;
        _cat.Checked = cat;
    }

    private static ToolStripMenuItem Item(string text, Action onClick, bool check = false)
    {
        var it = new ToolStripMenuItem(text) { CheckOnClick = check };
        it.Click += (_, _) => onClick();
        return it;
    }

    private static Icon BuildIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var grad = new LinearGradientBrush(new Rectangle(0, 0, 32, 32),
                Color.FromArgb(0x7B, 0x5C, 0xF6), Color.FromArgb(0xB0, 0x4C, 0xE0), 45f);
            g.FillEllipse(grad, 1, 1, 30, 30);

            using var white = new SolidBrush(Color.White);
            using var pen = new Pen(Color.White, 2f);
            g.FillEllipse(white, 10, 19, 6, 5);   // note heads
            g.FillEllipse(white, 19, 16, 6, 5);
            g.DrawLine(pen, 15, 21, 15, 10);       // stems
            g.DrawLine(pen, 24, 18, 24, 8);
            g.DrawLine(pen, 15, 10, 24, 8);        // beam
        }
        IntPtr h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(h);
        }
    }

    private void OnMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _disposed)
            return;

        // Match the native tray-menu contract without calling NotifyIcon's
        // private ShowContextMenu method. The foreground owner makes the popup
        // dismiss on the very next outside click; WM_NULL completes the pattern
        // recommended for notification-area context menus.
        SetForegroundWindow(_messageWindow.Handle);
        _menu.Show(Cursor.Position);
        PostMessage(_messageWindow.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _icon.MouseClick -= OnMouseClick;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _iconImage.Dispose();
        _messageWindow.Dispose();
    }

    private const uint WM_NULL = 0;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private sealed class TrayMessageWindow : NativeWindow, IDisposable
    {
        public TrayMessageWindow()
        {
            CreateHandle(new CreateParams
            {
                Caption = "TaskbarMusic.TrayMenuOwner",
                ExStyle = WS_EX_TOOLWINDOW,
            });
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
                DestroyHandle();
        }
    }
}
