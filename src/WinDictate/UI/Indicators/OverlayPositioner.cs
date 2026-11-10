using System.Windows;
using System.Windows.Interop;

namespace WinDictate.UI.Indicators;

/// <summary>
/// Puts an indicator window on the monitor the user is working on, resting on
/// the top edge of the taskbar and centred. Works across monitors with
/// different scaling: positions are set in physical pixels through Win32, while
/// WPF keeps ownership of the window's size.
/// </summary>
internal static class OverlayPositioner
{
    /// <param name="window">A window that has already been shown (it needs a handle).</param>
    /// <param name="targetWindow">The window being dictated into, or zero for whichever has focus now.</param>
    /// <param name="widthFraction">
    /// When set, the window is resized to this fraction of the work area's width
    /// (never below <paramref name="minimumWidthDip"/>); otherwise its width is left alone.
    /// </param>
    /// <returns>false when the monitor could not be read; the window then stays where it was.</returns>
    public static bool PlaceBottomCentre(Window window, IntPtr targetWindow = default, double? widthFraction = null, double minimumWidthDip = 0)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;
        if (!IndicatorNative.TryGetTargetWorkArea(targetWindow, out PixelRect work, out double scale)) return false;

        // 1. Hop onto the target monitor first, so WPF adopts that monitor's DPI.
        IndicatorNative.SetWindowPos(hwnd, IntPtr.Zero, work.Left, work.Top, 0, 0,
            IndicatorNative.SWP_NOSIZE | IndicatorNative.SWP_NOZORDER | IndicatorNative.SWP_NOACTIVATE);

        // 2. Let WPF size the window in device-independent pixels at that DPI.
        if (widthFraction is double fraction)
            window.Width = IndicatorPlacement.FractionalWidthDip(work, scale, fraction, minimumWidthDip);

        // 3. Place it using the pixel size it really ended up with.
        if (!IndicatorNative.GetWindowRect(hwnd, out IndicatorNative.RECT rect)) return false;
        (int x, int y) = IndicatorPlacement.BottomCentre(work, rect.Right - rect.Left, rect.Bottom - rect.Top);
        return IndicatorNative.SetWindowPos(hwnd, IndicatorNative.HWND_TOPMOST, x, y, 0, 0,
            IndicatorNative.SWP_NOSIZE | IndicatorNative.SWP_NOACTIVATE);
    }
}
