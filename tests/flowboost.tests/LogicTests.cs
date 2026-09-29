using System.IO;
using System.Reflection;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Input;
using flowboost.Models;
using flowboost.Services;
using Xunit;

namespace flowboost.Tests;

public sealed class LogicTests
{
    [Fact]
    public void AssemblyPublishesVersionAndEmbeddedChangelog()
    {
        var appAssembly = typeof(SettingsService).Assembly;
        var informationalVersion = appAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Assert.Equal("0.1.2", informationalVersion);

        using var changelogStream = appAssembly.GetManifestResourceStream("flowboost.CHANGELOG.md");
        Assert.NotNull(changelogStream);
        using var reader = new StreamReader(changelogStream);
        Assert.Contains("[0.1.2]", reader.ReadToEnd());
    }

    [Fact]
    public void InputHasExpectedX64Size() => Assert.Equal(40, SelectionService.InputSize);

    [Fact]
    public void NormalizeRepairsNullsAndInvalidEnums()
    {
        var settings = new AppSettings { Model = " ", Theme = (AppTheme)999, Presets = [new Preset { Hotkey = null!, ResponseLanguage = null! }] };
        var normalized = SettingsService.Normalize(settings);
        Assert.Equal("gpt-5", normalized.Model);
        Assert.Equal(AppTheme.System, normalized.Theme);
        Assert.NotNull(normalized.Presets[0].Hotkey);
        Assert.Equal("English", normalized.Presets[0].ResponseLanguage);
    }

    [Fact]
    public void ValidateRejectsDuplicateEnabledHotkeys()
    {
        var settings = new AppSettings { Presets = [
            new Preset { Enabled = true, Hotkey = new HotkeyBinding { Modifiers = ModifierKeys.Control, Key = Key.T } },
            new Preset { Enabled = true, Hotkey = new HotkeyBinding { Modifiers = ModifierKeys.Control, Key = Key.T } }
        ] };
        Assert.Throws<InvalidOperationException>(() => SettingsService.ValidateForSave(settings));
    }

    [Fact]
    public void HotkeyBindingFormatsAndParses()
    {
        var original = new HotkeyBinding { Modifiers = ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, Key = Key.T };
        Assert.Equal("Ctrl+Alt+Shift+T", original.ToString());
        Assert.True(HotkeyBinding.TryParse(original.ToString(), out var parsed));
        Assert.Equal(original.ToString(), parsed.ToString());
        Assert.True(HotkeyBinding.TryParse(null, out var empty));
        Assert.Equal(Key.None, empty.Key);
    }

