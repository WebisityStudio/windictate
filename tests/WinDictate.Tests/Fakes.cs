using System.Collections.Concurrent;
using System.Text;
using WinDictate.Core;

namespace WinDictate.Tests;

/// <summary>Records overlay calls ("listening", "status:…", "flash:…", "notice:…", "hide").</summary>
internal sealed class FakeOverlay : IDictationOverlay
{
    private readonly object _lock = new();
    private readonly List<string> _calls = new();

    public string[] Calls
    {
        get { lock (_lock) return _calls.ToArray(); }
    }

    public void ShowListening(IntPtr targetWindow) => Add("listening");
    public void ShowStatus(string text) => Add("status:" + text);
    public void SetLevel(float peak) { }
    public void Flash(string text, bool success, int holdMs) => Add((holdMs >= 2000 ? "notice:" : "flash:") + text);
    public void Hide() => Add("hide");

    private void Add(string call)
    {
        lock (_lock) _calls.Add(call);
    }
}

/// <summary>A microphone whose recording "contains" the utterance id it was created for.</summary>
internal sealed class FakeRecorder : IAudioCapture
{
    public event Action<float>? LevelChanged;
    public event Action? CaptureStarted;
    public event Action? CaptureLost;

    /// <summary>The device is unplugged mid-recording.</summary>
    public void Disconnect() => CaptureLost?.Invoke();

    public FakeRecorder(string utterance, bool voiced)
    {
        Utterance = utterance;
        var level = voiced ? 0.02f : 0.0003f;
        Result = new RecordingResult(
            Encoding.UTF8.GetBytes(utterance),
            1.0,
            Enumerable.Range(0, 33).Select(_ => new AudioLevel(480, level)).ToArray());
    }

    public string Utterance { get; }
    public RecordingResult Result { get; }

    /// <summary>When set, Start() blocks until it completes (a slow Bluetooth headset).</summary>
    public TaskCompletionSource? OpenGate { get; set; }

    /// <summary>When false, the test raises CaptureStarted itself.</summary>
    public bool StartCapturingImmediately { get; set; } = true;

    public bool ThrowOnStart { get; set; }
    public volatile bool Started;
    public volatile bool Stopped;
    public volatile bool Disposed;

    public void Start(int deviceIndex)
    {
        if (ThrowOnStart) throw new InvalidOperationException("no device");
        OpenGate?.Task.Wait();
        if (StartCapturingImmediately) BeginCapture();
        Started = true; // after BeginCapture, so a test that sees it knows capture has begun
    }

    public void BeginCapture()
    {
        CaptureStarted?.Invoke();
        LevelChanged?.Invoke(0.2f);
    }

    public RecordingResult Stop()
    {
        Stopped = true;
        return Result;
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakePlatform : IDictationPlatform
{
    private readonly ConcurrentQueue<FakeRecorder> _next = new();
    private readonly ConcurrentQueue<FakeRecorder> _created = new();

    public volatile bool ModifiersHeld;

    /// <summary>Where keyboard focus is (window, focused control). Tests move it to simulate clicks.</summary>
    public InsertionTarget Target { get; set; } = new(new IntPtr(42), new IntPtr(7));

    public FakeRecorder Expect(string utterance, bool voiced = true)
    {
        var recorder = new FakeRecorder(utterance, voiced);
        _next.Enqueue(recorder);
        return recorder;
    }

    public FakeRecorder[] Created => _created.ToArray();

    public InsertionTarget CurrentTarget() => Target;
    public bool ModifierKeysHeld() => ModifiersHeld;
    public int ResolveMicrophone(AppSettings settings) => 0;

    public IAudioCapture CreateRecorder()
    {
        if (!_next.TryDequeue(out var recorder))
            throw new InvalidOperationException("test did not expect another recording");
        _created.Enqueue(recorder);
        return recorder;
    }
}

/// <summary>Turns utterance "A" into "Text A.", optionally waiting on a per-utterance gate.</summary>
internal sealed class FakeSpeech : ISpeechToText
{
    public readonly ConcurrentDictionary<string, TaskCompletionSource> Gates = new();
    public readonly ConcurrentDictionary<string, string> Texts = new();
    public readonly ConcurrentQueue<string> Transcribed = new();
    public readonly HashSet<string> Failing = new();

    public volatile bool Ready = true;   // a model is loaded
    public volatile bool FailEnsure;     // the model file fails its checksum
    private int _ensureCalls;
    public int EnsureCalls => Volatile.Read(ref _ensureCalls);

    public bool IsReady => Ready;

    public TaskCompletionSource Hold(string utterance)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Gates[utterance] = gate;
        return gate;
    }

    public Task EnsureReadyAsync(IProgress<string>? status, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _ensureCalls);
        if (FailEnsure) throw new ModelUnavailableException(ModelCheck.WrongHash);
        Ready = true;
        return Task.CompletedTask;
    }

