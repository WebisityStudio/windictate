using System.Diagnostics;
using System.Windows.Threading;

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
/// </summary>
public static class TextInserter
{
    public static async Task<InsertOutcome> InsertAsync(string text, AppSettings settings, Dispatcher dispatcher)
    {
        // If the user still physically holds Ctrl/Alt from the hotkey, sending
        // Ctrl+V now would become Ctrl+Alt+V (or arbitrary shortcuts in type mode).
        // Abort rather than fire the wrong shortcut into the target app — but the
        // words shouldn't be lost, so leave them on the clipboard when allowed.
        if (!await WaitForModifierReleaseAsync(1500))
        {
            if (settings.KeepInClipboard && TrySetClipboard(text, dispatcher))
                return InsertOutcome.CopiedOnly;
            return InsertOutcome.Failed;
        }

        if (string.Equals(settings.InsertMode, "type", StringComparison.OrdinalIgnoreCase))
        {
            NativeMethods.SendUnicodeText(text);
            if (settings.KeepInClipboard)
                TrySetClipboard(text, dispatcher); // backup copy; the text is already typed
            return InsertOutcome.Inserted;
        }

        string? previous = null;
        bool hadText = false;
        if (!settings.KeepInClipboard)
        {
            dispatcher.Invoke(() =>
            {
                try
                {
                    if (System.Windows.Forms.Clipboard.ContainsText())
                    {
                        previous = System.Windows.Forms.Clipboard.GetText();
                        hadText = true;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn("Clipboard read failed: " + ex.Message);
                }
            });
        }

        // If the transcript never made it onto the clipboard, Ctrl+V would paste
        // whatever was there before — let the failure abort the whole insertion.
        dispatcher.Invoke(() => System.Windows.Forms.Clipboard.SetDataObject(text, true, 10, 50));

        NativeMethods.SendCtrlV();

        if (settings.KeepInClipboard)
            return InsertOutcome.Inserted; // transcript intentionally stays on the clipboard

        await Task.Delay(Math.Max(100, settings.ClipboardRestoreDelayMs));

        dispatcher.Invoke(() =>
        {
            try
            {
                if (hadText && previous != null)
                    System.Windows.Forms.Clipboard.SetDataObject(previous, true, 10, 50);
                else
                    System.Windows.Forms.Clipboard.Clear();
            }
            catch (Exception ex)
            {
                Logger.Warn("Clipboard restore failed: " + ex.Message);
            }
        });
        return InsertOutcome.Inserted;
    }

    private static bool TrySetClipboard(string text, Dispatcher dispatcher)
    {
        try
        {
            dispatcher.Invoke(() => System.Windows.Forms.Clipboard.SetDataObject(text, true, 10, 50));
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn("Clipboard write failed: " + ex.Message);
            return false;
        }
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
