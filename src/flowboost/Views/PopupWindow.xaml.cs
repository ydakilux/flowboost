using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MdXaml;
using flowboost.Models;
using flowboost.Services;

namespace flowboost.Views;

public partial class PopupWindow : Window
{
    private ChatSession? _session;
    private readonly DispatcherTimer _renderTimer;
    private readonly DispatcherTimer _busyTimer;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _elapsedTimer;
    private readonly List<MarkdownScrollViewer> _answers = [];
    private MarkdownScrollViewer? _activeAnswer;
    private string _pendingMarkdown = string.Empty;
    private string _lastAnswer = string.Empty;
    private bool _closing;
    private bool _hasShown;
    private bool _isReady;
    private bool _hasResponseFinished;
    private bool _isTurnActive;
    private bool _hasTurnError;
    private bool _wasAborted;
    private int _busyFrame;
    private int _elapsedSeconds;
    private Preset? _preset;
    private readonly string _initialPrompt;
    private string? _lastPrompt;
    private bool _isRetrying;
    public bool IsClosing => _closing || (_hasShown && !IsVisible);

    public PopupWindow(ChatSession session, string initialSelectionPreview)
        : this(session.PresetName, session.Model, initialSelectionPreview, null)
    {
        AttachSession(session);
    }

    public PopupWindow(string presetName, string model, string initialSelectionPreview)
        : this(presetName, model, initialSelectionPreview, null)
    {
    }

    public PopupWindow(Preset preset, string model, string initialSelectionPreview)
        : this(preset.Name, model, initialSelectionPreview, preset)
    {
    }

