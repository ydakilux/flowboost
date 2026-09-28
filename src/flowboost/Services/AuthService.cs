using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Net.Http.Headers;

namespace flowboost.Services;

public sealed record DeviceCodeInfo(string UserCode, string VerificationUri, int ExpiresIn, string DeviceCode, int Interval);

public sealed class AuthService
{
    private const string ExpiredSignInStatus = "Your GitHub sign-in has expired. Sign in again.";
    private const string ClientId = "Ov23liqG8kTL46zQIcIk";
    private const string DeviceCodeEndpoint = "https://github.com/login/device/code";
    private const string AccessTokenEndpoint = "https://github.com/login/oauth/access_token";
    private static readonly HttpClient DefaultHttpClient = new();
    private readonly HttpClient _httpClient;
    private readonly string _tokenPath;
    private readonly object _sync = new();
    private bool _isSignedIn;
    private string _statusText = "Not signed in to GitHub";
    private string? _accessToken;
    private string? _activeDeviceCode;
    private long _flowGeneration;

    public bool IsSignedIn { get { lock (_sync) return _isSignedIn; } }
    public string StatusText { get { lock (_sync) return _statusText; } }
    public string? AccessToken { get { lock (_sync) return _accessToken; } }
    public event Action? AuthStateChanged;
    public event Action<string>? Error;

    public AuthService() : this(DefaultHttpClient, Path.Combine(AppDataDirectory.Root, "token.bin")) { }

