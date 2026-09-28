using System.Threading;
using System.Windows;
using System.Diagnostics;
using System.IO;
using flowboost.Models;
using flowboost.Services;
using flowboost.Tray;
using flowboost.Views;

namespace flowboost;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private HotkeyService? _hotkeys;
    private SelectionService? _selection;
    private TrayIcon? _tray;
    private AutostartService? _autostart;
    private SettingsWindow? _settingsWindow;
    private DeviceCodeWindow? _deviceCodeWindow;
    private UpdateService _updates = null!;
    private UpdateAvailableWindow? _updateWindow;
    private bool _exiting;
    private readonly SemaphoreSlim _captureLock = new(1, 1);
    private readonly HashSet<PopupWindow> _popups = [];
    private readonly object _authStateLock = new();
    private Task _authStateTask = Task.CompletedTask;
    private Task<bool>? _signInTask;
    private IReadOnlyList<string> _hotkeyFailures = [];

    public new static App Current => (App)System.Windows.Application.Current;
    public SettingsService Settings { get; private set; } = null!;
    public CopilotService Copilot { get; private set; } = null!;
    public AuthService Auth { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(true, "Local\\flowboost.SingleInstance", out var created);
        if (!created) { Shutdown(); return; }
        Settings = new SettingsService(); Auth = new AuthService();
        Copilot = new CopilotService(Auth, Settings, Dispatcher);
        _selection = new SelectionService(); _autostart = new AutostartService();
        _hotkeys = new HotkeyService();
        _hotkeys.Pressed += OnHotkeyPressed;
        Settings.Changed += OnSettingsChanged;
        _tray = new TrayIcon(OpenSettings, SignIn, SetAutostart, ExitApplication);
        UpdateTray();
        Settings.ReportLoadError();
        Auth.Error += message => Dispatcher.InvokeAsync(() => _tray?.Balloon("GitHub sign-in", message));
        _autostart.RemoveLegacyValue();
        try { _autostart.SetEnabled(Settings.Current.StartWithWindows); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { _tray.Balloon("Autostart", ex.Message); }
        RegisterHotkeys();
        Auth.AuthStateChanged += OnAuthStateChanged;
        _updates = new UpdateService();
        _ = CheckForUpdatesAtStartupAsync();
        if (!Auth.IsSignedIn)
        {
            ShowSettings();
            _tray?.Balloon("flowboost", "Sign in to GitHub Copilot to get started");
        }
        else
        {
            _ = ValidateSavedSignInAsync();
        }
    }

    private async Task CheckForUpdatesAtStartupAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            await CheckForUpdatesAsync(manual: false);
        }
        catch (Exception ex)
        {
            AppLog.Write("Update check failed", ex);
        }
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(bool manual)
    {
        if (_exiting) return new UpdateCheckResult(UpdateCheckStatus.Failed, null);

        var result = await _updates.CheckAsync(CancellationToken.None);
        var info = result.Info;
        if (result.Status != UpdateCheckStatus.UpdateAvailable || info is null ||
            (!manual && info.LatestVersion.ToString() == Settings.Current.SkippedUpdateVersion))
            return result;

        UpdateDecision? decision;
        try
        {
            decision = await Dispatcher.InvokeAsync<UpdateDecision?>(() =>
            {
                if (_exiting || _updateWindow is not null) return null;

                var window = new UpdateAvailableWindow(info)
                {
                    Topmost = true,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };
                if (_settingsWindow?.IsVisible == true) window.Owner = _settingsWindow;
                _updateWindow = window;
                try
                {
                    window.ShowDialog();
                    return window.Decision;
                }
                finally { _updateWindow = null; }
            }).Task;
        }
        catch (Exception ex)
        {
            AppLog.Write("Update check failed", ex);
            _tray?.Balloon("Updates", "Could not show the update dialog.");
            return result;
        }

        if (decision == UpdateDecision.OpenReleasePage)
        {
            try { Process.Start(new ProcessStartInfo(info.ReleaseUrl) { UseShellExecute = true }); }
            catch (Exception ex)
            {
                AppLog.Write("Update check failed", ex);
                _tray?.Balloon("Updates", "Could not open the release page.");
            }
        }
        else if (decision == UpdateDecision.Skip)
        {
            try
            {
                var settings = Settings.Current.Clone();
                settings.SkippedUpdateVersion = info.LatestVersion.ToString();
                Settings.Save(settings);
            }
            catch (Exception ex)
            {
                AppLog.Write("Update check failed", ex);
                _tray?.Balloon("Updates", "Could not save the skipped version.");
            }
        }

        return result;
    }

    private async Task ValidateSavedSignInAsync()
    {
        try { await Auth.ValidateSavedTokenAsync(CancellationToken.None); }
        catch (Exception) { AppLog.Write("Saved GitHub sign-in validation failed"); }
    }

    public static void OpenSettings() => Current.ShowSettings();

    private void ShowSettings()
    {
        try
        {
            if (_settingsWindow is null)
            {
                _settingsWindow = new SettingsWindow();
                _settingsWindow.Closed += (_, _) => _settingsWindow = null;
                _settingsWindow.ShowHotkeyRegistrationResults(_hotkeyFailures);
            }
            if (!_settingsWindow.IsVisible) _settingsWindow.Show();
            _settingsWindow.Activate();
        }
        catch (Exception ex)
        {
            AppLog.Write("Settings could not be opened", ex);
            _tray?.Balloon("Settings", $"Could not open Settings: {ex.Message}");
        }
    }

    private async void OnHotkeyPressed(Preset preset)
    {
        if (!await _captureLock.WaitAsync(0)) return;
        PopupWindow? popup = null;
        try
        {
            var capture = await _selection!.CaptureAsync();
            if (capture.Status != SelectionCaptureStatus.Success)
            {
                AppLog.WriteSelectionCaptureFailure(capture.Status);
                var message = capture.Status switch
                {
                    SelectionCaptureStatus.HeldModifiers => "Release shortcut keys and try again",
                    SelectionCaptureStatus.NoText => "No text selected",
                    SelectionCaptureStatus.ClipboardUnchanged => "Could not copy the selection. Try again",
                    SelectionCaptureStatus.ForegroundChanged => "The active window changed before the selection could be copied. Try again",
                    _ => "Could not capture the selection. Try again"
                };
                _tray?.Balloon("flowboost", message);
                return;
            }
            var selection = capture.Text!;
            popup = new PopupWindow(preset.Name, Settings.Current.Model, selection);
            _popups.Add(popup);
            popup.Closed += (_, _) => _popups.Remove(popup);
            popup.ShowNearCursor();

            ChatSession? session = null;
            try
            {
                session = await Copilot.CreateSessionAsync(preset);
                if (popup.IsClosing)
                {
                    await session.DisposeAsync();
                    return;
                }

                popup.AttachSession(session);
                await session.SendAsync(preset.Prompt + "\n\n---\n" + selection);
            }
            catch (Exception ex)
            {
                if (popup.IsClosing)
                {
                    if (session is not null) await session.DisposeAsync();
                    return;
                }
                if (ex is not OperationCanceledException)
                    AppLog.Write(session is null ? "Session creation failed" : "Initial session send failed", ex);
                popup.ShowError(ex.Message);
            }
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException && (popup is null || !popup.IsClosing))
                AppLog.Write("Hotkey capture failed", ex);
            if (popup is not null && !popup.IsClosing) popup.ShowError(ex.Message);
            else if (popup is null) _tray?.Balloon("flowboost", ex.Message);
        }
        finally { _captureLock.Release(); }
    }

    private void OnSettingsChanged()
    {
        RegisterHotkeys();
        try { _autostart?.SetEnabled(Settings.Current.StartWithWindows); } catch (Exception ex) { _tray?.Balloon("Autostart", ex.Message); }
        UpdateTray();
    }

    private void SignIn() => _ = SignInAsync();

    public Task<bool> SignInAsync(Window? owner = null)
    {
        if (!Dispatcher.CheckAccess())
            return Dispatcher.InvokeAsync(() => SignInAsync(owner)).Task.Unwrap();
        if (_exiting) return Task.FromResult(false);
        if (_signInTask is not null)
        {
            FocusDeviceCodeWindow();
            return _signInTask;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _signInTask = completion.Task;
        _ = CompleteSignInAsync(owner, completion);
        return completion.Task;
    }

    private async Task CompleteSignInAsync(Window? owner, TaskCompletionSource<bool> completion)
    {
        DeviceCodeWindow? dialog = null;
        try
        {
            dialog = new DeviceCodeWindow(Auth);
            _deviceCodeWindow = dialog;
            var dialogOwner = owner ?? (_settingsWindow?.IsVisible == true ? _settingsWindow : null);
            if (dialogOwner is not null && dialogOwner.IsVisible) dialog.Owner = dialogOwner;
            if (dialog.ShowDialog() == true)
            {
                await RefreshAuthAsync();
                completion.TrySetResult(true);
            }
            else completion.TrySetResult(false);
        }
        catch (Exception ex)
        {
            AppLog.Write("GitHub sign-in failed", ex);
            _tray?.Balloon("GitHub sign-in", ex.Message);
            completion.TrySetResult(false);
        }
        finally
        {
            if (ReferenceEquals(_deviceCodeWindow, dialog)) _deviceCodeWindow = null;
            if (ReferenceEquals(_signInTask, completion.Task)) _signInTask = null;
        }
    }

    private void FocusDeviceCodeWindow()
    {
        try
        {
            if (_deviceCodeWindow?.WindowState == WindowState.Minimized)
                _deviceCodeWindow.WindowState = WindowState.Normal;
            _deviceCodeWindow?.Activate();
        }
        catch (Exception ex)
        {
            AppLog.Write("Could not focus the GitHub sign-in window", ex);
            _tray?.Balloon("GitHub sign-in", "The sign-in window could not be focused.");
        }
    }

    public async Task RefreshAuthAsync()
    {
        await GetAuthStateTask();
        UpdateTray();
        _settingsWindow?.RefreshAccountStatus();
    }

    public async Task SignOutAsync()
    {
        CloseDeviceCodeWindow();
        Auth.SignOut();
        await GetAuthStateTask();
        UpdateTray();
        _settingsWindow?.RefreshAccountStatus();
    }

    private void CloseDeviceCodeWindow()
    {
        var dialog = _deviceCodeWindow;
        if (dialog is null) return;
        try { dialog.Close(); }
        catch (Exception ex) { AppLog.Write("Could not close the GitHub sign-in window", ex); }
    }
    private void SetAutostart(bool enabled)
    {
        try
        {
            var settings = Settings.Current.Clone(); settings.StartWithWindows = enabled; Settings.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        { _tray?.Balloon("flowboost", $"Could not update startup setting: {ex.Message}"); UpdateTray(); }
    }
    private void UpdateTray() => _tray?.Update(Auth.IsSignedIn, Settings.Current.StartWithWindows);

    private void OnAuthStateChanged()
    {
        lock (_authStateLock)
        {
            _authStateTask = ReconcileAuthStateSafelyAsync(_authStateTask);
        }
    }

    private Task GetAuthStateTask()
    {
        lock (_authStateLock) return _authStateTask;
    }

    private async Task ReconcileAuthStateSafelyAsync(Task previousReconciliation)
    {
        try
        {
            await previousReconciliation;
            await Dispatcher.InvokeAsync(ReconcileAuthStateAsync).Task.Unwrap();
        }
        catch (Exception ex)
        {
            AppLog.Write("Authentication state update failed", ex);
            _tray?.Balloon("GitHub sign-in", ex.Message);
        }
    }

    private async Task ReconcileAuthStateAsync()
    {
        if (!Auth.IsSignedIn)
        {
            CloseDeviceCodeWindow();
            if (string.Equals(Auth.StatusText, "Your GitHub sign-in has expired. Sign in again.", StringComparison.Ordinal))
            {
                ShowSettings();
                _tray?.Balloon("GitHub sign-in", "Your GitHub sign-in has expired. Sign in again.");
            }
        }
        foreach (var popup in _popups.ToArray()) popup.Close();
        await Copilot.RefreshAuthenticationAsync();
        UpdateTray();
        _settingsWindow?.RefreshAccountStatus();
    }

    private void RegisterHotkeys()
    {
        if (_hotkeys is null) return;
        _hotkeyFailures = _hotkeys.RegisterAll(Settings.Current.Presets);
        foreach (var failure in _hotkeyFailures) _tray?.Balloon("flowboost hotkey", failure);
        _settingsWindow?.ShowHotkeyRegistrationResults(_hotkeyFailures);
    }

    private void ShowErrorPopup(string title, string message, bool settings)
    {
        var popup = new PopupWindow(title, message, settings);
        _popups.Add(popup); popup.Closed += (_, _) => _popups.Remove(popup); popup.ShowNearCursor();
    }

    private async void ExitApplication() => await ExitAsync();
    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        try
        {
            _hotkeys?.StopIntake();
            CloseDeviceCodeWindow();
            foreach (var popup in _popups.ToArray()) popup.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await Copilot.DisposeAsync().AsTask().WaitAsync(timeout.Token); }
            catch (OperationCanceledException) { AppLog.Write("Timed out while shutting down Copilot service"); }
            catch (Exception ex) { AppLog.Write("Copilot shutdown failed", ex); }
        }
        catch (Exception ex) { AppLog.Write("Application shutdown failed", ex); }
        finally
        {
            try { _hotkeys?.Dispose(); } catch (Exception ex) { AppLog.Write("Hotkey shutdown failed", ex); }
            try { _tray?.Dispose(); } catch (Exception ex) { AppLog.Write("Tray shutdown failed", ex); }
            try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { }
            _mutex?.Dispose();
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_exiting) { _hotkeys?.Dispose(); _tray?.Dispose(); _mutex?.Dispose(); }
        base.OnExit(e);
    }
}
