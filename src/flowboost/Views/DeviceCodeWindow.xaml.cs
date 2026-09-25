using System.Diagnostics;
using System.Windows;
using flowboost.Services;

namespace flowboost.Views;

public partial class DeviceCodeWindow : Window
{
    private const string DeviceSignInUrl = "https://github.com/login/device";
    private readonly AuthService _auth;
    private readonly CancellationTokenSource _cancellation = new();
    private DeviceCodeInfo? _deviceCode;
    private bool _finished;
    private bool _windowClosed;
    private bool _asyncCompleted;
    private bool _cancellationDisposed;

    public DeviceCodeWindow(AuthService auth)
    {
        InitializeComponent();
        _auth = auth;
        ViewTheme.Apply(this, ((flowboost.App)System.Windows.Application.Current).Settings.Current.Theme);
        Loaded += async (_, _) => await StartSignInAsync();
        Closed += (_, _) =>
        {
            _windowClosed = true;
            if (!_finished) CancelCurrentFlow();
            DisposeCancellationIfComplete();
        };
    }

    private async Task StartSignInAsync()
    {
        try
        {
            StatusText.Text = "Requesting a one-time sign-in code…";
            var deviceCode = await _auth.StartDeviceFlowAsync(_cancellation.Token);
            _deviceCode = deviceCode;
            if (_cancellation.IsCancellationRequested)
            {
                _auth.CancelDeviceFlow(deviceCode);
                return;
            }

            UserCodeText.Text = deviceCode.UserCode;
            CopyCodeButton.IsEnabled = true;
            OpenBrowserButton.IsEnabled = true;
            StatusText.Text = "Enter the one-time code on GitHub. Waiting for approval…";

            var authorized = await _auth.PollForTokenAsync(deviceCode, _cancellation.Token);
            if (_cancellation.IsCancellationRequested || _finished) return;

            if (authorized)
            {
                _finished = true;
                StatusText.Text = "Signed in successfully.";
                DialogResult = true;
            }
            else
            {
                ShowError("The one-time code expired before sign-in finished. Close this window and try again.");
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_finished && !_cancellation.IsCancellationRequested)
            {
                AppLog.Write("GitHub device sign-in failed");
                ShowError(GetFriendlyErrorMessage(ex));
            }
        }
        finally
        {
            _asyncCompleted = true;
            DisposeCancellationIfComplete();
        }
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UserCodeText.Text)) return;
        try
        {
            System.Windows.Clipboard.SetText(UserCodeText.Text);
            StatusText.Text = "Code copied. Enter it on the GitHub device sign-in page.";
            ErrorText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            AppLog.Write("Could not copy GitHub device sign-in code", ex);
            ShowError("Could not copy the code. Select it and copy it manually.");
        }
    }

    private void OpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(DeviceSignInUrl) { UseShellExecute = true });
            StatusText.Text = "GitHub device sign-in opened. Enter the one-time code shown above.";
            ErrorText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            AppLog.Write("Could not open GitHub device sign-in page", ex);
            ShowError("Could not open your browser. Visit https://github.com/login/device and enter the code above.");
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        StatusText.Text = "Sign-in did not finish.";
        OpenBrowserButton.IsEnabled = !string.IsNullOrWhiteSpace(UserCodeText.Text);
        CopyCodeButton.IsEnabled = !string.IsNullOrWhiteSpace(UserCodeText.Text);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _finished = true;
        CancelCurrentFlow();
        DialogResult = false;
    }

    private void CancelCurrentFlow()
    {
        if (_deviceCode is not null) _auth.CancelDeviceFlow(_deviceCode);
        if (!_asyncCompleted && !_cancellationDisposed) _cancellation.Cancel();
    }

    private void DisposeCancellationIfComplete()
    {
        if (!_windowClosed || !_asyncCompleted || _cancellationDisposed) return;
        _cancellation.Dispose();
        _cancellationDisposed = true;
    }

    private static string GetFriendlyErrorMessage(Exception exception)
    {
        var message = exception.Message;
        if (string.Equals(message, "GitHub device authorization was denied.", StringComparison.Ordinal))
            return "Sign-in was declined on GitHub. You can close this window and try again.";
        if (string.Equals(message, "GitHub device authorization expired. Start sign-in again.", StringComparison.Ordinal))
            return "The one-time code expired. Close this window and start sign-in again.";
        if (message.Contains("device_flow_disabled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message, "GitHub device authorization is disabled for this OAuth App. Enable the device flow in the app settings.", StringComparison.Ordinal))
            return "This GitHub OAuth App does not have Device Flow enabled. The app owner must enable Device Flow in the OAuth App settings.";
        if (string.Equals(message, "incorrect_client_credentials", StringComparison.Ordinal))
            return "GitHub rejected the app's sign-in configuration. Please contact the app maintainer.";
        return "Could not complete GitHub Copilot sign-in. Check your connection and try again.";
    }
}
