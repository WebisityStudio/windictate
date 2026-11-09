namespace WinDictate.UI.Indicators;

/// <summary>A rectangle in physical screen pixels (virtual-desktop coordinates).</summary>
internal readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>
/// Pure placement maths, kept free of Win32 and WPF so it can be unit-tested
/// on any OS. Coordinates are physical pixels and may be negative (a monitor
/// to the left of, or above, the primary one).
/// </summary>
internal static class IndicatorPlacement
{
    /// <summary>
    /// Top-left corner that centres a window of the given pixel size horizontally
    /// in <paramref name="work"/> and rests it on the work area's bottom edge
    /// (the top of a bottom taskbar).
    /// </summary>
    public static (int X, int Y) BottomCentre(PixelRect work, int widthPx, int heightPx)
    {
        // Floor division keeps the result stable for negative coordinates.
        int x = work.Left + (int)Math.Floor((work.Width - widthPx) / 2.0);
        int y = work.Bottom - heightPx;
        return (x, y);
    }

    /// <summary>
    /// Width in device-independent pixels for an indicator that should span a
    /// fraction of the work area, never narrower than <paramref name="minimumDip"/>.
    /// </summary>
    public static double FractionalWidthDip(PixelRect work, double dpiScale, double fraction, double minimumDip)
    {
        if (dpiScale <= 0) dpiScale = 1;
        double workDip = work.Width / dpiScale;
        return Math.Max(minimumDip, Math.Round(workDip * Math.Clamp(fraction, 0.05, 1.0)));
    }
}
