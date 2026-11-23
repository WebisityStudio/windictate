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
    PartlyTyped,   // Windows stopped accepting keystrokes part-way through
    MaybePasted,   // Windows took only part of Ctrl+V: the paste may or may not have happened
    Requested      // the user asked for the last dictation to be copied
}

public sealed record InsertResult(
    InsertOutcome Outcome,
    bool Typed = false,                 // delivered as keystrokes (type mode, or instead of pasting)
    bool OnClipboard = false,           // this text is on the clipboard afterwards
    bool EarlierTextKept = false,       // an earlier undelivered dictation stays on the clipboard instead
    int RecoveryCount = 0,              // CopiedOnly: how many dictations the clipboard now holds
    RecoveryReason Reason = RecoveryReason.None,
    bool ClipboardNotRestored = false,  // putting the user's clipboard back failed
    bool TypedToSpareClipboard = false); // typed because the clipboard held something that could not be put back exactly

public enum SendResult
{
    Sent,
    KeysHeld, // a modifier key is physically down: nothing was sent
    Partial,  // only part of the input was accepted
    Failed    // Windows did not accept the input (for example a secure desktop is active)
}

/// <summary>
/// What was on the clipboard. <see cref="OnlyText"/> is true when it held plain
/// text and nothing else (or nothing at all), which is the only case in which
/// WinDictate can put it back exactly.
/// </summary>
public sealed record ClipboardSnapshot(bool Readable, string? Text, bool OnlyText)
{
    public static readonly ClipboardSnapshot Unreadable = new(false, null, false);

    /// <summary>True when putting this back would restore the clipboard exactly.</summary>
    public bool CanRestore => Readable && OnlyText;
}

/// <summary>Operating-system primitives for inserting text (Windows: WindowsTextTarget).
/// Clipboard calls may take a while; they are only made from worker threads.</summary>
public interface ITextTarget
{
    /// <summary>Puts text on the clipboard, marked so that Windows clipboard history
    /// and cloud clipboard leave it out.</summary>
    Task<bool> SetClipboardTextAsync(string text);

    /// <summary>Reads the clipboard without changing it.</summary>
    Task<ClipboardSnapshot> ReadClipboardAsync();

    Task<bool> ClearClipboardAsync();

    /// <summary>Changes whenever anyone changes the clipboard.</summary>
    long ClipboardSequence { get; }

    /// <summary>Where keyboard input would go right now.</summary>
    InsertionTarget CurrentTarget();

    /// <summary>Ctrl+V into the focused app, unless a modifier key is held. Partial means
    /// the V key went down but not every key event was accepted, so the paste has
    /// probably, but not certainly, happened.</summary>
    SendResult SendPaste();

    /// <summary>Types the text as Unicode keystrokes, unless a modifier key is held.</summary>
    SendResult TypeText(string text);
}

public sealed record InsertOptions(bool TypeMode, int ClipboardRestoreDelayMs)
{
    public static InsertOptions From(AppSettings settings) => new(
        string.Equals(settings.InsertMode, "type", StringComparison.OrdinalIgnoreCase),
        settings.ClipboardRestoreDelayMs);
}

/// <summary>
/// Inserts transcripts into the focused application without leaving them
/// behind.
///
/// "paste" mode (default): the user's clipboard is read, the transcript is put
/// on the clipboard, Ctrl+V is sent, and the user's clipboard is put back. This
/// only works exactly for plain text, so when the clipboard holds anything else
/// (a picture, files, formatted text from Word or a web page) or cannot be read,
/// the transcript is typed instead and the clipboard is not touched at all. If
/// the clipboard cannot be set, the transcript is typed as well (never paste
/// whatever was there). "type" mode: synthesised Unicode key events; the
/// clipboard is never used. Clipboard contents are held in memory only and never
/// logged; everything WinDictate puts on the clipboard is marked so that Windows
/// clipboard history and cloud clipboard leave it out.
///
/// Nothing is sent unless keyboard focus is still where the dictation was
/// started. No transcript is dropped silently: when it cannot be delivered it
/// is left on the clipboard and the caller tells the user.
///
/// That undelivered text is protected for a short while, so the user has time
/// to paste it: further undelivered dictations are appended to it, and
/// dictations that do go in are typed rather than pasted. The protection covers
/// dictations spoken before the user was told, and anything inserted in the
/// half minute after; it ends as soon as the user copies something else.
/// After <see cref="DefaultClearMs"/> the undelivered text is removed from the
/// clipboard (what was there before is put back if it was plain text), unless
/// the user has copied something else in the meantime.
///
/// Calls are serialised internally (<see cref="RecoveryAnnounced"/> may be called
/// from any thread at any time).
/// </summary>
public sealed class TextInserter
{
    /// <summary>How long undelivered text on the clipboard is protected after the user is told about it.</summary>
    public const int DefaultProtectMs = 30_000;

