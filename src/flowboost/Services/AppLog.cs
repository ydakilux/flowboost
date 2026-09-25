using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace flowboost.Services;

internal static class AppLog
{
    private static readonly object Sync = new();

    public static void Write(string context, Exception? exception = null)
    {
        WriteCore(Path.Combine(AppDataDirectory.Root, "log.txt"), context, exception, null);
    }

    internal static void WriteSessionErrorEvent()
    {
        WriteCore(Path.Combine(AppDataDirectory.Root, "log.txt"), "Session SDK error event", null, "sdk-reported-error");
    }

    internal static void WriteSelectionCaptureFailure(SelectionCaptureStatus status) =>
        Write("Selection capture " + GetSelectionCaptureStatusName(status));

    internal static void WriteSelectionCaptureFailureTo(string path, SelectionCaptureStatus status) =>
        WriteTo(path, "Selection capture " + GetSelectionCaptureStatusName(status));

    private static string GetSelectionCaptureStatusName(SelectionCaptureStatus status) => status switch
    {
        SelectionCaptureStatus.HeldModifiers => "held modifiers",
        SelectionCaptureStatus.NoText => "no text",
        SelectionCaptureStatus.ClipboardUnchanged => "clipboard unchanged",
        SelectionCaptureStatus.ForegroundChanged => "foreground changed",
        _ => "failed"
    };

    // This path-taking overload keeps tests away from the user's application data.
    internal static void WriteTo(string path, string context, Exception? exception = null)
    {
        WriteCore(path, context, exception, null);
    }

    private static void WriteCore(string path, string context, Exception? exception, string? classification)
    {
        var eventName = GetFixedEventName(context);
        var errorType = exception is null ? classification : GetSafeExceptionType(exception);
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.UtcNow:O} event={eventName}{(errorType is null ? "" : $" error-type={errorType}")}{Environment.NewLine}");

        try
        {
            lock (Sync)
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(line);
            }
            Debug.Write(line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Debug.WriteLine("flowboost logging failed.");
        }
    }

    private static string GetFixedEventName(string context) => context switch
    {
        "Settings could not be opened" => "settings-open-failed",
        "GitHub sign-in failed" => "github-sign-in-failed",
        "Could not focus the GitHub sign-in window" => "github-sign-in-focus-failed",
        "Could not close the GitHub sign-in window" => "github-sign-in-close-failed",
        "Authentication state update failed" => "authentication-state-update-failed",
        "Timed out while shutting down Copilot service" => "copilot-shutdown-timeout",
        "Copilot shutdown failed" => "copilot-shutdown-failed",
        "Application shutdown failed" => "application-shutdown-failed",
        "Hotkey shutdown failed" => "hotkey-shutdown-failed",
        "Tray shutdown failed" => "tray-shutdown-failed",
        "GitHub device sign-in failed" => "github-device-sign-in-failed",
        "Could not copy GitHub device sign-in code" => "github-device-code-copy-failed",
        "Could not open GitHub device sign-in page" => "github-device-page-open-failed",
        "Could not load bundled release notes" => "release-notes-load-failed",
        "Could not load saved GitHub token" => "github-token-load-failed",
        "Could not remove legacy startup entry" => "legacy-startup-remove-failed",
        "Session abort failed" => "session-abort-failed",
        "Session dispose failed" => "session-dispose-failed",
        "Session deletion failed" => "session-delete-failed",
        "Could not back up invalid settings" => "settings-backup-failed",
        "Legacy MXKeypad app data migration failed" => "legacy-data-migration-failed",
        "Session SDK error event" => "session-sdk-error",
        "Session send failed" => "session-send-failed",
        "Selection capture held modifiers" => "selection-held-modifiers",
        "Selection capture no text" => "selection-no-text",
        "Selection capture clipboard unchanged" => "selection-clipboard-unchanged",
        "Selection capture foreground changed" => "selection-foreground-changed",
        "Selection capture failed" => "selection-capture-failed",
        "Session creation failed" => "session-creation-failed",
        "Initial session send failed" => "initial-session-send-failed",
        "Hotkey capture failed" => "hotkey-capture-failed",
        _ => "operation-failed"
    };

    private static string GetSafeExceptionType(Exception exception)
    {
        var typeName = exception.GetType().Name;
        var safe = new string(typeName.Where(char.IsAsciiLetterOrDigit).Take(80).ToArray());
        return safe.Length == 0 ? "Exception" : safe;
    }
}
