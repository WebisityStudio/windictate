using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using WinDictate.UI;

namespace WinDictate.Core;

/// <summary>
/// Orchestrates one dictation session at a time:
/// hotkey held -> record -> (release) -> transcribe locally -> optional cleanup -> insert.
/// All heavy work runs off the keyboard-hook thread so the global hook never stalls.
/// </summary>
public sealed class DictationController : IDisposable
{
    public event Action<string>? StatusChanged; // for the tray tooltip

    private readonly Dispatcher _dispatcher;
    private readonly OverlayWindow _overlay;
    private readonly Transcriber _transcriber = new();
    private AppSettings _settings;

    private readonly object _sync = new();
    private bool _busy;             // a transcribe pipeline is running
    private AudioRecorder? _recorder;
    private Stopwatch? _holdTimer;
    private string _targetTitle = "";
    private CancellationTokenSource? _overlayDelay;
    private bool _modelWarming;

    public DictationController(Dispatcher dispatcher, OverlayWindow overlay, AppSettings settings)
    {
        _dispatcher = dispatcher;
        _overlay = overlay;
        _settings = settings;
        _ = WarmUpModelAsync();
    }

    public void UpdateSettings(AppSettings settings)
    {
        _settings = settings;
        _ = WarmUpModelAsync();
    }

    private async Task WarmUpModelAsync()
    {
        if (_modelWarming) return;
        _modelWarming = true;
        try
        {
            var progress = new Progress<string>(s => StatusChanged?.Invoke(s));
            await _transcriber.EnsureReadyAsync(_settings.Model, progress);
            StatusChanged?.Invoke("Ready");
        }
        catch (Exception ex)
        {
            Logger.Error("Model warm-up failed", ex);
            StatusChanged?.Invoke("Model not ready — check log");
        }
        finally
        {
            _modelWarming = false;
        }
    }

    // ----- Session start (hotkey held) -----

    public void OnSessionStarted()
    {
        lock (_sync)
        {
            if (_busy || _recorder != null) return; // ignore re-entry while inserting
            try
            {
                _recorder = new AudioRecorder();
                _recorder.LevelChanged += OnLevel;
                _recorder.Start(_settings.MicDeviceIndex);
                _holdTimer = Stopwatch.StartNew();
            }
            catch (Exception ex)
            {
                Logger.Error("Could not start microphone", ex);
                _recorder = null;
                _dispatcher.BeginInvoke(() => _overlay.Flash("No microphone", false));
                return;
            }
        }

        _targetTitle = NativeMethods.GetForegroundWindowTitle();

        // Delay the overlay slightly so a quick accidental tap shows nothing.
        var delay = new CancellationTokenSource();
        _overlayDelay = delay;
        _dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await Task.Delay(180, delay.Token);
                _overlay.ShowListening(_targetTitle);
            }
            catch (TaskCanceledException) { }
        });
    }

    private void OnLevel(float peak) => _dispatcher.BeginInvoke(() => _overlay.SetLevel(peak));

    // ----- Session end (hotkey released or cancelled) -----

    public void OnSessionEnded(bool cancelled)
    {
        AudioRecorder? recorder;
        Stopwatch? timer;
        lock (_sync)
        {
            recorder = _recorder;
            timer = _holdTimer;
            _recorder = null;
            _holdTimer = null;
            if (recorder == null) return;
            _busy = true;
        }

        _overlayDelay?.Cancel();
        long heldMs = timer?.ElapsedMilliseconds ?? 0;

        _ = Task.Run(() => RunPipelineAsync(recorder, cancelled, heldMs));
    }

    private async Task RunPipelineAsync(AudioRecorder recorder, bool cancelled, long heldMs)
    {
        try
        {
            RecordingResult rec;
            try { rec = recorder.Stop(); }
            finally { recorder.Dispose(); }

            if (cancelled)
            {
                await ShowFlash("Cancelled", false);
                return;
            }
            if (heldMs < _settings.MinHoldMs)
            {
                await HideOverlay();
                return; // accidental tap
            }
            if (rec.DurationSeconds < 0.3 || rec.AverageLevel < _settings.SilenceThreshold)
            {
                await ShowFlash("No speech detected", false);
                return;
            }

            await ShowStatus("Transcribing…");

            if (!_transcriber.IsReady)
                await _transcriber.EnsureReadyAsync(_settings.Model,
                    new Progress<string>(async s => await ShowStatus(s)));

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

            text = text.Trim();
            await ShowStatus("Inserting…");
            await TextInserter.InsertAsync(text, _settings, _dispatcher);

            if (_settings.LogTranscripts) AppendHistory(text, rec.DurationSeconds);

            int words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            await ShowFlash($"Inserted {words} word{(words == 1 ? "" : "s")}", true);
        }
        catch (Exception ex)
        {
            Logger.Error("Dictation pipeline failed", ex);
            await ShowFlash("Error — see log", false);
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

    private Task ShowStatus(string text) => _dispatcher.InvokeAsync(() => _overlay.ShowStatus(text)).Task;
    private Task ShowFlash(string text, bool ok) => _dispatcher.InvokeAsync(() => _overlay.Flash(text, ok)).Task;
    private Task HideOverlay() => _dispatcher.InvokeAsync(() => _overlay.HideOverlay()).Task;

    public void Dispose()
    {
        _recorder?.Dispose();
        _transcriber.Dispose();
    }
}
