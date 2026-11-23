using System.IO;
using System.Text;
using Whisper.net;

namespace WinDictate.Core;

/// <summary>
/// Local speech-to-text via whisper.cpp (Whisper.net bindings). Runs entirely
/// on this PC. Uses only the fixed model that ships with the program
/// (<see cref="PilotModel"/>): it never downloads anything, and a model file
/// whose checksum does not match is not loaded.
/// </summary>
public sealed class Transcriber : ISpeechToText
{
    private readonly string _modelPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhisperFactory? _factory;

    public Transcriber(string? modelPath = null)
    {
        _modelPath = modelPath ?? PilotModel.DefaultPath;
    }

    public bool IsReady => _factory != null;

    /// <summary>
    /// Checks and loads the model, once. Never does the slow work on the
    /// caller's thread: the checksum and the model load (WhisperFactory.FromPath
    /// reads and initialises the whole model synchronously) run on the thread pool.
    /// </summary>
    /// <exception cref="ModelUnavailableException">The model file is missing, damaged or not the approved file.</exception>
    public async Task EnsureReadyAsync(IProgress<string>? status, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_factory != null) return;

            status?.Report("Loading speech model…");
            ModelCheck check = await Task.Run(() => PilotModel.Verify(_modelPath), ct).ConfigureAwait(false);
            if (check != ModelCheck.Ok)
            {
                Logger.Error($"Speech model check failed: {check}");
                throw new ModelUnavailableException(check);
            }
            _factory = await Task.Run(() => WhisperFactory.FromPath(_modelPath), ct).ConfigureAwait(false);
            Logger.Info($"Loaded model {PilotModel.FileName} (SHA-256 {PilotModel.ShortHash}…, verified)");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> TranscribeAsync(Stream wav, AppSettings settings, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Check inside the gate and inside the try, so this throw still
            // releases the gate (otherwise every later call would wait forever).
            if (_factory == null) throw new InvalidOperationException("Speech model is not loaded yet");

            var builder = _factory.CreateBuilder().WithLanguage(PilotModel.Language);

            int threads = settings.Threads > 0 ? settings.Threads : Math.Min(8, Environment.ProcessorCount);
            builder = builder.WithThreads(threads);

            if (!string.IsNullOrWhiteSpace(settings.VocabularyHint))
                builder = builder.WithPrompt(settings.VocabularyHint);

            var processor = builder.Build();
            var sb = new StringBuilder();
            await using (processor.ConfigureAwait(false))
            {
                await foreach (var segment in processor.ProcessAsync(wav, ct).ConfigureAwait(false))
                    sb.Append(segment.Text);
            }

            // Strips non-speech annotations like [BLANK_AUDIO] or (coughing) but
            // keeps dictated content in brackets.
            return TranscriptText.StripAnnotations(sb.ToString());
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        // Freeing the model under a running load or transcription would crash in
        // native code, so wait briefly for it; at shutdown the OS reclaims the rest.
        if (_gate.Wait(TimeSpan.FromSeconds(2)))
        {
            _factory?.Dispose();
            _factory = null;
        }
    }
}
