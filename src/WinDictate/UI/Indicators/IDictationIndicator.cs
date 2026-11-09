namespace WinDictate.UI.Indicators;

/// <summary>Which shape the listening indicator takes.</summary>
public enum IndicatorStyle
{
    /// <summary>A small glass capsule above the taskbar with a live waveform.</summary>
    Pill,

    /// <summary>No shape at all: the taskbar edge glows and breathes with the voice.</summary>
    Glow
}

/// <summary>Tone of a short worded notice.</summary>
public enum NoticeKind
{
    /// <summary>Neutral information, e.g. "Copied. Press Ctrl+V".</summary>
    Info,

    /// <summary>Something did not work, e.g. "Didn't catch that".</summary>
    Warning,

    /// <summary>A wait the user cannot shorten, e.g. "Getting ready".</summary>
    Busy
}

/// <summary>
/// What the user sees while dictating. Implementations are small, click-through
/// and never take focus from the app being dictated into. They stay wordless
/// unless something needs explaining.
/// Every member must be called on the UI thread.
/// </summary>
public interface IDictationIndicator : IDisposable
{
    /// <summary>
    /// The microphone is capturing. Call this only once capture has really
    /// started, never on the key press alone.
    /// </summary>
    /// <param name="targetWindow">
    /// The window being dictated into, used only to pick the monitor to appear on.
    /// Pass <see cref="IntPtr.Zero"/> for whichever window has focus now.
    /// </param>
    void ShowListening(IntPtr targetWindow);

    /// <summary>Latest microphone peak, 0..1. Cheap; expected about 30 times a second.</summary>
    void SetLevel(float peak);

    /// <summary>The keys were released and the speech is being turned into text.</summary>
    void ShowWorking();

    /// <summary>The text was inserted. Confirms briefly, then hides itself.</summary>
    void ShowDone();

    /// <summary>
    /// Something needs words. Shown in the same glass and hidden again after a
    /// moment (a <see cref="NoticeKind.Busy"/> notice stays longer, until replaced).
    /// </summary>
    void ShowNotice(string text, NoticeKind kind);

    /// <summary>Hide at once, with no confirmation (cancelled, accidental tap).</summary>
    void HideNow();
}
