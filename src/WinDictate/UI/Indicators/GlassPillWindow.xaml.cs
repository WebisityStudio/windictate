using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;
using Rectangle = System.Windows.Shapes.Rectangle;
using Size = System.Windows.Size;

namespace WinDictate.UI.Indicators;

/// <summary>
/// The glass pill: a small capsule above the taskbar. It shows a live waveform
/// while the user speaks, shrinks to three dots while the speech is transcribed,
/// ticks when the text is in, and only ever shows words when something needs
/// explaining. It is click-through and never takes focus.
/// </summary>
public partial class GlassPillWindow : Window, IDictationIndicator
{
    private const double ListenWidth = 132;
    private const double CompactWidth = 56;
    private const double MaxNoticeWidth = 360;
    private const double NoticePadding = 34;        // left + right breathing room around a notice
    private const double RestingBottomMargin = 22;  // gap between the capsule and the taskbar
    private const double BarHeight = 18;
    private const double BarMinHeight = 3;
    private const double TickDash = 7;              // dash length that hides the whole tick

    private enum Phase { Hidden, Listening, Working, Done, Notice }

    private readonly Rectangle[] _bars = new Rectangle[WaveformShaper.BarCount];
    private readonly Ellipse[] _dots = new Ellipse[3];
    private readonly TranslateTransform[] _dotShifts = new TranslateTransform[3];
    private readonly UIElement[] _layers;
    private readonly WaveformShaper _shaper = new();
    private readonly DispatcherTimer _autoHide;

    private Phase _phase = Phase.Hidden;
    private IndicatorPalette _palette = IndicatorPalette.Light;
    private float _peak;
    private TimeSpan _lastFrame;
    private bool _rendering;
    private int _generation;   // bumped on every state change so stale callbacks can tell they are stale
    private bool _disposed;

    public GlassPillWindow()
    {
        InitializeComponent();
        _layers = new UIElement[] { ListenLayer, WorkLayer, Tick, NoticeLayer };
        BuildBars();
        BuildDots();

        _autoHide = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        _autoHide.Tick += (_, _) =>
        {
            _autoHide.Stop();
            FadeOutAndHide();
        };

        SourceInitialized += (_, _) => IndicatorNative.MakeOverlay(new WindowInteropHelper(this).Handle);
    }

    /// <summary>
    /// Extra distance above the resting position, in device-independent pixels.
    /// The edge-glow style lifts its notices slightly so they clear the glow.
    /// </summary>
    public double Lift
    {
        get => Capsule.Margin.Bottom - RestingBottomMargin;
        set => Capsule.Margin = new Thickness(0, 0, 0, RestingBottomMargin + Math.Clamp(value, 0, 20));
    }

    // ----- IDictationIndicator -----

    public void ShowListening(IntPtr targetWindow)
    {
        if (_disposed) return;
        bool fresh = Prepare(targetWindow);
        _phase = Phase.Listening;
        _peak = 0;
        _shaper.Reset();
        StopDots();
        StopPulse();
        ResetTick();
        ShowLayer(ListenLayer, animate: !fresh);
        Morph(ListenWidth, animate: !fresh);
        StartRendering();
        Enter(fresh);
    }

    public void SetLevel(float peak)
    {
        if (_phase == Phase.Listening) _peak = peak;
    }

    public void ShowWorking()
    {
        if (_disposed) return;
        bool fresh = Prepare();
        _phase = Phase.Working;
        StopRendering();
        StopPulse();
        ResetTick();
        ShowLayer(WorkLayer, animate: !fresh);
        Morph(CompactWidth, animate: !fresh);
        StartDots();
        Enter(fresh);
    }

    public void ShowDone()
    {
        if (_disposed) return;
        bool fresh = Prepare();
        _phase = Phase.Done;
        StopRendering();
        StopDots();
        StopPulse();
        ShowLayer(Tick, animate: !fresh);
        Morph(CompactWidth, animate: !fresh);
        DrawTick();
        Enter(fresh);
        HideAfter(900);
    }

