using System.Runtime.InteropServices;

namespace WinDictate.UI.Indicators;

/// <summary>
/// The few documented Win32 calls the indicators need: window styles that keep
/// them click-through and unfocusable, and monitor geometry so they appear on
/// the screen the user is working on. Self-contained on purpose.
/// </summary>
internal static class IndicatorNative
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x00000020;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_NOACTIVATE = 0x08000000;

    private const uint MONITOR_DEFAULTTONULL = 0;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>
    /// Makes a window click-through, keeps it out of Alt+Tab and stops it ever
    /// taking focus from the app the user is dictating into.
    /// </summary>
    public static void MakeOverlay(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>
    /// Work area (screen minus taskbar) and DPI scale of the monitor the user is
    /// working on: the one holding <paramref name="preferredWindow"/> if that is
    /// still a window on some monitor, else the one holding the foreground
    /// window, else the one under the mouse pointer, else the primary monitor.
    /// </summary>
    public static bool TryGetTargetWorkArea(IntPtr preferredWindow, out PixelRect work, out double dpiScale)
    {
        work = default;
        dpiScale = 1;
        try
        {
            IntPtr monitor = IntPtr.Zero;
            if (preferredWindow != IntPtr.Zero && IsWindow(preferredWindow))
                monitor = MonitorFromWindow(preferredWindow, MONITOR_DEFAULTTONULL);
            IntPtr foreground = monitor == IntPtr.Zero ? GetForegroundWindow() : IntPtr.Zero;
            if (foreground != IntPtr.Zero)
                monitor = MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero && GetCursorPos(out POINT cursor))
                monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
                monitor = MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY);
            if (monitor == IntPtr.Zero) return false;

            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfoW(monitor, ref info)) return false;
            work = new PixelRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom);
            if (work.Width <= 0 || work.Height <= 0) return false;

            if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
                dpiScale = dpiX / 96.0;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
