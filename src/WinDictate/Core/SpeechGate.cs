namespace WinDictate.Core;

/// <summary>Level of one capture buffer: its sample count and mean absolute amplitude (0..1).</summary>
public readonly record struct AudioLevel(int Samples, float MeanAbs);

/// <summary>A finished recording: 16 kHz mono 16-bit WAV bytes (memory only) plus per-buffer levels.</summary>
public sealed record RecordingResult(byte[] WavBytes, double DurationSeconds, IReadOnlyList<AudioLevel> Levels);

/// <summary>
/// Decides whether a recording contains any voiced audio worth transcribing.
/// It only rejects clips with no real voice in them: a buffer counts as voiced
/// when its level reaches <c>SilenceThreshold</c>, or, for quiet microphones,
/// when it stands well clear of the clip's own background level. The clip
/// passes once the voiced buffers add up to <c>MinVoicedMs</c>. Pauses and
/// quiet input therefore no longer drag a real dictation below the gate the way
/// the old whole-clip average did.
/// </summary>
public static class SpeechGate
{
    public const int SampleRate = 16000;

    /// <summary>Voiced buffers must be at least this many times the background level.</summary>
    public const double NoiseFloorRatio = 2.5;

    /// <summary>Never treat audio below this level as voice (a muted or dead microphone).</summary>
    public const double AbsoluteFloor = 0.0005;

    /// <summary>Share of the quietest buffers used to estimate the background level.</summary>
    private const double NoiseFloorPercentile = 0.10;

    public static bool HasVoice(RecordingResult recording, AppSettings settings) =>
        HasVoice(recording.Levels, settings.SilenceThreshold, settings.MinVoicedMs);

    public static bool HasVoice(IReadOnlyList<AudioLevel> levels, double silenceThreshold, int minVoicedMs) =>
        VoicedSeconds(levels, silenceThreshold) * 1000.0 >= Math.Max(0, minVoicedMs);

    /// <summary>Total duration of the buffers classed as voiced.</summary>
    public static double VoicedSeconds(IReadOnlyList<AudioLevel> levels, double silenceThreshold)
    {
        if (levels.Count == 0) return 0;
        double threshold = VoicedThreshold(levels, silenceThreshold);
        long voicedSamples = 0;
        foreach (var level in levels)
            if (level.MeanAbs >= threshold) voicedSamples += level.Samples;
        return voicedSamples / (double)SampleRate;
    }

    /// <summary>
    /// Level a buffer must reach to count as voiced: the configured threshold at
    /// most, lower when the clip's own background is quiet, never below
    /// <see cref="AbsoluteFloor"/>.
    /// </summary>
    public static double VoicedThreshold(IReadOnlyList<AudioLevel> levels, double silenceThreshold)
    {
        double ceiling = silenceThreshold > 0 ? silenceThreshold : AbsoluteFloor;
        double adaptive = NoiseFloor(levels) * NoiseFloorRatio;
        return Math.Max(AbsoluteFloor, Math.Min(ceiling, adaptive));
    }

    private static double NoiseFloor(IReadOnlyList<AudioLevel> levels)
    {
        if (levels.Count == 0) return 0;
        var sorted = levels.Select(l => (double)l.MeanAbs).OrderBy(v => v).ToArray();
        int index = (int)Math.Floor((sorted.Length - 1) * NoiseFloorPercentile);
        return sorted[index];
    }
}
