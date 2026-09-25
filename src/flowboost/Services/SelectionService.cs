using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace flowboost.Services;

public sealed class SelectionService
{
    private const uint KeyUp = 0x0002;
    private const int ClipboardTimeoutMs = 1000;
    private const int ModifierReleaseTimeoutMs = 2000;
    private const int RetryDelayMs = 40;

    public static int InputSize => Marshal.SizeOf<INPUT>();

    internal async Task<SelectionCaptureResult> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var initialForeground = GetForegroundWindow();
        var deadline = Stopwatch.StartNew();
        while (AnyModifierDown() && deadline.ElapsedMilliseconds < ModifierReleaseTimeoutMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GetForegroundWindow() != initialForeground) return new(SelectionCaptureStatus.ForegroundChanged);
            await Task.Delay(10, cancellationToken);
        }
        if (GetForegroundWindow() != initialForeground) return new(SelectionCaptureStatus.ForegroundChanged);
        if (AnyModifierDown()) return new(SelectionCaptureStatus.HeldModifiers);

        var sequenceBefore = GetClipboardSequenceNumber();
        if (!SendChord()) throw new InvalidOperationException("Windows could not send Ctrl+C to the active window.");

        var clipboardDeadline = Stopwatch.StartNew();
        var copyObserved = false;
        while (clipboardDeadline.ElapsedMilliseconds < ClipboardTimeoutMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GetForegroundWindow() != initialForeground)
                return ResolveClipboardObservation(foregroundChanged: true, sequenceChanged: copyObserved, text: null);
            var sequenceAfter = GetClipboardSequenceNumber();
            if (sequenceAfter != sequenceBefore) copyObserved = true;
            if (copyObserved)
            {
                try
                {
                    if (System.Windows.Clipboard.ContainsText())
                    {
                        var text = System.Windows.Clipboard.GetText();
                        if (!string.IsNullOrWhiteSpace(text))
                            return ResolveClipboardObservation(foregroundChanged: false, sequenceChanged: true, text);
                    }
                }
                catch (ExternalException) { /* Retry while another process owns the clipboard. */ }
                catch (InvalidOperationException) { /* Retry while another process owns the clipboard. */ }
            }
            await Task.Delay(RetryDelayMs, cancellationToken);
        }
        return ResolveClipboardObservation(foregroundChanged: false, sequenceChanged: copyObserved, text: null);
    }

    internal static SelectionCaptureResult ResolveClipboardObservation(bool foregroundChanged, bool sequenceChanged, string? text)
    {
        if (foregroundChanged) return new(SelectionCaptureStatus.ForegroundChanged);
        if (!sequenceChanged) return new(SelectionCaptureStatus.ClipboardUnchanged);
        return string.IsNullOrWhiteSpace(text)
            ? new(SelectionCaptureStatus.NoText)
            : new(SelectionCaptureStatus.Success, text);
    }

    private static bool AnyModifierDown() => Down(0x10) || Down(0x11) || Down(0x12) || Down(0x5B) || Down(0x5C);
    private static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    private static bool SendChord()
    {
        var inputs = new[] { Input.Key(0x11, false), Input.Key(0x43, false), Input.Key(0x43, true), Input.Key(0x11, true) };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
    }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)] internal struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] internal struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] internal struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }
    private static class Input
    {
        public static INPUT Key(ushort key, bool up) => new() { type = 1, U = new InputUnion { ki = new KEYBDINPUT { wVk = key, dwFlags = up ? KeyUp : 0 } } };
    }
}

internal enum SelectionCaptureStatus
{
    Success,
    HeldModifiers,
    ForegroundChanged,
    ClipboardUnchanged,
    NoText
}

internal sealed record SelectionCaptureResult(SelectionCaptureStatus Status, string? Text = null);
