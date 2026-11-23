namespace WinDictate.Core;

/// <summary>
/// The pilot-use notice shown on first launch. Only the fact that it was
/// acknowledged (this version number) is stored, in settings.json.
/// </summary>
public static class PilotNotice
{
    /// <summary>Bump when the wording changes, so everyone sees the new notice once.</summary>
    public const string Version = "1";

    public const string Text =
        "This pilot tool is intended for post-event dictation and drafting. " +
        "Do not use it to record live conversations, calls or meetings. " +
        "Review all transcribed text before saving, sending or submitting it. " +
        "Speech recognition may misinterpret names, numbers and specialist terminology.";

    /// <summary>A short how-to under the notice.</summary>
    public static string HowTo(string hotkey) =>
        $"Hold {hotkey} and speak; let go to insert the text where your cursor is. " +
        "Press Esc before letting go to cancel. Everything stays on this PC.";

    public static bool IsAcknowledged(AppSettings settings) =>
        string.Equals(settings.NoticeAcknowledged, Version, StringComparison.Ordinal);
}
