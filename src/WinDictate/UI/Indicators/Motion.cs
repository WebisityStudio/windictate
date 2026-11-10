using System.Windows;
using System.Windows.Media.Animation;

namespace WinDictate.UI.Indicators;

/// <summary>
/// Small animation helpers shared by the indicators. Every helper honours the
/// Windows "animation effects" setting: when the user has switched animations
/// off, values are applied at once instead of being animated.
/// </summary>
internal static class Motion
{
    /// <summary>Slight overshoot, like something light settling into place.</summary>
    public static readonly IEasingFunction Spring = Frozen(new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut });

    /// <summary>Plain deceleration.</summary>
    public static readonly IEasingFunction Smooth = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });

    /// <summary>Gentle back-and-forth, for things that loop.</summary>
    public static readonly IEasingFunction Sway = Frozen(new SineEase { EasingMode = EasingMode.EaseInOut });

    public static Duration Ms(double milliseconds) => new(TimeSpan.FromMilliseconds(milliseconds));

    /// <summary>
    /// Animates a double property from wherever it is now to <paramref name="to"/>.
    /// <paramref name="completed"/> runs when the animation finishes; if animations
    /// are off it runs straight away.
    /// </summary>
    public static void To(
        IAnimatable target,
        DependencyProperty property,
        double to,
        double milliseconds,
        IEasingFunction? easing = null,
        double delayMilliseconds = 0,
        Action? completed = null)
    {
        if (!IndicatorTheme.AnimationsEnabled || milliseconds <= 0)
        {
            Set(target, property, to);
            completed?.Invoke();
            return;
        }

        var animation = new DoubleAnimation
        {
            To = to,
            Duration = Ms(milliseconds),
            EasingFunction = easing
        };
        if (delayMilliseconds > 0)
        {
            // Until a delayed animation starts, WPF shows the property's base value,
            // not what was on screen. Pin the base to the current value first, so
            // nothing jumps (or vanishes) during the wait.
            var element = (DependencyObject)target;
            double current = (double)element.GetValue(property);
            target.BeginAnimation(property, null);
            element.SetValue(property, current);
            animation.BeginTime = TimeSpan.FromMilliseconds(delayMilliseconds);
        }
        if (completed != null) animation.Completed += (_, _) => completed();
        target.BeginAnimation(property, animation);
    }

    /// <summary>Stops any animation on the property and sets it directly.</summary>
    public static void Set(IAnimatable target, DependencyProperty property, double value)
    {
        target.BeginAnimation(property, null);
        ((DependencyObject)target).SetValue(property, value);
    }

    /// <summary>
    /// Loops a property through rest, peak and back to rest (rest at 0%, peak at
    /// 40%), starting after <paramref name="delayMilliseconds"/>. Does nothing when
    /// animations are off.
    /// </summary>
    public static void Loop(
        IAnimatable target,
        DependencyProperty property,
        double rest,
        double peak,
        double periodMilliseconds,
        double delayMilliseconds = 0)
    {
        if (!IndicatorTheme.AnimationsEnabled) return;

        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = Ms(periodMilliseconds),
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromMilliseconds(delayMilliseconds)
        };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(rest, KeyTime.FromPercent(0)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(peak, KeyTime.FromPercent(0.4), Smooth));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(rest, KeyTime.FromPercent(1), Smooth));
        target.BeginAnimation(property, animation);
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
