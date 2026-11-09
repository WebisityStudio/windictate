using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using WinDictate.UI;

namespace WinDictate.Core;

/// <summary>
/// Orchestrates one dictation session at a time:
/// hotkey held -> record -> (release) -> transcribe locally -> optional cleanup -> insert.
/// The hotkey callbacks arrive on the keyboard-hook thread and must return
/// immediately, so everything slow (opening the microphone, Whisper, HTTP,
/// SendInput) runs on worker threads.
/// </summary>
public sealed class DictationController : IDisposable
{
    public event Action<string>? StatusChanged;  // tray tooltip / icon
    public event Action? MaxDurationReached;     // lets the app reset the hotkey monitor

    private readonly Dispatcher _dispatcher;
    private readonly OverlayWindow _overlay;
    private readonly Transcriber _transcriber = new();
    private AppSettings _settings;

    private readonly object _sync = new();
    private Session? _current;
    private bool _busy;        // a transcription pipeline is running

    private sealed class Session
    {
        public readonly Stopwatch Held = Stopwatch.StartNew();
        public Task<AudioRecorder?> StartTask = Task.FromResult<AudioRecorder?>(null);
        public readonly CancellationTokenSource OverlayDelay = new();
        public System.Threading.Timer? MaxTimer;
        public bool Ended;
    }

    public DictationController(Dispatcher dispatcher, OverlayWindow overlay, AppSettings settings)
    {
        _dispatcher = dispatcher;
        _overlay = overlay;
        _settings = settings;
        StartWarmUp();
    }

    public void UpdateSettings(AppSettings settings)
    {
        _settings = settings;
        StartWarmUp();
    }

    /// <summary>
    /// Downloads/loads the model on the thread pool. Called from the UI thread,
    /// which must never wait for the multi-second model load. Overlapping calls
    /// queue on the transcriber's gate, so a model change is never skipped.
    /// </summary>
    private void StartWarmUp() => _ = Task.Run(WarmUpModelAsync);

    private async Task WarmUpModelAsync()
    {
        try
        {
            var progress = new Progress<string>(s => StatusChanged?.Invoke(s));
            await _transcriber.EnsureReadyAsync(_settings.Model, progress);
            StatusChanged?.Invoke("Ready");
        }
        catch (Exception ex)
        {
            Logger.Error("Model warm-up failed", ex);
            StatusChanged?.Invoke("Model not ready, check log");
        }
    }

    // ----- Hotkey held (hook thread: flip state, defer the real work) -----

