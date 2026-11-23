using System.IO;
using NAudio.Utils;
using NAudio.Wave;

namespace WinDictate.Core;

/// <summary>
/// Records microphone audio to an in-memory 16 kHz mono 16-bit WAV, which is
/// exactly the input format Whisper expects. One instance per dictation session.
/// Audio never touches disk.
/// </summary>
public sealed class AudioRecorder : IAudioCapture
{
    public event Action<float>? LevelChanged; // peak level 0..1 per buffer
    public event Action? CaptureStarted;      // first buffer arrived: the device really is recording
    public event Action? CaptureLost;         // the device stopped on its own while recording

    /// <summary>Once audio has started, this long without a buffer means the device has gone.</summary>
    private const int StallLimitMs = 2000;

    private WaveInEvent? _waveIn;
    private MemoryStream? _ms;
    private WaveFileWriter? _writer;
    private readonly TaskCompletionSource<bool> _stopped =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _writeLock = new();
    private readonly List<AudioLevel> _levels = new();
    private long _sampleCount;
    private bool _stopping;
    private bool _captureStarted;
    private volatile bool _stopRequested;   // Stop or Dispose was called: a stop is expected
    private long _lastDataTick;             // Environment.TickCount64 of the latest buffer
    private int _lostRaised;
    private System.Threading.Timer? _stallTimer;

    /// <summary>Product names of the current waveIn capture devices, by index.</summary>
    public static IReadOnlyList<string> DeviceNames()
    {
        var names = new List<string>();
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            names.Add(WaveInEvent.GetCapabilities(i).ProductName);
        return names;
    }

    /// <summary>
    /// The device index to record from for these settings: the saved microphone
    /// by name wherever it is now, or the system default when it is missing.
    /// </summary>
    public static int ResolveDevice(AppSettings settings)
    {
        var devices = DeviceNames();
        if (MicrophoneSelector.IsMissing(settings.MicDeviceName, devices))
            Logger.Warn("Chosen microphone is not connected, recording from the system default");
        return MicrophoneSelector.Resolve(settings.MicDeviceName, settings.MicDeviceIndex, devices);
    }

    public void Start(int deviceIndex)
    {
        var format = new WaveFormat(SpeechGate.SampleRate, 16, 1);
        _ms = new MemoryStream();
        _writer = new WaveFileWriter(new IgnoreDisposeStream(_ms), format);
        _waveIn = new WaveInEvent
        {
            DeviceNumber = deviceIndex,
            WaveFormat = format,
            BufferMilliseconds = 30
        };
        _waveIn.DataAvailable += OnData;
        _waveIn.RecordingStopped += OnRecordingStopped;
        _waveIn.StartRecording();
        _stallTimer = new System.Threading.Timer(_ => CheckForStall(), null, 500, 500);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _stopped.TrySetResult(true);
        if (_stopRequested) return;
        // Not asked to stop: the device was unplugged or disabled, or its driver failed.
        string detail = e.Exception == null ? "" : $" ({e.Exception.GetType().Name} 0x{e.Exception.HResult:X8})";
        RaiseLost("Microphone stopped while recording" + detail);
    }

    /// <summary>A device that silently stops delivering buffers is treated as gone, too.</summary>
    private void CheckForStall()
    {
        if (_stopRequested || !Volatile.Read(ref _captureStarted)) return;
        if (Environment.TickCount64 - Interlocked.Read(ref _lastDataTick) > StallLimitMs)
            RaiseLost("Microphone stopped delivering audio while recording");
    }

    private void RaiseLost(string reason)
    {
        if (Interlocked.Exchange(ref _lostRaised, 1) != 0) return;
        _stallTimer?.Dispose();
        Logger.Warn(reason);
        CaptureLost?.Invoke();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        float peak = 0;
        bool firstAudio = false;
        lock (_writeLock)
        {
            if (_stopping || _writer == null) return;
            _writer.Write(e.Buffer, 0, e.BytesRecorded);
            double sumAbs = 0;
            int samples = 0;
            for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
            {
                short sample = BitConverter.ToInt16(e.Buffer, i);
                float abs = Math.Abs((int)sample) / 32768f;
                if (abs > peak) peak = abs;
                sumAbs += abs;
                samples++;
            }
            if (samples > 0)
            {
                _levels.Add(new AudioLevel(samples, (float)(sumAbs / samples)));
                _sampleCount += samples;
                // StartRecording can return well before audio flows (Bluetooth
                // headsets switching profile), so the first buffer is the real start.
                Interlocked.Exchange(ref _lastDataTick, Environment.TickCount64);
                firstAudio = !_captureStarted;
                Volatile.Write(ref _captureStarted, true);
            }
        }
        // Raised outside the lock: handlers take the controller's lock.
        if (firstAudio) CaptureStarted?.Invoke();
        LevelChanged?.Invoke(peak);
    }

    public RecordingResult Stop()
    {
        if (_waveIn == null || _ms == null || _writer == null)
            throw new InvalidOperationException("Recorder was not started");

        _stopRequested = true;
        _stallTimer?.Dispose();
        try { _waveIn.StopRecording(); }
        catch (Exception ex) { Logger.Warn($"Stopping the microphone failed: {ex.GetType().Name} 0x{ex.HResult:X8}"); }
        _stopped.Task.Wait(1500);

        lock (_writeLock)
        {
            _stopping = true;
            _writer.Dispose(); // finalizes the WAV header inside _ms
            _writer = null;
            double duration = _sampleCount / (double)SpeechGate.SampleRate;
            byte[] audio = _ms.ToArray();
            ForgetBuffer(); // the caller's copy is the only one left; it clears that after use
            return new RecordingResult(audio, duration, _levels.ToArray());
        }
    }

    /// <summary>Overwrites the in-memory recording buffer with zeros. Audio is never written to disk.</summary>
    private void ForgetBuffer()
    {
        if (_ms == null) return;
        try { Array.Clear(_ms.GetBuffer()); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _stopRequested = true;
        _stallTimer?.Dispose();
        try { _waveIn?.Dispose(); } catch { }
        lock (_writeLock)
        {
            _stopping = true;
            try { _writer?.Dispose(); } catch { }
            _writer = null;
            ForgetBuffer();
        }
        _ms?.Dispose();
    }
}
