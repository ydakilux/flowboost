using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using flowboost.Services;
using Xunit;

namespace flowboost.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public async Task NewerReleaseIsReportedWithParsedVersionAndUrl()
    {
        var result = await CheckAsync(HttpStatusCode.OK,
            "{\"tag_name\":\"v0.2.0\",\"name\":\"Release 0.2.0\",\"html_url\":\"https://github.com/ydakilux/flowboost/releases/tag/v0.2.0\"}");

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.NotNull(result.Info);
        Assert.Equal(new Version(0, 1, 0), result.Info.CurrentVersion);
        Assert.Equal(new Version(0, 2, 0), result.Info.LatestVersion);
        Assert.Equal("https://github.com/ydakilux/flowboost/releases/tag/v0.2.0", result.Info.ReleaseUrl);
        Assert.Equal("Release 0.2.0", result.Info.ReleaseName);
    }

    [Fact]
    public async Task ParsesTrustedReleaseAssetsAndSize()
    {
        var result = await CheckAsync(HttpStatusCode.OK, """{"tag_name":"v0.2.0","assets":[{"name":"flowboost.exe","browser_download_url":"https://github.com/ydakilux/flowboost/releases/download/v0.2.0/flowboost.exe","size":12345},{"name":"flowboost.exe.sha256","browser_download_url":"https://github.com/ydakilux/flowboost/releases/download/v0.2.0/flowboost.exe.sha256"}]}""");

        Assert.Equal("https://github.com/ydakilux/flowboost/releases/download/v0.2.0/flowboost.exe", result.Info!.ExeUrl);
        Assert.Equal("https://github.com/ydakilux/flowboost/releases/download/v0.2.0/flowboost.exe.sha256", result.Info.Sha256Url);
        Assert.Equal(12345, result.Info.ExeSize);
    }

    [Theory]
    [InlineData("https://evil.example/ydakilux/flowboost/releases/download/v0.2.0/flowboost.exe")]
    [InlineData("http://github.com/ydakilux/flowboost/releases/download/v0.2.0/flowboost.exe")]
    public async Task RejectsUntrustedAssetUrls(string exeUrl)
    {
        var result = await CheckAsync(HttpStatusCode.OK, $$"""{"tag_name":"v0.2.0","assets":[{"name":"flowboost.exe","browser_download_url":"{{exeUrl}}","size":123},{"name":"flowboost.exe.sha256","browser_download_url":"https://github.com/ydakilux/flowboost/releases/download/v0.2.0/flowboost.exe.sha256"}]}""");
        Assert.Null(result.Info!.ExeUrl);
        Assert.NotNull(result.Info.Sha256Url);
    }

    [Fact]
    public async Task MissingOrUnrecognizedAssetsAreNull()
    {
        var missing = await CheckAsync(HttpStatusCode.OK, "{\"tag_name\":\"v0.2.0\"}");
        var unrecognized = await CheckAsync(HttpStatusCode.OK, """{"tag_name":"v0.2.0","assets":[{"name":"other.exe","browser_download_url":"https://github.com/ydakilux/flowboost/releases/download/v0.2.0/other.exe"}]}""");
        Assert.Null(missing.Info!.ExeUrl);
        Assert.Null(missing.Info.Sha256Url);
        Assert.Null(unrecognized.Info!.ExeUrl);
        Assert.Null(unrecognized.Info.Sha256Url);
    }

    [Theory]
    [InlineData("v0.1.0")]
    [InlineData("v0.0.9")]
    public async Task SameOrOlderReleaseIsUpToDate(string tag)
    {
        var result = await CheckAsync(HttpStatusCode.OK, $"{{\"tag_name\":\"{tag}\"}}");
        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task MissingReleaseIsUpToDate()
    {
        var result = await CheckAsync(HttpStatusCode.NotFound, "");
        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task PrereleaseTagIsNotOffered()
    {
        var result = await CheckAsync(HttpStatusCode.OK, "{\"tag_name\":\"v0.2.0-beta.1\"}");
        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "not json")]
    [InlineData(HttpStatusCode.InternalServerError, "error")]
    public async Task MalformedJsonAndServerErrorsFail(HttpStatusCode status, string body)
    {
        var result = await CheckAsync(status, body);
        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
    }

    [Fact]
    public async Task HttpRequestExceptionFails()
    {
        using var client = new HttpClient(new FakeHandler((_, _) => throw new HttpRequestException("offline")));
        var result = await new UpdateService(client, new Version(0, 1, 0)).CheckAsync(CancellationToken.None);
        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
    }

    [Fact]
    public async Task UrlOnAnotherHostFallsBackToReleasesPage()
    {
        var result = await CheckAsync(HttpStatusCode.OK,
            "{\"tag_name\":\"v0.2.0\",\"html_url\":\"https://evil.example/ydakilux/flowboost/releases/tag/v0.2.0\"}");
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("https://github.com/ydakilux/flowboost/releases", result.Info!.ReleaseUrl);
    }

    [Fact]
    public async Task GitHubRequestHeadersAreSet()
    {
        HttpRequestMessage? observedRequest = null;
        using var client = new HttpClient(new FakeHandler((request, _) =>
        {
            observedRequest = request;
            return Task.FromResult(JsonResponse(HttpStatusCode.NotFound, ""));
        }));

        await new UpdateService(client, new Version(0, 1, 0)).CheckAsync(CancellationToken.None);

        Assert.NotNull(observedRequest);
        Assert.Contains(observedRequest.Headers.Accept, header => header.MediaType == "application/vnd.github+json");
        Assert.Contains(observedRequest.Headers.UserAgent, agent => agent.Product?.Name == "flowboost" && agent.Product.Version == "0.1.0");
        Assert.Equal("2022-11-28", observedRequest.Headers.GetValues("X-GitHub-Api-Version").Single());
    }

    private static async Task<UpdateCheckResult> CheckAsync(HttpStatusCode status, string body)
    {
        using var client = new HttpClient(new FakeHandler((_, _) => Task.FromResult(JsonResponse(status, body))));
        return await new UpdateService(client, new Version(0, 1, 0)).CheckAsync(CancellationToken.None);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
