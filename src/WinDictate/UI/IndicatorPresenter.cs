using System.Windows.Threading;
using WinDictate.Core;
using WinDictate.UI.Indicators;

namespace WinDictate.UI;

/// <summary>
/// Connects the dictation controller to the listening indicator (glass pill or
/// edge glow). Every call is posted to the UI thread at one priority, so calls
/// arrive in order, and returns immediately, which makes it safe to call from
/// the keyboard-hook thread.
///
/// The controller reports in words ("Transcribing…", "No speech detected");
/// <see cref="IndicatorCues"/> turns those into wordless states for the ordinary
/// cases and one short line for a problem or a wait.
/// </summary>
internal sealed class IndicatorPresenter : IDictationOverlay, IDisposable
{
    /// <summary>However briefly the controller asks, words stay long enough to read.</summary>
    private const int MinimumNoticeMs = 1600;

    private readonly Dispatcher _dispatcher;
    private IDictationIndicator _indicator; // UI thread only
    private IndicatorStyle _style;          // UI thread only
    private bool _disposed;                 // UI thread only

    /// <summary>Create on the UI thread: the indicator's windows belong to it.</summary>
    public IndicatorPresenter(Dispatcher dispatcher, IndicatorStyle style)
    {
        _dispatcher = dispatcher;
        _style = style;
        _indicator = IndicatorFactory.Create(style);
    }

    /// <summary>The indicator in use. UI thread only.</summary>
    internal IDictationIndicator Indicator => _indicator;

    /// <summary>Switches to another style (from Settings). UI thread only.</summary>
    public void UseStyle(IndicatorStyle style)
    {
        if (_disposed || style == _style) return;
        IDictationIndicator old = _indicator;
        _indicator = IndicatorFactory.Create(style);
        _style = style;
        old.Dispose();
    }

    public void ShowListening(IntPtr targetWindow) => Post(() => _indicator.ShowListening(targetWindow));

    public void ShowStatus(string text) => Post(() => Show(IndicatorCues.ForStatus(text), MinimumNoticeMs));

    public void SetLevel(float peak) => Post(() => _indicator.SetLevel(peak));

    public void Flash(string text, bool success, int holdMs) =>
        Post(() => Show(IndicatorCues.ForResult(text, success), Math.Max(holdMs, MinimumNoticeMs)));

    public void Hide() => Post(() => _indicator.HideNow());

    /// <summary>UI thread only.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _indicator.Dispose();
    }

    private void Show(IndicatorCue cue, int holdMilliseconds)
    {
        switch (cue.Action)
        {
            case CueAction.Working:
                _indicator.ShowWorking();
                break;
            case CueAction.Done:
                _indicator.ShowDone();
                break;
            default:
                _indicator.ShowNotice(cue.Text, cue.Kind, holdMilliseconds);
                break;
        }
    }

    private void Post(Action action) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            if (!_disposed) action();
        });
}