    private PopupWindow(string presetName, string model, string initialSelectionPreview, Preset? preset)
    {
        InitializeComponent();
        var app = (flowboost.App)System.Windows.Application.Current;
        _preset = preset;
        _initialPrompt = preset is null ? string.Empty : preset.Prompt + "\n\n---\n" + initialSelectionPreview;
        ViewTheme.Apply(this, app.Settings.Current.Theme);
        PresetTitle.Text = presetName;
        ModelLabel.Text = model;
        var preview = initialSelectionPreview.Trim();
        if (preview.Length > 320) preview = preview[..320] + "…";
        SelectionPreview.Text = string.IsNullOrWhiteSpace(initialSelectionPreview)
            ? string.Empty : $"Selected text  ·  {preview}";
        SelectionPreview.Visibility = string.IsNullOrWhiteSpace(initialSelectionPreview) ? Visibility.Collapsed : Visibility.Visible;
        _renderTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
        _renderTimer.Tick += RenderPendingMarkdown;
        _busyTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(130) };
        _busyTimer.Tick += AnimateBusyIndicator;
        _statusTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += HideCompletedStatus;
        _elapsedTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += UpdateElapsedStatus;
        SetHeaderStatus("Connecting…", true);
        _isTurnActive = true;
        PreviewKeyDown += OnPreviewKeyDown;
        Activated += OnPopupActivated;
        Closed += OnClosed;
        SizeToSelection(initialSelectionPreview);
    }

    public PopupWindow(string presetName, string errorMessage, bool showOpenSettings)
    {
        InitializeComponent();
        _initialPrompt = string.Empty;
        ViewTheme.Apply(this, ((flowboost.App)System.Windows.Application.Current).Settings.Current.Theme);
        PresetTitle.Text = string.IsNullOrWhiteSpace(presetName) ? "flowboost" : presetName;
        ModelLabel.Text = string.Empty;
        CopyButton.Visibility = Visibility.Collapsed;
        FollowupBox.Visibility = Visibility.Collapsed;
        SendButton.Visibility = Visibility.Collapsed;
        ErrorLabel.Text = errorMessage;
        ErrorLabel.Visibility = Visibility.Visible;
        if (showOpenSettings)
        {
            var button = new System.Windows.Controls.Button { Content = "Open settings", Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            button.Click += (_, _) => { flowboost.App.OpenSettings(); Close(); };
            ConversationPanel.Children.Insert(1, button);
        }
        _renderTimer = new DispatcherTimer();
        _busyTimer = new DispatcherTimer();
        _statusTimer = new DispatcherTimer();
        _elapsedTimer = new DispatcherTimer();
        PreviewKeyDown += OnPreviewKeyDown;
        Activated += OnPopupActivated;
        Closed += OnClosed;
    }

    /// <summary>Takes ownership of the session and disposes it when this popup closes.</summary>
    public void AttachSession(ChatSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_closing)
        {
            _ = DisposeSessionSafelyAsync(session);
            return;
        }
        if (_session is not null)
        {
            _ = DisposeSessionSafelyAsync(session);
            throw new InvalidOperationException("A session is already attached to this popup; the supplied session was disposed.");
        }

        _session = session;
        _isReady = false;
        session.Delta += OnDelta;
        session.Completed += OnCompleted;
        session.Error += OnError;
        session.Aborted += OnAborted;
        session.Idle += OnIdle;
        session.StateChanged += OnSessionStateChanged;
        _isReady = true;
        if (session.IsBusy)
        {
            _isTurnActive = true;
            StartElapsedTimer();
            SetHeaderStatus("Waiting…", true);
        }
        UpdateSessionControls(session.IsBusy);
        CopyButton.IsEnabled = !string.IsNullOrEmpty(_lastAnswer);
        if (!session.IsBusy) FocusComposerSoon();
    }

    public Task StartTurnAsync(string fullPrompt)
    {
        _lastPrompt = fullPrompt;
        return SendTurnAsync(fullPrompt, showUserMessage: false);
    }

    public void ShowNearCursor()
    {
        if (IsClosing || _hasShown) return;
        var cursor = NativePoint.Cursor;
        var monitor = MonitorFromPoint(cursor, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            var bounds = info.Work;
            var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
            var monitorScale = GetMonitorScale(monitor);
            var scaleX = transform.HasValue ? transform.Value.Transform(new Vector(1, 0)).Length : 1d / monitorScale;
            var scaleY = transform.HasValue ? transform.Value.Transform(new Vector(0, 1)).Length : 1d / monitorScale;
            if (scaleX <= 0) scaleX = 1; if (scaleY <= 0) scaleY = 1;
            var workLeft = bounds.Left * scaleX; var workTop = bounds.Top * scaleY;
            var workWidth = (bounds.Right - bounds.Left) * scaleX; var workHeight = (bounds.Bottom - bounds.Top) * scaleY;
            MinWidth = Math.Min(MinWidth, workWidth);
            MinHeight = Math.Min(MinHeight, workHeight);
            var width = Math.Min(Math.Max(MinWidth, Width), workWidth);
            var height = Math.Min(Math.Max(MinHeight, Height), workHeight);
            Width = width; Height = height;
            var cursorX = cursor.X * scaleX; var cursorY = cursor.Y * scaleY;
            Left = Math.Max(workLeft, Math.Min(cursorX + 16, workLeft + workWidth - width));
            Top = Math.Max(workTop, Math.Min(cursorY + 20, workTop + workHeight - height));
        }
        else
        {
            Width = Math.Max(MinWidth, Width); Height = Math.Max(MinHeight, Height);
            Left = cursor.X + 16; Top = cursor.Y + 20;
        }
        if (!IsVisible) Show();
        _hasShown = true;
        Activate();
        FocusComposerSoon();
    }

    private void SizeToSelection(string selection)
    {
        var length = selection?.Length ?? 0;
        Width = Math.Min(920, Math.Max(640, 560 + Math.Min(length, 1200) * 0.22));
        Height = Math.Min(760, Math.Max(480, 390 + Math.Min(length, 1800) * 0.18));
    }

    private static double GetMonitorScale(IntPtr monitor)
    {
        try
        {
            if (GetDpiForMonitor(monitor, 0, out var x, out _) == 0 && x > 0) return x / 96d;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return 1d;
    }

    private void OnDelta(string text)
    {
        if (_closing) return;
        _isTurnActive = true;
        StartElapsedTimer();
        _wasAborted = false;
        SetHeaderStatus("Responding…", true);
        EnsureAnswerViewer();
        _pendingMarkdown += text;
        if (!_renderTimer.IsEnabled) _renderTimer.Start();
    }

    private void OnCompleted(string answer)
    {
        if (_closing) return;
        _renderTimer.Stop();
        _pendingMarkdown = string.Empty;
        _lastAnswer = answer;
        _hasResponseFinished = true;
        _isTurnActive = false;
        ResetElapsedTimer();
        _hasTurnError = false;
        _wasAborted = false;
        ErrorLabel.Visibility = Visibility.Collapsed;
        SetHeaderStatus("Done", false, autoHide: true);
        EnsureAnswerViewer();
        SetMarkdown(_answers[^1], answer);
        _activeAnswer = null;
        ConversationScroll.ScrollToEnd();
        CopyButton.IsEnabled = !string.IsNullOrEmpty(answer);
        UpdateSessionControls(false);
        FocusComposerSoon(ignoreBusy: true);
    }

    private void OnError(ChatError error)
    {
        if (_closing) return;
        _hasResponseFinished = true;
        _isTurnActive = false;
        ResetElapsedTimer();
        _hasTurnError = true;
        _renderTimer.Stop();
        _activeAnswer = null;
        ErrorLabel.Text = error.Message;
        ErrorLabel.Visibility = Visibility.Visible;
        OpenSettingsButton.Visibility = error.Kind == ChatErrorKind.SignInExpired ? Visibility.Visible : Visibility.Collapsed;
        ModelRetryPanel.Visibility = error.Kind == ChatErrorKind.ModelUnavailable && _preset is not null ? Visibility.Visible : Visibility.Collapsed;
        if (ModelRetryPanel.Visibility == Visibility.Visible) _ = LoadModelsAsync(error.Model ?? ModelLabel.Text);
        UpdateSessionControls(_session?.IsBusy == true);
        HideHeaderStatus();
    }

    public void ShowError(string message)
    {
        if (_closing) return;
        _hasResponseFinished = true;
        _isTurnActive = false;
        _hasTurnError = true;
        ResetElapsedTimer();
        ErrorLabel.Text = message;
        ErrorLabel.Visibility = Visibility.Visible;
        OpenSettingsButton.Visibility = Visibility.Collapsed;
        ModelRetryPanel.Visibility = Visibility.Collapsed;
        UpdateSessionControls(_session?.IsBusy == true);
        HideHeaderStatus();
    }

    private async Task LoadModelsAsync(string failedModel)
    {
        if (_preset is null || _closing) return;
        ModelComboBox.Items.Clear();
        ModelListStatus.Visibility = Visibility.Collapsed;
        var app = (flowboost.App)System.Windows.Application.Current;
        IReadOnlyList<string> models;
        try { models = await app.Copilot.ListModelsAsync(); }
        catch (Exception)
        {
            models = [failedModel];
            ModelListStatus.Text = "Couldn't load model list";
            ModelListStatus.Visibility = Visibility.Visible;
        }
        if (_closing || ModelRetryPanel.Visibility != Visibility.Visible) return;
        var uniqueModels = models.Where(model => !string.IsNullOrWhiteSpace(model)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (uniqueModels.Count == 0) uniqueModels.Add(failedModel);
        foreach (var model in uniqueModels) ModelComboBox.Items.Add(model);
        var preferred = uniqueModels.FirstOrDefault(model => !string.Equals(model, failedModel, StringComparison.OrdinalIgnoreCase));
        ModelComboBox.SelectedItem = preferred ?? uniqueModels[0];
        RetryModelButton.IsEnabled = _session is not null;
    }

    private void OnIdle()
    {
        if (_closing) return;
        _hasResponseFinished = true;
        _isTurnActive = false;
        ResetElapsedTimer();
        UpdateSessionControls(false);
        if (_wasAborted) return;
        if (_hasTurnError) HideHeaderStatus();
        else
        {
            SetHeaderStatus("Done", false, autoHide: true);
            FocusComposerSoon();
        }
    }

    private void OnSessionStateChanged()
    {
        if (_closing || _session is null) return;
        var busy = _session.IsBusy;
        if (busy)
        {
            _hasResponseFinished = false;
            _isTurnActive = true;
            if (!_elapsedTimer.IsEnabled) StartElapsedTimer();
            CopyButton.IsEnabled = false;
        }
        UpdateSessionControls(busy);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        FocusComposerSoon();
    }

    private void UpdateSessionControls(bool isBusy)
    {
        var sessionBusy = isBusy || _session?.IsBusy == true;
        var canCompose = _isReady && (!sessionBusy || _hasResponseFinished);
        SendButton.IsEnabled = _isReady && !sessionBusy;
        FollowupBox.IsEnabled = canCompose;
        FollowupPlaceholder.Visibility = string.IsNullOrEmpty(FollowupBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        FollowupPlaceholder.Text = sessionBusy && !_hasResponseFinished ? "Waiting for reply…" : "Add a comment or question…";
        ComposerBorder.Background = (System.Windows.Media.Brush)FindResource(canCompose ? "PanelAltBackground" : "PanelBackground");
        ComposerBorder.Opacity = canCompose ? 1 : 0.72;
        CopyButton.IsEnabled = _isReady && !string.IsNullOrEmpty(_lastAnswer);
        StopButton.Visibility = sessionBusy && !_hasResponseFinished ? Visibility.Visible : Visibility.Collapsed;
        if (sessionBusy && _isTurnActive)
        {
            if (_elapsedSeconds < 15) SetHeaderStatus(_activeAnswer is null ? "Waiting…" : "Responding…", true);
        }
        else if (!sessionBusy && _hasResponseFinished)
        {
            _isTurnActive = false;
            if (_wasAborted)
            {
                if (HeaderStatusLabel.Text != "Stopped") SetHeaderStatus("Stopped", false, autoHide: true);
            }
            else if (HeaderStatusLabel.Text != "Done") SetHeaderStatus("Done", false, autoHide: true);
        }
    }

    private void EnsureAnswerViewer()
    {
        if (_activeAnswer is not null) return;
        var viewer = new MarkdownScrollViewer
        {
            Markdown = string.Empty,
            Foreground = (System.Windows.Media.Brush)FindResource("Foreground"),
            Background = System.Windows.Media.Brushes.Transparent,
            Margin = new Thickness(0, 0, 0, 12),
            Tag = "active"
        };
        _answers.Add(viewer);
        _activeAnswer = viewer;
        ConversationPanel.Children.Add(viewer);
    }

    private static void SetMarkdown(MarkdownScrollViewer viewer, string markdown) => viewer.Markdown = markdown;

    private void RenderPendingMarkdown(object? sender, EventArgs e)
    {
        if (_answers.Count == 0 || _pendingMarkdown.Length == 0) return;
        var delta = _pendingMarkdown;
        _pendingMarkdown = string.Empty;
        var viewer = _activeAnswer ?? _answers[^1];
        SetMarkdown(viewer, (viewer.Markdown ?? string.Empty) + delta);
        ConversationScroll.ScrollToEnd();
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendFollowupAsync();

    private async void FollowupBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            await SendFollowupAsync();
        }
    }

    private async Task SendFollowupAsync()
    {
        if (!_isReady || _session is null || _session.IsBusy || string.IsNullOrWhiteSpace(FollowupBox.Text)) return;
        var question = FollowupBox.Text.Trim();
        await SendTurnAsync(question);
    }

    private async Task SendTurnAsync(string question, bool showUserMessage = true, bool preserveStatus = false)
    {
        if (!_isReady || _session is null || _session.IsBusy || string.IsNullOrWhiteSpace(question)) return;
        ErrorLabel.Visibility = Visibility.Collapsed;
        OpenSettingsButton.Visibility = Visibility.Collapsed;
        ModelRetryPanel.Visibility = Visibility.Collapsed;
        if (!preserveStatus) RetryStatusLine.Visibility = Visibility.Collapsed;
        _hasResponseFinished = false;
        _isTurnActive = true;
        _wasAborted = false;
        _lastPrompt = question;
        StartElapsedTimer();
        _hasTurnError = false;
        _pendingMarkdown = string.Empty;
        if (showUserMessage)
        {
            var userTurn = new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap, Foreground = (System.Windows.Media.Brush)FindResource("Foreground"), Background = (System.Windows.Media.Brush)FindResource("PanelAltBackground"), Padding = new Thickness(10), Margin = new Thickness(48, 0, 0, 10), HorizontalAlignment = System.Windows.HorizontalAlignment.Right, MaxWidth = 520 };
            ConversationPanel.Children.Add(userTurn);
            FollowupBox.Clear();
        }
        _activeAnswer = null;
        _isTurnActive = true;
        SetHeaderStatus("Waiting…", true);
        UpdateSessionControls(true);
        ConversationScroll.ScrollToEnd();
        try { await _session.SendAsync(question); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async Task LoadModelsAndRetryAsync()
    {
        if (_closing || _isRetrying || _preset is null || ModelComboBox.SelectedItem is not string chosenModel) return;
        var app = (flowboost.App)System.Windows.Application.Current;
        var prompt = _lastPrompt ?? _session?.LastPrompt ?? _initialPrompt;
        if (string.IsNullOrWhiteSpace(prompt)) return;
        _isRetrying = true;
        ModelRetryPanel.IsEnabled = false;
        RetryStatusLine.Visibility = Visibility.Collapsed;
        ErrorLabel.Visibility = Visibility.Collapsed;
        ModelRetryPanel.Visibility = Visibility.Collapsed;
        OpenSettingsButton.Visibility = Visibility.Collapsed;
        var previous = _session;
        DetachSession(previous);
        _session = null;
        _isReady = false;
        UpdateSessionControls(false);
        try
        {
            if (previous is not null) await DisposeSessionSafelyAsync(previous);
            if (_closing) return;
            var session = await app.Copilot.CreateSessionAsync(_preset, chosenModel);
            if (_closing)
            {
                await DisposeSessionSafelyAsync(session);
                return;
            }
            AttachSession(session);
            ModelLabel.Text = chosenModel;
            if (UseModelByDefaultCheckBox.IsChecked == true)
            {
                try
                {
                    var settings = app.Settings.Current.Clone();
                    settings.Model = chosenModel;
                    app.Settings.Save(settings);
                }
                catch (Exception)
                {
                    RetryStatusLine.Text = "Couldn't save the default model setting.";
                    RetryStatusLine.Visibility = Visibility.Visible;
                }
            }
            await SendTurnAsync(prompt, showUserMessage: false, preserveStatus: true);
        }
        catch (Exception ex)
        {
            if (!_closing)
            {
                ShowError(ex.Message);
                if (_preset is not null) ModelRetryPanel.Visibility = Visibility.Visible;
                RetryStatusLine.Text = "Retry couldn't be started. Please try again.";
                RetryStatusLine.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _isRetrying = false;
            ModelRetryPanel.IsEnabled = true;
        }
    }

    private void DetachSession(ChatSession? session)
    {
        if (session is null) return;
        session.Delta -= OnDelta;
        session.Completed -= OnCompleted;
        session.Error -= OnError;
        session.Aborted -= OnAborted;
        session.Idle -= OnIdle;
        session.StateChanged -= OnSessionStateChanged;
    }

    private void StartElapsedTimer()
    {
        _elapsedSeconds = 0;
        _elapsedTimer.Stop();
        _elapsedTimer.Start();
    }

    private void ResetElapsedTimer()
    {
        _elapsedTimer.Stop();
        _elapsedSeconds = 0;
    }

    private void UpdateElapsedStatus(object? sender, EventArgs e)
    {
        if (_closing || !_isTurnActive) { _elapsedTimer.Stop(); return; }
        _elapsedSeconds++;
        if (_elapsedSeconds >= 15)
            SetHeaderStatus($"Waiting… {_elapsedSeconds} s", true);
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        var session = _session;
        if (_closing || session is null) return;
        StopButton.IsEnabled = false;
        try { await session.AbortAsync(); }
        catch (Exception) { }
        finally { if (!_closing) StopButton.IsEnabled = true; }
    }

    private void OnAborted()
    {
        if (_closing) return;
        _hasResponseFinished = true;
        _isTurnActive = false;
        _hasTurnError = false;
        ResetElapsedTimer();
        _renderTimer.Stop();
        _pendingMarkdown = string.Empty;
        _activeAnswer = null;
        StoppedLine.Visibility = Visibility.Visible;
        _wasAborted = true;
        ConversationPanel.Children.Remove(StoppedLine);
        ConversationPanel.Children.Add(StoppedLine);
        SetHeaderStatus("Stopped", false, autoHide: true);
        UpdateSessionControls(false);
        FocusComposerSoon(ignoreBusy: true);
        ConversationScroll.ScrollToEnd();
    }

    private void RetryModel_Click(object sender, RoutedEventArgs e) => _ = LoadModelsAndRetryAsync();
    private void OpenSettings_Click(object sender, RoutedEventArgs e) => flowboost.App.OpenSettings();

    private void FollowupBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FollowupPlaceholder.Visibility = string.IsNullOrEmpty(FollowupBox.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Composer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Activate();
        if (!_isReady || _session?.IsBusy == true) return;
        FocusManager.SetFocusedElement(this, FollowupBox);
        FollowupBox.Focus();
        Keyboard.Focus(FollowupBox);
    }

    private void OnPopupActivated(object? sender, EventArgs e) => FocusComposerSoon();

    private void FocusComposerSoon(bool ignoreBusy = false)
    {
        if (!_isReady || (!ignoreBusy && _session?.IsBusy == true) || !IsVisible) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (_closing || !_isReady || (!ignoreBusy && _session?.IsBusy == true) || !IsVisible) return;
            FocusManager.SetFocusedElement(this, FollowupBox);
            FollowupBox.Focus();
            Keyboard.Focus(FollowupBox);
        }));
    }

    private void SetBusyIndicator(bool active)
    {
        if (!active || _closing)
        {
            _busyTimer.Stop();
            BusyDots.Visibility = Visibility.Collapsed;
            return;
        }

        BusyDots.Visibility = Visibility.Visible;
        if (!SystemParameters.ClientAreaAnimation)
        {
            _busyTimer.Stop();
            BusyDot1.Opacity = BusyDot2.Opacity = BusyDot3.Opacity = 0.65;
            return;
        }

        if (!_busyTimer.IsEnabled) _busyTimer.Start();
    }

    private void AnimateBusyIndicator(object? sender, EventArgs e)
    {
        if (_closing || BusyDots.Visibility != Visibility.Visible || !SystemParameters.ClientAreaAnimation)
        {
            SetBusyIndicator(false);
            return;
        }

        var phase = _busyFrame++ % 6;
        BusyDot1.Opacity = GetBusyDotOpacity(phase);
        BusyDot2.Opacity = GetBusyDotOpacity((phase + 2) % 6);
        BusyDot3.Opacity = GetBusyDotOpacity((phase + 4) % 6);
    }

    private static double GetBusyDotOpacity(int phase) => phase switch
    {
        0 => 1.0,
        1 => 0.72,
        2 => 0.35,
        _ => 0.22
    };

    private void SetHeaderStatus(string text, bool busy, bool autoHide = false)
    {
        _statusTimer.Stop();
        HeaderStatusLabel.Text = text;
        HeaderStatusBadge.Visibility = Visibility.Visible;
        SetBusyIndicator(busy);
        if (autoHide) _statusTimer.Start();
    }

    private void HideCompletedStatus(object? sender, EventArgs e)
    {
        _statusTimer.Stop();
        if (!_isTurnActive && HeaderStatusLabel.Text == "Done") HideHeaderStatus();
        else if (!_isTurnActive && HeaderStatusLabel.Text == "Stopped") HideHeaderStatus();
    }

    private void HideHeaderStatus()
    {
        _statusTimer.Stop();
        _elapsedTimer.Stop();
        SetBusyIndicator(false);
        HeaderStatusBadge.Visibility = Visibility.Collapsed;
        HeaderStatusLabel.Text = string.Empty;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!_isReady || string.IsNullOrEmpty(_lastAnswer)) return;
        try { System.Windows.Clipboard.SetText(_lastAnswer); CopyButton.Content = "Copied"; }
        catch (Exception) { CopyButton.Content = "Could not copy"; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.Escape) { e.Handled = true; Close(); } }
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed || IsInsideButton(e.OriginalSource as DependencyObject)) return;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            AppLog.Write("Popup header drag could not be started");
        }
    }

    private static bool IsInsideButton(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is System.Windows.Controls.Button) return true;
            try
            {
                element = element switch
                {
                    ContentElement contentElement => ContentOperations.GetParent(contentElement) ?? (contentElement as FrameworkContentElement)?.Parent,
                    Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(element),
                    _ => LogicalTreeHelper.GetParent(element)
                };
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
        return false;
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _closing = true;
        _renderTimer.Stop();
        _busyTimer.Stop();
        _statusTimer.Stop();
        _elapsedTimer.Stop();
        HeaderStatusBadge.Visibility = Visibility.Collapsed;
        if (_session is null) return;
        DetachSession(_session);
        await DisposeSessionSafelyAsync(_session);
        _session = null;
    }

    private static async Task DisposeSessionSafelyAsync(ChatSession session)
    {
        try { await session.DisposeAsync(); }
        catch (Exception) { AppLog.Write("Popup session disposal failed"); }
    }

    private static class NativePoint
    {
        public static PixelPoint Cursor { get { GetCursorPos(out var point); return point; } }
    }
    [StructLayout(LayoutKind.Sequential)] private struct PixelPoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)] private struct MonitorInfo { public int Size; public Rect Monitor; public Rect Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PixelPoint point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(PixelPoint point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("Shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
