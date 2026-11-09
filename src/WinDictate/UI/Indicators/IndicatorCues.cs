using System.Text.RegularExpressions;

namespace WinDictate.UI.Indicators;

/// <summary>What an indicator should do in response to something the dictation controller reports.</summary>
internal enum CueAction
{
    /// <summary>Show the wordless "working" state.</summary>
    Working,

    /// <summary>Show the wordless confirmation, then hide.</summary>
    Done,

    /// <summary>Show a short line of words.</summary>
    Notice
}

internal readonly record struct IndicatorCue(CueAction Action, string Text = "", NoticeKind Kind = NoticeKind.Info);

/// <summary>
/// Translates the controller's status and result messages, which were written
/// for an overlay that always showed words, into what the quiet indicators
/// show: only a state for the ordinary cases, and one short plain line when
/// there is a problem or a wait. Anything unrecognised is still shown, so no
/// message is ever swallowed. Pure, so it is unit-tested on any OS.
/// </summary>
internal static class IndicatorCues
{
    private const int MaxNoticeLength = 40;
    private static readonly Regex Percent = new(@"(\d{1,3})\s*%", RegexOptions.CultureInvariant);

    /// <summary>For a progress status ("Transcribing…", "Downloading … 42%", "Opening microphone…").</summary>
    public static IndicatorCue ForStatus(string? status)
    {
        string text = (status ?? "").Trim();
        if (Begins(text, "Opening microphone"))
            return Notice("Opening microphone", NoticeKind.Busy);
        if (Begins(text, "Downloading"))
        {
            Match percent = Percent.Match(text);
            return Notice(percent.Success ? $"Getting ready {percent.Groups[1].Value}%" : "Getting ready", NoticeKind.Busy);
        }
        if (Begins(text, "Loading"))
            return Notice("Getting ready", NoticeKind.Busy);

        // Transcribing, cleaning up, inserting: all the same to the person waiting.
        return new IndicatorCue(CueAction.Working);
    }

    /// <summary>For a result ("Inserted 12 words", "No speech detected", "Not inserted · … press Ctrl+V").</summary>
    public static IndicatorCue ForResult(string? result, bool success)
    {
        string text = (result ?? "").Trim();
        if (success)
        {
            // Inserted, but the user's own clipboard could not be put back: worth saying,
            // because the dictation is still on the clipboard when they expect it not to be.
            return text.Contains("clipboard not restored", StringComparison.OrdinalIgnoreCase)
                ? Notice("Inserted. Clipboard not restored", NoticeKind.Info)
                : new IndicatorCue(CueAction.Done);
        }

        // Unknown message: show it as it is rather than hide a problem.
        return KnownProblem(text) ?? Notice(Shorten(text), NoticeKind.Warning);
    }

    /// <summary>True when a failed result has its own deliberate wording (rather than being shown as it came).</summary>
    public static bool Recognises(string? result) => KnownProblem((result ?? "").Trim()) != null;

    private static IndicatorCue? KnownProblem(string text)
    {
        if (Begins(text, "No speech")) return Notice("Didn't catch that", NoticeKind.Warning);
        if (Begins(text, "No microphone")) return Notice("No microphone found", NoticeKind.Warning);
        if (Begins(text, "Microphone not ready")) return Notice("Microphone wasn't ready", NoticeKind.Warning);
        if (Begins(text, "Cancelled")) return Notice("Cancelled", NoticeKind.Info);
        if (Begins(text, "Microphone not responding")) return Notice("Microphone not responding", NoticeKind.Warning);
        if (Begins(text, "Not inserted")) return Notice("Copied. Press Ctrl+V", NoticeKind.Info);
        if (Begins(text, "Focus moved")) return Notice("Focus moved. Press Ctrl+V", NoticeKind.Info);
        if (Begins(text, "Partly typed")) return Notice("Partly typed. Check, then Ctrl+V", NoticeKind.Warning);
        if (Begins(text, "May have been inserted")) return Notice("Check it arrived. If not, Ctrl+V", NoticeKind.Warning);
        if (Begins(text, "Couldn't insert")) return Notice("Couldn't insert. Dictate again", NoticeKind.Warning);
        if (Begins(text, "Error")) return Notice("Something went wrong", NoticeKind.Warning);
        return null;
    }

    private static IndicatorCue Notice(string text, NoticeKind kind) => new(CueAction.Notice, text, kind);

    private static bool Begins(string text, string prefix) =>
        text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static string Shorten(string text)
    {
        text = text.TrimEnd('.', '…', ' ');
        if (text.Length == 0) return "Something went wrong";
        return text.Length <= MaxNoticeLength ? text : text[..(MaxNoticeLength - 1)].TrimEnd() + "…";
    }
}
