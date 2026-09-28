using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace flowboost.Services;

public enum UpdateDecision { Later, Skip, OpenReleasePage }

public sealed record UpdateInfo(Version CurrentVersion, Version LatestVersion, string ReleaseUrl, string? ReleaseName);

public enum UpdateCheckStatus { UpdateAvailable, UpToDate, Failed }

public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Info);

public sealed class UpdateService
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/ydakilux/flowboost/releases/latest";
    private const string ReleasesPageUrl = "https://github.com/ydakilux/flowboost/releases";
    private static readonly HttpClient SharedHttpClient = new();
    private readonly HttpClient _httpClient;
    private readonly Version _currentVersion;

    public UpdateService() : this(SharedHttpClient, GetAssemblyVersion()) { }

    public UpdateService(HttpClient httpClient, Version currentVersion)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _currentVersion = NormalizeVersion(currentVersion ?? throw new ArgumentNullException(nameof(currentVersion)));
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.ParseAdd($"flowboost/{_currentVersion}");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, null);
            if (!response.IsSuccessStatusCode)
                return Failed(new HttpRequestException($"Update endpoint returned {(int)response.StatusCode}.", null, response.StatusCode));

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("tag_name", out var tagElement) ||
                tagElement.ValueKind != JsonValueKind.String)
                return Failed(new JsonException("Release tag was missing."));

            var tag = tagElement.GetString();
            if (string.IsNullOrWhiteSpace(tag)) return Failed(new JsonException("Release tag was empty."));
            if (tag[0] is 'v' or 'V') tag = tag[1..];
            if (tag.Contains('-') || tag.Contains('+'))
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, null);

            if (!Version.TryParse(tag, out var parsedVersion))
                return Failed(new JsonException("Release tag was not a valid version."));

            var latestVersion = NormalizeVersion(parsedVersion);
            var releaseName = GetOptionalString(document.RootElement, "name");
            var releaseUrl = GetReleaseUrl(document.RootElement);
            if (latestVersion <= _currentVersion)
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, null);

            return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable,
                new UpdateInfo(_currentVersion, latestVersion, releaseUrl, releaseName));
        }
        catch (Exception ex)
        {
            return Failed(ex);
        }
    }

    private static UpdateCheckResult Failed(Exception exception)
    {
        AppLog.Write("Update check failed", exception);
        return new UpdateCheckResult(UpdateCheckStatus.Failed, null);
    }

    private static string? GetOptionalString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static string GetReleaseUrl(JsonElement root)
    {
        var value = GetOptionalString(root, "html_url");
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps &&
            string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.StartsWith("/ydakilux/flowboost/", StringComparison.OrdinalIgnoreCase))
            return uri.AbsoluteUri;
        return ReleasesPageUrl;
    }

    private static Version NormalizeVersion(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build));

    private static Version GetAssemblyVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var versionText = informationalVersion.Split('+', 2)[0];
            if (Version.TryParse(versionText, out var parsed)) return NormalizeVersion(parsed);
        }

        return NormalizeVersion(assembly.GetName().Version ?? new Version(0, 0, 0));
    }
}
