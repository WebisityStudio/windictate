namespace WinDictate.Core;

/// <summary>
/// Where a dictation was meant to go: the foreground window and, when Windows
/// exposes one, its focused control, captured when the hotkey was pressed.
/// Browsers and other single-window apps report one control for all their
/// fields, so a move between fields inside such an app cannot be detected.
/// </summary>
public readonly record struct InsertionTarget(IntPtr Window, IntPtr Focus)
{
    /// <summary>True when <paramref name="current"/> is still the same place. Parts that were not known are not compared.</summary>
    public bool Matches(InsertionTarget current)
    {
        if (Window == IntPtr.Zero) return true;
        if (current.Window != Window) return false;
        return Focus == IntPtr.Zero || current.Focus == IntPtr.Zero || current.Focus == Focus;
    }
}

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

/// <summary>Why a dictation ended up on the clipboard instead of in the app.</summary>
public enum RecoveryReason
{
    None,
    NotDelivered,  // keys held too long, input rejected, workstation locked since the dictation
    TargetChanged, // focus moved to another window or field since the hotkey was pressed
    PartlyTyped    // Windows stopped accepting keystrokes part-way through
}

public sealed record InsertResult(
    InsertOutcome Outcome,
    bool Typed = false,                // delivered as keystrokes (type mode, or instead of pasting)
    bool OnClipboard = false,          // this text is on the clipboard afterwards
    bool EarlierTextKept = false,      // an earlier undelivered dictation stays on the clipboard instead
    int RecoveryCount = 0,             // CopiedOnly: how many dictations the clipboard now holds
    RecoveryReason Reason = RecoveryReason.None,
    bool ClipboardNotRestored = false); // KeepInClipboard off, but putting the previous text back failed

public enum SendResult
{
    Sent,
    KeysHeld, // a modifier key is physically down: nothing was sent
    Partial,  // only part of the input was accepted
    Failed    // Windows did not accept the input (for example a secure desktop is active)
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

    /// <summary>Where keyboard input would go right now.</summary>
    InsertionTarget CurrentTarget();

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
/// Nothing is sent unless keyboard focus is still where the dictation was
/// started. No transcript is dropped silently: when it cannot be delivered it
/// is left on the clipboard (whatever KeepInClipboard says) and the caller
/// tells the user. Until the user copies something else, that undelivered text
/// is protected: further undelivered dictations are appended to it, and later
/// dictations are typed rather than pasted so they never replace it.
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

    // The user's own clipboard text, captured before a paste that was refused
    // because a key went down. A retry restores this rather than re-reading the
    // clipboard, which may still hold the transcript.
    private (string ForText, string? Previous)? _savedPrevious;

    public TextInserter(ITextTarget target, Func<int, Task>? delay = null)
    {
        _target = target;
        _delay = delay ?? (ms => Task.Delay(ms));
    }

    public async Task<InsertResult> InsertAsync(string text, InsertOptions options, InsertionTarget target)
    {
        bool protect = RecoveryStillOnClipboard();
        // While an undelivered dictation waits on the clipboard, type: a paste
        // would replace it, and putting it back afterwards races with apps that
        // read the clipboard late.
        return options.TypeMode || protect
            ? await TypeAsync(text, options, target, protect).ConfigureAwait(false)
            : await PasteAsync(text, options, target).ConfigureAwait(false);
    }

    /// <summary>
    /// Insertion is impossible (keys held too long, input blocked, workstation
    /// locked since the dictation): leave the text on the clipboard. An earlier
    /// undelivered dictation still on the clipboard is kept and this one appended.
    /// </summary>
    public Task<InsertResult> KeepForRecoveryAsync(string text, RecoveryReason reason = RecoveryReason.NotDelivered) =>
        KeepForRecoveryAsync(text, RecoveryStillOnClipboard(), reason);

    /// <param name="appendToRecovery">Whether the protected text is still on the clipboard,
    /// decided before this insertion's own clipboard write (which changes the sequence number).</param>
    private async Task<InsertResult> KeepForRecoveryAsync(string text, bool appendToRecovery, RecoveryReason reason)
    {
        _savedPrevious = null;
        string combined = text;
        int count = 1;
        if (appendToRecovery && _recoveryText != null)
        {
            combined = Join(_recoveryText, text);
            count = _recoveryCount + 1;
        }
        if (!await _target.SetClipboardTextAsync(combined).ConfigureAwait(false))
            return new InsertResult(InsertOutcome.Lost, Reason: reason);

        _recoveryText = combined;
        _recoveryCount = count;
        _recoverySequence = _target.ClipboardSequence;
        return new InsertResult(InsertOutcome.CopiedOnly, OnClipboard: true, RecoveryCount: count, Reason: reason);
    }

