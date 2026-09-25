using System.IO;
using System.Reflection;
using System.Windows;

namespace flowboost.Views;

public partial class ReleaseNotesWindow : Window
{
    private const string ChangelogResourceName = "flowboost.CHANGELOG.md";

    public ReleaseNotesWindow()
    {
        InitializeComponent();
        var version = GetDisplayVersion();
        ViewTheme.Apply(this, ((flowboost.App)System.Windows.Application.Current).Settings.Current.Theme);
        VersionText.Text = $"Version {version}";

        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ChangelogResourceName);
            if (stream is null)
            {
                ShowLoadError("Release notes aren't available in this installation. Try reinstalling the app or check the project release page.");
                return;
            }

            using var reader = new StreamReader(stream);
            ReleaseNotesText.Text = reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            flowboost.Services.AppLog.Write("Could not load bundled release notes", ex);
            ShowLoadError("Release notes couldn't be loaded. Try reinstalling the app or check the project release page.");
        }
    }

    private static string GetDisplayVersion()
    {
        var informationalVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = string.IsNullOrWhiteSpace(informationalVersion) ? "unknown" : informationalVersion;
        var metadataSeparator = version.IndexOf('+');
        return metadataSeparator >= 0 ? version[..metadataSeparator] : version;
    }

    private void ShowLoadError(string message)
    {
        ReleaseNotesText.Visibility = Visibility.Collapsed;
        ReleaseNotesError.Text = message;
        ReleaseNotesError.Visibility = Visibility.Visible;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
