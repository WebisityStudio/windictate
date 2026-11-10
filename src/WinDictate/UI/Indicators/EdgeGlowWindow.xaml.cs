using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;

namespace WinDictate.UI.Indicators;

/// <summary>
/// The edge glow: no shape and no words. The top edge of the taskbar lights up
/// and breathes with the user's voice, a highlight travels along it while the
/// speech is transcribed, and it brightens once and fades when the text is in.
/// Click-through; never takes focus. Worded notices are shown by a
/// <see cref="GlassPillWindow"/> instead (see <see cref="EdgeGlowIndicator"/>).
/// </summary>
public partial class EdgeGlowWindow : Window
{
    private const double WidthFraction = 0.44;   // of the work area
    private const double MinimumWidth = 360;

    // Resting look, and how far the voice pushes it.
    private const double HaloRestOpacity = 0.25, HaloVoiceOpacity = 0.60;
    private const double HaloRestScale = 0.55, HaloVoiceScale = 0.50;
    private const double LineRestOpacity = 0.55, LineVoiceOpacity = 0.45;

    private enum Phase { Hidden, Listening, Working, Done }

    private readonly WaveformShaper _shaper = new();
    private readonly DispatcherTimer _autoHide;
    private Phase _phase = Phase.Hidden;
    private float _peak;
    private TimeSpan _lastFrame;
    private bool _rendering;
    private int _generation;
    private bool _disposed;

    public EdgeGlowWindow()
    {
        InitializeComponent();
        _autoHide = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        _autoHide.Tick += (_, _) =>
        {
            _autoHide.Stop();
            FadeOutAndHide();
        };
        SourceInitialized += (_, _) => IndicatorNative.MakeOverlay(new WindowInteropHelper(this).Handle);
    }

    public void ShowListening(IntPtr targetWindow)
    {
        if (_disposed) return;
        bool fresh = Prepare(targetWindow);
        _phase = Phase.Listening;
        _peak = 0;
        _shaper.Reset();
        StopSweep();
        // The voice drives these from now on, so no animation may hold them.
        Motion.Set(Halo, OpacityProperty, HaloRestOpacity);
        Motion.Set(HaloScale, ScaleTransform.ScaleYProperty, HaloRestScale);
        Motion.Set(Line, OpacityProperty, LineRestOpacity);
        StartRendering();
        Motion.To(Root, OpacityProperty, 1, fresh ? 500 : 200);
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
        Motion.To(Halo, OpacityProperty, HaloRestOpacity, 300);
        Motion.To(HaloScale, ScaleTransform.ScaleYProperty, HaloRestScale, 300, Motion.Smooth);
        Motion.To(Line, OpacityProperty, 0.35, 300);
        StartSweep();
        Motion.To(Root, OpacityProperty, 1, fresh ? 500 : 200);
    }

    public void ShowDone()
    {
        if (_disposed) return;
        Prepare();
        _phase = Phase.Done;
        StopRendering();
        StopSweep();

        // Brighten once as the text lands...
        Motion.To(Halo, OpacityProperty, 0.85, 180);
        Motion.To(HaloScale, ScaleTransform.ScaleYProperty, 1.0, 180, Motion.Smooth);
        Motion.To(Line, OpacityProperty, 1, 180);
        Motion.To(Root, OpacityProperty, 1, 120);

        // ...hold it for a moment, then fade. A timer does the holding, so the
        // confirmation is still seen when Windows animations are switched off
        // (it then stays a little longer, as there is no fade to notice).
        HideAfter(IndicatorTheme.AnimationsEnabled ? 220 : 600);
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

    private void HideAfter(double milliseconds)
    {
        _autoHide.Interval = TimeSpan.FromMilliseconds(milliseconds);
        _autoHide.Start();
    }

    private void FadeOutAndHide()
    {
        int generation = _generation;
        Motion.To(Root, OpacityProperty, 0, 700, completed: () =>
        {
            if (generation == _generation) Rest();   // otherwise it was shown again meanwhile
        });
    }

    /// <returns>true when the glow was not on screen.</returns>
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
            OverlayPositioner.PlaceBottomCentre(this, targetWindow, WidthFraction, MinimumWidth);
        }
        return fresh;
    }

    private void Rest()
    {
        StopRendering();
        StopSweep();
        _phase = Phase.Hidden;
        if (IsVisible) Hide();
    }

    private void StartSweep()
    {
        if (!IndicatorTheme.AnimationsEnabled)
        {
            // No motion: show the plain line a little brighter instead.
            Motion.Set(Sweep, OpacityProperty, 0);
            Motion.Set(Line, OpacityProperty, 0.8);
            return;
        }

        UpdateLayout();
        double track = LineHost.ActualWidth;
        if (track <= 0) return;
        double width = Math.Round(track * 0.55);
        Sweep.Width = width;

        var travel = new DoubleAnimation
        {
            From = -0.1 * track,
            To = track - width + 0.1 * track,
            Duration = Motion.Ms(1100),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = Motion.Sway
        };
        SweepShift.BeginAnimation(TranslateTransform.XProperty, travel);
        Motion.To(Sweep, OpacityProperty, 1, 250);
    }

    private void StopSweep()
    {
        Motion.Set(Sweep, OpacityProperty, 0);
        Motion.Set(SweepShift, TranslateTransform.XProperty, 0);
    }

    private void ApplyPalette()
    {
        IndicatorPalette palette = IndicatorTheme.Current();
        Color glow = palette.Glow;
        Color clear = Color.FromArgb(0, glow.R, glow.G, glow.B);

        HaloInner.Color = Color.FromArgb(0x8C, glow.R, glow.G, glow.B);
        HaloMiddle.Color = Color.FromArgb(0x33, glow.R, glow.G, glow.B);
        HaloOuter.Color = clear;

        LineStart.Color = clear;
        LineLeft.Color = glow;
        LineCore.Color = palette.GlowCore;
        LineRight.Color = glow;
        LineEnd.Color = clear;

        SweepStart.Color = clear;
        SweepCore.Color = palette.GlowCore;
        SweepEnd.Color = clear;
    }

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
        double level = _shaper.Level;
        Halo.Opacity = HaloRestOpacity + level * HaloVoiceOpacity;
        HaloScale.ScaleY = HaloRestScale + level * HaloVoiceScale;
        Line.Opacity = LineRestOpacity + level * LineVoiceOpacity;
    }
}