    private async Task<InsertResult> TypeAsync(string text, InsertOptions options, InsertionTarget target, bool protect)
    {
        if (!target.Matches(_target.CurrentTarget()))
            return await KeepForRecoveryAsync(text, protect, RecoveryReason.TargetChanged).ConfigureAwait(false);

        switch (_target.TypeText(text))
        {
            case SendResult.KeysHeld:
                return new InsertResult(InsertOutcome.KeysHeld);
            case SendResult.Failed:
                return await KeepForRecoveryAsync(text, protect, RecoveryReason.NotDelivered).ConfigureAwait(false);
            case SendResult.Partial:
                return await KeepForRecoveryAsync(text, protect, RecoveryReason.PartlyTyped).ConfigureAwait(false);
        }

        if (protect) return new InsertResult(InsertOutcome.Inserted, Typed: true, EarlierTextKept: true);
        // Backup copy for re-pasting; the text is already typed, so a failure here is harmless.
        bool onClipboard = options.KeepInClipboard && await _target.SetClipboardTextAsync(text).ConfigureAwait(false);
        return new InsertResult(InsertOutcome.Inserted, Typed: true, OnClipboard: onClipboard);
    }

    private async Task<InsertResult> PasteAsync(string text, InsertOptions options, InsertionTarget target)
    {
        // With KeepInClipboard off, the user's previous clipboard text goes back afterwards.
        bool restoreAfter = !options.KeepInClipboard;
        string? previous = null;
        if (restoreAfter)
        {
            if (_savedPrevious is { } saved && saved.ForText == text)
            {
                previous = saved.Previous;
            }
            else
            {
                var (ok, current) = await _target.GetClipboardTextAsync().ConfigureAwait(false);
                previous = ok ? current : null; // non-text or unreadable: cleared afterwards
            }
            _savedPrevious = (text, previous);
        }

        if (!await _target.SetClipboardTextAsync(text).ConfigureAwait(false))
        {
            // Ctrl+V would paste whatever is on the clipboard now: type the text instead.
            if (!target.Matches(_target.CurrentTarget()))
                return await KeepForRecoveryAsync(text, false, RecoveryReason.TargetChanged).ConfigureAwait(false);
            switch (_target.TypeText(text))
            {
                case SendResult.KeysHeld:
                    return new InsertResult(InsertOutcome.KeysHeld);
                case SendResult.Failed:
                    return await KeepForRecoveryAsync(text, false, RecoveryReason.NotDelivered).ConfigureAwait(false);
                case SendResult.Partial:
                    return await KeepForRecoveryAsync(text, false, RecoveryReason.PartlyTyped).ConfigureAwait(false);
            }
            _savedPrevious = null;
            return new InsertResult(InsertOutcome.Inserted, Typed: true);
        }

        if (!target.Matches(_target.CurrentTarget()))
            return await KeepForRecoveryAsync(text, false, RecoveryReason.TargetChanged).ConfigureAwait(false);

        var sent = _target.SendPaste();
        if (sent == SendResult.KeysHeld)
        {
            // Nothing was pasted. Put the user's text back now; the retry uses the
            // saved copy even if this restore fails.
            if (restoreAfter) await RestoreAsync(previous).ConfigureAwait(false);
            return new InsertResult(InsertOutcome.KeysHeld);
        }
        if (sent != SendResult.Sent)
            return await KeepForRecoveryAsync(text, false, RecoveryReason.NotDelivered).ConfigureAwait(false);

        _savedPrevious = null;
        if (!restoreAfter)
            return new InsertResult(InsertOutcome.Inserted, OnClipboard: true); // stays for re-pasting

        // The target app reads the clipboard after it receives Ctrl+V, so wait
        // before putting the previous contents back.
        await _delay(Math.Max(100, options.ClipboardRestoreDelayMs)).ConfigureAwait(false);
        bool restored = await RestoreAsync(previous).ConfigureAwait(false);
        return new InsertResult(InsertOutcome.Inserted, OnClipboard: !restored, ClipboardNotRestored: !restored);
    }

    private Task<bool> RestoreAsync(string? previous) =>
        previous != null ? _target.SetClipboardTextAsync(previous) : _target.ClearClipboardAsync();

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
