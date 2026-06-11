using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using WinDictate.Core;
using WinDictate.UI;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace WinDictate;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private KeyboardHook? _hook;
    private HotkeyMonitor? _hotkeys;
    private DictationController? _controller;
    private OverlayWindow? _overlay;
    private NotifyIcon? _tray;
    private AppSettings _settings = new();
    private Icon? _idleIcon;
    private Icon? _busyIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, "WinDictate.SingleInstance.9b1f", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("WinDictate is already running (look for the tray icon).", "WinDictate",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            Logger.Error("Unhandled UI exception", ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            Logger.Error("Unhandled domain exception", ex.ExceptionObject as Exception);

        Logger.Init();
        _settings = AppSettings.Load();

        _overlay = new OverlayWindow();
        _overlay.Show();   // realize the handle so we can set ex-styles
        _overlay.HideOverlay();

        _controller = new DictationController(Dispatcher, _overlay, _settings);
        _controller.StatusChanged += OnStatus;

        _hotkeys = new HotkeyMonitor(_settings.Hotkey);
        _hotkeys.SessionStarted += () => _controller!.OnSessionStarted();
        _hotkeys.SessionEnded += cancelled => _controller!.OnSessionEnded(cancelled);

        _hook = new KeyboardHook();
        _hook.KeyEvent += (vk, down) => _hotkeys!.OnKeyEvent(vk, down);
        try
        {
            _hook.Install();
        }
        catch (Exception ex)
        {
            Logger.Error("Keyboard hook failed to install", ex);
            MessageBox.Show("WinDictate could not register the global hotkey. " +
                "Antivirus or group policy may block low-level keyboard hooks.\n\n" + ex.Message,
                "WinDictate", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        SetupTray();
        OnStatus("Starting…");
        Logger.Info($"Ready. Hotkey = {_hotkeys.ActiveHotkeyText}");
    }

    private void SetupTray()
    {
        _idleIcon = IconFactory.Create(false);
        _busyIcon = IconFactory.Create(true);

        var menu = new ContextMenuStrip();
        var header = new ToolStripMenuItem($"WinDictate v{Logger.AppVersion}") { Enabled = false };
        menu.Items.Add(header);
        menu.Items.Add(new ToolStripSeparator());

        var hotkeyInfo = new ToolStripMenuItem($"Hold {_hotkeys!.ActiveHotkeyText} to dictate") { Enabled = false };
        menu.Items.Add(hotkeyInfo);

        var enabledItem = new ToolStripMenuItem("Enabled") { Checked = true, CheckOnClick = true };
        enabledItem.CheckedChanged += (_, _) =>
        {
            _hotkeys!.Enabled = enabledItem.Checked;
            OnStatus(enabledItem.Checked ? "Ready" : "Paused");
        };
        menu.Items.Add(enabledItem);

        menu.Items.Add(new ToolStripSeparator());

        var settingsItem = new ToolStripMenuItem("Settings…");
        settingsItem.Click += (_, _) => OpenSettings(hotkeyInfo);
        menu.Items.Add(settingsItem);

        var logItem = new ToolStripMenuItem("Open log");
        logItem.Click += (_, _) => OpenFile(Logger.LogPath);
        menu.Items.Add(logItem);

        menu.Items.Add(new ToolStripSeparator());

        var quitItem = new ToolStripMenuItem("Quit");
        quitItem.Click += (_, _) => Shutdown();
        menu.Items.Add(quitItem);

        _tray = new NotifyIcon
        {
            Icon = _idleIcon,
            Visible = true,
            Text = "WinDictate",
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => OpenSettings(hotkeyInfo);
    }

    private void OpenSettings(ToolStripMenuItem hotkeyInfo)
    {
        var existing = Windows.OfType<SettingsWindow>().FirstOrDefault();
        if (existing != null) { existing.Activate(); return; }

        var win = new SettingsWindow(_settings);
        win.Saved += () =>
        {
            _hotkeys!.Reconfigure(_settings.Hotkey);
            hotkeyInfo.Text = $"Hold {_hotkeys.ActiveHotkeyText} to dictate";
            _controller!.UpdateSettings(_settings);
            Logger.Info($"Settings updated. Hotkey = {_hotkeys.ActiveHotkeyText}, model = {_settings.Model}");
        };
        win.Show();
        win.Activate();
    }

    private void OnStatus(string status)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_tray == null) return;
            var text = $"WinDictate — {status}";
            _tray.Text = text.Length > 63 ? text[..63] : text;
            bool busy = status is "Transcribing…" or "Cleaning up…" or "Inserting…";
            _tray.Icon = busy ? _busyIcon : _idleIcon;
        });
    }

    private static void OpenFile(string path)
    {
        try
        {
            if (!File.Exists(path)) { File.WriteAllText(path, ""); }
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not open file: " + ex.Message);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Info("Shutting down");
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _hook?.Dispose();
        _controller?.Dispose();
        _idleIcon?.Dispose();
        _busyIcon?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}

/// <summary>Draws a simple microphone tray icon at runtime so no asset file is needed.</summary>
internal static class IconFactory
{
    public static Icon Create(bool active)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var body = active ? Color.FromArgb(0xFF, 0x4D, 0x6D) : Color.FromArgb(0x2E, 0x9A, 0xFF);
            using var brush = new SolidBrush(body);
            using var pen = new Pen(body, 2.4f);
            g.FillRoundedRectangle(brush, 12, 5, 8, 14, 4);   // capsule
            g.DrawArc(pen, 8, 8, 16, 16, 20, 140);            // cradle
            g.DrawLine(pen, 16, 24, 16, 28);                  // stem
            g.DrawLine(pen, 12, 28, 20, 28);                  // base
        }
        IntPtr hIcon = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(hIcon).Clone(); }
        finally { NativeMethods.DestroyIcon(hIcon); }
    }

    private static void FillRoundedRectangle(this Graphics g, Brush brush, float x, float y, float w, float h, float r)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(x, y, r * 2, r * 2, 180, 90);
        path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}