    public async Task<string> TranscribeAsync(Stream wav, AppSettings settings, CancellationToken ct = default)
    {
        string utterance = new StreamReader(wav).ReadToEnd();
        if (Gates.TryGetValue(utterance, out var gate)) await gate.Task.WaitAsync(ct);
        lock (Failing)
            if (Failing.Contains(utterance)) throw new InvalidOperationException("whisper failed");
        Transcribed.Enqueue(utterance);
        return Texts.TryGetValue(utterance, out var text) ? text : $"Text {utterance}.";
    }

    public void Dispose() { }
}

/// <summary>An in-memory clipboard and focused app.</summary>
internal sealed class FakeTextTarget : ITextTarget
{
    private readonly object _lock = new();
    private readonly List<string> _delivered = new();
    private string? _clipboard;
    private bool _otherFormats;   // the clipboard holds a picture, files or formatted text as well
    private long _sequence;

    public volatile bool ClipboardFails;
    public volatile bool InputBlocked;           // SendInput rejected (elevated window, secure desktop)
    public volatile bool TypingStopsPartWay;     // Windows accepts only the first keystrokes
    public volatile bool PasteStopsPartWay;      // Windows accepts only part of Ctrl+V (the V key did go down)
    public volatile bool PasteGoesNowhere;       // Ctrl+V reaches no text field: nothing reads the clipboard
    public volatile bool OtherProgramReadsFirst; // a clipboard manager reads each new offer before the paste
    private FakeOffer? _offer;
    public Func<bool> KeysHeld { get; set; } = () => false;

    /// <summary>Return true to make a particular clipboard write fail.</summary>
    public Func<string, bool> RejectWrite { get; set; } = _ => false;

    /// <summary>Where keyboard input would go now (the harness points this at the fake platform).</summary>
    public Func<InsertionTarget> Current { get; set; } = () => new(new IntPtr(42), new IntPtr(7));

    public InsertionTarget CurrentTarget() => Current();

    /// <summary>What reached the focused app, in order: "paste:text" or "type:text".</summary>
    public string[] Delivered
    {
        get { lock (_lock) return _delivered.ToArray(); }
    }

    public string? Clipboard
    {
        get { lock (_lock) return _clipboard; }
    }

    /// <summary>True while the clipboard holds something besides plain text.</summary>
    public bool ClipboardHasOtherContent
    {
        get { lock (_lock) return _otherFormats; }
    }

    /// <summary>The user copies a picture (or files): nothing WinDictate could put back from text alone.</summary>
    public void UserCopiesPicture() => UserCopies(null, otherFormats: true);

    /// <summary>The user copies formatted text (from Word or a web page): text plus other formats.</summary>
    public void UserCopiesFormatted(string text) => UserCopies(text, otherFormats: true);

    /// <summary>The user copies something themselves.</summary>
    public void UserCopies(string text) => UserCopies(text, otherFormats: false);

