using System.Text.RegularExpressions;

namespace WinDictate.Core;

/// <summary>
/// Pure text handling between Whisper and insertion (unit-tested off Windows).
/// </summary>
public static class TranscriptText
{
    // Whisper marks non-speech audio with annotations such as [BLANK_AUDIO],
    // [MUSIC], [ Silence ], (coughing), (upbeat music) or *sighs*. Dictated text
    // can contain brackets too ("[GBP]", "(not the current account)"), so a span
    // is only treated as an annotation when every word in it describes a sound
    // and at least one word is an actual sound or noise word.

    private static readonly HashSet<string> SoundWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "blank", "audio", "silence", "silent", "pause", "speech", "foreign",
        "music", "musical", "song", "singing", "sings", "humming", "hums", "whistling", "whistles",
        "applause", "applauding", "clapping", "claps", "cheering", "cheers",
        "laughter", "laugh", "laughs", "laughing", "chuckle", "chuckles", "chuckling",
        "giggle", "giggles", "giggling",
        "cough", "coughs", "coughing", "sneeze", "sneezes", "sneezing",
        "sniff", "sniffs", "sniffing", "sniffles", "sniffling",
        "sigh", "sighs", "sighing", "gasp", "gasps", "gasping", "groan", "groans", "groaning",
        "yawn", "yawns", "yawning", "breath", "breaths", "breathing", "breathes",
        "inhales", "exhales", "panting", "throat",
        "noise", "noises", "sound", "sounds", "static", "beep", "beeps", "beeping",
        "buzz", "buzzing", "ringing", "rings", "click", "clicks", "clicking",
        "typing", "tapping", "knocking", "knock", "knocks", "footsteps", "rustling",
        "thud", "bang", "crash", "whoosh", "chime", "chimes", "ding", "door", "slams",
        "crying", "cries", "sobbing", "sobs", "screaming", "screams", "shouting", "shouts",
        "inaudible", "unintelligible", "indistinct", "mumbling", "mumbles",
        "muttering", "mutters", "whispering", "whispers", "crosstalk", "chatter", "chattering",
        "engine", "traffic", "wind", "rain", "thunder", "siren", "chirping", "barking"
    };

    // Words that may accompany a sound word ("upbeat music", "clears throat",
    // "speaking in foreign language") but never make an annotation on their own.
    private static readonly HashSet<string> DescriptorWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "in", "of", "on", "with", "no", "some",
        "soft", "softly", "gentle", "gently", "light", "lightly", "loud", "loudly",
        "quiet", "quietly", "faint", "faintly", "distant", "background", "heavy", "heavily",
        "upbeat", "dramatic", "suspenseful", "ominous", "cheerful", "sad", "slow", "fast",
        "intense", "mysterious", "tense", "eerie", "calm", "relaxing", "nervous", "nervously",
        "playing", "plays", "continues", "continuing", "fades", "fading", "ends", "starts", "begins",
        "audience", "crowd", "people", "man", "woman", "men", "women", "person", "speaker",
        "speaking", "speaks", "language", "clears", "clearing", "phone", "keyboard", "dog", "birds"
    };

    private const char Removed = '\u0001'; // placeholder for a removed annotation; never in Whisper output

    private static readonly Regex SquareSpan = new(@"\[([^\[\]\r\n]{1,80})\]", RegexOptions.CultureInvariant);
    private static readonly Regex RoundSpan = new(@"\(([^()\r\n]{1,80})\)", RegexOptions.CultureInvariant);
    private static readonly Regex StarSpan = new(@"\*([^*\r\n]{1,60})\*", RegexOptions.CultureInvariant);
    private static readonly Regex MusicNotes = new("[♩♪♫♬]+", RegexOptions.CultureInvariant);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant);

    // A run of removed annotations, possibly separated by spaces.
    private const string RemovedRun = "\u0001(?:\\s*\u0001)*";
    // Annotation at the start or after a sentence/clause mark, followed by its own
    // now-orphaned punctuation: "Hello. (laughs). Next" -> "Hello. Next".
    private static readonly Regex OrphanedPunctuation =
        new(@"(?:^|(?<=[.!?,;:]))\s*" + RemovedRun + @"\s*[.,;:!?]+", RegexOptions.CultureInvariant);
    // Annotation right before punctuation: "I agree (laughs)." -> "I agree."
    private static readonly Regex BeforePunctuation =
        new(@"\s*" + RemovedRun + @"\s*(?=[.,;:!?])", RegexOptions.CultureInvariant);
    // Annotation between words: "Hello [BLANK_AUDIO] world" -> "Hello world".
    private static readonly Regex BetweenWords =
        new(@"\s*" + RemovedRun + @"\s*", RegexOptions.CultureInvariant);

    /// <summary>
    /// Removes Whisper's non-speech annotations and tidies the whitespace they
    /// leave behind, without touching dictated content in brackets.
    /// </summary>
    public static string StripAnnotations(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";

        var text = SquareSpan.Replace(raw, RemoveIfAnnotation);
        text = RoundSpan.Replace(text, RemoveIfAnnotation);
        text = StarSpan.Replace(text, RemoveIfAnnotation);
        text = MusicNotes.Replace(text, Removed.ToString());

        text = Whitespace.Replace(text, " ");
        if (text.Contains(Removed))
        {
            text = OrphanedPunctuation.Replace(text, "");
            text = BeforePunctuation.Replace(text, "");
            text = BetweenWords.Replace(text, " ");
        }
        return text.Trim();
    }

    private static string RemoveIfAnnotation(Match m) =>
        IsAnnotation(m.Groups[1].Value) ? Removed.ToString() : m.Value;

    /// <summary>True when a bracketed span's inner text only describes non-speech audio.</summary>
    internal static bool IsAnnotation(string inner)
    {
        var words = inner.Split(new[] { ' ', '\t', '_', '-', Removed }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('.', ',', '!', '?', ';', ':', '"', '\'', '’'))
            .Where(w => w.Length > 0)
            .ToList();
        if (words.Count == 0 || words.Count > 6) return false;

        bool anySound = false;
        foreach (var word in words)
        {
            if (SoundWords.Contains(word)) anySound = true;
            else if (!DescriptorWords.Contains(word)) return false;
        }
        return anySound;
    }
}
