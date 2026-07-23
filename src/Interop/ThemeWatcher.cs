using Microsoft.Win32;

namespace TaskbarMusic.Interop;

/// <summary>Reads the current Windows app theme (light vs dark).</summary>
internal static class ThemeWatcher
{
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
}
