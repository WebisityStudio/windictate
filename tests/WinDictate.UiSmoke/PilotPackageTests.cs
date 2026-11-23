using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using WinDictate.Core;
using Xunit;
using Xunit.Abstractions;

namespace WinDictate.UiSmoke;

/// <summary>
/// Checks on the assembled pilot package, run by the official build after the
/// release tool has made it. They need two environment variables, set by
/// .github/workflows/pilot-release.yml:
///   PILOT_RUNTIME_DIR  the package's 01-Runtime\win-x64 folder;
///   PILOT_SPEECH_WAV   a 16 kHz mono WAV of synthesised speech with invented words.
/// Without them (for example on a developer's PC) they record "not checked" and pass.
/// </summary>
public class PilotPackageTests
{
    private readonly ITestOutputHelper _output;

    public PilotPackageTests(ITestOutputHelper output) => _output = output;

    private static string? RuntimeDir => Environment.GetEnvironmentVariable("PILOT_RUNTIME_DIR") is { Length: > 0 } d && Directory.Exists(d) ? d : null;
    private static string? SpeechWav => Environment.GetEnvironmentVariable("PILOT_SPEECH_WAV") is { Length: > 0 } f && File.Exists(f) ? f : null;

    [Fact]
    public void ThePackagedModel_PassesTheProgramsOwnCheck()
    {
        if (RuntimeDir is not { } runtime)
        {
            Ui.Note("packaged-model", "NOT CHECKED: PILOT_RUNTIME_DIR is not set");
            return;
        }
        var clock = Stopwatch.StartNew();
        ModelCheck check = PilotModel.Verify(Path.Combine(runtime, "models", PilotModel.FileName));
        Assert.Equal(ModelCheck.Ok, check);
        Ui.Note("packaged-model", $"checked: {PilotModel.FileName} in the package passes the program's size and SHA-256 check ({clock.ElapsedMilliseconds} ms)");
    }

    /// <summary>
    /// The speech engine and model from the package turn real (synthesised)
    /// speech into the right words, on this machine, with nothing downloaded.
    /// </summary>
    [Fact]
    public async Task ThePackagedEngineAndModel_TranscribeSpeech()
    {
        if (RuntimeDir is not { } runtime || SpeechWav is not { } wav)
        {
            Ui.Note("offline-transcription", "NOT CHECKED: PILOT_RUNTIME_DIR or PILOT_SPEECH_WAV is not set");
            return;
        }

        // The native engine this test loads must be the very files the package ships.
        string mine = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64");
        string packaged = Path.Combine(runtime, "runtimes", "win-x64");
        foreach (string dll in Directory.GetFiles(packaged, "*.dll"))
        {
            string copy = Path.Combine(mine, Path.GetFileName(dll));
            Assert.True(File.Exists(copy), $"the test's own copy of {Path.GetFileName(dll)} is missing");
            Assert.Equal(Hash(dll), Hash(copy));
        }

        var settings = new AppSettings();
        using var transcriber = new Transcriber(Path.Combine(runtime, "models", PilotModel.FileName));
        var load = Stopwatch.StartNew();
        await transcriber.EnsureReadyAsync(null);
        load.Stop();

        byte[] audio = File.ReadAllBytes(wav);
        double seconds = (audio.Length - 44) / 32000.0;
        var run = Stopwatch.StartNew();
        string text;
        using (var stream = new MemoryStream(audio, writable: false))
            text = await transcriber.TranscribeAsync(stream, settings);
        run.Stop();

        _output.WriteLine($"Transcript: {text}");
        foreach (string word in new[] { "quarterly", "report", "finance", "Friday" })
            Assert.Contains(word, text, StringComparison.OrdinalIgnoreCase);
        Ui.Note("offline-transcription",
            $"checked: the packaged engine and model transcribed {seconds:F1} s of synthesised speech correctly in {run.Elapsed.TotalSeconds:F1} s " +
            $"(model check and load {load.Elapsed.TotalSeconds:F1} s, {Environment.ProcessorCount} logical processors). Transcript: \"{text.Trim()}\"");
    }

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
