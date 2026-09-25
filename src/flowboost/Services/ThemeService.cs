using Microsoft.Win32;
using flowboost.Models;

namespace flowboost.Services;

public static class ThemeService
{
    public static bool IsDark(AppTheme theme)
    {
        if (theme == AppTheme.Dark) return true;
        if (theme == AppTheme.Light) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return Convert.ToInt32(key?.GetValue("AppsUseLightTheme") ?? 1) == 0;
        }
        catch { return false; }
    }
}
