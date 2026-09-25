using System.Windows.Threading;
using System.IO;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using flowboost.Models;

namespace flowboost.Services;

[System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Evaluation", "SDK0040", Justification = "Permission callback is required to reject every tool request.")]
public sealed class CopilotService : IAsyncDisposable
{
    private const string AuthenticationRejectedMessage = "Not authenticated. Please authenticate first.";
    private const string SavedSignInRejectedMessage = "Copilot rejected the saved sign-in. Sign in again from Settings.";
    private const string ConnectionRecoveryFailedMessage = "Copilot connection was lost. Check your connection and try again.";

    private readonly AuthService _auth;
    private readonly SettingsService _settings;
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly HashSet<ChatSession> _sessions = [];
    private readonly object _sessionsLock = new();
    private CopilotClient? _client;
    private string? _clientToken;
    private bool _stopped;
    public bool IsAuthenticated => _auth.IsSignedIn;

    public CopilotService(AuthService auth, SettingsService settings, Dispatcher dispatcher)
    {
        _auth = auth; _settings = settings; _dispatcher = dispatcher;
    }

    public Task<bool> CheckAuthenticationAsync() => Task.FromResult(!string.IsNullOrWhiteSpace(_auth.AccessToken));

    public async Task RefreshAuthenticationAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stopped)
            {
                await DisposeSessionsAsync().ConfigureAwait(false);
                return;
            }
            await DisposeSessionsAsync().ConfigureAwait(false);
            var old = _client; _client = null;
            _clientToken = null;
            if (old is not null) await old.DisposeAsync().ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task<ChatSession> CreateSessionAsync(Preset p)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stopped) throw new ObjectDisposedException(nameof(CopilotService));
            if (string.IsNullOrWhiteSpace(_auth.AccessToken)) throw new NotAuthenticatedException();
            var client = await GetClientAsync().ConfigureAwait(false);
            var config = CreateSessionConfig(p);
            GitHub.Copilot.CopilotSession sdkSession;
            try { sdkSession = await client.CreateSessionAsync(config).ConfigureAwait(false); }
            catch (Exception ex) when (IsAuthenticationRejection(ex))
            {
                throw new InvalidOperationException(SavedSignInRejectedMessage);
            }
            var result = new ChatSession(p.Name, _settings.Current.Model, sdkSession, _dispatcher, session => DeleteSessionAsync(client, session));
            lock (_sessionsLock) _sessions.Add(result);
            return result;
        }
        finally { _lifecycle.Release(); }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stopped) throw new ObjectDisposedException(nameof(CopilotService));
            if (string.IsNullOrWhiteSpace(_auth.AccessToken)) throw new NotAuthenticatedException();
            var client = await GetClientAsync().ConfigureAwait(false);
            IList<ModelInfo> result;
            try
            {
                result = await client.ListModelsAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (IsAuthenticationRejection(ex))
            {
                result = await RecreateAndRetryModelListAsync(connectionWasLost: false).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsConnectionLost(ex))
            {
                result = await RecreateAndRetryModelListAsync(connectionWasLost: true).ConfigureAwait(false);
            }
            return result.Select(m => m.Id).ToArray();
        }
        finally { _lifecycle.Release(); }
    }

    private async Task<CopilotClient> GetClientAsync()
    {
        var token = _auth.AccessToken;
        if (string.IsNullOrWhiteSpace(token)) throw new NotAuthenticatedException();
        if (_client is not null && string.Equals(_clientToken, token, StringComparison.Ordinal)) return _client;
        if (_client is not null)
        {
            await DropClientAsync().ConfigureAwait(false);
        }
        var candidate = new CopilotClient(new CopilotClientOptions
        {
            GitHubToken = token,
            BaseDirectory = AppDataDirectory.Copilot,
            UseLoggedInUser = false
        });
        try
        {
            Directory.CreateDirectory(AppDataDirectory.Copilot);
            await candidate.StartAsync().ConfigureAwait(false);
        }
        catch
        {
            await candidate.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        var old = _client;
        _client = candidate;
        _clientToken = token;
        if (old is not null)
        {
            await DisposeSessionsAsync().ConfigureAwait(false);
            await old.DisposeAsync().ConfigureAwait(false);
        }
        return candidate;
    }

    private async Task<IList<ModelInfo>> RecreateAndRetryModelListAsync(bool connectionWasLost)
    {
        await DropClientAsync().ConfigureAwait(false);
        try
        {
            var replacement = await GetClientAsync().ConfigureAwait(false);
            return await replacement.ListModelsAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsAuthenticationRejection(ex))
        {
            throw new InvalidOperationException(SavedSignInRejectedMessage);
        }
        catch (Exception ex) when (IsConnectionLost(ex))
        {
            LogConnectionRecoveryFailure();
            throw new InvalidOperationException(ConnectionRecoveryFailedMessage);
        }
        catch (Exception) when (connectionWasLost)
        {
            LogConnectionRecoveryFailure();
            throw new InvalidOperationException(ConnectionRecoveryFailedMessage);
        }
    }

    private async Task DropClientAsync()
    {
        await DisposeSessionsAsync().ConfigureAwait(false);
        var old = _client;
        _client = null;
        _clientToken = null;
        if (old is not null) await old.DisposeAsync().ConfigureAwait(false);
    }

    private static bool IsAuthenticationRejection(Exception exception) =>
        exception.GetType().Name == "RemoteRpcException" &&
        exception.Message.Contains(AuthenticationRejectedMessage, StringComparison.OrdinalIgnoreCase);

    private static bool IsConnectionLost(Exception exception) => exception.GetType().Name == "ConnectionLostException";

    private static void LogConnectionRecoveryFailure() =>
        AppLog.Write("Copilot models.list connection recovery failed");

    private static SessionConfig CreateSessionConfig(Preset p) => new()
    {
        Model = App.Current.Settings.Current.Model,
        Streaming = true,
        SystemMessage = new SystemMessageConfig { Mode = SystemMessageMode.Replace, Content = $"You are a concise assistant. Respond in {p.ResponseLanguage}. Format with Markdown." },
        InfiniteSessions = new() { Enabled = false },
        AvailableTools = [],
#pragma warning disable SDK0040
        OnPermissionRequest = (_, _) => Task.FromResult(PermissionDecision.UserNotAvailable())
#pragma warning restore SDK0040
    };

    private async Task DeleteSessionAsync(CopilotClient owner, ChatSession session)
    {
        try
        {
            lock (_sessionsLock) _sessions.Remove(session);
            await owner.DeleteSessionAsync(session.SessionId).ConfigureAwait(false);
        }
        catch (Exception ex) { AppLog.Write("Session deletion failed", ex); }
    }

    private async Task DisposeSessionsAsync()
    {
        ChatSession[] sessions;
        lock (_sessionsLock) sessions = _sessions.ToArray();
        foreach (var session in sessions) await session.DisposeAsync().ConfigureAwait(false);
        lock (_sessionsLock) _sessions.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stopped) return;
            _stopped = true;
            var client = _client; _client = null;
            _clientToken = null;
            ChatSession[] sessions;
            lock (_sessionsLock) sessions = _sessions.ToArray();
            foreach (var session in sessions) await session.DisposeAsync().ConfigureAwait(false);
            lock (_sessionsLock) _sessions.Clear();
            if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); _lifecycle.Dispose(); }
    }
}
