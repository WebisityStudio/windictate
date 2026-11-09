namespace WinDictate.Core;

public enum InsertOutcome
{
    /// <summary>The text was sent to the focused app.</summary>
    Inserted,

    /// <summary>The text could not be delivered and is on the clipboard for the user to paste.</summary>
    CopiedOnly,

    /// <summary>The text could be neither delivered nor copied (the user is told).</summary>
    Lost,

    /// <summary>A modifier key went down just before sending: nothing was sent, try again later.</summary>
    KeysHeld
}

public sealed record InsertResult(
    InsertOutcome Outcome,
    bool Typed = false,           // delivered as keystrokes (type mode, or paste mode's fallback)
    bool OnClipboard = false,     // this text is on the clipboard afterwards
    bool EarlierTextKept = false, // an earlier undelivered dictation was left on the clipboard instead
    int RecoveryCount = 0);       // CopiedOnly: how many dictations the clipboard now holds

public enum SendResult
{
    Sent,
    KeysHeld, // a modifier key is physically down: nothing was sent
    Failed    // Windows did not accept the input (for example an elevated window has focus)
}

/// <summary>Operating-system primitives for inserting text (Windows: WindowsTextTarget).
/// Clipboard calls may take a while; they are only made from worker threads.</summary>
public interface ITextTarget
{
    Task<bool> SetClipboardTextAsync(string text);
    Task<(bool Ok, string? Text)> GetClipboardTextAsync();
    Task<bool> ClearClipboardAsync();

    /// <summary>Changes whenever anyone changes the clipboard.</summary>
    long ClipboardSequence { get; }

    /// <summary>Ctrl+V into the focused app, unless a modifier key is held.</summary>
    SendResult SendPaste();

    /// <summary>Types the text as Unicode keystrokes, unless a modifier key is held.</summary>
    SendResult TypeText(string text);
}

public sealed record InsertOptions(bool TypeMode, bool KeepInClipboard, int ClipboardRestoreDelayMs)
{
    public static InsertOptions From(AppSettings settings) => new(
        string.Equals(settings.InsertMode, "type", StringComparison.OrdinalIgnoreCase),
        settings.KeepInClipboard,
        settings.ClipboardRestoreDelayMs);
}

/// <summary>
/// Inserts transcripts into the focused application.
/// "paste" mode: put the transcript on the clipboard and send Ctrl+V; if the
/// clipboard cannot be set, type it instead (never paste whatever was there).
/// "type" mode: synthesize Unicode key events.
/// With KeepInClipboard (default) the transcript stays on the clipboard afterwards
/// for re-pasting. With it off, paste mode restores the previous clipboard text
/// (held in memory only, never logged) and type mode never touches the clipboard.
///
/// No transcript is dropped silently: when it cannot be delivered it is left on
/// the clipboard (whatever KeepInClipboard says) and the caller tells the user.
/// Until the user copies something else, that undelivered text is protected:
/// further undelivered dictations are appended to it, and later successful
/// insertions put it back on the clipboard afterwards instead of replacing it.
///
/// Not thread-safe: the controller's insertion stage calls it one at a time.
/// </summary>
public sealed class TextInserter
{
    private readonly ITextTarget _target;
    private readonly Func<int, Task> _delay;

    private string? _recoveryText;  // undelivered dictation(s) we left on the clipboard
    private long _recoverySequence; // clipboard sequence number right after we set it
    private int _recoveryCount;

    public TextInserter(ITextTarget target, Func<int, Task>? delay = null)
    {
        _target = target;
        _delay = delay ?? (ms => Task.Delay(ms));
    }

    public async Task<InsertResult> InsertAsync(string text, InsertOptions options)
    {
        bool protect = RecoveryStillOnClipboard();
        return options.TypeMode
            ? await TypeAsync(text, options, protect).ConfigureAwait(false)
            : await PasteAsync(text, options, protect).ConfigureAwait(false);
    }

    /// <summary>
    /// Insertion is impossible (keys held too long, input blocked, workstation
    /// locked since the dictation): leave the text on the clipboard. An earlier
    /// undelivered dictation still on the clipboard is kept and this one appended.
    /// </summary>
    public Task<InsertResult> KeepForRecoveryAsync(string text) =>
        KeepForRecoveryAsync(text, RecoveryStillOnClipboard());

