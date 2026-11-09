using System.Runtime.InteropServices;

namespace WinDictate.Core;

/// <summary>
/// Windows implementation of the insertion primitives: the clipboard through
/// <see cref="ClipboardAccess"/> (its own STA thread, never the UI thread) and
/// keystrokes through SendInput. Nothing is sent while a modifier key is
/// physically held, because Ctrl+V would become Ctrl+Alt+V. Clipboard and
/// transcript contents are never logged.
/// </summary>
internal sealed class WindowsTextTarget : ITextTarget
{
    public Task<bool> SetClipboardTextAsync(string text) => ClipboardAccess.TrySetTextAsync(text);

    public Task<(bool Ok, string? Text)> GetClipboardTextAsync() => ClipboardAccess.TryGetTextAsync();

    public Task<bool> ClearClipboardAsync() => ClipboardAccess.TryClearAsync();

    public long ClipboardSequence => NativeMethods.GetClipboardSequenceNumber();

    public SendResult SendPaste()
    {
        if (NativeMethods.AnyModifierHeld()) return SendResult.KeysHeld;
        if (NativeMethods.SendCtrlV()) return SendResult.Sent;
        Logger.Warn($"Windows rejected the Ctrl+V keystrokes (error {Marshal.GetLastWin32Error()}); an elevated window or secure desktop may have focus");
        return SendResult.Failed;
    }

    public SendResult TypeText(string text)
    {
        if (NativeMethods.AnyModifierHeld()) return SendResult.KeysHeld;
        if (NativeMethods.SendUnicodeText(text)) return SendResult.Sent;
        Logger.Warn($"Windows rejected the typed keystrokes (error {Marshal.GetLastWin32Error()}); an elevated window or secure desktop may have focus");
        return SendResult.Failed;
    }
}

/// <summary>Windows implementation of the controller's OS queries.</summary>
internal sealed class WindowsDictationPlatform : IDictationPlatform
{
    public IntPtr ForegroundWindow() => NativeMethods.GetForegroundWindow();

    public bool ModifierKeysHeld() => NativeMethods.AnyModifierHeld();

    public int ResolveMicrophone(AppSettings settings) => AudioRecorder.ResolveDevice(settings);

    public IAudioCapture CreateRecorder() => new AudioRecorder();
}
