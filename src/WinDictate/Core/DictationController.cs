using System.Diagnostics;
using System.IO;
using System.Threading.Channels;

namespace WinDictate.Core;

/// <summary>Timing knobs. The defaults are the app's; tests shorten them.</summary>
public sealed record DictationTimings
{
    /// <summary>The listening overlay waits this long, so accidental taps show nothing.</summary>
    public int OverlayDelayMs { get; init; } = 180;

    /// <summary>If the microphone is still not capturing by then, say it is opening.</summary>
    public int SlowMicNoticeMs { get; init; } = 700;

    /// <summary>When modifier keys stay held this long after a dictation, stop waiting
    /// and leave the text on the clipboard instead of inserting it.</summary>
    public int ModifierWaitLimitMs { get; init; } = 4000;

    /// <summary>How often a waiting insertion re-checks recording and key state.</summary>
    public int InsertPollMs { get; init; } = 25;

    /// <summary>Give up on a microphone that has still not opened this long after the
    /// hotkey was released, so one hung device cannot hold up later dictations.</summary>
    public int MicOpenTimeoutMs { get; init; } = 10_000;

    /// <summary>After the user is told that a dictation is waiting on the clipboard,
    /// later dictations leave the clipboard alone for this long (see <see cref="TextInserter"/>).</summary>
    public int RecoveryProtectMs { get; init; } = TextInserter.DefaultProtectMs;

    /// <summary>How long ordinary results stay up, and notices the user must read.</summary>
    public int FlashHoldMs { get; init; } = 700;
    public int NoticeHoldMs { get; init; } = 2500;
}

/// <summary>
/// Orchestrates dictation: hotkey held -> record -> (release) -> transcribe
/// locally -> optional cleanup -> insert.
///
/// Recording is decoupled from processing, so a new recording can start while
/// earlier ones are still being transcribed. Finished recordings enter a
/// two-stage queue (transcription, then insertion); each stage handles one
/// dictation at a time in the order spoken, so results are inserted in that
/// order while a later one can be transcribed as an earlier one waits. Nothing
/// is inserted while a recording is in progress or a modifier key is held
/// (Ctrl+V would become Ctrl+Alt+V), and nothing is inserted unless keyboard
/// focus is still in the window (and, where Windows exposes it, the control)
/// where the dictation was started. A transcript that cannot be inserted is
/// left on the clipboard and the overlay says so; none is dropped silently.
///
/// Threading: OnSessionStarted/OnSessionEnded come from the hotkey monitor,
/// usually on the keyboard-hook thread while the monitor's lock is held, so they
/// only record state and hand work to the thread pool. All slow work (opening
/// the microphone, Whisper, HTTP, clipboard, SendInput) runs on worker threads.
/// Overlay calls only post to the UI thread; they are made under this class's
/// lock so they reach the screen in the order decided.
/// </summary>
public sealed class DictationController : IDisposable
{
    /// <summary>Tray status text; raised on any thread.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>The recording time limit was reached for this hotkey session id;
    /// the app asks the hotkey monitor to stop it.</summary>
    public event Action<long>? MaxDurationReached;

    private const int OverlayFadeMs = 350; // the indicator's fade-out after a notice

    private readonly IDictationOverlay _overlay;
    private readonly IDictationPlatform _platform;
    private readonly ISpeechToText _speech;
    private readonly TextInserter _inserter;
    private readonly Func<string, CleanupSettings, Task<string>> _cleanup;
    private readonly DictationTimings _timings;
    private volatile AppSettings _settings; // private snapshot; replaced, never mutated

    private readonly object _lock = new();
    private Session? _recording;      // the session being recorded, if any
    private int _inFlight;            // ended sessions not yet fully processed
    private string? _pipelineStatus;  // latest status of the queue while it is busy
    private string? _deferredNotice;  // notice that arrived during a recording
    private bool _deferredNoticeIsRecovery; // ...and it is about text waiting on the clipboard
    private long _noticeUntil;        // a notice owns the overlay until this tick count
    private int _contextEpoch;        // bumped when the workstation locks or the system suspends
    private bool _locked;
    private bool _suspended;

