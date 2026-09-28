using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using flowboost.Models;
using System.Windows.Input;

namespace flowboost.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
    };
    private readonly string _path = Path.Combine(AppDataDirectory.Root, "settings.json");
    public AppSettings Current { get; private set; }
    public event Action? Changed;
    public event Action<string>? Error;
    public string? LastError { get; private set; }

    public SettingsService()
    {
        try
        {
            Current = File.Exists(_path) ? Normalize(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions) ?? throw new JsonException("The settings file contained no settings.")) : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            try { if (File.Exists(_path)) File.Copy(_path, Path.Combine(Path.GetDirectoryName(_path)!, "settings.invalid.json"), true); }
            catch (Exception backupEx) when (backupEx is IOException or UnauthorizedAccessException) { AppLog.Write("Could not back up invalid settings", backupEx); }
            Current = new AppSettings();
            LastError = $"Settings could not be loaded and were reset to defaults: {ex.Message}";
        }
    }

    public void ReportLoadError() { if (LastError is not null) Error?.Invoke(LastError); }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = ValidateForSave(settings);
        var temp = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(normalized, JsonOptions));
            File.Move(temp, _path, true);
            Current = normalized;
            LastError = null;
            Changed?.Invoke();
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    public static AppSettings Normalize(AppSettings? settings)
    {
        settings ??= new AppSettings();
        if (string.IsNullOrWhiteSpace(settings.Model)) settings.Model = "gpt-5";
        if (!Enum.IsDefined(settings.Theme)) settings.Theme = AppTheme.System;
        settings.SkippedUpdateVersion ??= "";
        settings.Presets ??= AppSettings.CreateDefaultPresets();
        foreach (var preset in settings.Presets)
        {
            if (preset is null) continue;
            preset.Name ??= ""; preset.Prompt ??= ""; preset.ResponseLanguage ??= "English";
            preset.Hotkey ??= new HotkeyBinding();
            const ModifierKeys allowed = ModifierKeys.Alt | ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Windows;
            if (!Enum.IsDefined(preset.Hotkey.Key) || (preset.Hotkey.Modifiers & ~allowed) != 0) preset.Hotkey = new HotkeyBinding();
        }
        settings.Presets.RemoveAll(p => p is null);
        return settings;
    }

    public static AppSettings ValidateForSave(AppSettings? settings)
    {
        var normalized = Normalize(settings);
        var conflicts = normalized.Presets.Where(p => p.Enabled && p.Hotkey.Key != Key.None)
            .GroupBy(p => (p.Hotkey.Modifiers, p.Hotkey.Key)).FirstOrDefault(g => g.Count() > 1);
        if (conflicts is not null) throw new InvalidOperationException($"Enabled presets share the hotkey {conflicts.First().Hotkey}.");
        return normalized;
    }
}

internal static class AppDataDirectory
{
    private static readonly object MigrationLock = new();
    private static bool _migrated;
    public static string Root
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var root = Path.Combine(appData, "flowboost");
            lock (MigrationLock)
            {
                if (!_migrated)
                {
                    _migrated = true;
                    var legacy = Path.Combine(appData, "MXKeypad");
                    try { if (Directory.Exists(legacy) && !Directory.Exists(root)) Directory.Move(legacy, root); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Write("Legacy MXKeypad app data migration failed", ex); }
                }
            }
            return root;
        }
    }
    public static string Copilot => Path.Combine(Root, "copilot");
}
