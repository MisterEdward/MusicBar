using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace TaskbarMusic.Interop;

/// <summary>Reads and watches the current Windows app theme (light vs dark).</summary>
internal sealed class ThemeWatcher : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private bool _lastTheme;
    private bool _disposed;

    public event Action<bool>? ThemeChanged;

    public ThemeWatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _lastTheme = IsLightTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public static bool IsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var v = key?.GetValue("AppsUseLightTheme");
            return v is int i && i != 0;
        }
        catch
        {
            return false; // default to dark glass
        }
    }

    public static void ApplyResources(ResourceDictionary resources, bool light)
    {
        ArgumentNullException.ThrowIfNull(resources);

        static SolidColorBrush Brush(Color color) => new(color);
        resources["GlassBrush"] = Brush(light
            ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x4D, 0x00, 0x00, 0x00));
        resources["StrokeBrush"] = Brush(light
            ? Color.FromArgb(0x22, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        resources["TextPrimary"] = Brush(light
            ? Color.FromRgb(0x10, 0x10, 0x10)
            : Colors.White);
        resources["TextSecondary"] = Brush(light
            ? Color.FromArgb(0xB0, 0x10, 0x10, 0x10)
            : Color.FromArgb(0xB4, 0xFF, 0xFF, 0xFF));
        resources["IconHover"] = Brush(light
            ? Color.FromArgb(0x14, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
        resources["IconPressed"] = Brush(light
            ? Color.FromArgb(0x28, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
        resources["AccentBrush"] = Brush(light
            ? Color.FromRgb(0x00, 0x67, 0xC0)
            : Color.FromRgb(0x60, 0xCD, 0xFF));
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            return;

        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (_disposed)
                    return;

                bool current = IsLightTheme();
                if (current == _lastTheme)
                    return;

                _lastTheme = current;
                ThemeChanged?.Invoke(current);
            });
        }
        catch (InvalidOperationException)
        {
            // The dispatcher completed shutdown between the checks and enqueue.
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        ThemeChanged = null;
    }
}
