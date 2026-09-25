using System.IO;
using flowboost.Services;
using Xunit;

namespace flowboost.Tests;

public sealed class AppLogTests
{
    [Fact]
    public void WriteAppendsUtcEventAndExceptionTypeWithoutSensitiveDetails()
    {
        using var store = new TemporaryLogStore();
        const string existing = "existing diagnostic entry\n";
        Directory.CreateDirectory(Path.GetDirectoryName(store.LogPath)!);
        File.WriteAllText(store.LogPath, existing);

        AppLog.WriteTo(store.LogPath, "Session send failed", new InvalidOperationException("prompt=private\r\naccess_token=secret stack details"));

        var contents = File.ReadAllText(store.LogPath);
        Assert.StartsWith(existing, contents);
        var appended = Assert.Single(contents[existing.Length..].Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("event=session-send-failed", appended);
        Assert.Contains("error-type=InvalidOperationException", appended);
        Assert.Contains("Z event=", appended);
        Assert.DoesNotContain("private", appended);
        Assert.DoesNotContain("secret", appended);
        Assert.DoesNotContain("stack details", appended);
        Assert.DoesNotContain("\r", appended);
        Assert.DoesNotContain("\n", appended);
    }

    [Fact]
    public void WriteDoesNotPersistUnrecognizedContext()
    {
        using var store = new TemporaryLogStore();

        AppLog.WriteTo(store.LogPath, "token=should-not-be-written\nselection", new Exception("private message"));

        var contents = File.ReadAllText(store.LogPath);
        Assert.Contains("event=operation-failed", contents);
        Assert.Contains("error-type=Exception", contents);
        Assert.DoesNotContain("should-not-be-written", contents);
        Assert.DoesNotContain("selection", contents);
        Assert.DoesNotContain("private message", contents);
    }

    [Fact]
    public void WriteMapsHotkeyFailureEventsWithoutSensitiveDetails()
    {
        using var store = new TemporaryLogStore();
        Directory.CreateDirectory(Path.GetDirectoryName(store.LogPath)!);

        AppLog.WriteSelectionCaptureFailureTo(store.LogPath, SelectionCaptureStatus.HeldModifiers);
        AppLog.WriteSelectionCaptureFailureTo(store.LogPath, SelectionCaptureStatus.NoText);
        AppLog.WriteSelectionCaptureFailureTo(store.LogPath, SelectionCaptureStatus.ClipboardUnchanged);
        AppLog.WriteSelectionCaptureFailureTo(store.LogPath, SelectionCaptureStatus.ForegroundChanged);
        AppLog.WriteTo(store.LogPath, "Session creation failed", new InvalidOperationException("prompt and token secret"));
        AppLog.WriteTo(store.LogPath, "Initial session send failed", new IOException("clipboard selection secret"));
        AppLog.WriteTo(store.LogPath, "Hotkey capture failed", new Exception("response private"));

        var contents = File.ReadAllText(store.LogPath);
        Assert.Contains("event=selection-held-modifiers", contents);
        Assert.Contains("event=selection-no-text", contents);
        Assert.Contains("event=selection-clipboard-unchanged", contents);
        Assert.Contains("event=selection-foreground-changed", contents);
        Assert.Contains("event=session-creation-failed error-type=InvalidOperationException", contents);
        Assert.Contains("event=initial-session-send-failed error-type=IOException", contents);
        Assert.Contains("event=hotkey-capture-failed error-type=Exception", contents);
        Assert.DoesNotContain("prompt", contents);
        Assert.DoesNotContain("token", contents);
        Assert.DoesNotContain("secret", contents);
        Assert.DoesNotContain("clipboard selection", contents);
        Assert.DoesNotContain("response private", contents);
    }

    private sealed class TemporaryLogStore : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "flowboost-log-tests", Guid.NewGuid().ToString("N"));
        public string LogPath => Path.Combine(_directory, "log.txt");

        public void Dispose()
        {
            try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
