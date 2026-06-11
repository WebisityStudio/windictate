using System.IO;
using NAudio.Utils;
using NAudio.Wave;

namespace WinDictate.Core;

public sealed record RecordingResult(byte[] WavBytes, double DurationSeconds, double AverageLevel);

/// <summary>
/// Records microphone audio to an in-memory 16 kHz mono 16-bit WAV, which is
/// exactly the input format Whisper expects. One instance per dictation session.
/// Audio never touches disk.
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    public event Action<float>? LevelChanged; // peak level 0..1 per buffer

    private WaveInEvent? _waveIn;
    private MemoryStream? _ms;
    private WaveFileWriter? _writer;
    private readonly TaskCompletionSource<bool> _stopped =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _writeLock = new();
    private double _sumAbs;
    private long _sampleCount;
    private bool _stopping;

    public void Start(int deviceIndex)
    {
        var format = new WaveFormat(16000, 16, 1);
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
        lock (_writeLock)
        {
            if (_stopping || _writer == null) return;
            _writer.Write(e.Buffer, 0, e.BytesRecorded);
            float peak = 0;
            for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
            {
                short sample = BitConverter.ToInt16(e.Buffer, i);
                float abs = Math.Abs((int)sample) / 32768f;
                if (abs > peak) peak = abs;
                _sumAbs += abs;
                _sampleCount++;
            }
            LevelChanged?.Invoke(peak);
        }
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
        }

        double duration = _sampleCount / 16000.0;
        double average = _sampleCount > 0 ? _sumAbs / _sampleCount : 0;
        return new RecordingResult(_ms.ToArray(), duration, average);
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