    private void UserCopies(string? text, bool otherFormats)
    {
        lock (_lock)
        {
            _otherFormats = otherFormats;
            _clipboard = text;
            _sequence++;
        }
    }

    public long ClipboardSequence
    {
        get { lock (_lock) return _sequence; }
    }

    public Task<IPasteOffer?> OfferForPasteAsync(string text)
    {
        lock (_lock)
        {
            if (ClipboardFails || RejectWrite(text)) return Task.FromResult<IPasteOffer?>(null);
            _otherFormats = false;
            _clipboard = text;
            _sequence++;
            _offer = new FakeOffer(this, _sequence, text, OtherProgramReadsFirst);
            return Task.FromResult<IPasteOffer?>(_offer);
        }
    }

    /// <summary>Delayed-rendering offer: "read" when the fake paste takes the text.</summary>
    private sealed class FakeOffer : IPasteOffer
    {
        private readonly FakeTextTarget _owner;
        private readonly long _sequence;
        private readonly bool _readFirstByOther;
        public bool Read;

        public FakeOffer(FakeTextTarget owner, long sequence, string text, bool readFirstByOther)
        {
            _owner = owner;
            _sequence = sequence;
            _readFirstByOther = readFirstByOther;
        }

        public void PasteSent() { }

        public Task<PasteRead> WaitForReadAsync(int timeoutMs) =>
            Task.FromResult(_readFirstByOther ? PasteRead.Unknown : Read ? PasteRead.Read : PasteRead.NotRead);

        public Task<ReplaceResult> ReplaceWithAsync(string? text)
        {
            lock (_owner._lock)
            {
                if (_owner._sequence != _sequence) return Task.FromResult(ReplaceResult.LeftAlone);
                if (_owner.ClipboardFails || (text != null && _owner.RejectWrite(text))) return Task.FromResult(ReplaceResult.Failed);
                _owner._clipboard = text;
                _owner._otherFormats = false;
                _owner._sequence++;
                return Task.FromResult(ReplaceResult.Replaced);
            }
        }

        public Task<bool> KeepAsync() => Task.FromResult(true);

        public void Dispose() { }
    }

    public Task<bool> SetClipboardTextAsync(string text)
    {
        lock (_lock)
        {
            if (ClipboardFails || RejectWrite(text)) return Task.FromResult(false);
            _otherFormats = false;
            _clipboard = text;
            _sequence++;
            return Task.FromResult(true);
        }
    }

    public Task<ClipboardSnapshot> ReadClipboardAsync()
    {
        lock (_lock)
            return Task.FromResult(ClipboardFails ? ClipboardSnapshot.Unreadable : new ClipboardSnapshot(true, _clipboard, !_otherFormats));
    }

    public Task<bool> ClearClipboardAsync()
    {
        lock (_lock)
        {
            if (ClipboardFails) return Task.FromResult(false);
            _clipboard = null;
            _otherFormats = false;
            _sequence++;
            return Task.FromResult(true);
        }
    }

    public SendResult SendPaste()
    {
        if (KeysHeld()) return SendResult.KeysHeld;
        lock (_lock)
        {
            if (InputBlocked) return SendResult.Failed;
            if (!PasteGoesNowhere)
            {
                _delivered.Add("paste:" + _clipboard);
                if (_offer != null) _offer.Read = true;
            }
            return PasteStopsPartWay ? SendResult.Partial : SendResult.Sent;
        }
    }

    public SendResult TypeText(string text)
    {
        if (KeysHeld()) return SendResult.KeysHeld;
        lock (_lock)
        {
            if (InputBlocked) return SendResult.Failed;
            if (TypingStopsPartWay)
            {
                _delivered.Add("type:" + text[..Math.Min(3, text.Length)]);
                return SendResult.Partial;
            }
            _delivered.Add("type:" + text);
            return SendResult.Sent;
        }
    }
}
