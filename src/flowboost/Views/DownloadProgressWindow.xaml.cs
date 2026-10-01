using System.Diagnostics;
using System.IO;
using System.Windows;
using flowboost.Services;

namespace flowboost.Views;

public partial class DownloadProgressWindow : Window
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly UpdateInfo _info;
    private bool _completed;

    public DownloadProgressWindow(UpdateInfo info)
    {
        InitializeComponent();
        _info = info;
        ViewTheme.Apply(this, ((flowboost.App)System.Windows.Application.Current).Settings.Current.Theme);
        Closed += (_, _) => { if (!_completed) _cancellation.Cancel(); _cancellation.Dispose(); };
    }

    public CancellationToken CancellationToken => _cancellation.Token;

    public void CancelDownload()
    {
        if (!_cancellation.IsCancellationRequested && !_completed) _cancellation.Cancel();
    }

    public void ReportProgress(long received, long? total)
    {
        if (total is > 0)
        {
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Value = Math.Clamp(received * 100d / total.Value, 0, 100);
            ProgressText.Text = $"Downloaded {received / 1048576d:0.0} MB of {total.Value / 1048576d:0.0} MB";
        }
        else
        {
            DownloadProgress.IsIndeterminate = true;
            ProgressText.Text = $"Downloaded {received / 1048576d:0.0} MB";
        }
    }

    public void ShowFailure(string message)
    {
        _completed = true;
        DownloadProgress.IsIndeterminate = false;
        DownloadProgress.Value = 0;
        ProgressText.Text = "The update was not installed.";
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Collapsed;
        CloseButton.Visibility = Visibility.Visible;
        OpenGitHubButton.Visibility = Visibility.Visible;
    }

    public void ShowRestarting()
    {
        _completed = true;
        ProgressText.Text = "Update verified. Restarting flowboost…";
        DownloadProgress.IsIndeterminate = true;
        CancelButton.Visibility = Visibility.Collapsed;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelDownload();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenGitHub_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_info.ReleaseUrl) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            AppLog.Write("Update install failed", ex);
            ErrorText.Text = "Could not open GitHub. Visit the flowboost releases page in your browser.";
        }
    }
}
