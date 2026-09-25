using System.Windows.Input;

namespace flowboost.Models;

public sealed class Preset
{
    public string Name { get; set; } = "";
    public string Prompt { get; set; } = "";
    public HotkeyBinding Hotkey { get; set; } = new();
    public string ResponseLanguage { get; set; } = "English";
    public bool Enabled { get; set; } = true;
}

public sealed class HotkeyBinding
{
    public ModifierKeys Modifiers { get; set; }
    public Key Key { get; set; }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(Key.ToString());
        return string.Join("+", parts);
    }

    public static bool TryParse(string? text, out HotkeyBinding binding)
    {
        binding = new HotkeyBinding();
        if (string.IsNullOrWhiteSpace(text)) return true;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        foreach (var part in parts[..^1])
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": binding.Modifiers |= ModifierKeys.Control; break;
                case "alt": binding.Modifiers |= ModifierKeys.Alt; break;
                case "shift": binding.Modifiers |= ModifierKeys.Shift; break;
                case "win": case "windows": binding.Modifiers |= ModifierKeys.Windows; break;
                default: return false;
            }
        }
        return Enum.TryParse(parts[^1], true, out Key key) && key != Key.None && (binding.Key = key) != Key.None;
    }
}

public enum AppTheme { System, Light, Dark }

public sealed class AppSettings
{
    public string Model { get; set; } = "gpt-5";
    public List<Preset> Presets { get; set; } = CreateDefaultPresets();
    public bool StartWithWindows { get; set; } = true;
    public AppTheme Theme { get; set; } = AppTheme.System;

    public AppSettings Clone() => new()
    {
        Model = Model,
        Presets = Presets.Select(p => new Preset
        {
            Name = p.Name, Prompt = p.Prompt, Hotkey = new HotkeyBinding { Modifiers = p.Hotkey.Modifiers, Key = p.Hotkey.Key },
            ResponseLanguage = p.ResponseLanguage, Enabled = p.Enabled
        }).ToList(),
        StartWithWindows = StartWithWindows, Theme = Theme
    };

    public static List<Preset> CreateDefaultPresets() =>
    [
        Make("TLDR", "TLDR: summarize the following text concisely in a few bullet points.", Key.T),
        Make("Explain", "Explain the following text clearly and simply.", Key.E),
        Make("Translate", "Translate the following text to English.", Key.R),
        Make("Fix grammar", "Fix grammar and spelling of the following text. Return only the corrected text.", Key.G)
    ];

    private static Preset Make(string name, string prompt, Key key) => new()
    {
        Name = name, Prompt = prompt,
        Hotkey = new HotkeyBinding { Modifiers = ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, Key = key }
    };
}
