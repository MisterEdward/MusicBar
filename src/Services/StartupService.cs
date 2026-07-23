using Microsoft.Win32;

namespace TaskbarMusic.Services;

/// <summary>
/// Toggles "start with Windows" via the per-user Run key
/// (HKCU\...\CurrentVersion\Run). No admin rights needed, and it self-heals the
/// stored path on launch in case the .exe was moved.
/// </summary>
internal static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TaskbarMusic";

    private static string? ExePath => Environment.ProcessPath;

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return !string.IsNullOrEmpty(key?.GetValue(ValueName) as string);
        }
        catch { return false; }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return;

            if (enabled)
            {
                var path = ExePath;
                if (!string.IsNullOrEmpty(path))
                    key.SetValue(ValueName, $"\"{path}\"");
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch { /* best effort */ }
    }

    /// <summary>If autostart is on, rewrite the path (handles a moved .exe).</summary>
    public static void RefreshIfEnabled()
    {
        if (IsEnabled()) SetEnabled(true);
    }
}
