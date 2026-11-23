using System.IO;

namespace WinDictate.Core;

// Seams between DictationController and Windows, so the controller's queueing,
// ordering and insertion rules can be unit-tested on any OS. The Windows
// implementations are AudioRecorder, Transcriber, WindowsDictationPlatform,
// WindowsTextTarget and UI.IndicatorPresenter.

/// <summary>Microphone capture for one dictation session.</summary>
public interface IAudioCapture : IDisposable
{
    /// <summary>Peak level 0..1 of each captured buffer; raised on the capture thread.</summary>
    event Action<float>? LevelChanged;

    /// <summary>The first audio buffer arrived, so the microphone really is recording.
    /// Raised once, on the capture thread.</summary>
    event Action? CaptureStarted;

    /// <summary>The device stopped delivering audio on its own (unplugged, disabled,
    /// driver error) while recording. Raised once, on any thread.</summary>
    event Action? CaptureLost;

    /// <summary>Opens the device and starts capturing. Can take a second or more
    /// (Bluetooth headsets); only called on worker threads.</summary>
    void Start(int deviceIndex);

    /// <summary>Stops capturing and returns the recording (in memory only).</summary>
    RecordingResult Stop();
}

/// <summary>Local speech-to-text with the one fixed model this build ships with.</summary>
public interface ISpeechToText : IDisposable
{
    bool IsReady { get; }

    /// <summary>Checks and loads the model.</summary>
    /// <exception cref="ModelUnavailableException">The model file is missing or not the approved file.</exception>
    Task EnsureReadyAsync(IProgress<string>? status, CancellationToken ct = default);

    Task<string> TranscribeAsync(Stream wav, AppSettings settings, CancellationToken ct = default);
}

/// <summary>Operating-system queries the controller needs.</summary>
public interface IDictationPlatform
{
    /// <summary>The window and control that have keyboard focus. Cheap and non-blocking
    /// (called on the hook thread when the hotkey is pressed).</summary>
    InsertionTarget CurrentTarget();

    /// <summary>True while Shift, Ctrl, Alt or Win is physically held.</summary>
    bool ModifierKeysHeld();

    /// <summary>Device index to record from for these settings.</summary>
    int ResolveMicrophone(AppSettings settings);

    IAudioCapture CreateRecorder();
}

/// <summary>
/// The recording overlay. Implementations post each call to the UI thread in
/// call order and return immediately; they are called from any thread,
/// including the keyboard-hook thread.
/// </summary>
public interface IDictationOverlay
{
    /// <summary>Listening, with the level meter. The indicator appears on the target window's
    /// screen and, where it can, names the target application (never its window title).</summary>
    void ShowListening(IntPtr targetWindow);

    void ShowStatus(string text);
    void SetLevel(float peak);

    /// <summary>A message that fades out after <paramref name="holdMs"/>.</summary>
    void Flash(string text, bool success, int holdMs);

    void Hide();
}