    public void OnSessionStarted()
    {
        Session session;
        lock (_sync)
        {
            if (_busy || _current != null) return;
            session = new Session();
            _current = session;
        }

        // Runs on the keyboard-hook thread: only grab the handle here. The title
        // is read on the UI thread (GetWindowText may send WM_GETTEXT).
        IntPtr target = NativeMethods.GetForegroundWindow();

        session.StartTask = Task.Run<AudioRecorder?>(() =>
        {
            var recorder = new AudioRecorder();
            recorder.LevelChanged += OnLevel;
            try
            {
                recorder.Start(AudioRecorder.ResolveDevice(_settings));
            }
            catch (Exception ex)
            {
                Logger.Error("Could not start microphone", ex);
                recorder.Dispose();
                session.OverlayDelay.Cancel();
                _dispatcher.BeginInvoke(() => _overlay.Flash("No microphone", false));
                return null;
            }
            lock (_sync)
            {
                if (!session.Ended) return recorder;
            }
            // Hotkey already released before the device finished opening.
            try { recorder.Stop(); } catch { }
            recorder.Dispose();
            return null;
        });

        int maxMs = Math.Max(5, _settings.MaxRecordSeconds) * 1000;
        session.MaxTimer = new System.Threading.Timer(_ => OnMaxDuration(session), null, maxMs, Timeout.Infinite);

        StatusChanged?.Invoke("Listening…");

        // Delay the overlay slightly so accidental taps show nothing.
        var token = session.OverlayDelay.Token;
        _dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await Task.Delay(180, token);
                _overlay.ShowListening(NativeMethods.GetWindowTitle(target));
            }
            catch (TaskCanceledException) { }
        });
    }

    private void OnMaxDuration(Session session)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_current, session) || session.Ended) return;
        }
        Logger.Warn($"Recording hit the {_settings.MaxRecordSeconds}s limit, stopping");
        MaxDurationReached?.Invoke(); // monitor forgets the held combo
        OnSessionEnded(false);
    }

    private void OnLevel(float peak) => _dispatcher.BeginInvoke(() => _overlay.SetLevel(peak));

    // ----- Hotkey released or cancelled (hook thread or max-duration timer) -----

    public void OnSessionEnded(bool cancelled)
    {
        Session? session;
        lock (_sync)
        {
            session = _current;
            _current = null;
            if (session == null) return;
            session.Ended = true;
            _busy = true;
        }
        session.OverlayDelay.Cancel();
        session.MaxTimer?.Dispose();
        long heldMs = session.Held.ElapsedMilliseconds;

        _ = Task.Run(() => RunPipelineAsync(session, cancelled, heldMs));
    }

    private async Task RunPipelineAsync(Session session, bool cancelled, long heldMs)
    {
        try
        {
            AudioRecorder? recorder = await session.StartTask;
            if (recorder == null)
            {
                await HideOverlay(); // mic failure already showed its own message
                return;
            }

            RecordingResult rec;
            try { rec = recorder.Stop(); }
            finally { recorder.Dispose(); }

            if (cancelled || heldMs < _settings.MinHoldMs)
            {
                // Quick taps and AltGr-style cancels vanish silently; a
                // deliberate cancel of a real recording gets feedback.
                if (cancelled && heldMs >= _settings.MinHoldMs)
                    await ShowFlash("Cancelled", false);
                else
                    await HideOverlay();
                return;
            }
            // Reject only clips without real voiced audio; a quiet microphone or a
            // long clip with pauses used to fail the old whole-clip average.
            if (rec.DurationSeconds < 0.3 || !SpeechGate.HasVoice(rec, _settings))
            {
                await ShowFlash("No speech detected", false);
                return;
            }

            await ShowStatus("Transcribing…");

            if (!_transcriber.IsReady)
                await _transcriber.EnsureReadyAsync(_settings.Model,
                    new Progress<string>(s => _ = ShowStatus(s)));

            string text;
            using (var ms = new MemoryStream(rec.WavBytes))
                text = await _transcriber.TranscribeAsync(ms, _settings);

            if (string.IsNullOrWhiteSpace(text))
            {
                await ShowFlash("No speech detected", false);
                return;
            }

            if (_settings.Cleanup.Enabled && !string.IsNullOrWhiteSpace(_settings.Cleanup.BaseUrl))
            {
                await ShowStatus("Cleaning up…");
                text = await CleanupService.CleanAsync(text, _settings.Cleanup);
            }

            text = TranscriptText.ForInsertion(text, _settings.AddTrailingSpace);
            if (text.Length == 0)
            {
                await ShowFlash("No speech detected", false);
                return;
            }
            await ShowStatus("Inserting…");
            var outcome = await TextInserter.InsertAsync(text, _settings);
            if (outcome == InsertOutcome.Failed)
            {
                await ShowFlash("Release keys then re-dictate", false);
                return;
            }

            if (_settings.LogTranscripts) AppendHistory(text, rec.DurationSeconds);

            int words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            string plural = words == 1 ? "" : "s";
            if (outcome == InsertOutcome.CopiedOnly)
            {
                Logger.Info($"Dictated {rec.DurationSeconds:F1}s of audio into {words} words (clipboard only, keys still held)");
                await ShowFlash($"{words} word{plural} in clipboard — press Ctrl+V", true);
            }
            else
            {
                Logger.Info($"Dictated {rec.DurationSeconds:F1}s of audio into {words} words");
                await ShowFlash(_settings.KeepInClipboard
                    ? $"Inserted {words} word{plural} · in clipboard"
                    : $"Inserted {words} word{plural}", true);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Dictation pipeline failed", ex);
            await ShowFlash("Error, see log", false);
        }
        finally
        {
            lock (_sync) { _busy = false; }
            StatusChanged?.Invoke("Ready");
        }
    }

    private static void AppendHistory(string text, double seconds)
    {
        try
        {
            var line = $"{DateTime.Now:o}\t{seconds:F1}s\t{text.Replace('\t', ' ').Replace('\n', ' ')}";
            File.AppendAllText(Path.Combine(AppSettings.DataDir, "transcription-history.tsv"), line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not write history: " + ex.Message);
        }
    }

    private Task ShowStatus(string text)
    {
        StatusChanged?.Invoke(text);
        return _dispatcher.InvokeAsync(() => _overlay.ShowStatus(text)).Task;
    }

    private Task ShowFlash(string text, bool ok) =>
        _dispatcher.InvokeAsync(() => _overlay.Flash(text, ok)).Task;

    private Task HideOverlay() =>
        _dispatcher.InvokeAsync(() => _overlay.HideOverlay()).Task;

    public void Dispose()
    {
        Session? session;
        lock (_sync)
        {
            session = _current;
            _current = null;
        }
        if (session != null)
        {
            session.Ended = true;
            session.OverlayDelay.Cancel();
            session.MaxTimer?.Dispose();
            _ = session.StartTask.ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion) t.Result?.Dispose();
            });
        }
        _transcriber.Dispose();
    }
}