    /// <summary>How long undelivered text stays on the clipboard after the user is told about it.</summary>
    public const int DefaultClearMs = 120_000;

    private readonly ITextTarget _target;
    private readonly Func<int, Task> _delay;
    private readonly int _protectMs;
    private readonly int _clearMs;
    private readonly Func<long> _clock; // milliseconds; the timeline "startedAt" values are on
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _recoveryText;  // undelivered dictation(s) we left on the clipboard
    private long _recoverySequence; // clipboard sequence number while our recovery text is on it
    private int _recoveryCount;
    private long _recoveredAt;      // when the latest undelivered dictation went to the clipboard
    private long _announcedAt = long.MinValue; // when the user was last told about it (any thread)
    private ClipboardSnapshot? _beforeRecovery; // what the clipboard held before the recovery text, if it can be put back
    private bool _protected;        // later dictations still leave the recovery text alone

    // The user's own clipboard, captured before a paste that was refused because a
    // key went down. A retry restores this if the clipboard still holds the
    // transcript, rather than mistaking the transcript for the user's content.
    private (string ForText, ClipboardSnapshot Previous)? _savedPrevious;

    public TextInserter(ITextTarget target, Func<int, Task>? delay = null,
        int protectMs = DefaultProtectMs, int clearMs = DefaultClearMs, Func<long>? clock = null)
    {
        _target = target;
        _delay = delay ?? (ms => Task.Delay(ms));
        _protectMs = Math.Max(0, protectMs);
        _clearMs = Math.Max(0, clearMs);
        _clock = clock ?? (() => Environment.TickCount64);
    }

    /// <param name="startedAt">When this dictation was started (hotkey down), on the
    /// inserter's clock. Leave out for "just now".</param>
    public async Task<InsertResult> InsertAsync(string text, InsertOptions options, InsertionTarget target,
        long startedAt = long.MaxValue)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            bool protect = RecoveryIsProtected(startedAt);
            // While an undelivered dictation waits on the clipboard, type: a paste
            // would briefly replace it, and putting it back races with apps that
            // read the clipboard late.
            return options.TypeMode || protect
                ? await TypeAsync(text, target, protect, spareClipboard: false).ConfigureAwait(false)
                : await PasteAsync(text, options, target).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Insertion is impossible (keys held too long, input blocked, workstation
    /// locked since the dictation): leave the text on the clipboard. An earlier
    /// undelivered dictation that is still protected there is kept and this one appended.
    /// </summary>
    public async Task<InsertResult> KeepForRecoveryAsync(string text, RecoveryReason reason = RecoveryReason.NotDelivered,
        long startedAt = long.MaxValue)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            bool append = RecoveryIsProtected(startedAt);
            ClipboardSnapshot? before = append ? null : await _target.ReadClipboardAsync().ConfigureAwait(false);
            return await KeepForRecoveryAsync(text, append, reason, before).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Puts <paramref name="text"/> on the clipboard because the user asked for it
    /// (the last dictation, from the tray menu). It is treated like undelivered
    /// text: protected briefly, then removed after <see cref="DefaultClearMs"/>.
    /// </summary>
    public async Task<bool> OfferAsync(string text)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ClipboardSnapshot before = await _target.ReadClipboardAsync().ConfigureAwait(false);
            var result = await KeepForRecoveryAsync(text, false, RecoveryReason.Requested, before).ConfigureAwait(false);
            if (result.Outcome != InsertOutcome.CopiedOnly) return false;
            RecoveryAnnounced();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The user has just been shown the notice about text waiting on the
    /// clipboard: their time to paste it starts now. (The notice can come later
    /// than the failure, because it waits for a recording in progress to end.)
    /// </summary>
    public void RecoveryAnnounced() => Volatile.Write(ref _announcedAt, _clock());

    /// <summary>
    /// Removes undelivered text from the clipboard once its time is up, putting
    /// back what was there before when that was plain text. Does nothing if the
    /// user has copied something since, or if the time is not up yet.
    /// </summary>
    /// <returns>True when text was removed.</returns>
    public Task<bool> ClearExpiredRecoveryAsync() => ClearRecoveryAsync(onlyIfExpired: true);

    /// <summary>As <see cref="ClearExpiredRecoveryAsync"/>, without waiting for the time to be up (when WinDictate exits).</summary>
    public Task<bool> ClearRecoveryNowAsync() => ClearRecoveryAsync(onlyIfExpired: false);

    private async Task<bool> ClearRecoveryAsync(bool onlyIfExpired)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_recoveryText == null) return false;
            if (!IsOurRecoveryOnClipboard())
            {
                ForgetRecovery(); // the user has copied something since: theirs now
                return false;
            }
            long told = Math.Max(_recoveredAt, Volatile.Read(ref _announcedAt));
            if (onlyIfExpired && _clock() - told < _clearMs) return false;

