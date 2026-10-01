using System.Net;
using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using flowboost.Services;
using Xunit;

namespace flowboost.Tests;

public sealed class UpdateInstallerTests
{
    private const string ExeUrl = "https://github.com/ydakilux/flowboost/releases/download/v0.2.0/flowboost.exe";
    private const string HashUrl = "https://github.com/ydakilux/flowboost/releases/download/v0.2.0/flowboost.exe.sha256";

    [Fact]
    public async Task DownloadWritesVerifiedExeAndReportsProgress()
    {
        using var directory = new TempDirectory();
        var exePath = Path.Combine(directory.Path, "flowboost.exe");
        var bytes = ValidExe();
        using var client = new HttpClient(new FakeHandler((request, _) => Task.FromResult(Response(request.RequestUri!.ToString(), bytes))));
        var installer = new UpdateInstaller(client, 4);
        var reports = new List<(long received, long? total)>();

        var downloaded = await installer.DownloadAsync(Info(Hash(bytes)), new InlineProgress(reports.Add), CancellationToken.None, exePath);

        Assert.Equal(exePath + ".download", downloaded);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(downloaded));
        Assert.NotEmpty(reports);
        Assert.Equal(bytes.Length, reports[^1].received);
        Assert.Equal(bytes.Length, reports[^1].total);
    }

    [Fact]
    public async Task HashMismatchDeletesTempAndThrows()
    {
        await AssertDownloadFails("""0000000000000000000000000000000000000000000000000000000000000000  flowboost.exe""", HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("not a checksum")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000 other.exe")]
    public async Task MissingOrGarbledHashDeletesTempAndThrows(string checksum) => await AssertDownloadFails(checksum, HttpStatusCode.OK);

    [Fact]
    public Task Http404DeletesTempAndThrows()
    {
        return AssertDownloadFails(Hash(ValidExe()), HttpStatusCode.NotFound);
    }

    [Fact]
    public Task MissingChecksumDownloadFailsAndCleansUp()
    {
        return AssertDownloadFails("", HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CancellationDeletesTempAndThrows()
    {
        using var directory = new TempDirectory();
        var exePath = Path.Combine(directory.Path, "flowboost.exe");
        await File.WriteAllTextAsync(exePath + ".download", "stale");
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new FakeHandler((request, token) =>
        {
            if (request.RequestUri!.ToString() == HashUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  flowboost.exe") });
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(cancellation.Token);
        }));

        var exception = await Assert.ThrowsAsync<UpdateInstallException>(() => new UpdateInstaller(client, 4)
            .DownloadAsync(Info("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), new InlineProgress(_ => { }), cancellation.Token, exePath));

        Assert.Contains("canceled", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(exePath + ".download"));
    }

    [Fact]
    public void ApplySwapsFilesAndKeepsPreviousVersion()
    {
        using var directory = new TempDirectory();
        var exePath = Path.Combine(directory.Path, "flowboost.exe");
        var downloaded = exePath + ".download";
        File.WriteAllText(exePath, "old version");
        File.WriteAllText(downloaded, "new version");

        new UpdateInstaller(new HttpClient(), 4).Apply(downloaded, exePath);

        Assert.Equal("new version", File.ReadAllText(exePath));
        Assert.Equal("old version", File.ReadAllText(exePath + ".old"));
        Assert.False(File.Exists(downloaded));
    }

    [Fact]
    public void ApplyRollsBackWhenDownloadedFileIsMissing()
    {
        using var directory = new TempDirectory();
        var exePath = Path.Combine(directory.Path, "flowboost.exe");
        File.WriteAllText(exePath, "old version");

        Assert.Throws<UpdateInstallException>(() => new UpdateInstaller(new HttpClient(), 4).Apply(exePath + ".missing", exePath));

        Assert.Equal("old version", File.ReadAllText(exePath));
        Assert.False(File.Exists(exePath + ".old"));
    }

    [Fact]
    public void CleanupLeftoversDeletesOldAndDownloadFiles()
    {
        using var directory = new TempDirectory();
        var exePath = Path.Combine(directory.Path, "flowboost.exe");
        File.WriteAllText(exePath + ".old", "old");
        File.WriteAllText(exePath + ".download", "temporary");

        UpdateInstaller.CleanupLeftovers(exePath);

        Assert.False(File.Exists(exePath + ".old"));
        Assert.False(File.Exists(exePath + ".download"));
    }

    private static async Task AssertDownloadFails(string checksum, HttpStatusCode exeStatus, HttpStatusCode hashStatus = HttpStatusCode.OK)
    {
        using var directory = new TempDirectory();
        var exePath = Path.Combine(directory.Path, "flowboost.exe");
        using var client = new HttpClient(new FakeHandler((request, _) =>
        {
            if (request.RequestUri!.ToString() == HashUrl)
                return Task.FromResult(new HttpResponseMessage(hashStatus) { Content = new StringContent(checksum) });
            return Task.FromResult(new HttpResponseMessage(exeStatus) { Content = new ByteArrayContent(ValidExe()) });
        }));

        await Assert.ThrowsAsync<UpdateInstallException>(() => new UpdateInstaller(client, 4)
            .DownloadAsync(Info(Hash(ValidExe())), new InlineProgress(_ => { }), CancellationToken.None, exePath));
        Assert.False(File.Exists(exePath + ".download"));
    }

    private static HttpResponseMessage Response(string url, byte[] exe)
    {
        if (url == HashUrl)
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{Hash(exe)}  flowboost.exe", Encoding.UTF8) };
        var content = new ByteArrayContent(exe);
        content.Headers.ContentLength = exe.Length;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static UpdateInfo Info(string hash) => new(new Version(0, 1, 3), new Version(0, 2, 0), "https://github.com/ydakilux/flowboost/releases", null, ExeUrl, HashUrl);
    private static byte[] ValidExe() => [(byte)'M', (byte)'Z', .. Enumerable.Repeat((byte)0x5a, 64)];
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private sealed class InlineProgress(Action<(long received, long? total)> report) : IProgress<(long received, long? total)>
    {
        public void Report((long received, long? total) value) => report(value);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "flowboost-update-tests", Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