    [Fact]
    public void LegacyOAuthClientIdIsIgnoredWhenSettingsAreDeserialized()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"OAuthClientId\":\"legacy-value\"}");

        Assert.NotNull(settings);
        Assert.DoesNotContain("OAuthClientId", System.Text.Json.JsonSerializer.Serialize(settings));
    }

    [Fact]
    public void AuthStartsSignedOutWithoutReadingOrWritingUserCredentials()
    {
        var tokenPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "token.bin");
        using var httpClient = new HttpClient(new ThrowIfUsedHandler());
        var auth = new AuthService(httpClient, tokenPath);

        Assert.False(auth.IsSignedIn);
        Assert.Null(auth.AccessToken);
        Assert.Equal("Not signed in to GitHub", auth.StatusText);
        Assert.False(File.Exists(tokenPath));
    }

    [Fact]
    public async Task SavedTokenValidation401ExpiresSignInButPreservesTokenFile()
    {
        using var store = new TemporaryTokenStore();
        CreateSavedToken(store.TokenPath, "saved-test-token");
        var originalTokenFile = File.ReadAllBytes(store.TokenPath);
        using var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("private response body")
        });
        using var client = new HttpClient(handler);
        var auth = new AuthService(client, store.TokenPath);
        var stateChanges = 0;
        auth.AuthStateChanged += () => stateChanges++;

        await auth.ValidateSavedTokenAsync(CancellationToken.None);

        Assert.False(auth.IsSignedIn);
        Assert.Null(auth.AccessToken);
        Assert.Equal("Your GitHub sign-in has expired. Sign in again.", auth.StatusText);
        Assert.Equal(1, stateChanges);
        Assert.True(File.Exists(store.TokenPath));
        Assert.Equal(originalTokenFile, File.ReadAllBytes(store.TokenPath));
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.github.com/user", request.Uri);
        Assert.Equal("application/vnd.github+json", request.Accept);
        Assert.Equal("Bearer saved-test-token", request.Authorization);
        Assert.Equal("flowboost/0.1.2", request.UserAgent);
    }

    [Fact]
    public async Task SavedTokenValidation200LeavesSignInUnchanged()
    {
        using var store = new TemporaryTokenStore();
        CreateSavedToken(store.TokenPath, "saved-test-token");
        using var handler = new ScriptedHandler(_ => JsonResponse("{}"));
        using var client = new HttpClient(handler);
        var auth = new AuthService(client, store.TokenPath);
        var stateChanges = 0;
        auth.AuthStateChanged += () => stateChanges++;

        await auth.ValidateSavedTokenAsync(CancellationToken.None);

        Assert.True(auth.IsSignedIn);
        Assert.Equal("saved-test-token", auth.AccessToken);
        Assert.Equal("Signed in to GitHub", auth.StatusText);
        Assert.Equal(0, stateChanges);
        Assert.True(File.Exists(store.TokenPath));
    }

    [Fact]
    public async Task SavedTokenValidationNetworkFailureLeavesSignInUnchanged()
    {
        using var store = new TemporaryTokenStore();
        CreateSavedToken(store.TokenPath, "saved-test-token");
        using var client = new HttpClient(new ThrowHttpHandler());
        var auth = new AuthService(client, store.TokenPath);
        var stateChanges = 0;
        auth.AuthStateChanged += () => stateChanges++;

        await auth.ValidateSavedTokenAsync(CancellationToken.None);

        Assert.True(auth.IsSignedIn);
        Assert.Equal("saved-test-token", auth.AccessToken);
        Assert.Equal("Signed in to GitHub", auth.StatusText);
        Assert.Equal(0, stateChanges);
        Assert.True(File.Exists(store.TokenPath));
    }

    [Fact]
    public void CopilotAuthenticationRejectionRecognizesMissingGitHubOAuthToken()
    {
        var method = typeof(CopilotService).GetMethod("IsAuthenticationRejection", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var rejected = (bool)method.Invoke(null, [new RemoteRpcException("No GitHub OAuth token or Copilot HMAC key provided")])!;

        Assert.True(rejected);
    }

    [Fact]
    public async Task DeviceFlowStartFailureIsObservableWithoutNetworkAccess()
    {
        using var store = new TemporaryTokenStore();
        using var httpClient = new HttpClient(new ThrowIfUsedHandler());
        var auth = new AuthService(httpClient, store.TokenPath);
        string? error = null;
        auth.Error += message => error = message;

        var exception = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => auth.StartDeviceFlowAsync(CancellationToken.None));

        Assert.Contains("Could not start GitHub device authorization", exception.Message);
        Assert.Contains("Could not start GitHub device authorization", error);
        Assert.DoesNotContain("network disabled for test", exception.Message);
        Assert.DoesNotContain("network disabled for test", error);
        Assert.False(auth.IsSignedIn);
        Assert.Null(auth.AccessToken);
    }

    [Fact]
    public async Task StartDeviceFlowSendsPublicClientAndRequiredScope()
    {
        using var store = new TemporaryTokenStore();
        using var handler = new ScriptedHandler(_ => JsonResponse("""{"device_code":"private-device","user_code":"ABCD-EFGH","verification_uri":"https://github.com/login/device","expires_in":60,"interval":1}"""));
        using var client = new HttpClient(handler);
        var auth = new AuthService(client, store.TokenPath);

        var info = await auth.StartDeviceFlowAsync(CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://github.com/login/device/code", request.Uri);
        Assert.Contains("application/json", request.Accept);
        Assert.Contains("client_id=Ov23liqG8kTL46zQIcIk", request.Body);
        Assert.Contains("scope=read%3Auser", request.Body);
        Assert.Equal("ABCD-EFGH", info.UserCode);
        Assert.Equal("private-device", info.DeviceCode);
        Assert.False(File.Exists(store.TokenPath));
    }

    [Fact]
    public async Task PollWaitsThroughAuthorizationPendingThenSavesToken()
    {
        using var store = new TemporaryTokenStore();
        using var handler = new ScriptedHandler(_ => JsonResponse("""{"device_code":"private-device","user_code":"ABCD-EFGH","verification_uri":"https://github.com/login/device","expires_in":30,"interval":1}"""),
            _ => JsonResponse("""{"error":"authorization_pending","error_description":"do not show this"}"""),
            _ => JsonResponse("""{"access_token":"test-token-value","token_type":"bearer","scope":"read:user"}"""));
        using var client = new HttpClient(handler);
        var auth = new AuthService(client, store.TokenPath);
        var stateChanges = 0;
        auth.AuthStateChanged += () => stateChanges++;
        var deviceCode = await auth.StartDeviceFlowAsync(CancellationToken.None);

        Assert.True(await auth.PollForTokenAsync(deviceCode, CancellationToken.None));

        Assert.Equal("test-token-value", auth.AccessToken);
        Assert.True(auth.IsSignedIn);
        Assert.Equal("Signed in to GitHub", auth.StatusText);
        Assert.Equal(1, stateChanges);
        Assert.True(File.Exists(store.TokenPath));
        var reloadedAuth = new AuthService(client, store.TokenPath);
        Assert.Equal(auth.AccessToken, reloadedAuth.AccessToken);
        Assert.True(reloadedAuth.IsSignedIn);
        Assert.Equal(3, handler.Requests.Count);
        var polls = handler.Requests.Skip(1).ToArray();
        Assert.All(polls, request =>
        {
            Assert.Equal("https://github.com/login/oauth/access_token", request.Uri);
            Assert.Contains("client_id=Ov23liqG8kTL46zQIcIk", request.Body);
            Assert.Contains("device_code=private-device", request.Body);
            Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", request.Body);
        });
    }

    [Theory]
    [InlineData("access_denied", "denied")]
    [InlineData("expired_token", "expired")]
    [InlineData("device_flow_disabled", "disabled")]
    [InlineData("incorrect_client_credentials", "client ID")]
    public async Task PollReportsKnownOAuthErrorsWithoutUntrustedDescription(string oauthError, string expected)
    {
        using var store = new TemporaryTokenStore();
        using var handler = new ScriptedHandler(_ => JsonResponse("""{"device_code":"private-device","user_code":"ABCD-EFGH","verification_uri":"https://github.com/login/device","expires_in":30,"interval":1}"""),
            _ => JsonResponse($"{{\"error\":\"{oauthError}\",\"error_description\":\"private untrusted text\"}}"));
        using var client = new HttpClient(handler);
        var auth = new AuthService(client, store.TokenPath);
        string? reportedError = null;
        auth.Error += message => reportedError = message;
        var deviceCode = await auth.StartDeviceFlowAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => auth.PollForTokenAsync(deviceCode, CancellationToken.None));

        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private untrusted text", exception.Message);
        Assert.DoesNotContain("private untrusted text", reportedError);
        Assert.False(auth.IsSignedIn);
        Assert.False(File.Exists(store.TokenPath));
    }

    [Theory]
    [InlineData("device_flow_disabled", "disabled")]
    [InlineData("incorrect_client_credentials", "client ID")]
    public async Task StartDeviceFlowReportsAllowlistedConfigurationErrorsSafely(string oauthError, string expected)
    {
        using var store = new TemporaryTokenStore();
        using var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent($"{{\"error\":\"{oauthError}\",\"error_description\":\"untrusted server detail\"}}", Encoding.UTF8, "application/json")
        });
        using var client = new HttpClient(handler);
        var auth = new AuthService(client, store.TokenPath);
        string? reportedError = null;
        auth.Error += message => reportedError = message;

        var exception = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => auth.StartDeviceFlowAsync(CancellationToken.None));

        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("untrusted server detail", exception.Message);
        Assert.DoesNotContain("untrusted server detail", reportedError);
        Assert.False(auth.IsSignedIn);
        Assert.False(File.Exists(store.TokenPath));
    }

    [Fact]
    public async Task StartDeviceFlowRejectsMissingRequiredFields()
    {
        using var store = new TemporaryTokenStore();
        using var handler = new ScriptedHandler(_ => JsonResponse("""{"user_code":"ABCD-EFGH","verification_uri":"https://github.com/login/device","expires_in":60}"""));
        using var client = new HttpClient(handler);
        var auth = new AuthService(client, store.TokenPath);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => auth.StartDeviceFlowAsync(CancellationToken.None));

        Assert.Contains("device authorization", exception.Message);
        Assert.False(auth.IsSignedIn);
        Assert.False(File.Exists(store.TokenPath));
    }

    [Fact]
    public async Task CancellationInvalidatesFlowAndPreventsTokenPersistence()
    {
        using var store = new TemporaryTokenStore();
        using var handler = new ScriptedHandler(_ => JsonResponse("""{"device_code":"private-device","user_code":"ABCD-EFGH","verification_uri":"https://github.com/login/device","expires_in":30,"interval":1}"""),
            _ => JsonResponse("""{"access_token":"must-not-persist"}"""));
        using var client = new HttpClient(handler);
        var auth = new AuthService(client, store.TokenPath);
        var deviceCode = await auth.StartDeviceFlowAsync(CancellationToken.None);

        auth.CancelDeviceFlow(deviceCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => auth.PollForTokenAsync(deviceCode, CancellationToken.None));

        Assert.False(auth.IsSignedIn);
        Assert.False(File.Exists(store.TokenPath));
        Assert.Single(handler.Requests);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static void CreateSavedToken(string tokenPath, string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(tokenPath)!);
        File.WriteAllBytes(tokenPath, ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));
    }

    private sealed class ThrowIfUsedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new InvalidOperationException("network disabled for test"));
    }

    private sealed class ThrowHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("network failure"));
    }

    private sealed class ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int _nextResponse;
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri!.ToString(), request.Headers.Accept.ToString(),
                request.Headers.Authorization?.ToString() ?? "", string.Join(" ", request.Headers.UserAgent), body));
            var index = Interlocked.Increment(ref _nextResponse) - 1;
            if (index >= responses.Length) throw new InvalidOperationException("Unexpected HTTP request in test.");
            return responses[index](request);
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, string Uri, string Accept, string Authorization, string UserAgent, string Body);

    private sealed class RemoteRpcException(string message) : Exception(message);

    private sealed class TemporaryTokenStore : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "flowboost-auth-tests", Guid.NewGuid().ToString("N"));
        public string TokenPath => Path.Combine(_directory, "token.bin");
        public void Dispose()
        {
            try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