    // Stage one receives each ended session's "finish recording" task in the
    // order spoken; stage two receives transcribed dictations in the same order.
    private readonly Channel<Task<Job?>> _recorded =
        Channel.CreateUnbounded<Task<Job?>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<Job> _transcribed =
        Channel.CreateUnbounded<Job>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _transcribeLoop;
    private readonly Task _insertLoop;

    public DictationController(
        AppSettings settings,
        IDictationOverlay overlay,
        IDictationPlatform platform,
        ISpeechToText speech,
        ITextTarget textTarget,
        Func<string, CleanupSettings, Task<string>>? cleanup = null,
        DictationTimings? timings = null)
    {
        _settings = settings.Clone();
        _overlay = overlay;
        _platform = platform;
        _speech = speech;
        _timings = timings ?? new DictationTimings();
        _inserter = new TextInserter(textTarget, protectMs: _timings.RecoveryProtectMs);
        _cleanup = cleanup ?? CleanupService.CleanAsync;
        _transcribeLoop = Task.Run(() => TranscribeLoopAsync(_shutdown.Token));
        _insertLoop = Task.Run(() => InsertLoopAsync(_shutdown.Token));
    }

    /// <summary>Starts loading the model in the background. Call after subscribing to <see cref="StatusChanged"/>.</summary>
    public void Start() => StartWarmUp();

    public void UpdateSettings(AppSettings settings)
    {
        _settings = settings.Clone();
        StartWarmUp();
    }

    /// <summary>
    /// The workstation locked or disconnected (true), or is back (false). While
    /// away, finished dictations wait; any dictation finished before the lock is
    /// put on the clipboard with a notice instead of being typed into whatever
    /// has focus afterwards.
    /// </summary>
    public void SetWorkstationLocked(bool locked)
    {
        lock (_lock)
        {
            if (locked && !_locked) _contextEpoch++;
            _locked = locked;
            // An unlock means the system is running, even if a resume event was missed.
            if (!locked) _suspended = false;
        }
    }

    /// <summary>The system is suspending (true) or has resumed (false); see <see cref="SetWorkstationLocked"/>.</summary>
    public void SetSuspended(bool suspended)
    {
        lock (_lock)
        {
            if (suspended && !_suspended) _contextEpoch++;
            _suspended = suspended;
        }
    }

    // ----- Model warm-up (thread pool: the UI thread must never wait for it) -----

    /// <summary>
    /// Downloads/loads the model on the thread pool. Overlapping calls queue on
    /// the transcriber's gate, so a model change is never skipped.
    /// </summary>
    private void StartWarmUp() => _ = Task.Run(WarmUpModelAsync);

    private async Task WarmUpModelAsync()
    {
        try
        {
            var progress = new InlineProgress(s => StatusChanged?.Invoke(s));
            await _speech.EnsureReadyAsync(_settings.Model, progress, _shutdown.Token).ConfigureAwait(false);
            if (IsIdle()) StatusChanged?.Invoke("Ready");
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Logger.Error("Model warm-up failed", ex);
            StatusChanged?.Invoke("Model not ready, check log");
        }
    }

    // ----- Hotkey held (hook thread, under the hotkey monitor's lock) -----

    public void OnSessionStarted(long hotkeyId)
    {
        var settings = _settings;
        var session = new Session(hotkeyId, settings, _platform.CurrentTarget());
        Session? stale;
        lock (_lock)
        {
            stale = _recording; // the monitor always ends a session before the next starts
            _recording = session;
            // Someone is dictating, so the system is awake and unlocked (the hotkey
            // cannot be seen on the lock screen) even if a resume or unlock event was
            // missed; otherwise every finished dictation would wait forever.
            _suspended = false;
            _locked = false;
        }
        if (stale != null) Abandon(stale);

        session.MicTask = Task.Run(() => OpenMicrophone(session));
        int maxMs = Math.Max(5, settings.MaxRecordSeconds) * 1000;
        session.MaxTimer = new System.Threading.Timer(_ => OnMaxDuration(session), null, maxMs, Timeout.Infinite);
        session.OverlayTimer = new System.Threading.Timer(_ => OnOverlayDue(session), null, _timings.OverlayDelayMs, Timeout.Infinite);
        session.SlowMicTimer = new System.Threading.Timer(_ => OnSlowMicrophone(session), null, _timings.SlowMicNoticeMs, Timeout.Infinite);
    }