    // Injectable seam permits deterministic HTTP tests with an isolated token path.
    public AuthService(HttpClient httpClient, string tokenPath)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _tokenPath = tokenPath ?? throw new ArgumentNullException(nameof(tokenPath));
        LoadToken();
    }

    public async Task<DeviceCodeInfo> StartDeviceFlowAsync(CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref _flowGeneration);
        lock (_sync)
        {
            _activeDeviceCode = null;
            _statusText = _isSignedIn ? "Signed in to GitHub" : "Starting GitHub device authorization…";
        }
        using var cancellationRegistration = cancellationToken.Register(() => CancelFlow(generation, null));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, DeviceCodeEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = ClientId,
                    ["scope"] = "read:user"
                })
            };
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (TryGetOAuthError(payload, out var oauthError))
                throw new AuthFlowException(GetOAuthErrorMessage(oauthError));
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var code = RequiredString(root, "device_code");
            var userCode = RequiredString(root, "user_code");
            var verificationUri = RequiredString(root, "verification_uri");
            var expiresIn = RequiredPositiveInt(root, "expires_in");
            var interval = OptionalPositiveInt(root, "interval", 5);
            lock (_sync)
            {
                if (generation != _flowGeneration || cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);
                _activeDeviceCode = code;
                _statusText = "Complete GitHub device authorization in your browser.";
            }
            return new DeviceCodeInfo(userCode, verificationUri, expiresIn, code, interval);
        }
        catch (OperationCanceledException) { throw; }
        catch (AuthFlowException ex)
        {
            ReportFailure(ex.Message);
            throw;
        }
        catch (Exception)
        {
            ReportFailure("Could not start GitHub device authorization.");
            throw new InvalidOperationException("Could not start GitHub device authorization.");
        }
    }

    public async Task<bool> PollForTokenAsync(DeviceCodeInfo deviceCode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deviceCode);
        if (string.IsNullOrWhiteSpace(deviceCode.DeviceCode) || deviceCode.ExpiresIn <= 0 || deviceCode.Interval <= 0)
            throw new ArgumentException("Device authorization information is invalid.", nameof(deviceCode));
        var generation = Volatile.Read(ref _flowGeneration);
        lock (_sync)
        {
            if (generation != _flowGeneration || !string.Equals(_activeDeviceCode, deviceCode.DeviceCode, StringComparison.Ordinal))
                throw new InvalidOperationException("This GitHub device authorization flow is no longer active.");
        }
        using var cancellationRegistration = cancellationToken.Register(() => CancelFlow(generation, deviceCode.DeviceCode));
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(deviceCode.ExpiresIn);
        var interval = TimeSpan.FromSeconds(deviceCode.Interval);

        try
        {
            while (DateTimeOffset.UtcNow < expiresAt)
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                if (generation != Volatile.Read(ref _flowGeneration)) throw new OperationCanceledException(cancellationToken);
                if (DateTimeOffset.UtcNow >= expiresAt)
                    throw new AuthFlowException("GitHub device authorization expired. Start sign-in again.", true);

                using var request = new HttpRequestMessage(HttpMethod.Post, AccessTokenEndpoint)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["client_id"] = ClientId,
                        ["device_code"] = deviceCode.DeviceCode,
                        ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
                    })
                };
                request.Headers.Accept.ParseAdd("application/json");
                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (TryGetOAuthError(payload, out var oauthError) && oauthError is not ("authorization_pending" or "slow_down"))
                    throw new AuthFlowException(GetOAuthErrorMessage(oauthError), true);
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                if (root.TryGetProperty("access_token", out var tokenValue) && tokenValue.ValueKind == JsonValueKind.String)
                {
                    var token = tokenValue.GetString();
                    if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("GitHub returned an empty access token.");
                    SaveToken(token, generation, deviceCode.DeviceCode, cancellationToken);
                    AuthStateChanged?.Invoke();
                    return true;
                }

                var error = root.TryGetProperty("error", out var errorValue) ? errorValue.GetString() : null;
                switch (error)
                {
                    case "authorization_pending":
                        continue;
                    case "slow_down":
                        interval += TimeSpan.FromSeconds(5);
                        continue;
                    case "expired_token":
                    case "access_denied":
                    case "device_flow_disabled":
                    case "incorrect_client_credentials":
                        throw new AuthFlowException(GetOAuthErrorMessage(error), true);
                    case null:
                    case "":
                        throw new AuthFlowException("GitHub returned an unexpected device authorization response.", true);
                    default:
                        throw new AuthFlowException(GetOAuthErrorMessage(error), true);
                }
            }

            throw new AuthFlowException("GitHub device authorization expired. Start sign-in again.", true);
        }
        catch (OperationCanceledException) { throw; }
        catch (AuthFlowException ex)
        {
            ReportFailure(ex.Message);
            if (ex.EndFlow) CancelDeviceFlow(deviceCode);
            throw;
        }
        catch (Exception)
        {
            ReportFailure("GitHub device authorization failed.");
            CancelDeviceFlow(deviceCode);
            throw new InvalidOperationException("GitHub device authorization failed.");
        }
    }

    /// <summary>Invalidates the active device authorization flow. Pass its info to avoid cancelling a newer flow.</summary>
    public void CancelDeviceFlow(DeviceCodeInfo? deviceCode = null)
    {
        lock (_sync)
        {
            if (deviceCode is not null && !string.Equals(_activeDeviceCode, deviceCode.DeviceCode, StringComparison.Ordinal)) return;
            Interlocked.Increment(ref _flowGeneration);
            _activeDeviceCode = null;
            _statusText = _isSignedIn ? "Signed in to GitHub" : "GitHub sign-in cancelled.";
        }
    }

    public void SignOut()
    {
        Exception? deletionFailure = null;
        lock (_sync)
        {
            try
            {
                if (File.Exists(_tokenPath)) File.Delete(_tokenPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                deletionFailure = ex;
            }
            if (deletionFailure is null)
            {
                Interlocked.Increment(ref _flowGeneration);
                _accessToken = null;
                _isSignedIn = false;
                _statusText = "Not signed in to GitHub";
                _activeDeviceCode = null;
            }
        }
        if (deletionFailure is not null)
        {
            ReportFailure("Could not remove the saved GitHub token.");
            throw new IOException("Could not remove the saved GitHub token.", deletionFailure);
        }
        AuthStateChanged?.Invoke();
    }

    public async Task ValidateSavedTokenAsync(CancellationToken cancellationToken)
    {
        string? token;
        lock (_sync) token = _isSignedIn ? _accessToken : null;
        if (string.IsNullOrWhiteSpace(token)) return;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.UserAgent.ParseAdd("flowboost/0.1.0");
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                MarkSavedSignInRejected();
        }
        catch (HttpRequestException) { }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
    }

    public void MarkSavedSignInRejected()
    {
        var changed = false;
        lock (_sync)
        {
            if (_isSignedIn || !string.Equals(_statusText, ExpiredSignInStatus, StringComparison.Ordinal))
            {
                _isSignedIn = false;
                _accessToken = null;
                _statusText = ExpiredSignInStatus;
                changed = true;
            }
        }
        if (changed) AuthStateChanged?.Invoke();
    }

    private void LoadToken()
    {
        try
        {
            if (!File.Exists(_tokenPath)) return;
            var encrypted = File.ReadAllBytes(_tokenPath);
            var token = System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser));
            if (string.IsNullOrWhiteSpace(token)) throw new CryptographicException("Saved GitHub token was empty.");
            _accessToken = token;
            _isSignedIn = true;
            _statusText = "Signed in to GitHub";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            AppLog.Write("Could not load saved GitHub token");
            _statusText = "Could not load the saved GitHub token. Sign in again.";
            Error?.Invoke(_statusText);
        }
    }

    private void SaveToken(string token, long generation, string deviceCode, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_tokenPath) ?? throw new InvalidOperationException("Token path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = _tokenPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var encrypted = ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(temporaryPath, encrypted);
            lock (_sync)
            {
                if (generation != _flowGeneration || cancellationToken.IsCancellationRequested ||
                    !string.Equals(_activeDeviceCode, deviceCode, StringComparison.Ordinal))
                    throw new OperationCanceledException(cancellationToken);
                File.Move(temporaryPath, _tokenPath, true);
                _accessToken = token;
                _isSignedIn = true;
                _statusText = "Signed in to GitHub";
                _activeDeviceCode = null;
            }
        }
        catch
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            throw;
        }
    }

    private void ReportFailure(string message)
    {
        AppLog.Write(message);
        lock (_sync) _statusText = message;
        Error?.Invoke(message);
    }

    private void CancelFlow(long generation, string? deviceCode)
    {
        lock (_sync)
        {
            if (generation != _flowGeneration) return;
            if (deviceCode is not null && !string.Equals(_activeDeviceCode, deviceCode, StringComparison.Ordinal)) return;
            Interlocked.Increment(ref _flowGeneration);
            _activeDeviceCode = null;
            _statusText = _isSignedIn ? "Signed in to GitHub" : "GitHub sign-in cancelled.";
        }
    }

    private static string RequiredString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidOperationException($"GitHub response did not include {name}.");

    private static int RequiredPositiveInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) && result > 0
            ? result
            : throw new InvalidOperationException($"GitHub response did not include a valid {name}.");

    private static int OptionalPositiveInt(JsonElement root, string name, int fallback) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) && result > 0 ? result : fallback;

    private static bool TryGetOAuthError(string payload, out string error)
    {
        error = "";
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("error", out var value) || value.ValueKind != JsonValueKind.String)
                return false;
            error = value.GetString() ?? "";
            return error.Length > 0;
        }
        catch (JsonException) { return false; }
    }

    private static string GetOAuthErrorMessage(string error) => error switch
    {
        "authorization_pending" => "GitHub authorization is still pending.",
        "slow_down" => "GitHub asked the app to slow down authorization polling.",
        "expired_token" => "GitHub device authorization expired. Start sign-in again.",
        "access_denied" => "GitHub device authorization was denied.",
        "device_flow_disabled" => "GitHub device authorization is disabled for this OAuth App. Enable the device flow in the app settings.",
        "incorrect_client_credentials" => "GitHub rejected the OAuth App client ID. Verify the app configuration.",
        "unsupported_grant_type" => "GitHub rejected the device authorization grant. Verify the OAuth App device-flow configuration.",
        "incorrect_client_id" => "GitHub rejected the OAuth App client ID. Verify the app configuration.",
        _ => "GitHub device authorization failed. Check the OAuth App configuration and try again."
    };

    private sealed class AuthFlowException(string message, bool endFlow = false) : InvalidOperationException(message)
    {
        public bool EndFlow { get; } = endFlow;
    }
}
