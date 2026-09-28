using System.Windows.Threading;
using GitHub.Copilot;

namespace flowboost.Services;

public sealed class ChatSession : IAsyncDisposable
{
    private const string SavedSignInRejectedMessage = "Your GitHub sign-in has expired. Sign in again from Settings.";
    private readonly CopilotSession _session;
    private readonly Dispatcher _dispatcher;
    private readonly Func<ChatSession, Task> _deleteFromClient;
    private readonly Action _markSavedSignInRejected;
    private readonly object _stateLock = new();
    private readonly Action<AssistantMessageDeltaEvent> _deltaHandler;
    private readonly Action<AssistantMessageEvent> _completeHandler;
    private readonly Action<SessionErrorEvent> _errorHandler;
    private readonly Action<SessionIdleEvent> _idleHandler;
    private readonly IDisposable _deltaSubscription;
    private readonly IDisposable _completeSubscription;
    private readonly IDisposable _errorSubscription;
    private readonly IDisposable _idleSubscription;
    private Task? _disposeTask;
    private bool _closing;
    private bool _isBusy;
    private string _answer = "";
    public string PresetName { get; }
    public string Model { get; }
    public bool IsBusy { get { lock (_stateLock) return _isBusy; } private set { lock (_stateLock) _isBusy = value; } }
    internal string SessionId => _session.SessionId;
    internal event Action<ChatSession>? Disposed;
    public event Action<string>? Delta;
    public event Action<string>? Completed;
    public event Action<string>? Error;
    public event Action? Idle;
    public event Action? StateChanged;

    internal ChatSession(string presetName, string model, CopilotSession session, Dispatcher dispatcher, Func<ChatSession, Task> deleteFromClient, Action markSavedSignInRejected)
    {
        PresetName = presetName; Model = model; _session = session; _dispatcher = dispatcher; _deleteFromClient = deleteFromClient;
        _markSavedSignInRejected = markSavedSignInRejected;
        _deltaHandler = e => Dispatch(() =>
        {
            if (IsClosing) return;
            var delta = e.Data.DeltaContent; _answer += delta; Delta?.Invoke(delta);
        });
        _completeHandler = e => Dispatch(() =>
        {
            if (IsClosing) return;
            _answer = e.Data.Content; Completed?.Invoke(_answer);
        });
        _errorHandler = e => Dispatch(() =>
        {
            if (IsClosing) return;
            AppLog.WriteSessionErrorEvent();
            var message = e.Data.Message;
            if (message.Contains("No GitHub OAuth token", StringComparison.OrdinalIgnoreCase))
            {
                _markSavedSignInRejected();
                message = SavedSignInRejectedMessage;
            }
            SetBusy(false); Error?.Invoke(message);
        });
        _idleHandler = _ => Dispatch(() =>
        {
            if (IsClosing) return;
            SetBusy(false); Idle?.Invoke();
        });
        _deltaSubscription = _session.On(_deltaHandler); _completeSubscription = _session.On(_completeHandler);
        _errorSubscription = _session.On(_errorHandler); _idleSubscription = _session.On(_idleHandler);
    }

    public async Task SendAsync(string text)
    {
        lock (_stateLock)
        {
            if (_closing) throw new ObjectDisposedException(nameof(ChatSession));
            if (_isBusy) throw new InvalidOperationException("A response is already in progress.");
            _isBusy = true;
            _answer = "";
        }
        await RaiseStateChangedAsync().ConfigureAwait(false);
        try { await _session.SendAsync(new MessageOptions { Prompt = text }).ConfigureAwait(false); }
        catch (Exception ex)
        {
            AppLog.Write("Session send failed", ex);
            SetBusy(false);
            throw;
        }
    }

    public async Task AbortAsync()
    {
        if (!IsClosing && IsBusy) await _session.AbortAsync().ConfigureAwait(false);
    }

    private bool IsClosing { get { lock (_stateLock) return _closing; } }
    private void SetBusy(bool busy)
    {
        var changed = false;
        lock (_stateLock)
        {
            if (_closing || _isBusy == busy) return;
            _isBusy = busy; changed = true;
        }
        if (changed) Dispatch(() => { if (!IsClosing) StateChanged?.Invoke(); });
    }
    private async Task RaiseStateChangedAsync() => await _dispatcher.InvokeAsync(() => StateChanged?.Invoke());
    private void Dispatch(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _ = _dispatcher.InvokeAsync(action);
    }

    public ValueTask DisposeAsync()
    {
        lock (_stateLock)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            _closing = true;
            _disposeTask = DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try { if (IsBusy) await _session.AbortAsync().ConfigureAwait(false); }
        catch (Exception ex) { AppLog.Write("Session abort failed", ex); }
        _deltaSubscription.Dispose(); _completeSubscription.Dispose(); _errorSubscription.Dispose(); _idleSubscription.Dispose();
        try { await _session.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { AppLog.Write("Session dispose failed", ex); }
        try { await _deleteFromClient(this).ConfigureAwait(false); }
        catch (Exception ex) { AppLog.Write("Session deletion failed", ex); }
        Disposed?.Invoke(this);
    }
}
