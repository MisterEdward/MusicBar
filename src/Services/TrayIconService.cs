using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
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
    private readonly ToolStripMenuItem _startup, _lock, _cat;

    // NotifyIcon's own right-click path calls a private ShowContextMenu() that does
    // SetForegroundWindow(...) first — required for the menu to dismiss on the very
    // next click. Our manual left-click Show() skipped that, leaving an orphaned
    // topmost popup that ate the next click ("need one more click anywhere" bug).
    private static readonly MethodInfo? ShowContextMenuMethod =
        typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);

    public event Action? StartupToggled, LockToggled, CatToggled, ResetRequested, ExitRequested;

    public TrayIconService()
    {
        _startup = Item("Start with Windows", () => StartupToggled?.Invoke(), check: true);
        _lock = Item("Lock position", () => LockToggled?.Invoke(), check: true);
        _cat = Item("Idle cat", () => CatToggled?.Invoke(), check: true);
        var reset = Item("Reset position", () => ResetRequested?.Invoke());
        var exit = Item("Exit", () => ExitRequested?.Invoke());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _startup, _lock, _cat, new ToolStripSeparator(), reset, new ToolStripSeparator(), exit,
        });

        _icon = new NotifyIcon
        {
            Text = "Taskbar Music",
            Visible = true,
            Icon = BuildIcon(),
            ContextMenuStrip = menu,
        };
        // Left-click should also open the menu (default only opens on right-click).
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (ShowContextMenuMethod != null) ShowContextMenuMethod.Invoke(_icon, null);
            else menu.Show(Cursor.Position); // fallback if the private API ever changes
        };
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
        using var tmp = Icon.FromHandle(h);
        return (Icon)tmp.Clone();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
