using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskbarMusic.Services;

/// <summary>Tiny JSON-on-disk settings store (position, volume step, cat toggle).</summary>
internal sealed class AppSettings
{
    public double Left { get; set; } = double.NaN;   // NaN => auto-place near taskbar
    public double Top { get; set; } = double.NaN;
    public double VolumeStep { get; set; } = 0.02;    // per wheel notch (2%)
    public bool CatEnabled { get; set; } = true;      // idle easter egg
    public bool Locked { get; set; } = false;         // disable dragging
}

internal static class SettingsService
{
    private const double DefaultVolumeStep = 0.02;
    private const double MinVolumeStep = 0.001;
    private const double MaxVolumeStep = 0.25;
    private const double MaxCoordinateMagnitude = 1_000_000;
    private static readonly object IoLock = new();

    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskbarMusic");

    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static AppSettings Load()
    {
        lock (IoLock)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                    return Validate(loaded ?? new AppSettings());
                }
            }
            catch (Exception ex)
            {
                App.LogException("Settings load", ex);
            }
            return new AppSettings();
        }
    }

    public static void Save(AppSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);

        lock (IoLock)
        {
            string? tempPath = null;
            try
            {
                Directory.CreateDirectory(Dir);
                tempPath = Path.Combine(Dir, $"settings.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
                var validated = Validate(s);

                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(stream, validated, Options);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tempPath, FilePath, overwrite: true);
                tempPath = null;
            }
            catch (Exception ex)
            {
                App.LogException("Settings save", ex);
            }
            finally
            {
                if (tempPath is not null)
                {
                    try { File.Delete(tempPath); }
                    catch { /* best-effort cleanup of an incomplete write */ }
                }
            }
        }
    }

    private static AppSettings Validate(AppSettings settings)
    {
        return new AppSettings
        {
            Left = ValidCoordinate(settings.Left) ? settings.Left : double.NaN,
            Top = ValidCoordinate(settings.Top) ? settings.Top : double.NaN,
            VolumeStep = double.IsFinite(settings.VolumeStep) &&
                         settings.VolumeStep >= MinVolumeStep &&
                         settings.VolumeStep <= MaxVolumeStep
                ? settings.VolumeStep
                : DefaultVolumeStep,
            CatEnabled = settings.CatEnabled,
            Locked = settings.Locked,
        };
    }

    private static bool ValidCoordinate(double value) =>
        double.IsNaN(value) || double.IsFinite(value) && Math.Abs(value) <= MaxCoordinateMagnitude;
}
