using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using flowboost.Models;
using flowboost.Services;

namespace flowboost.Views;

public partial class SettingsWindow : Window
{
    private readonly flowboost.App _app;
    private readonly AppSettings _draft;
    private readonly ObservableCollection<Preset> _presets;
    private readonly Action _authStateHandler;
    private bool _loadingFields;
    private bool _accountActionInProgress;

    public SettingsWindow()
    {
        InitializeComponent();
        _app = (flowboost.App)System.Windows.Application.Current;
        _draft = _app.Settings.Current.Clone();
        _presets = new ObservableCollection<Preset>(_draft.Presets);
        ViewTheme.Apply(this, _draft.Theme);
        AppVersionText.Text = $"Version {GetDisplayVersion()}";
        PresetList.ItemsSource = _presets;
        PresetList.DisplayMemberPath = nameof(Preset.Name);
        ModelBox.Text = _draft.Model;
        StartWithWindowsCheck.IsChecked = _draft.StartWithWindows;
        ThemeBox.SelectedIndex = _draft.Theme switch { AppTheme.Light => 1, AppTheme.Dark => 2, _ => 0 };
        PresetList.SelectedIndex = _presets.Count > 0 ? 0 : -1;
        UpdateAccountStatus();
        _authStateHandler = () => Dispatcher.InvokeAsync(UpdateAccountStatus);
        _app.Auth.AuthStateChanged += _authStateHandler;
        Closed += (_, _) => _app.Auth.AuthStateChanged -= _authStateHandler;
        Loaded += async (_, _) => await RefreshModelsAsync();
    }

    private void UpdateAccountStatus()
    {
        if (!_accountActionInProgress)
            AccountStatus.Text = _app.Auth.IsSignedIn
                ? "A GitHub sign-in is saved. Copilot checks it when you load models or use Copilot."
                : _app.Auth.StatusText;
        SignInButton.Content = _app.Auth.IsSignedIn ? "Sign in again" : "Sign in";
        SignInButton.IsEnabled = !_accountActionInProgress;
        SignOutButton.IsEnabled = !_accountActionInProgress && _app.Auth.IsSignedIn;
    }

