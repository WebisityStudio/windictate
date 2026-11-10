using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WinDictate.UI;

/// <summary>
/// Asks Windows 11 for its own blurred-glass window background (the material it
/// uses for menus and flyouts). Where Windows cannot or will not draw it (older
/// builds, transparency effects switched off, high contrast, battery saver),
/// nothing changes and the window keeps the plain background its theme gave it.
/// </summary>
internal static class WindowBackdrop
{
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_TRANSIENTWINDOW = 3; // "acrylic": blurs whatever is behind the window

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <returns>True if Windows accepted the request.</returns>
    public static bool TryUseGlass(Window window)
    {
        try
        {
            if (SystemParameters.HighContrast) return false;
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return false;
            int kind = DWMSBT_TRANSIENTWINDOW;
            return DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref kind, sizeof(int)) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