            ClipboardSnapshot? before = _beforeRecovery;
            ForgetRecovery();
            bool done = before is { CanRestore: true, Text: not null }
                ? await _target.SetClipboardTextAsync(before.Text).ConfigureAwait(false)
                : await _target.ClearClipboardAsync().ConfigureAwait(false);
            if (!done) Logger.Warn("Could not remove an undelivered dictation from the clipboard");
            return done;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <param name="appendToRecovery">Whether the protected text is still on the clipboard,
    /// decided before this insertion's own clipboard write (which changes the sequence number).</param>
    /// <param name="before">What the clipboard held before this insertion touched it, if known.</param>
    private async Task<InsertResult> KeepForRecoveryAsync(string text, bool appendToRecovery, RecoveryReason reason,
        ClipboardSnapshot? before)
    {
        _savedPrevious = null;
        string combined = text;
        int count = 1;
        ClipboardSnapshot? restoreLater = before;
        if (appendToRecovery && _recoveryText != null)
        {
            combined = Join(_recoveryText, text);
            count = _recoveryCount + 1;
            restoreLater = _beforeRecovery;
        }
        else if (before != null && _recoveryText != null && IsOurRecoveryOnClipboard())
        {
            // Replacing our own older recovery text: what goes back later is what preceded it.
            restoreLater = _beforeRecovery;
        }

        if (!await _target.SetClipboardTextAsync(combined).ConfigureAwait(false))
            return new InsertResult(InsertOutcome.Lost, Reason: reason);

        _recoveryText = combined;
        _recoveryCount = count;
        _recoverySequence = _target.ClipboardSequence;
        _recoveredAt = _clock();
        _beforeRecovery = restoreLater is { CanRestore: true } ? restoreLater : null;
        _protected = true;
        return new InsertResult(InsertOutcome.CopiedOnly, OnClipboard: true, RecoveryCount: count, Reason: reason);
    }

    private async Task<InsertResult> TypeAsync(string text, InsertionTarget target, bool protect, bool spareClipboard)
    {
        if (!target.Matches(_target.CurrentTarget()))
            return await KeepForRecoveryAsync(text, protect, RecoveryReason.TargetChanged, await BeforeAsync(protect)).ConfigureAwait(false);

        switch (_target.TypeText(text))
        {
            case SendResult.KeysHeld:
                return new InsertResult(InsertOutcome.KeysHeld);
            case SendResult.Failed:
                return await KeepForRecoveryAsync(text, protect, RecoveryReason.NotDelivered, await BeforeAsync(protect)).ConfigureAwait(false);
            case SendResult.Partial:
                return await KeepForRecoveryAsync(text, protect, RecoveryReason.PartlyTyped, await BeforeAsync(protect)).ConfigureAwait(false);
        }

        _savedPrevious = null;
        return new InsertResult(InsertOutcome.Inserted, Typed: true, EarlierTextKept: protect,
            TypedToSpareClipboard: spareClipboard);
    }

    /// <summary>The clipboard before a recovery write, unless the recovery text is appended to.</summary>
    private async Task<ClipboardSnapshot?> BeforeAsync(bool appending) =>
        appending ? null : await _target.ReadClipboardAsync().ConfigureAwait(false);

