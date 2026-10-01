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
        if (info.ExeUrl is not null && info.Sha256Url is not null)
        {
            ExplanationText.Text = "Update now downloads the new version, verifies its checksum, then restarts flowboost. Nothing is installed without your choice.";
            UpdateNowButton.Visibility = Visibility.Visible;
            UpdateNowButton.IsDefault = true;
            OpenGitHubButton.IsDefault = false;
        }
    }

    public UpdateDecision Decision { get; private set; } = UpdateDecision.Later;

    private void OpenGitHub_Click(object sender, RoutedEventArgs e)
    {
        Decision = UpdateDecision.OpenReleasePage;
        DialogResult = true;
    }

    private void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        Decision = UpdateDecision.InstallNow;
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