    public void ShowNotice(string text, NoticeKind kind)
    {
        if (_disposed) return;
        bool fresh = Prepare();
        _phase = Phase.Notice;
        StopRendering();
        StopDots();
        StopPulse();
        ResetTick();

        NoticeText.Text = text ?? string.Empty;
        NoticeDot.Fill = Frozen(kind == NoticeKind.Warning ? _palette.Warning : _palette.Info);
        NoticeLayer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = Math.Clamp(Math.Ceiling(NoticeLayer.DesiredSize.Width) + NoticePadding, CompactWidth, MaxNoticeWidth);

        ShowLayer(NoticeLayer, animate: !fresh);
        Morph(width, animate: !fresh);
        if (kind == NoticeKind.Busy) StartPulse();
        Enter(fresh);
        // A wait stays until something replaces it; the long timeout is only a safety net.
        HideAfter(kind == NoticeKind.Busy ? 15000 : 2000);
    }

    public void HideNow()
    {
        if (_disposed) return;
        _generation++;
        _autoHide.Stop();
        Motion.Set(Root, OpacityProperty, 0);
        Rest();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _generation++;
        _autoHide.Stop();
        StopRendering();
        Close();
    }

    // ----- Showing and hiding -----

    /// <summary>Gets ready for a new state.</summary>
    /// <returns>true when the capsule was not on screen and has to make an entrance.</returns>
    private bool Prepare(IntPtr targetWindow = default)
    {
        _generation++;
        _autoHide.Stop();
        ApplyPalette();

        bool fresh = _phase == Phase.Hidden || !IsVisible;
        if (fresh)
        {
            Motion.Set(Root, OpacityProperty, 0);
            if (!IsVisible) Show();
            // Only placed when it appears, so it never jumps between monitors mid-dictation.
            OverlayPositioner.PlaceBottomCentre(this, targetWindow);
        }
        return fresh;
    }

    private void Enter(bool fresh)
    {
        if (fresh)
        {
            // Rise a few pixels and settle, while fading in.
            Motion.Set(RootShift, TranslateTransform.YProperty, 10);
            Motion.Set(RootScale, ScaleTransform.ScaleXProperty, 0.9);
            Motion.Set(RootScale, ScaleTransform.ScaleYProperty, 0.9);
            Motion.To(RootShift, TranslateTransform.YProperty, 0, 500, Motion.Spring);
            Motion.To(RootScale, ScaleTransform.ScaleXProperty, 1, 500, Motion.Spring);
            Motion.To(RootScale, ScaleTransform.ScaleYProperty, 1, 500, Motion.Spring);
            Motion.To(Root, OpacityProperty, 1, 350);
        }
        else
        {
            // Already on screen, possibly part-way through fading out: come back to rest.
            Motion.To(RootShift, TranslateTransform.YProperty, 0, 200, Motion.Smooth);
            Motion.To(RootScale, ScaleTransform.ScaleXProperty, 1, 200, Motion.Smooth);
            Motion.To(RootScale, ScaleTransform.ScaleYProperty, 1, 200, Motion.Smooth);
            Motion.To(Root, OpacityProperty, 1, 200);
        }
    }

    private void HideAfter(double milliseconds)
    {
        _autoHide.Interval = TimeSpan.FromMilliseconds(milliseconds);
        _autoHide.Start();
    }

    private void FadeOutAndHide()
    {
        int generation = _generation;
        Motion.To(Root, OpacityProperty, 0, 350, completed: () =>
        {
            if (generation == _generation) Rest();   // otherwise it was shown again meanwhile
        });
    }

    private void Rest()
    {
        StopRendering();
        StopDots();
        StopPulse();
        _phase = Phase.Hidden;
        if (IsVisible) Hide();
    }

    // ----- Parts -----

    private void ShowLayer(UIElement visible, bool animate)
    {
        foreach (UIElement layer in _layers)
        {
            double opacity = ReferenceEquals(layer, visible) ? 1 : 0;
            if (animate) Motion.To(layer, OpacityProperty, opacity, 250);
            else Motion.Set(layer, OpacityProperty, opacity);
        }
    }

