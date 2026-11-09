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
        _waveIn.RecordingStopped += (_, _) => _stopped.TrySetResult(true);
        _waveIn.StartRecording();
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
                firstAudio = !_captureStarted;
                _captureStarted = true;
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

        _waveIn.StopRecording();
        _stopped.Task.Wait(1500);

        lock (_writeLock)
        {
            _stopping = true;
            _writer.Dispose(); // finalizes the WAV header inside _ms
            _writer = null;
            double duration = _sampleCount / (double)SpeechGate.SampleRate;
            return new RecordingResult(_ms.ToArray(), duration, _levels.ToArray());
        }
    }

    public void Dispose()
    {
        try { _waveIn?.Dispose(); } catch { }
        lock (_writeLock)
        {
            _stopping = true;
            try { _writer?.Dispose(); } catch { }
            _writer = null;
        }
        _ms?.Dispose();
    }
}
