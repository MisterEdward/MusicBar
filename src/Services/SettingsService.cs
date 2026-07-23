using System.IO;
using System.Text.Json;

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
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskbarMusic");

    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* corrupt file -> defaults */ }
        return new AppSettings();
    }

    public static void Save(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(s, Options));
        }
        catch { /* best effort */ }
    }
}