    private IAudioCapture? OpenMicrophone(Session session)
    {
        IAudioCapture? recorder = null;
        try
        {
            recorder = _platform.CreateRecorder();
            recorder.LevelChanged += peak => OnLevel(session, peak);
            recorder.CaptureStarted += () => OnCaptureStarted(session);
            recorder.Start(_platform.ResolveMicrophone(session.Settings));
            return recorder; // stopped by FinishRecordingAsync (or Abandon), whenever the session ends
        }
        catch (Exception ex)
        {
            Logger.Error("Could not start microphone", ex);
            try { recorder?.Dispose(); } catch { }
            lock (_lock)
            {
                session.MicFailed = true;
                if (ReferenceEquals(_recording, session))
                {
                    session.MicFailureShown = true;
                    _overlay.Flash("No microphone", false, _timings.FlashHoldMs);
                }
            }
            return null;
        }
    }

    // "Listening…" appears only once the microphone really delivers audio (a
    // Bluetooth headset can take a second or more) and never for a quick tap.
    private void OnCaptureStarted(Session session)
    {
        lock (_lock)
        {
            if (session.Ended || session.CaptureStarted) return;
            session.CaptureStarted = true;
            ShowListeningIfDueLocked(session);
        }
        StatusChanged?.Invoke("Listening…");
    }

    private void OnOverlayDue(Session session)
    {
        lock (_lock)
        {
            if (session.Ended) return;
            session.OverlayDue = true;
            ShowListeningIfDueLocked(session);
        }
    }

    private void ShowListeningIfDueLocked(Session session)
    {
        if (!session.OverlayDue || !session.CaptureStarted || session.ListeningShown) return;
        session.ListeningShown = true;
        _overlay.ShowListening(session.Target.Window);
    }

    private void OnSlowMicrophone(Session session)
    {
        lock (_lock)
        {
            if (session.Ended || session.CaptureStarted || session.MicFailed) return;
            _overlay.ShowStatus("Opening microphone…");
        }
    }

    private void OnLevel(Session session, float peak)
    {
        if (session.ListeningShown && !session.Ended) _overlay.SetLevel(peak);
    }

    private void OnMaxDuration(Session session)
    {
        lock (_lock)
        {
            if (session.Ended) return;
        }
        Logger.Warn($"Recording hit the {session.Settings.MaxRecordSeconds}s limit, stopping");
        // The hotkey monitor ends the session (Stopped) and ignores the still-held
        // keys; its SessionEnded event then finishes this session as usual.
        MaxDurationReached?.Invoke(session.HotkeyId);
    }

    // ----- Hotkey released or cancelled (hook, watchdog, timer or UI thread) -----

    public void OnSessionEnded(long hotkeyId, SessionEndReason reason)
    {
        Session session;
        lock (_lock)
        {
            if (_recording == null || _recording.HotkeyId != hotkeyId) return;
            session = _recording;
            _recording = null;
            session.Ended = true;
            session.EndReason = reason;
            session.HeldMs = session.Held.ElapsedMilliseconds;
            session.CapturedBeforeEnd = session.CaptureStarted;
            session.Epoch = _contextEpoch;
            _inFlight++;
            // Queued here, under the lock, so the queue order is the order spoken.
            // The microphone is stopped straight away on the thread pool.
            _recorded.Writer.TryWrite(Task.Run(() => FinishRecordingAsync(session)));
            if (_deferredNotice != null)
            {
                ShowNoticeLocked(_deferredNotice, _deferredNoticeIsRecovery);
                _deferredNotice = null;
                _deferredNoticeIsRecovery = false;
            }
        }
        session.DisposeTimers();
    }

