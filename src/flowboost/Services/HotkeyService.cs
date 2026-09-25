using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using flowboost.Models;

namespace flowboost.Services;

public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private readonly HwndSource _source;
    private readonly Dictionary<int, Preset> _presets = [];
    private int _nextId = 0x4D00;
    public event Action<Preset>? Pressed;
    private bool _stopped;

    public HotkeyService()
    {
        var parameters = new HwndSourceParameters("flowboost.Hotkeys") { Width = 0, Height = 0, WindowStyle = 0x800000 };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public IReadOnlyList<string> RegisterAll(IEnumerable<Preset> presets)
    {
        foreach (var id in _presets.Keys.ToArray()) UnregisterHotKey(_source.Handle, id);
        _presets.Clear(); _nextId = 0x4D00;
        var failures = new List<string>();
        foreach (var preset in presets.Where(p => p.Enabled))
        {
            if (preset.Hotkey.Key == Key.None) continue;
            var id = _nextId++;
            var modifiers = (uint)preset.Hotkey.Modifiers | 0x4000; // MOD_NOREPEAT
            var key = KeyInterop.VirtualKeyFromKey(preset.Hotkey.Key);
            if (RegisterHotKey(_source.Handle, id, modifiers, (uint)key)) _presets[id] = preset;
            else failures.Add($"Could not register {preset.Hotkey} for {preset.Name}; it may already be in use.");
        }
        return failures;
    }

    public void StopIntake() { _stopped = true; foreach (var id in _presets.Keys.ToArray()) UnregisterHotKey(_source.Handle, id); _presets.Clear(); }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!_stopped && msg == WmHotkey && _presets.TryGetValue(wParam.ToInt32(), out var preset)) { handled = true; Pressed?.Invoke(preset); }
        return IntPtr.Zero;
    }
    public void Dispose()
    {
        _stopped = true;
        foreach (var id in _presets.Keys.ToArray()) UnregisterHotKey(_source.Handle, id);
        _source.RemoveHook(WndProc); _source.Dispose();
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
