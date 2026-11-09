using System.Diagnostics;

namespace WinDictate.Core;

public enum InsertOutcome
{
    Inserted,   // text was sent to the focused app
    CopiedOnly, // keystrokes could not be sent, but the transcript is on the clipboard
    Failed      // nothing was delivered anywhere
}

/// <summary>
/// Inserts the final transcript into the focused application.
/// "paste" mode: set the transcript on the clipboard and send Ctrl+V.
/// "type" mode: synthesizes Unicode key events.
/// With KeepInClipboard (default) the transcript stays on the clipboard afterwards,
/// so it can be pasted again anywhere — including when the focus turned out not to
/// be a text box and the insertion landed nowhere. With it off, paste mode restores
/// the previous clipboard (held in memory only, never logged) and type mode never
/// touches the clipboard at all.
/// Runs on a worker thread; clipboard calls go through <see cref="ClipboardAccess"/>
/// so they never block the UI thread.
/// </summary>
public static class TextInserter
{
    public static async Task<InsertOutcome> InsertAsync(string text, AppSettings settings)
    {
        // If the user still physically holds Ctrl/Alt from the hotkey, sending
        // Ctrl+V now would become Ctrl+Alt+V (or arbitrary shortcuts in type mode).
        // Abort rather than fire the wrong shortcut into the target app — but the
        // words shouldn't be lost, so leave them on the clipboard when allowed.
        if (!await WaitForModifierReleaseAsync(1500))
        {
            if (settings.KeepInClipboard && await ClipboardAccess.TrySetTextAsync(text))
                return InsertOutcome.CopiedOnly;
            return InsertOutcome.Failed;
        }

        if (string.Equals(settings.InsertMode, "type", StringComparison.OrdinalIgnoreCase))
        {
            NativeMethods.SendUnicodeText(text);
            if (settings.KeepInClipboard)
                await ClipboardAccess.TrySetTextAsync(text); // backup copy; the text is already typed
            return InsertOutcome.Inserted;
        }

        string? previous = null;
        bool hadText = false;
        if (!settings.KeepInClipboard)
        {
            var (ok, current) = await ClipboardAccess.TryGetTextAsync();
            hadText = ok && current != null;
            previous = current;
        }

        // If the transcript never made it onto the clipboard, Ctrl+V would paste
        // whatever was there before — abort the insertion instead.
        if (!await ClipboardAccess.TrySetTextAsync(text))
            return InsertOutcome.Failed;

        NativeMethods.SendCtrlV();

        if (settings.KeepInClipboard)
            return InsertOutcome.Inserted; // transcript intentionally stays on the clipboard

        await Task.Delay(Math.Max(100, settings.ClipboardRestoreDelayMs));

        if (hadText && previous != null)
            await ClipboardAccess.TrySetTextAsync(previous);
        else
            await ClipboardAccess.TryClearAsync();
        return InsertOutcome.Inserted;
    }

    private static async Task<bool> WaitForModifierReleaseAsync(int timeoutMs)
    {
        int[] modifiers = { 0x10, 0x11, 0x12, 0x5B, 0x5C }; // Shift, Ctrl, Alt, LWin, RWin
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            bool anyDown = modifiers.Any(vk => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0);
            if (!anyDown) return true;
            await Task.Delay(15);
        }
        return false;
    }
}
