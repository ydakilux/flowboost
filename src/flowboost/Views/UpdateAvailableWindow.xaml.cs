using System.Windows;
using flowboost.Services;

namespace flowboost.Views;

public partial class UpdateAvailableWindow : Window
{
    public UpdateAvailableWindow(UpdateInfo info)
    {
        InitializeComponent();
        ViewTheme.Apply(this, ((flowboost.App)System.Windows.Application.Current).Settings.Current.Theme);

        VersionText.Text = $"Version {info.CurrentVersion}  →  {info.LatestVersion}";
        if (!string.IsNullOrWhiteSpace(info.ReleaseName))
        {
            ReleaseNameText.Text = info.ReleaseName;
            ReleaseNameText.Visibility = Visibility.Visible;
        }
    }

    public UpdateDecision Decision { get; private set; } = UpdateDecision.Later;

    private void OpenGitHub_Click(object sender, RoutedEventArgs e)
    {
        Decision = UpdateDecision.OpenReleasePage;
        DialogResult = true;
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        Decision = UpdateDecision.Skip;
        DialogResult = true;
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        Decision = UpdateDecision.Later;
        DialogResult = false;
    }
}
