using System.Diagnostics;
using System.Windows.Threading;

namespace WinDictate.UI.Indicators;

/// <summary>Creates the indicator for a style. Call on the UI thread.</summary>
public static class IndicatorFactory
{
    public static IDictationIndicator Create(IndicatorStyle style) => style switch
    {
        IndicatorStyle.Glow => new EdgeGlowIndicator(),
        _ => new GlassPillWindow()
    };

    /// <summary>Reads a style from settings text; anything unrecognised means the default pill.</summary>
    public static IndicatorStyle ParseStyle(string? value) =>
        string.Equals(value?.Trim(), "glow", StringComparison.OrdinalIgnoreCase) ? IndicatorStyle.Glow : IndicatorStyle.Pill;
}

/// <summary>
/// The edge-glow style as a whole: the glow itself for listening, working and
/// done, plus a glass pill that appears only when something needs words.
/// </summary>
internal sealed class EdgeGlowIndicator : IDictationIndicator
{
    private readonly EdgeGlowWindow _glow = new();
    private readonly GlassPillWindow _words = new() { Lift = 8 };

    public void ShowListening(IntPtr targetWindow)
    {
        _words.HideNow();
        _glow.ShowListening(targetWindow);
    }

    public void SetLevel(float peak) => _glow.SetLevel(peak);

    public void ShowWorking()
    {
        _words.HideNow();
        _glow.ShowWorking();
    }

    public void ShowDone()
    {
        _words.HideNow();
        _glow.ShowDone();
    }

    public void ShowNotice(string text, NoticeKind kind)
    {
        _glow.HideNow();
        _words.ShowNotice(text, kind);
    }

    public void HideNow()
    {
        _glow.HideNow();
        _words.HideNow();
    }

    public void Dispose()
    {
        _glow.Dispose();
        _words.Dispose();
    }
}

/// <summary>
/// Plays one pretend dictation on an indicator: listening with a synthetic
/// voice, working, done. Used by the Settings preview and for checking a build
/// by eye. Records nothing and inserts nothing.
/// </summary>
public sealed class IndicatorDemo
{
    private const double ListenSeconds = 2.6;
    private const double WorkSeconds = 1.3;

    private readonly IDictationIndicator _indicator;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private bool _working;

    private IndicatorDemo(IDictationIndicator indicator, Dispatcher dispatcher)
    {
        _indicator = indicator;
        _timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TimeSpan.FromMilliseconds(30) };
        _timer.Tick += OnTick;
    }

    /// <summary>True until the pretend dictation has finished.</summary>
    public bool IsRunning => _timer.IsEnabled;

    /// <summary>Starts the demo. Call on the UI thread that owns <paramref name="indicator"/>.</summary>
    public static IndicatorDemo Play(IDictationIndicator indicator)
    {
        var demo = new IndicatorDemo(indicator, Dispatcher.CurrentDispatcher);
        demo._indicator.ShowListening(IntPtr.Zero);
        demo._clock.Start();
        demo._timer.Start();
        return demo;
    }

    /// <summary>Stops early and hides the indicator.</summary>
    public void Stop()
    {
        if (!_timer.IsEnabled) return;
        _timer.Stop();
        _indicator.HideNow();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        double t = _clock.Elapsed.TotalSeconds;
        if (t < ListenSeconds)
        {
            _indicator.SetLevel(SyntheticPeak(t));
        }
        else if (t < ListenSeconds + WorkSeconds)
        {
            if (_working) return;
            _working = true;
            _indicator.ShowWorking();
        }
        else
        {
            _timer.Stop();
            _indicator.ShowDone();
        }
    }

    /// <summary>A speech-like microphone peak, around the level of ordinary talking.</summary>
    internal static float SyntheticPeak(double seconds) =>
        (float)(0.03 + 0.27 * Math.Abs(Math.Sin(seconds * 4.7) * Math.Sin(seconds * 1.9 + 0.7)));
}