    private async Task<Job?> FinishRecordingAsync(Session session)
    {
        var settings = session.Settings;
        Task opened;
        using (var giveUp = new CancellationTokenSource())
        {
            opened = await Task.WhenAny(session.MicTask, Task.Delay(_timings.MicOpenTimeoutMs, giveUp.Token)).ConfigureAwait(false);
            giveUp.Cancel(); // the microphone answered (or not): stop the timer either way
        }
        if (opened != session.MicTask)
        {
            // A hung driver must not hold up every later dictation in the queue.
            Logger.Warn($"Microphone did not open within {_timings.MicOpenTimeoutMs / 1000} s; dictation discarded");
            CloseWhenOpened(session.MicTask);
            SessionFeedback("Microphone not responding");
            return null;
        }
        IAudioCapture? recorder;
        try { recorder = await session.MicTask.ConfigureAwait(false); }
        catch (Exception ex)
        {
            Logger.Error("Opening the microphone failed", ex);
            recorder = null;
        }

        bool micFailed, micFailureShown;
        lock (_lock)
        {
            micFailed = session.MicFailed;
            micFailureShown = session.MicFailureShown;
        }

        bool deliberate = session.HeldMs >= settings.MinHoldMs;
        if (recorder == null)
        {
            if (micFailed && !micFailureShown && deliberate) SessionFeedback("No microphone");
            else if (!micFailed) SessionDone();
            return null;
        }

        RecordingResult rec;
        try { rec = recorder.Stop(); }
        finally { recorder.Dispose(); }

        if (session.EndReason == SessionEndReason.KeysLost)
        {
            // The release never reached the hook (secure desktop, UAC prompt,
            // elevated window): nothing heard since then is meant for insertion.
            Logger.Warn("Dictation cancelled: the hotkey was no longer held but its release never arrived");
            SessionFeedback("Cancelled · hotkey interrupted");
            return null;
        }
        if (!session.CapturedBeforeEnd)
        {
            // Released before the microphone delivered any audio (a slow Bluetooth
            // headset): there is nothing to transcribe, so say why.
            if (deliberate && session.EndReason.KeepsAudio()) SessionFeedback("Microphone not ready");
            else SessionDone();
            return null;
        }
        if (session.EndReason == SessionEndReason.Cancelled || !deliberate)
        {
            // Quick taps and AltGr-style cancels vanish silently; a deliberate
            // cancel of a real recording gets feedback.
            if (session.EndReason == SessionEndReason.Cancelled && deliberate) SessionFeedback("Cancelled");
            else SessionDone();
            return null;
        }
        // Reject only clips without real voiced audio; a quiet microphone or a
        // long clip with pauses used to fail the old whole-clip average.
        if (rec.DurationSeconds < 0.3 || !SpeechGate.HasVoice(rec, settings))
        {
            SessionFeedback("No speech detected");
            return null;
        }

        PipelineStatus("Transcribing…");
        return new Job(rec, settings, session.Epoch, session.Target, session.StartedAt);
    }

    // ----- Stage one: transcription and cleanup, one at a time, in order -----

