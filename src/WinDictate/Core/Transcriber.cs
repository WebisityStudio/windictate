using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Whisper.net;

namespace WinDictate.Core;

/// <summary>
/// Local speech-to-text via whisper.cpp (Whisper.net bindings). Runs entirely
/// on-device. Models are GGML files downloaded once into %APPDATA%\WinDictate\models,
/// or dropped there manually on machines without internet access.
/// </summary>
public sealed class Transcriber : IDisposable
{
    public static string ModelsDir => Path.Combine(AppSettings.DataDir, "models");
    public static string ModelPath(string model) => Path.Combine(ModelsDir, $"ggml-{model}.bin");

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhisperFactory? _factory;
    private string? _loadedPath;

    public bool IsReady => _factory != null;

    public async Task EnsureReadyAsync(string model, IProgress<string>? status, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var path = ModelPath(model);
            if (!File.Exists(path))
                await DownloadModelAsync(model, path, status, ct);

            if (_factory == null || _loadedPath != path)
            {
                status?.Report("Loading speech model…");
                _factory?.Dispose();
                _factory = null;
                _factory = WhisperFactory.FromPath(path);
                _loadedPath = path;
                Logger.Info($"Loaded model {model} ({new FileInfo(path).Length / 1024 / 1024} MB)");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task DownloadModelAsync(string model, string path, IProgress<string>? status, CancellationToken ct)
    {
        Directory.CreateDirectory(ModelsDir);
        var url = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{model}.bin";
        Logger.Info($"Downloading model {model} from {url}");
        status?.Report($"Downloading {model} model…");

        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? -1;

        var tmp = path + ".part";
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var target = File.Create(tmp))
        {
            var buffer = new byte[1 << 16];
            long done = 0;
            long lastPercent = -1;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0)
                {
                    long percent = done * 100 / total;
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        status?.Report($"Downloading {model} model… {percent}%");
                    }
                }
            }
        }
        File.Move(tmp, path, true);
        Logger.Info($"Model {model} downloaded");
    }

    public async Task<string> TranscribeAsync(Stream wav, AppSettings settings, CancellationToken ct = default)
    {
        if (_factory == null) throw new InvalidOperationException("Speech model is not loaded yet");

        await _gate.WaitAsync(ct);
        try
        {
            var builder = _factory.CreateBuilder();
            builder = settings.Language.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? builder.WithLanguageDetection()
                : builder.WithLanguage(string.IsNullOrWhiteSpace(settings.Language) ? "en" : settings.Language);

            int threads = settings.Threads > 0 ? settings.Threads : Math.Min(8, Environment.ProcessorCount);
            builder = builder.WithThreads(threads);

            if (!string.IsNullOrWhiteSpace(settings.VocabularyHint))
                builder = builder.WithPrompt(settings.VocabularyHint);

            await using var processor = builder.Build();
            var sb = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(wav, ct))
                sb.Append(segment.Text);

            return PostProcess(sb.ToString());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Strips non-speech annotations like [BLANK_AUDIO] or (coughing).</summary>
    private static string PostProcess(string raw)
    {
        var text = Regex.Replace(raw, @"\[[^\]]*\]|\([^)]*\)", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text;
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _gate.Dispose();
    }
}