    private void Morph(double width, bool animate)
    {
        if (animate) Motion.To(Capsule, WidthProperty, width, 450, Motion.Spring);
        else Motion.Set(Capsule, WidthProperty, width);
    }

    private void BuildBars()
    {
        for (int i = 0; i < _bars.Length; i++)
        {
            var bar = new Rectangle
            {
                Width = 3,
                Height = BarMinHeight,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Margin = new Thickness(1.5, 0, 1.5, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _bars[i] = bar;
            Bars.Children.Add(bar);
        }
    }

    private void BuildDots()
    {
        for (int i = 0; i < _dots.Length; i++)
        {
            var shift = new TranslateTransform();
            var dot = new Ellipse
            {
                Width = 4.5,
                Height = 4.5,
                Margin = new Thickness(2, 0, 2, 0),
                Opacity = 0.25,
                RenderTransform = shift
            };
            _dots[i] = dot;
            _dotShifts[i] = shift;
            WorkLayer.Children.Add(dot);
        }
    }

    private void StartDots()
    {
        if (!IndicatorTheme.AnimationsEnabled)
        {
            foreach (Ellipse dot in _dots) Motion.Set(dot, OpacityProperty, 0.8);
            return;
        }
        for (int i = 0; i < _dots.Length; i++)
        {
            Motion.Loop(_dots[i], OpacityProperty, rest: 0.25, peak: 1, periodMilliseconds: 1200, delayMilliseconds: 180 * i);
            Motion.Loop(_dotShifts[i], TranslateTransform.YProperty, rest: 0, peak: -1.5, periodMilliseconds: 1200, delayMilliseconds: 180 * i);
        }
    }

    private void StopDots()
    {
        for (int i = 0; i < _dots.Length; i++)
        {
            Motion.Set(_dots[i], OpacityProperty, 0.25);
            Motion.Set(_dotShifts[i], TranslateTransform.YProperty, 0);
        }
    }

    private void StartPulse() => Motion.Loop(NoticeDot, OpacityProperty, rest: 1, peak: 0.3, periodMilliseconds: 1400);

    private void StopPulse() => Motion.Set(NoticeDot, OpacityProperty, 1);

    private void DrawTick()
    {
        Motion.Set(Tick, Shape.StrokeDashOffsetProperty, TickDash);
        Motion.To(Tick, Shape.StrokeDashOffsetProperty, 0, 450, Motion.Smooth, delayMilliseconds: 100);
    }

    private void ResetTick() => Motion.Set(Tick, Shape.StrokeDashOffsetProperty, TickDash);

    private void ApplyPalette()
    {
        _palette = IndicatorTheme.Current();
        SolidColorBrush ink = Frozen(_palette.Ink);

        Capsule.Background = Frozen(_palette.Fill);
        Capsule.BorderBrush = Frozen(_palette.Edge);
        Shadow.Color = _palette.Shadow;
        Shadow.Opacity = _palette.ShadowOpacity;
        RecDot.Fill = Frozen(_palette.Recording);
        Tick.Stroke = Frozen(_palette.Success);
        NoticeText.Foreground = ink;
        foreach (Rectangle bar in _bars) bar.Fill = ink;
        foreach (Ellipse dot in _dots) dot.Fill = ink;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // ----- Waveform -----

    private void StartRendering()
    {
        if (_rendering) return;
        _rendering = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopRendering()
    {
        if (!_rendering) return;
        _rendering = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        TimeSpan now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _lastFrame) return;   // WPF can raise this more than once per frame
        double dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : (now - _lastFrame).TotalSeconds;
        _lastFrame = now;

        _shaper.Advance(_peak, dt);
        IReadOnlyList<double> heights = _shaper.Heights;
        for (int i = 0; i < _bars.Length; i++)
            _bars[i].Height = Math.Max(BarMinHeight, BarHeight * heights[i]);
    }
}
