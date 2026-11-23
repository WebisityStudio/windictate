using WinDictate.Core;

namespace WinDictate.UI;

/// <summary>What the line at the top of Settings says, and whether it is all good news.</summary>
internal readonly record struct SettingsStatus(bool AllGood, string Text);

/// <summary>
/// The wording and small decisions behind the Settings window, kept apart from
/// the window so they can be unit-tested on any OS.
/// </summary>
internal static class SettingsLogic
{
    /// <summary>The keys to draw as keycaps for a shortcut such as "Ctrl+Alt".</summary>
    public static string[] HotkeyKeys(string? hotkey)
    {
        string[] keys = (hotkey ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return keys.Length > 0 ? keys : new[] { "Ctrl", "Alt" };
    }

    /// <summary>
    /// The one-line summary at the top. In this build nothing ever leaves the PC,
    /// so the only thing that can be wrong is the speech model.
    /// </summary>
    /// <param name="model">The result of the model check, or null while it is still running.</param>
    public static SettingsStatus StatusLine(ModelCheck? model) => model switch
    {
        ModelCheck.Ok => new SettingsStatus(true, "Ready. Everything stays on this PC."),
        null => new SettingsStatus(true, "Checking the speech model. Everything stays on this PC."),
        ModelCheck.Missing => new SettingsStatus(false, "Speech model missing: reinstall WinDictate"),
        _ => new SettingsStatus(false, "Speech model failed its check: reinstall WinDictate")
    };

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
