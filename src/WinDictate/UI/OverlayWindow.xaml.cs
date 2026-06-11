using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WinDictate.Core;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace WinDictate.UI;

public partial class OverlayWindow : Window
{
    public OverlayWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Make the overlay click-through and non-activating so it never steals
        // focus from the app the user is dictating into.
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = NativeMethods.GetWindowLongPtrW(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLongPtrW(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
        PositionBottomCentre();
    }

    private void PositionBottomCentre()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Bottom - ActualHeight - 48;
    }

    public void ShowListening(string targetTitle)
    {
        StatusText.Text = "Listening…";
        TargetText.Text = "→ " + targetTitle;
        Dot.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x6D));
        MeterTrack.Visibility = Visibility.Visible;
        MeterFill.Width = 0;
        Opacity = 1;
        Show();
        PositionBottomCentre();
        StartPulse();
    }

    public void ShowStatus(string text)
    {
        StatusText.Text = text;
        TargetText.Text = "";
        Dot.Fill = new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0xFF));
        MeterTrack.Visibility = Visibility.Collapsed;
        StopPulse();
    }

    public void SetLevel(float peak)
    {
        double w = Math.Clamp(peak * 3.0, 0, 1) * 60.0;
        MeterFill.Width = w;
    }

    public void Flash(string text, bool success)
    {
        StatusText.Text = text;
        TargetText.Text = "";
        Dot.Fill = new SolidColorBrush(success
            ? Color.FromRgb(0x4C, 0xD9, 0x64)
            : Color.FromRgb(0xFF, 0xA5, 0x00));
        MeterTrack.Visibility = Visibility.Collapsed;
        StopPulse();
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(900))
        {
            BeginTime = TimeSpan.FromMilliseconds(700)
        };
        fade.Completed += (_, _) => Hide();
        BeginAnimation(OpacityProperty, fade);
    }

    public void HideOverlay()
    {
        StopPulse();
        BeginAnimation(OpacityProperty, null);
        Hide();
    }

    private void StartPulse()
    {
        var pulse = new DoubleAnimation(0.9, 0.0, TimeSpan.FromMilliseconds(1100))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        PulseRing.BeginAnimation(OpacityProperty, pulse);
        var grow = new DoubleAnimation(0.6, 1.4, TimeSpan.FromMilliseconds(1100))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        var scale = new ScaleTransform(1, 1);
        PulseRing.RenderTransformOrigin = new Point(0.5, 0.5);
        PulseRing.RenderTransform = scale;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private void StopPulse()
    {
        PulseRing.BeginAnimation(OpacityProperty, null);
        PulseRing.Opacity = 0;
        if (PulseRing.RenderTransform is ScaleTransform s)
        {
            s.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            s.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }
    }
}