    /// <param name="appendToRecovery">Whether the protected text is still on the clipboard,
    /// decided before this insertion's own clipboard write (which changes the sequence number).</param>
    private async Task<InsertResult> KeepForRecoveryAsync(string text, bool appendToRecovery)
    {
        string combined = text;
        int count = 1;
        if (appendToRecovery && _recoveryText != null)
        {
            combined = Join(_recoveryText, text);
            count = _recoveryCount + 1;
        }
        if (!await _target.SetClipboardTextAsync(combined).ConfigureAwait(false))
            return new InsertResult(InsertOutcome.Lost);

        _recoveryText = combined;
        _recoveryCount = count;
        _recoverySequence = _target.ClipboardSequence;
        return new InsertResult(InsertOutcome.CopiedOnly, OnClipboard: true, RecoveryCount: count);
    }

    private async Task<InsertResult> TypeAsync(string text, InsertOptions options, bool protect)
    {
        var sent = _target.TypeText(text);
        if (sent == SendResult.KeysHeld) return new InsertResult(InsertOutcome.KeysHeld);
        if (sent == SendResult.Failed) return await KeepForRecoveryAsync(text, protect).ConfigureAwait(false);

        if (protect) return new InsertResult(InsertOutcome.Inserted, Typed: true, EarlierTextKept: true);
        // Backup copy for re-pasting; the text is already typed, so a failure here is harmless.
        bool onClipboard = options.KeepInClipboard && await _target.SetClipboardTextAsync(text).ConfigureAwait(false);
        return new InsertResult(InsertOutcome.Inserted, Typed: true, OnClipboard: onClipboard);
    }

    private async Task<InsertResult> PasteAsync(string text, InsertOptions options, bool protect)
    {
        // What the clipboard should hold afterwards when it is not this transcript:
        // the protected undelivered text, or the user's previous text.
        bool restoreAfter = protect || !options.KeepInClipboard;
        string? restoreText = null;
        if (protect)
        {
            restoreText = _recoveryText;
        }
        else if (!options.KeepInClipboard)
        {
            var (ok, previous) = await _target.GetClipboardTextAsync().ConfigureAwait(false);
            restoreText = ok ? previous : null; // non-text or unreadable: cleared afterwards
        }

        if (!await _target.SetClipboardTextAsync(text).ConfigureAwait(false))
        {
            // Ctrl+V would paste whatever is on the clipboard now: type the text instead.
            var typed = _target.TypeText(text);
            if (typed == SendResult.KeysHeld) return new InsertResult(InsertOutcome.KeysHeld);
            if (typed == SendResult.Failed) return await KeepForRecoveryAsync(text, protect).ConfigureAwait(false);
            return new InsertResult(InsertOutcome.Inserted, Typed: true, EarlierTextKept: protect);
        }

        var sent = _target.SendPaste();
        if (sent == SendResult.KeysHeld)
        {
            // Nothing was pasted. Put the clipboard back now, so the retry does not
            // mistake this transcript for the user's own clipboard text.
            if (restoreAfter) await RestoreAsync(restoreText, protect).ConfigureAwait(false);
            return new InsertResult(InsertOutcome.KeysHeld);
        }
        if (sent == SendResult.Failed)
            return await KeepForRecoveryAsync(text, protect).ConfigureAwait(false);

        if (!restoreAfter)
            return new InsertResult(InsertOutcome.Inserted, OnClipboard: true); // stays for re-pasting

        // The target app reads the clipboard after it receives Ctrl+V, so wait
        // before putting the previous contents back.
        await _delay(Math.Max(100, options.ClipboardRestoreDelayMs)).ConfigureAwait(false);
        await RestoreAsync(restoreText, protect).ConfigureAwait(false);
        return new InsertResult(InsertOutcome.Inserted, EarlierTextKept: protect);
    }

    private async Task RestoreAsync(string? text, bool protectedText)
    {
        if (text != null)
        {
            if (await _target.SetClipboardTextAsync(text).ConfigureAwait(false) && protectedText)
                _recoverySequence = _target.ClipboardSequence; // still ours
        }
        else
        {
            await _target.ClearClipboardAsync().ConfigureAwait(false);
        }
    }

    private bool RecoveryStillOnClipboard()
    {
        if (_recoveryText == null) return false;
        if (_target.ClipboardSequence == _recoverySequence) return true;
        // The user has copied something since: they have dealt with it.
        _recoveryText = null;
        _recoveryCount = 0;
        return false;
    }

    private static string Join(string earlier, string next) =>
        earlier.Length == 0 || char.IsWhiteSpace(earlier[^1]) ? earlier + next : earlier + " " + next;
}