    private async Task TranscribeLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var finishing in _recorded.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                Job? job = null;
                try
                {
                    job = await finishing.ConfigureAwait(false);
                    if (job != null)
                    {
                        job.Text = await TranscribeAsync(job, ct).ConfigureAwait(false);
                        if (job.Text.Length == 0)
                        {
                            PipelineFlash("No speech detected", false);
                            job = null;
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Error("Dictation pipeline failed", ex);
                    PipelineNotice("Error, see log");
                    job = null;
                }

                if (job == null || !_transcribed.Writer.TryWrite(job))
                    ItemDone();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task<string> TranscribeAsync(Job job, CancellationToken ct)
    {
        if (!_speech.IsReady)
        {
            // First use (or every load so far failed). A model change is loaded by the
            // warm-up instead: if that fails, for example offline, dictation keeps
            // using the model that is already loaded rather than failing each time.
            PipelineStatus("Loading speech model…");
            await _speech.EnsureReadyAsync(_settings.Model, new InlineProgress(PipelineStatus), ct).ConfigureAwait(false);
        }
        PipelineStatus("Transcribing…");

        string text;
        using (var wav = new MemoryStream(job.Recording.WavBytes, writable: false))
            text = await _speech.TranscribeAsync(wav, job.Settings, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return "";

        var cleanup = job.Settings.Cleanup;
        if (cleanup.Enabled && !string.IsNullOrWhiteSpace(cleanup.BaseUrl))
        {
            PipelineStatus("Cleaning up…");
            text = await _cleanup(text, cleanup).ConfigureAwait(false);
        }
        return TranscriptText.ForInsertion(text, job.Settings.AddTrailingSpace);
    }

    // ----- Stage two: insertion, one at a time, in order -----

    private async Task InsertLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var job in _transcribed.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await DeliverAsync(job, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Error("Text insertion failed", ex);
                    await KeepForRecoveryAsync(job).ConfigureAwait(false);
                }
                finally
                {
                    ItemDone();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task DeliverAsync(Job job, CancellationToken ct)
    {
        Stopwatch? keysHeld = null;
        while (true)
        {
            var readiness = CheckReadiness(job);
            if (readiness == Readiness.Ready)
            {
                PipelineStatus("Inserting…");
                var result = await _inserter.InsertAsync(job.Text, InsertOptions.From(job.Settings), job.Target, job.StartedAt).ConfigureAwait(false);
                if (result.Outcome != InsertOutcome.KeysHeld)
                {
                    Report(job, result);
                    return;
                }
                readiness = Readiness.KeysHeld; // a key went down just before sending: wait again
            }

            if (readiness == Readiness.ContextChanged)
            {
                Logger.Info("Workstation locked or suspended since this dictation: keeping it on the clipboard instead of inserting");
                await KeepForRecoveryAsync(job).ConfigureAwait(false);
                return;
            }

            if (readiness == Readiness.KeysHeld)
            {
                keysHeld ??= Stopwatch.StartNew();
                if (keysHeld.ElapsedMilliseconds >= _timings.ModifierWaitLimitMs)
                {
                    Logger.Warn("Modifier keys stayed held after a dictation: keeping it on the clipboard instead of inserting");
                    await KeepForRecoveryAsync(job).ConfigureAwait(false);
                    return;
                }
            }
            else
            {
                keysHeld = null; // recording or away: the user is busy, so no deadline
            }

            await Task.Delay(_timings.InsertPollMs, ct).ConfigureAwait(false);
        }
    }

    private Readiness CheckReadiness(Job job)
    {
        lock (_lock)
        {
            if (_locked || _suspended) return Readiness.Away;
            if (job.Epoch != _contextEpoch) return Readiness.ContextChanged;
            if (_recording != null) return Readiness.Recording;
        }
        return _platform.ModifierKeysHeld() ? Readiness.KeysHeld : Readiness.Ready;
    }

    private async Task KeepForRecoveryAsync(Job job)
    {
        InsertResult result;
        try { result = await _inserter.KeepForRecoveryAsync(job.Text, startedAt: job.StartedAt).ConfigureAwait(false); }
        catch (Exception ex)
        {
            Logger.Error("Could not leave the dictation on the clipboard", ex);
            result = new InsertResult(InsertOutcome.Lost);
        }
        Report(job, result);
    }

    private void Report(Job job, InsertResult result)
    {
        int words = job.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        string counted = words == 1 ? "1 word" : $"{words} words";
        double seconds = job.Recording.DurationSeconds;
        if (job.Settings.LogTranscripts) AppendHistory(job.Text, seconds);

        switch (result.Outcome)
        {
            case InsertOutcome.Inserted:
                bool typedInstead = result.Typed && !InsertOptions.From(job.Settings).TypeMode;
                Logger.Info($"Dictated {seconds:F1}s of audio into {words} words" +
                            (typedInstead ? " (typed instead of pasted)" : "") +
                            (result.ClipboardNotRestored ? "; the previous clipboard could not be restored" : ""));
                PipelineFlash(result.EarlierTextKept ? $"Inserted {counted} · earlier dictation still in clipboard"
                            : result.ClipboardNotRestored ? $"Inserted {counted} · clipboard not restored"
                            : result.OnClipboard ? $"Inserted {counted} · in clipboard"
                            : $"Inserted {counted}", true);
                break;
            case InsertOutcome.CopiedOnly:
                Logger.Warn($"Could not insert {words} words ({result.Reason}); left them on the clipboard");
                bool several = result.RecoveryCount > 1; // the clipboard also holds earlier undelivered text
                string held = several ? $"{result.RecoveryCount} dictations" : counted;
                string whole = several ? held : "full text";
                PipelineNotice(result.Reason switch
                {
                    RecoveryReason.TargetChanged => $"Focus moved · {held} in clipboard — press Ctrl+V",
                    RecoveryReason.PartlyTyped => $"Partly typed · {whole} in clipboard — check, then Ctrl+V",
                    RecoveryReason.MaybePasted => $"May have been inserted · {held} in clipboard — check, then Ctrl+V",
                    _ => $"Not inserted · {held} in clipboard — press Ctrl+V"
                }, recovery: true);
                break;
            default:
                Logger.Error("Could not insert or copy a dictation");
                PipelineNotice("Couldn't insert or copy — please dictate again");
                break;
        }
    }

    private static void AppendHistory(string text, double seconds)
    {
        try
        {
            var line = $"{DateTime.Now:o}\t{seconds:F1}s\t{text.Trim().Replace('\t', ' ').Replace('\n', ' ')}";
            File.AppendAllText(Path.Combine(AppSettings.DataDir, "transcription-history.tsv"), line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not write history: " + ex.Message);
        }
    }

    private void ItemDone()
    {
        bool idle;
        lock (_lock)
        {
            _inFlight = Math.Max(0, _inFlight - 1);
            idle = _inFlight == 0;
            if (idle) _pipelineStatus = null;
            idle &= _recording == null;
        }
        if (idle) StatusChanged?.Invoke("Ready");
    }

    private bool IsIdle()
    {
        lock (_lock) return _recording == null && _inFlight == 0;
    }

    // ----- Overlay arbitration -----
    // A recording owns the overlay. Results of earlier dictations wait their
    // turn: statuses and ordinary results are skipped while a recording or a
    // notice is showing; a notice (something the user must act on) that arrives
    // during a recording is shown as soon as that recording ends.

    private bool NoticeShowingLocked => Environment.TickCount64 < _noticeUntil;

    private void PipelineStatus(string text)
    {
        bool tray;
        lock (_lock)
        {
            _pipelineStatus = text;
            tray = _recording == null;
            if (tray && !NoticeShowingLocked) _overlay.ShowStatus(text);
        }
        if (tray) StatusChanged?.Invoke(text);
    }

    private void PipelineFlash(string text, bool success)
    {
        lock (_lock)
        {
            if (_recording != null || NoticeShowingLocked) return;
            _overlay.Flash(text, success, _timings.FlashHoldMs);
        }
    }

    /// <param name="recovery">The notice says a dictation is waiting on the clipboard.</param>
    private void PipelineNotice(string text, bool recovery = false)
    {
        lock (_lock)
        {
            if (_recording != null)
            {
                // Only one notice is kept for when the recording ends. Text waiting on
                // the clipboard is the one the user must act on, so another problem
                // does not replace it.
                if (recovery || !_deferredNoticeIsRecovery)
                {
                    _deferredNotice = text;
                    _deferredNoticeIsRecovery = recovery;
                }
                return;
            }
            ShowNoticeLocked(text, recovery);
        }
    }

    private void ShowNoticeLocked(string text, bool recovery)
    {
        _overlay.Flash(text, false, _timings.NoticeHoldMs);
        _noticeUntil = Environment.TickCount64 + _timings.NoticeHoldMs + OverlayFadeMs;
        // The user's time to paste the waiting text starts when they are told.
        if (recovery) _inserter.RecoveryAnnounced();
    }

    /// <summary>Feedback about a session that just ended; dropped if a newer recording already owns the overlay.</summary>
    private void SessionFeedback(string text)
    {
        lock (_lock)
        {
            if (_recording != null || NoticeShowingLocked) return;
            _overlay.Flash(text, false, _timings.FlashHoldMs);
        }
    }

    /// <summary>A session ended with nothing to say (quick tap): go back to the queue's status, or hide.</summary>
    private void SessionDone()
    {
        lock (_lock)
        {
            if (_recording != null || NoticeShowingLocked) return;
            if (_pipelineStatus != null) _overlay.ShowStatus(_pipelineStatus);
            else _overlay.Hide();
        }
    }

    private void Abandon(Session session)
    {
        lock (_lock)
        {
            session.Ended = true;
            session.EndReason = SessionEndReason.Cancelled;
        }
        session.DisposeTimers();
        CloseWhenOpened(session.MicTask);
    }

    /// <summary>Stops and frees a microphone whenever its (possibly slow) open finishes.</summary>
    private static void CloseWhenOpened(Task<IAudioCapture?> micTask) =>
        _ = micTask.ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion || t.Result == null) return;
            try { t.Result.Stop(); } catch { }
            t.Result.Dispose();
        }, TaskScheduler.Default);

    public void Dispose()
    {
        Session? session;
        int unfinished;
        lock (_lock)
        {
            session = _recording;
            _recording = null;
            unfinished = _inFlight;
        }
        if (session != null) Abandon(session);
        if (unfinished > 0)
            Logger.Warn($"Quitting with {unfinished} dictation(s) not yet inserted; they are discarded");

        _shutdown.Cancel();
        _recorded.Writer.TryComplete();
        _transcribed.Writer.TryComplete();
        bool stopped;
        try { stopped = Task.WaitAll(new[] { _transcribeLoop, _insertLoop }, TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { stopped = true; }
        // Free the model only when no transcription can still be using it.
        if (stopped) _speech.Dispose();
    }

    private enum Readiness { Ready, Recording, KeysHeld, Away, ContextChanged }

    private sealed class Session
    {
        public Session(long hotkeyId, AppSettings settings, InsertionTarget target)
        {
            HotkeyId = hotkeyId;
            Settings = settings;
            Target = target;
        }

        public long HotkeyId { get; }
        public AppSettings Settings { get; }
        public InsertionTarget Target { get; } // where the user was typing when the hotkey went down
        public long StartedAt { get; } = Environment.TickCount64; // the text inserter's clock
        public Stopwatch Held { get; } = Stopwatch.StartNew();
        public Task<IAudioCapture?> MicTask { get; set; } = Task.FromResult<IAudioCapture?>(null);
        public System.Threading.Timer? MaxTimer { get; set; }
        public System.Threading.Timer? OverlayTimer { get; set; }
        public System.Threading.Timer? SlowMicTimer { get; set; }

        // Guarded by the controller's lock (OnLevel reads two of them without it).
        public volatile bool Ended;
        public volatile bool ListeningShown;
        public bool CaptureStarted;
        public bool CapturedBeforeEnd;
        public bool OverlayDue;
        public bool MicFailed;
        public bool MicFailureShown;
        public SessionEndReason EndReason;
        public long HeldMs;
        public int Epoch;

        public void DisposeTimers()
        {
            MaxTimer?.Dispose();
            OverlayTimer?.Dispose();
            SlowMicTimer?.Dispose();
        }
    }

    private sealed class Job
    {
        public Job(RecordingResult recording, AppSettings settings, int epoch, InsertionTarget target, long startedAt)
        {
            Recording = recording;
            Settings = settings;
            Epoch = epoch;
            Target = target;
            StartedAt = startedAt;
        }

        public RecordingResult Recording { get; }
        public AppSettings Settings { get; }
        public int Epoch { get; }
        public InsertionTarget Target { get; }
        public long StartedAt { get; } // when the hotkey went down (Environment.TickCount64)
        public string Text { get; set; } = "";
    }

    /// <summary>IProgress that reports on the calling thread (Progress&lt;T&gt; would post, reordering updates).</summary>
    private sealed class InlineProgress : IProgress<string>
    {
        private readonly Action<string> _report;
        public InlineProgress(Action<string> report) => _report = report;
        public void Report(string value) => _report(value);
    }
}
