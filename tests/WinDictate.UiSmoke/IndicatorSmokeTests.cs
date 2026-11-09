using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using WinDictate.UI.Indicators;
using Xunit;
using Color = System.Windows.Media.Color;

namespace WinDictate.UiSmoke;

/// <summary>
/// Opens the real indicator windows on a Windows desktop and walks them through
/// every state. Each step checks behaviour (visible or hidden, click-through,
/// never focused, resting on the taskbar edge, something actually drawn) and
/// saves a picture so the look can be checked by eye.
/// </summary>
public class IndicatorSmokeTests
{
    private static readonly (string Name, IndicatorPalette Palette, Color Backdrop)[] Looks =
    {
        ("light", IndicatorPalette.Light, Color.FromRgb(0xFF, 0xFF, 0xFF)),
        ("dark", IndicatorPalette.Dark, Color.FromRgb(0x1D, 0x21, 0x27)),
    };

    /// <summary>
    /// Every look, with Windows animations on and off. Server images (and many
    /// virtual desktops) ship with animations off, which takes a different path
    /// through the code, so both must be exercised whatever the machine is set to.
    /// </summary>
    private static IEnumerable<(string Tag, IndicatorPalette Palette, Color Backdrop, bool Animated)> Cases()
    {
        foreach (var look in Looks)
        {
            yield return (look.Name, look.Palette, look.Backdrop, true);
            yield return (look.Name + "-still", look.Palette, look.Backdrop, false);
        }
    }

    [Fact]
    public void GlassPill_GoesThroughEveryState_AndStaysOutOfTheWay() => Ui.Run(() =>
    {
        foreach (var look in Cases())
        {
            IndicatorTheme.Forced = look.Palette;
            IndicatorTheme.ForcedAnimations = look.Animated;
            var pill = new GlassPillWindow();
            try
            {
                pill.ShowListening();
                Ui.Speak(pill.SetLevel, 900);
                Assert.True(pill.IsVisible, "listening: should be on screen");
                AssertIsQuietOverlay(pill);
                Assert.True(Ui.Capture(pill, $"pill-1-listening-{look.Tag}", look.Backdrop) > 400, "listening: the capsule and waveform should be drawn");

                pill.ShowWorking();
                Ui.Pump(800);
                Assert.True(pill.IsVisible, "working: should stay on screen");
                Assert.True(Ui.Capture(pill, $"pill-2-working-{look.Tag}", look.Backdrop) > 200, "working: the capsule should be drawn");

                pill.ShowDone();
                Ui.Pump(600);
                Assert.True(Ui.Capture(pill, $"pill-3-done-{look.Tag}", look.Backdrop) > 200, "done: the capsule and tick should be drawn");
                Ui.Pump(1300);
                Assert.False(pill.IsVisible, "done: should hide itself after confirming");

                pill.ShowNotice("Didn't catch that", NoticeKind.Warning);
                Ui.Pump(700);
                Assert.True(pill.IsVisible, "notice: should appear from hidden");
                AssertIsQuietOverlay(pill);
                Assert.True(Ui.Capture(pill, $"pill-4-notice-warning-{look.Tag}", look.Backdrop) > 400, "notice: the words should be drawn");

                pill.ShowNotice("Copied. Press Ctrl+V", NoticeKind.Info);
                Ui.Pump(700);
                Ui.Capture(pill, $"pill-5-notice-info-{look.Tag}", look.Backdrop);

                pill.ShowNotice("Getting ready", NoticeKind.Busy);
                Ui.Pump(700);
                Ui.Capture(pill, $"pill-6-notice-busy-{look.Tag}", look.Backdrop);

                pill.HideNow();
                Assert.False(pill.IsVisible, "HideNow: should hide at once");

                // A notice must go away by itself.
                pill.ShowNotice("Didn't catch that", NoticeKind.Warning);
                Ui.Pump(2800);
                Assert.False(pill.IsVisible, "notice: should hide itself after about two seconds");
            }
            finally
            {
                pill.Dispose();
                IndicatorTheme.Forced = null;
                IndicatorTheme.ForcedAnimations = null;
            }
        }
    });

