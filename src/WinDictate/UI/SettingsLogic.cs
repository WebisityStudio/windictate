namespace WinDictate.UI;

/// <summary>The three plain choices offered for speech quality; each stands for one Whisper model.</summary>
internal enum SpeechQuality { Fast, Balanced, Accurate }

internal sealed record ModelInfo(string Name, string Size, string Summary);

/// <summary>What the line at the top of Settings says, and whether it is all good news.</summary>
internal readonly record struct SettingsStatus(bool AllGood, string Text);

/// <summary>
/// The wording and small decisions behind the Settings window, kept apart from
/// the window so they can be unit-tested on any OS.
/// </summary>
internal static class SettingsLogic
{
    /// <summary>The models the app knows how to download, smallest first.</summary>
    public static readonly IReadOnlyList<ModelInfo> Models = new[]
    {
        new ModelInfo("tiny.en", "75 MB", "fastest, least accurate"),
        new ModelInfo("base.en", "142 MB", "fast"),
        new ModelInfo("small.en", "466 MB", "balanced"),
        new ModelInfo("medium.en", "1.5 GB", "accurate, slower"),
        new ModelInfo("large-v3-turbo", "1.6 GB", "best, needs a strong PC")
    };

    public static string ModelFor(SpeechQuality quality) => quality switch
    {
        SpeechQuality.Fast => "base.en",
        SpeechQuality.Accurate => "medium.en",
        _ => "small.en"
    };

    /// <summary>The plain choice a model stands for, or null for a model only offered under Advanced.</summary>
    public static SpeechQuality? QualityOf(string? model)
    {
        foreach (SpeechQuality quality in Enum.GetValues<SpeechQuality>())
            if (string.Equals(ModelFor(quality), model?.Trim(), StringComparison.OrdinalIgnoreCase))
                return quality;
        return null;
    }

    public static ModelInfo? Find(string? model) =>
        Models.FirstOrDefault(m => string.Equals(m.Name, model?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The line under "Speech quality": what the choice means, and whether a download is still to come.</summary>
    public static string QualityLine(string model, bool downloaded)
    {
        string meaning = QualityOf(model) switch
        {
            SpeechQuality.Fast => "Quickest, for short notes",
            SpeechQuality.Balanced => "Balanced suits most laptops",
            SpeechQuality.Accurate => "Most accurate, noticeably slower",
            _ => $"Using {model.Trim()}, chosen under Advanced"
        };
        return downloaded ? meaning : $"{meaning}. {DownloadNote(model)}";
    }

    /// <summary>The line under "Speech model" in Advanced.</summary>
    public static string ModelLine(string model, bool downloaded) =>
        downloaded ? "On this PC. Works with no internet" : DownloadNote(model);

    private static string DownloadNote(string model) =>
        Find(model) is { } known ? $"Downloads once, about {known.Size}" : "Downloads once on first use";

    /// <summary>The keys to draw as keycaps for a shortcut such as "Ctrl+Alt".</summary>
    public static string[] HotkeyKeys(string? hotkey)
    {
        string[] keys = (hotkey ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return keys.Length > 0 ? keys : new[] { "Ctrl", "Alt" };
    }

    /// <summary>
    /// The one-line summary at the top. It only says "everything stays on this PC"
    /// when that is true: with AI cleanup on, the text is sent to a service.
    /// </summary>
    public static SettingsStatus StatusLine(bool modelDownloaded, bool cleanupOn, string? cleanupUrl)
    {
        if (cleanupOn && !string.IsNullOrWhiteSpace(cleanupUrl))
        {
            string where = Uri.TryCreate(cleanupUrl.Trim(), UriKind.Absolute, out Uri? uri) && uri.Host.Length > 0
                ? uri.Host
                : "the cleanup service";
            return new SettingsStatus(false, $"AI cleanup is on: text is sent to {where}");
        }
        return modelDownloaded
            ? new SettingsStatus(true, "Ready. Everything stays on this PC.")
            : new SettingsStatus(false, "The speech model downloads on first use");
    }

    /// <summary>How full the microphone test bar is (0 to 1) for a peak level (0 to 1); follows loudness as the ear hears it.</summary>
    public static double MeterLevel(float peak)
    {
        const double quietDb = -52, loudDb = -8;
        if (float.IsNaN(peak) || peak <= 0) return 0;
        double db = 20 * Math.Log10(Math.Min(1f, peak));
        return Math.Clamp((db - quietDb) / (loudDb - quietDb), 0, 1);
    }

    /// <summary>A sound this loud during the microphone test counts as "it hears you".</summary>
    public const float HeardPeak = 0.02f;
}
