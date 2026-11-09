using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;

namespace WinDictate.UiSmoke;

/// <summary>Plumbing for tests that open real WPF windows.</summary>
internal static class Ui
{
    /// <summary>Where pictures are saved: UI_SMOKE_OUT if set, else next to the test binaries.</summary>
    public static string OutputDir { get; } = ResolveOutputDir();

    private static string ResolveOutputDir()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable("UI_SMOKE_OUT");
        string dir = string.IsNullOrWhiteSpace(fromEnvironment)
            ? Path.Combine(AppContext.BaseDirectory, "ui-smoke-out")
            : fromEnvironment;
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Runs <paramref name="body"/> on a fresh STA thread with its own dispatcher,
    /// as WPF requires, and rethrows anything it threw on the calling thread.
    /// </summary>
    public static void Run(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(90)))
            throw new TimeoutException("The UI smoke test did not finish within 90 seconds.");
        failure?.Throw();
    }

    /// <summary>Lets animations, timers and layout run for a while.</summary>
    public static void Pump(int milliseconds, Action? every30Milliseconds = null)
    {
        var frame = new DispatcherFrame();
        var stop = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        stop.Tick += (_, _) =>
        {
            stop.Stop();
            frame.Continue = false;
        };

        DispatcherTimer? beat = null;
        if (every30Milliseconds != null)
        {
            beat = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(30) };
            beat.Tick += (_, _) => every30Milliseconds();
            beat.Start();
        }

        stop.Start();
        Dispatcher.PushFrame(frame);
        beat?.Stop();
    }

    /// <summary>Feeds a speech-like microphone level for a while, as the recorder would.</summary>
    public static void Speak(Action<float> setLevel, int milliseconds)
    {
        double t = 0;
        Pump(milliseconds, () =>
        {
            t += 0.03;
            setLevel(WinDictate.UI.Indicators.IndicatorDemo.SyntheticPeak(t));
        });
    }

    /// <summary>
    /// Saves what a window is drawing right now, over a backdrop with grey bars
    /// standing in for text, as a PNG at twice real size.
    /// </summary>
    /// <param name="plain">Leave out the grey bars: for an ordinary window rather than an overlay.</param>
    /// <returns>How many pixels the window itself drew (anything not fully transparent).</returns>
    public static int Capture(Window window, string name, Color backdrop, bool plain = false)
    {
        window.UpdateLayout();
        if (window.Content is not FrameworkElement content)
            throw new InvalidOperationException($"{name}: the window has no content to capture.");
        double width = content.ActualWidth, height = content.ActualHeight;
        if (width < 1 || height < 1)
            throw new InvalidOperationException($"{name}: nothing has been laid out yet.");

        const double zoom = 2;
        int pixelWidth = (int)Math.Ceiling(width * zoom), pixelHeight = (int)Math.Ceiling(height * zoom);

        // What the window itself draws; transparent everywhere else.
        var drawn = new RenderTargetBitmap(pixelWidth, pixelHeight, 96 * zoom, 96 * zoom, PixelFormats.Pbgra32);
        drawn.Render(content);

        // The same, over a backdrop.
        var scene = new DrawingVisual();
        using (DrawingContext context = scene.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(backdrop), null, new Rect(0, 0, width, height));
            var textLine = new SolidColorBrush(Color.FromArgb(0x55, 0x80, 0x88, 0x92));
            int row = 0;
            for (double y = 10; !plain && y < height - 6; y += 14, row++)
                context.DrawRectangle(textLine, null, new Rect(16, y, Math.Max(40, width - 32 - row % 3 * 40), 5));
            context.DrawImage(drawn, new Rect(0, 0, width, height));
        }
        var composed = new RenderTargetBitmap(pixelWidth, pixelHeight, 96 * zoom, 96 * zoom, PixelFormats.Pbgra32);
        composed.Render(scene);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(composed));
        using (FileStream stream = File.Create(Path.Combine(OutputDir, name + ".png")))
            encoder.Save(stream);

        return CountDrawnPixels(drawn);
    }

    /// <summary>
    /// Tries to give a window the keyboard focus. Windows may refuse (a test
    /// process is not always allowed to take the foreground), so callers must
    /// check the result rather than assume it.
    /// </summary>
    public static bool TakeForeground(Window window)
    {
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (attempt == 1)
            {
                // A known nudge: Windows allows the switch after an Alt tap.
                Win32.keybd_event(Win32.VK_MENU, 0, 0, UIntPtr.Zero);
                Win32.keybd_event(Win32.VK_MENU, 0, Win32.KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            window.Activate();
            Win32.SetForegroundWindow(hwnd);
            Pump(250);
            if (Win32.GetForegroundWindow() == hwnd) return true;
        }
        return false;
    }

    /// <summary>Records something a person reading the results should know (for example a check that could not run).</summary>
    public static void Note(string name, string text) =>
        File.AppendAllText(Path.Combine(OutputDir, "notes.txt"), $"{name}: {text}{Environment.NewLine}");

    private static int CountDrawnPixels(BitmapSource bitmap)
    {
        int stride = bitmap.PixelWidth * 4;
        byte[] pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        int drawn = 0;
        for (int i = 3; i < pixels.Length; i += 4)
            if (pixels[i] > 16) drawn++;   // alpha channel of BGRA
        return drawn;
    }
}

/// <summary>The few Win32 reads the assertions need.</summary>
internal static class Win32
{
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TRANSPARENT = 0x00000020;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_LAYERED = 0x00080000;
    public const long WS_EX_NOACTIVATE = 0x08000000;
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    public const byte VK_MENU = 0x12;   // Alt
    public const byte VK_F24 = 0x87;    // a key nothing listens for
    public const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>Synthesises a key event (Windows marks it as injected).</summary>
    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO info);
}
