using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace flowboost.Services;

public sealed class UpdateInstallException(string message, Exception? innerException = null) : Exception(message, innerException);

public sealed class UpdateInstaller
{
    private const long ProductionMinimumExeSize = 1024 * 1024;
    private static readonly HttpClient SharedHttpClient = new();
    private readonly HttpClient _httpClient;
    private readonly long _minimumExeSize;

    public UpdateInstaller(HttpClient? httpClient = null) : this(httpClient ?? SharedHttpClient, ProductionMinimumExeSize) { }

    internal UpdateInstaller(HttpClient httpClient, long minimumExeSize)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (minimumExeSize < 1) throw new ArgumentOutOfRangeException(nameof(minimumExeSize));
        _minimumExeSize = minimumExeSize;
    }

    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<(long received, long? total)> progress,
        CancellationToken cancellationToken, string? targetExePath = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(progress);
        var exePath = targetExePath ?? Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
            throw new UpdateInstallException("Could not find the flowboost program folder. Open GitHub to download the update.");

        var tempPath = exePath + ".download";
        try
        {
            if (!IsTrustedDownloadUrl(info.Sha256Url) || !IsTrustedDownloadUrl(info.ExeUrl))
                throw new UpdateInstallException("The update download is not available. Open GitHub to download it.");

            var expectedHash = await DownloadHashAsync(info.Sha256Url!, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, info.ExeUrl);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new UpdateInstallException("Could not download the update. Check your connection or open GitHub.");

            var total = response.Content.Headers.ContentLength ?? info.ExeSize;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(tempPath))!);
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                long received = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, count);
                    received += count;
                    progress.Report((received, total));
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                var actualHash = Convert.ToHexString(hash.GetHashAndReset());
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new UpdateInstallException("The downloaded update did not pass its checksum check. Open GitHub to download it.");
            }

            var fileInfo = new FileInfo(tempPath);
            if (fileInfo.Length <= _minimumExeSize || !await StartsWithMZAsync(tempPath, cancellationToken).ConfigureAwait(false))
                throw new UpdateInstallException("The downloaded file is not a valid flowboost update. Open GitHub to download it.");

            cancellationToken.ThrowIfCancellationRequested();
            return tempPath;
        }
        catch (OperationCanceledException)
        {
            DeleteTempFile(tempPath);
            throw new UpdateInstallException("The update download was canceled.");
        }
        catch (UpdateInstallException)
        {
            DeleteTempFile(tempPath);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            DeleteTempFile(tempPath);
            var message = ex is UnauthorizedAccessException or System.Security.SecurityException or IOException
                ? "The flowboost program folder is not writable. Open GitHub to download the update manually."
                : "Could not download the update. Check your connection or open GitHub.";
            throw new UpdateInstallException(message, ex);
        }
        catch (Exception ex)
        {
            DeleteTempFile(tempPath);
            AppLog.Write("Update download failed", ex);
            throw new UpdateInstallException("Could not download the update. Check your connection or open GitHub.", ex);
        }
    }

    public void Apply(string downloadedPath, string currentExePath)
    {
        var oldPath = currentExePath + ".old";
        try
        {
            if (File.Exists(oldPath)) File.Delete(oldPath);
            File.Move(currentExePath, oldPath);
            try
            {
                File.Move(downloadedPath, currentExePath);
            }
            catch
            {
                if (!File.Exists(currentExePath) && File.Exists(oldPath)) File.Move(oldPath, currentExePath);
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            throw new UpdateInstallException("Could not replace flowboost. Your current version has been kept; open GitHub for help.", ex);
        }
    }

    public bool Relaunch(string currentExePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo(currentExePath, $"--updated-from {Environment.ProcessId}") { UseShellExecute = false });
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            AppLog.Write("Update install failed", ex);
            return false;
        }
    }

    public void Rollback(string currentExePath)
    {
        var oldPath = currentExePath + ".old";
        var failedPath = currentExePath + ".failed";
        try
        {
            if (!File.Exists(oldPath)) return;
            if (File.Exists(currentExePath)) File.Move(currentExePath, failedPath, overwrite: true);
            File.Move(oldPath, currentExePath);
            if (File.Exists(failedPath)) File.Delete(failedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            AppLog.Write("Update install failed", ex);
            throw new UpdateInstallException("Could not restore the previous flowboost version. Open GitHub for help.", ex);
        }
    }

    public static void CleanupLeftovers(string exePath)
    {
        foreach (var path in new[] { exePath + ".old", exePath + ".download" })
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { AppLog.Write("Update install failed", ex); }
        }
    }

    private async Task<string> DownloadHashAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new UpdateInstallException("Could not download the update checksum. Open GitHub to download it.");
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var fields = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 2 || !string.Equals(fields[1], "flowboost.exe", StringComparison.Ordinal) ||
            fields[0].Length != 64 || !fields[0].All(Uri.IsHexDigit))
            throw new UpdateInstallException("The update checksum file is missing or invalid. Open GitHub to download it.");
        return fields[0];
    }

    private static bool IsTrustedDownloadUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith("/ydakilux/flowboost/releases/download/", StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> StartsWithMZAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 2, useAsync: true);
        var header = new byte[2];
        var read = await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false);
        return read == 2 && header[0] == (byte)'M' && header[1] == (byte)'Z';
    }

    private static void DeleteTempFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { AppLog.Write("Update install failed", ex); }
    }
}