    [Fact]
    public void EdgeGlow_GoesThroughEveryState_AndStaysOutOfTheWay() => Ui.Run(() =>
    {
        foreach (var look in Cases())
        {
            IndicatorTheme.Forced = look.Palette;
            IndicatorTheme.ForcedAnimations = look.Animated;
            var glow = new EdgeGlowWindow();
            try
            {
                glow.ShowListening();
                Ui.Speak(glow.SetLevel, 900);
                Assert.True(glow.IsVisible, "listening: should be on screen");
                AssertIsQuietOverlay(glow, expectedWidthFraction: 0.44);
                Assert.True(Ui.Capture(glow, $"glow-1-listening-{look.Tag}", look.Backdrop) > 2000, "listening: the halo should be drawn");

                glow.ShowWorking();
                Ui.Pump(800);
                Assert.True(glow.IsVisible, "working: should stay on screen");
                Assert.True(Ui.Capture(glow, $"glow-2-working-{look.Tag}", look.Backdrop) > 500, "working: the edge should be drawn");

                glow.ShowDone();
                Ui.Pump(150);
                Assert.True(Ui.Capture(glow, $"glow-3-done-{look.Tag}", look.Backdrop) > 2000, "done: should brighten before it fades, not vanish");
                Ui.Pump(1300);
                Assert.False(glow.IsVisible, "done: should fade out and hide");

                glow.ShowListening();
                Ui.Pump(200);
                glow.HideNow();
                Assert.False(glow.IsVisible, "HideNow: should hide at once");
            }
            finally
            {
                glow.Dispose();
                IndicatorTheme.Forced = null;
                IndicatorTheme.ForcedAnimations = null;
            }
        }
    });

    [Theory]
    [InlineData(IndicatorStyle.Pill, true)]
    [InlineData(IndicatorStyle.Pill, false)]
    [InlineData(IndicatorStyle.Glow, true)]
    [InlineData(IndicatorStyle.Glow, false)]
    public void Demo_PlaysAWholeDictation_OnEitherStyle(IndicatorStyle style, bool animated) => Ui.Run(() =>
    {
        IndicatorTheme.ForcedAnimations = animated;
        IDictationIndicator indicator = IndicatorFactory.Create(style);
        try
        {
            IndicatorDemo demo = IndicatorDemo.Play(indicator);
            Assert.True(demo.IsRunning);
            Ui.Pump(4600);
            Assert.False(demo.IsRunning, "the demo should have reached the done state");

            // Words work on both styles: the glow hands them to a small pill.
            indicator.ShowNotice("Didn't catch that", NoticeKind.Warning);
            Ui.Pump(400);
            indicator.HideNow();
        }
        finally
        {
            indicator.Dispose();
            IndicatorTheme.ForcedAnimations = null;
        }
    });

    [Fact]
    public void StyleNames_FromSettings_AreForgiving()
    {
        Assert.Equal(IndicatorStyle.Glow, IndicatorFactory.ParseStyle("glow"));
        Assert.Equal(IndicatorStyle.Glow, IndicatorFactory.ParseStyle(" Glow "));
        Assert.Equal(IndicatorStyle.Pill, IndicatorFactory.ParseStyle("pill"));
        Assert.Equal(IndicatorStyle.Pill, IndicatorFactory.ParseStyle(null));
        Assert.Equal(IndicatorStyle.Pill, IndicatorFactory.ParseStyle("something else"));
    }

    /// <summary>
    /// The promises every indicator makes: clicks pass through it, it never takes
    /// focus, it stays out of Alt+Tab, and it rests centred on the taskbar edge.
    /// </summary>
    private static void AssertIsQuietOverlay(Window window, double? expectedWidthFraction = null)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        Assert.NotEqual(IntPtr.Zero, hwnd);

        long ex = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
        Assert.True((ex & Win32.WS_EX_TRANSPARENT) != 0, "must be click-through");
        Assert.True((ex & Win32.WS_EX_NOACTIVATE) != 0, "must never take focus");
        Assert.True((ex & Win32.WS_EX_TOOLWINDOW) != 0, "must stay out of Alt+Tab");
        Assert.True((ex & Win32.WS_EX_LAYERED) != 0, "must be a transparent layered window");
        Assert.NotEqual(hwnd, Win32.GetForegroundWindow());

        Assert.True(Win32.GetWindowRect(hwnd, out Win32.RECT rect));
        IntPtr monitor = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        var info = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        Assert.True(Win32.GetMonitorInfoW(monitor, ref info));

        Assert.Equal(info.rcWork.Bottom, rect.Bottom);   // rests on the top edge of the taskbar
        int offCentre = Math.Abs(rect.Left + rect.Right - (info.rcWork.Left + info.rcWork.Right));
        Assert.True(offCentre <= 2, $"should be centred on the work area (off by {offCentre / 2.0} px)");

        if (expectedWidthFraction is double fraction)
        {
            // The stated fraction of the work area, unless that would be narrower than the 360-unit minimum.
            double workWidth = info.rcWork.Right - info.rcWork.Left;
            double scale = System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleX;
            double expected = Math.Max(workWidth * fraction, 360 * scale);
            int actual = rect.Right - rect.Left;
            Assert.True(Math.Abs(actual - expected) <= workWidth * 0.03,
                $"should be about {expected:F0} px wide ({fraction:P0} of the work area); it is {actual} px");
        }
    }
}