    public void RefreshAccountStatus() => UpdateAccountStatus();
    public void ShowHotkeyRegistrationResults(IReadOnlyList<string> failures)
    {
        HotkeyHint.Text = failures.Count == 0 ? "Hotkeys are registered." : string.Join(" ", failures);
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (_accountActionInProgress) return;
        await RunAccountActionAsync("Complete sign-in in the sign-in window…", async () =>
        {
            await _app.SignInAsync(this);
        }, "Could not start GitHub Copilot sign-in. Please try again.");
    }

    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        await RunAccountActionAsync(
            "Signing out of flowboost…",
            async () => await _app.SignOutAsync(),
            "Could not sign out of flowboost. Please try again.");
    }

    private async Task RunAccountActionAsync(string progressMessage, Func<Task> action, string errorMessage)
    {
        if (_accountActionInProgress) return;

        _accountActionInProgress = true;
        AccountFeedback.Text = "";
        AccountFeedback.Visibility = Visibility.Collapsed;
        AccountStatus.Text = progressMessage;
        UpdateAccountStatusButtons();
        try
        {
            await action();
        }
        catch (Exception)
        {
            AppLog.Write("GitHub Copilot account action failed");
            AccountFeedback.Text = errorMessage;
            AccountFeedback.Visibility = Visibility.Visible;
        }
        finally
        {
            _accountActionInProgress = false;
            UpdateAccountStatus();
        }
    }

    private void UpdateAccountStatusButtons()
    {
        SignInButton.IsEnabled = false;
        SignOutButton.IsEnabled = false;
    }

    private async void RefreshModels_Click(object sender, RoutedEventArgs e) => await RefreshModelsAsync();

    private async Task RefreshModelsAsync()
    {
        RefreshModelsButton.IsEnabled = false;
        ModelStatus.Text = "Loading models…";
        ModelStatus.Foreground = (System.Windows.Media.Brush)FindResource("MutedForeground");
        try
        {
            var models = await _app.Copilot.ListModelsAsync();
            var currentText = ModelBox.Text;
            ModelBox.ItemsSource = models;
            ModelBox.Text = currentText;
            ModelStatus.Text = models.Count == 0 ? "No models found." : $"{models.Count} models available.";
            ModelStatus.Foreground = (System.Windows.Media.Brush)FindResource("MutedForeground");
        }
        catch (Exception ex)
        {
            ModelStatus.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            ModelStatus.Text = ex.Message == "Copilot rejected the saved sign-in. Sign in again from Settings."
                ? "Copilot rejected the saved sign-in. Sign in again from Settings."
                : "Couldn’t load models. Check your connection and try Refresh models again. If the problem continues, sign in again from Settings.";
        }
        finally { RefreshModelsButton.IsEnabled = true; }
    }

    private void PresetList_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadSelectedPreset();

    private void LoadSelectedPreset()
    {
        if (PresetList.SelectedItem is not Preset preset) return;
        _loadingFields = true;
        PresetNameBox.Text = preset.Name;
        PromptBox.Text = preset.Prompt;
        LanguageBox.Text = preset.ResponseLanguage;
        PresetEnabledCheck.IsChecked = preset.Enabled;
        HotkeyBox.Text = preset.Hotkey.ToString();
        _loadingFields = false;
    }

    private void PresetField_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loadingFields || PresetList.SelectedItem is not Preset preset) return;
        preset.Name = PresetNameBox.Text.Trim();
        preset.Prompt = PromptBox.Text;
        preset.ResponseLanguage = LanguageBox.Text.Trim();
        PresetList.Items.Refresh();
    }

    private void PresetEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loadingFields && PresetList.SelectedItem is Preset preset) preset.Enabled = PresetEnabledCheck.IsChecked == true;
    }

    private void AddPreset_Click(object sender, RoutedEventArgs e)
    {
        var preset = new Preset
        {
            Name = "New prompt",
            Prompt = "Help me with the selected text.",
            Hotkey = new HotkeyBinding(),
            ResponseLanguage = "English",
            Enabled = true
        };
        _presets.Add(preset);
        PresetList.SelectedItem = preset;
        PresetNameBox.Focus();
        PresetNameBox.SelectAll();
    }

    private void RemovePreset_Click(object sender, RoutedEventArgs e)
    {
        if (PresetList.SelectedItem is not Preset preset) return;
        var index = PresetList.SelectedIndex;
        _presets.Remove(preset);
        PresetList.SelectedIndex = Math.Min(index, _presets.Count - 1);
        if (_presets.Count == 0) ClearPresetFields();
    }

    private void ClearPresetFields()
    {
        _loadingFields = true;
        PresetNameBox.Clear(); PromptBox.Clear(); LanguageBox.Clear(); HotkeyBox.Clear(); PresetEnabledCheck.IsChecked = false;
        _loadingFields = false;
    }

    private void HotkeyBox_Click(object sender, MouseButtonEventArgs e)
    {
        HotkeyBox.Focus();
        HotkeyBox.Text = "Press a key…";
        HotkeyHint.Text = "Press a key combination. Esc cancels.";
        e.Handled = true;
    }

    private void HotkeyBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;
        if (e.Key == Key.Escape) { LoadSelectedPreset(); return; }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        if (PresetList.SelectedItem is not Preset preset) return;
        preset.Hotkey = new HotkeyBinding { Modifiers = Keyboard.Modifiers, Key = key };
        HotkeyBox.Text = preset.Hotkey.ToString();
        HotkeyHint.Text = "Key combination saved.";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _draft.Model = ModelBox.Text.Trim();
        _draft.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
        _draft.Theme = ThemeBox.SelectedIndex switch { 1 => AppTheme.Light, 2 => AppTheme.Dark, _ => AppTheme.System };
        _draft.Presets = [.. _presets];
        try
        {
            _app.Settings.Save(_draft.Clone());
            Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        { SaveStatus.Text = ex.Message; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void WhatChanged_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ReleaseNotesWindow { Owner = this };
        dialog.ShowDialog();
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckForUpdatesButton.IsEnabled = false;
        UpdateStatusText.Foreground = (System.Windows.Media.Brush)FindResource("MutedForeground");
        UpdateStatusText.Text = "Checking…";

        try
        {
            var result = await _app.CheckForUpdatesAsync(manual: true);
            switch (result.Status)
            {
                case UpdateCheckStatus.UpToDate:
                    UpdateStatusText.Text = "You're on the latest version.";
                    break;
                case UpdateCheckStatus.UpdateAvailable when result.Info is { } info:
                    UpdateStatusText.Text = $"Version {info.LatestVersion} is available.";
                    break;
                default:
                    ShowUpdateCheckFailure();
                    break;
            }
        }
        catch (Exception)
        {
            ShowUpdateCheckFailure();
        }
        finally
        {
            CheckForUpdatesButton.IsEnabled = true;
        }
    }

    private void ShowUpdateCheckFailure()
    {
        UpdateStatusText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        UpdateStatusText.Text = "Couldn't check for updates. Check your connection and try again.";
    }

    private static string GetDisplayVersion() => AppVersion.Display;
}