    private async Task<InsertResult> PasteAsync(string text, InsertOptions options, InsertionTarget target)
    {
        ClipboardSnapshot current = await _target.ReadClipboardAsync().ConfigureAwait(false);
        ClipboardSnapshot previous = current;
        // A retry after a key was held: if the clipboard still holds the transcript
        // (putting the user's content back failed) or cannot be read, the user's
        // content is the copy saved the first time. If it holds anything else, the
        // user has copied something new since, and that is what goes back.
        if (_savedPrevious is { } saved && saved.ForText == text && (!current.Readable || current.Text == text))
            previous = saved.Previous;
        _savedPrevious = (text, previous);

        if (!previous.CanRestore)
        {
            // Pasting would lose what is on the clipboard (or it cannot even be read):
            // type the text instead and leave the clipboard exactly as it is.
            return await TypeAsync(text, target, protect: false, spareClipboard: true).ConfigureAwait(false);
        }

        bool recoveryUnderneath = IsOurRecoveryOnClipboard();
        // If this paste fails, what goes back on the clipboard later is the user's own
        // content: when older undelivered text is underneath, what preceded that.
        ClipboardSnapshot? usersContent = recoveryUnderneath ? _beforeRecovery : previous;
        if (!await _target.SetClipboardTextAsync(text).ConfigureAwait(false))
        {
            // Ctrl+V would paste whatever is on the clipboard now: type the text instead.
            return await TypeAsync(text, target, protect: false, spareClipboard: false).ConfigureAwait(false);
        }
        long ours = _target.ClipboardSequence;

        if (!target.Matches(_target.CurrentTarget()))
            return await KeepForRecoveryAsync(text, false, RecoveryReason.TargetChanged, usersContent).ConfigureAwait(false);

        var sent = _target.SendPaste();
        if (sent == SendResult.KeysHeld)
        {
            // Nothing was pasted. Put the user's content back now; if this restore
            // fails, the retry still has the saved copy.
            await RestoreAsync(previous, ours, text, recoveryUnderneath).ConfigureAwait(false);
            return new InsertResult(InsertOutcome.KeysHeld);
        }
        if (sent == SendResult.Partial)
            return await KeepForRecoveryAsync(text, false, RecoveryReason.MaybePasted, usersContent).ConfigureAwait(false);
        if (sent != SendResult.Sent)
            return await KeepForRecoveryAsync(text, false, RecoveryReason.NotDelivered, usersContent).ConfigureAwait(false);

        _savedPrevious = null;
        // The target app reads the clipboard after it receives Ctrl+V, so wait
        // before putting the previous contents back.
        await _delay(Math.Max(100, options.ClipboardRestoreDelayMs)).ConfigureAwait(false);
        bool restored = await RestoreAsync(previous, ours, text, recoveryUnderneath).ConfigureAwait(false);
        if (!restored && _target.ClipboardSequence == ours)
        {
            // The transcript is still on the clipboard. Treat it like undelivered
            // text so that it is removed (and the user's text put back) when its
            // time is up, but do not make later dictations work around it.
            _recoveryText = text;
            _recoveryCount = 1;
            _recoverySequence = ours;
            _recoveredAt = _clock();
            _beforeRecovery = usersContent;
            _protected = false;
        }
        return new InsertResult(InsertOutcome.Inserted, OnClipboard: !restored, ClipboardNotRestored: !restored);
    }

    /// <summary>
    /// Puts back what was on the clipboard before <paramref name="text"/> was put
    /// there, unless someone else has changed the clipboard in the meantime.
    /// </summary>
    /// <returns>False when the transcript is still on the clipboard afterwards.</returns>
    private async Task<bool> RestoreAsync(ClipboardSnapshot previous, long ours, string text, bool recoveryUnderneath)
    {
        if (_target.ClipboardSequence != ours)
        {
            // Changed since our write. If it is no longer our transcript, it is
            // someone else's content: leave it.
            ClipboardSnapshot now = await _target.ReadClipboardAsync().ConfigureAwait(false);
            if (!now.Readable || now.Text != text) return true;
        }
        bool restored = previous.Text != null
            ? await _target.SetClipboardTextAsync(previous.Text).ConfigureAwait(false)
            : await _target.ClearClipboardAsync().ConfigureAwait(false);
        if (recoveryUnderneath)
        {
            // The undelivered text is back where it was (or lost, if the restore failed).
            if (restored) _recoverySequence = _target.ClipboardSequence;
            else ForgetRecovery();
        }
        return restored;
    }

    private bool IsOurRecoveryOnClipboard() => _recoveryText != null && _target.ClipboardSequence == _recoverySequence;

    /// <summary>
    /// Whether undelivered text is on the clipboard and must not be replaced by a
    /// dictation started at <paramref name="startedAt"/> that is being handled now.
    /// </summary>
    private bool RecoveryIsProtected(long startedAt)
    {
        if (_recoveryText == null) return false;
        if (!IsOurRecoveryOnClipboard())
        {
            ForgetRecovery(); // the user has copied something since: they have dealt with it
            return false;
        }
        if (!_protected) return false;
        long told = Math.Max(_recoveredAt, Volatile.Read(ref _announcedAt));
        // Spoken before the user was told: they have had no chance to paste yet,
        // however long this dictation waited in the queue.
        if (startedAt <= told) return true;
        if (_clock() - told < _protectMs) return true;
        // The user has had their time to paste it. The text stays on the clipboard
        // until it is cleared, but later dictations no longer work around it.
        _protected = false;
        return false;
    }

    private void ForgetRecovery()
    {
        _recoveryText = null;
        _recoveryCount = 0;
        _beforeRecovery = null;
        _protected = false;
    }

    private static string Join(string earlier, string next) =>
        earlier.Length == 0 || char.IsWhiteSpace(earlier[^1]) ? earlier + next : earlier + " " + next;
}
