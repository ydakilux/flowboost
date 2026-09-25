using System.Windows;
using flowboost.Models;
using flowboost.Services;

namespace flowboost.Views;

internal static class ViewTheme
{
    public static void Apply(Window window, AppTheme theme)
    {
        var dictionary = new ResourceDictionary
        {
            Source = new Uri(ThemeService.IsDark(theme)
                ? "/flowboost;component/Views/Themes/Dark.xaml"
                : "/flowboost;component/Views/Themes/Light.xaml", UriKind.Relative)
        };
        window.Resources.MergedDictionaries.Clear();
        window.Resources.MergedDictionaries.Add(dictionary);
    }
}
