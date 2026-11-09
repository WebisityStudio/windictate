using WinDictate.Core;
using Xunit;

namespace WinDictate.Tests;

public class SpeechGateTests
{
    private const double DefaultThreshold = 0.004; // AppSettings.SilenceThreshold default
    private const int DefaultMinVoicedMs = 120;    // AppSettings.MinVoicedMs default
    private const int BufferSamples = 480;         // AudioRecorder: 30 ms buffers at 16 kHz

    /// <summary>Builds per-buffer levels for (seconds, level) segments, with slight jitter like a real mic.</summary>
    private static List<AudioLevel> Clip(params (double Seconds, double Level)[] segments)
    {
        var levels = new List<AudioLevel>();
        var rng = new Random(42);
        foreach (var (seconds, level) in segments)
        {
            int buffers = (int)Math.Round(seconds * SpeechGate.SampleRate / BufferSamples);
            for (int i = 0; i < buffers; i++)
            {
                double jitter = 1 + (rng.NextDouble() - 0.5) * 0.3; // +/-15 %
                levels.Add(new AudioLevel(BufferSamples, (float)(level * jitter)));
            }
        }
        return levels;
    }

    private static double OldWholeClipAverage(List<AudioLevel> levels) =>
        levels.Sum(l => (double)l.MeanAbs * l.Samples) / levels.Sum(l => l.Samples);

    private static bool Gate(List<AudioLevel> levels, double threshold = DefaultThreshold) =>
        SpeechGate.HasVoice(levels, threshold, DefaultMinVoicedMs);

    [Fact]
    public void QuietMicrophone_WithPauses_IsAccepted()
    {
        // Speech well below the old 0.004 average, framed by quiet pauses.
        var clip = Clip((1.0, 0.0003), (1.2, 0.0025), (0.8, 0.0003));
        Assert.True(OldWholeClipAverage(clip) < DefaultThreshold); // the old gate discarded this
        Assert.True(Gate(clip));
    }

    [Fact]
    public void LongClip_MostlyPauses_IsAccepted()
    {
        // 2 s of normal speech inside 20 s of thinking time.
        var clip = Clip((6, 0.0005), (2, 0.02), (12, 0.0005));
        Assert.True(OldWholeClipAverage(clip) < DefaultThreshold); // the old gate discarded this
        Assert.True(Gate(clip));
    }

    [Fact]
    public void NormalSpeech_IsAccepted() =>
        Assert.True(Gate(Clip((0.3, 0.001), (1.5, 0.03), (0.3, 0.001))));

    [Fact]
    public void ShortWord_IsAccepted() =>
        Assert.True(Gate(Clip((0.4, 0.0008), (0.21, 0.015), (0.4, 0.0008))));

    [Fact]
    public void RoomSilence_IsRejected() =>
        Assert.False(Gate(Clip((3, 0.0004))));

    [Fact]
    public void DeadMicrophone_IsRejected() =>
        Assert.False(Gate(Clip((2, 0.0))));

    [Fact]
    public void SingleClick_InSilence_IsRejected() =>
        Assert.False(Gate(Clip((1, 0.0004), (0.03, 0.08), (1, 0.0004))));

    [Fact]
    public void NoisyRoom_IsNeverRejectedByTheGate()
    {
        // The gate only drops clips with no voice; in steady noise above the
        // threshold it lets Whisper decide rather than risk dropping speech.
        Assert.True(Gate(Clip((2, 0.006))));
    }

    [Fact]
    public void HigherConfiguredThreshold_IsRespected()
    {
        var noiseOnly = Clip((2, 0.006));
        Assert.False(Gate(noiseOnly, threshold: 0.01));

        var speechInNoise = Clip((1, 0.006), (0.5, 0.03), (1, 0.006));
        Assert.True(Gate(speechInNoise, threshold: 0.01));
    }

    [Fact]
    public void VoicedSeconds_CountsOnlyVoicedBuffers()
    {
        var clip = Clip((1, 0.0003), (0.6, 0.02), (1, 0.0003));
        Assert.InRange(SpeechGate.VoicedSeconds(clip, DefaultThreshold), 0.55, 0.65);
    }

    [Fact]
    public void EmptyRecording_IsRejected() =>
        Assert.False(SpeechGate.HasVoice(Array.Empty<AudioLevel>(), DefaultThreshold, DefaultMinVoicedMs));
}
