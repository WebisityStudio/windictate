using System.Diagnostics;
using System.Windows.Threading;

namespace WinDictate.Core;

/// <summary>
/// Inserts the final transcript into the focused application.
/// "paste" mode: save clipboard text, set transcript, send Ctrl+V, restore.
/// "type" mode: synthesizes Unicode key events, never touching the clipboard.
/// The previous clipboard is held in memory only and never logged.
/// </summary>
public static class TextInserter
{
    /// <returns>true if text was inserted; false if modifier keys were still held after 1.5 s.</returns>
    public static async Task<bool> InsertAsync(string text, AppSettings settings, Dispatcher dispatcher)
    {
        // If the user still physically holds Ctrl/Alt from the hotkey, sending
        // Ctrl+V now would become Ctrl+Alt+V (or arbitrary shortcuts in type mode).
        // Abort rather than fire the wrong shortcut into the target app.
        if (!await WaitForModifierReleaseAsync(1500))
            return false;

        if (string.Equals(settings.InsertMode, "type", StringComparison.OrdinalIgnoreCase))
        {
            NativeMethods.SendUnicodeText(text);
            return true;
        }

        string? previous = null;
        bool hadText = false;
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
            System.Windows.Forms.Clipboard.SetDataObject(text, true, 10, 50);
        });

        NativeMethods.SendCtrlV();

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
        return true;
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
