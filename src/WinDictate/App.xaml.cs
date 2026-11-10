using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using WinDictate.Core;
using WinDictate.UI;
using WinDictate.UI.Indicators;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace WinDictate;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private KeyboardHook? _hook;
    private HotkeyMonitor? _hotkeys;
    private HotkeyWatchdog? _watchdog;
    private DictationController? _controller;
    private IndicatorPresenter? _indicators;
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

        // The listening indicator (glass pill or edge glow); its windows are only
        // created on screen the first time something is shown.
        _indicators = new IndicatorPresenter(Dispatcher, IndicatorFactory.ParseStyle(_settings.Indicator));

        OnStatus("Starting…"); // before the model warm-up can report "Ready"
        _controller = new DictationController(_settings, _indicators,
            new WindowsDictationPlatform(), new Transcriber(), new WindowsTextTarget());
        _controller.StatusChanged += OnStatus;
        _controller.Start();

        _hotkeys = new HotkeyMonitor(_settings.Hotkey);
        _hotkeys.SessionStarted += id => _controller!.OnSessionStarted(id);
        _hotkeys.SessionEnded += (id, reason) =>
        {
            _controller!.OnSessionEnded(id, reason);
            // A release that never arrived may mean Windows dropped the hook.
            if (reason == SessionEndReason.KeysLost) _hook?.Reinstall();
        };
        _controller.MaxDurationReached += id => _hotkeys!.StopSession(id);

        // Ends a session whose key-up never arrives (Ctrl+Alt+Del, UAC prompt,
        // elevated window) so the microphone cannot stay open; off the hook thread.
        _watchdog = new HotkeyWatchdog(_hotkeys);

        // The hook runs on its own thread and calls the hotkey state machine
        // directly from the hook callback (see KeyboardHook and HotkeyMonitor).
        _hook = new KeyboardHook();
        _hook.KeyEvent += (vk, down) => _hotkeys!.OnKeyEvent(vk, down);

        // Stop recording if the workstation locks or disconnects — the key-up
        // events land on the secure desktop and are never delivered, so without
        // this the mic would keep capturing the room until MaxRecordSeconds.
        // Unlock and resume re-install the hook, which Windows may have dropped.
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;

        try
        {
            _hook.Start();
        }
        catch (Exception ex)
        {
            Logger.Error("Keyboard hook failed to install", ex);
            MessageBox.Show("WinDictate could not register the global hotkey. " +
                "Antivirus or group policy may block low-level keyboard hooks.\n\n" + ex.Message,
                "WinDictate", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        SetupTray();
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
        if (_lastStatus != null) OnStatus(_lastStatus); // a status reported before the tray existed
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
            _indicators!.UseStyle(IndicatorFactory.ParseStyle(_settings.Indicator));
            Logger.Info($"Settings updated. Hotkey = {_hotkeys.ActiveHotkeyText}, model = {_settings.Model}");
        };
        win.Show();
        win.Activate();
    }

    private void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionLock
                     or Microsoft.Win32.SessionSwitchReason.RemoteDisconnect
                     or Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect)
        {
            // Finished dictations wait, and are then kept on the clipboard rather
            // than typed into whatever has focus after the unlock.
            _controller?.SetWorkstationLocked(true);
            _hotkeys?.Reset(); // cancels any active dictation (audio discarded)
        }
        else if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionUnlock
                          or Microsoft.Win32.SessionSwitchReason.SessionLogon
                          or Microsoft.Win32.SessionSwitchReason.RemoteConnect
                          or Microsoft.Win32.SessionSwitchReason.ConsoleConnect)
        {
            _controller?.SetWorkstationLocked(false);
            RefreshHook($"session {e.Reason}");
        }
    }

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Suspend)
        {
            Logger.Info("Suspending: cancelling any dictation in progress");
            _controller?.SetSuspended(true);
            _hotkeys?.Reset(); // cancels any active dictation (audio discarded)
        }
        else if (e.Mode == Microsoft.Win32.PowerModes.Resume)
        {
            _controller?.SetSuspended(false);
            RefreshHook("resume");
        }
    }

    /// <summary>
    /// Windows silently drops a low-level hook that once ran too long, and gives
    /// no notice, so re-register it whenever it may have been lost. Key state
    /// from before is stale, so forget it as well.
    /// </summary>
    private void RefreshHook(string reason)
    {
        Logger.Info($"Re-installing keyboard hook after {reason}");
        _hook?.Reinstall();
        _hotkeys?.Reset();
    }

    private bool _downloadNotified;
    private string? _lastStatus; // UI thread only

    private void OnStatus(string status)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _lastStatus = status;
            if (_tray == null) return;
            var text = $"WinDictate - {status}";
            _tray.Text = text.Length > 63 ? text[..63] : text;
            bool busy = status is "Listening…" or "Transcribing…" or "Cleaning up…" or "Inserting…"
                        || status.StartsWith("Downloading", StringComparison.Ordinal);
            _tray.Icon = busy ? _busyIcon : _idleIcon;

            if (!_downloadNotified && status.StartsWith("Downloading", StringComparison.Ordinal))
            {
                _downloadNotified = true;
                _tray.ShowBalloonTip(5000, "WinDictate",
                    "Downloading the speech model (one time). Dictation will be ready in a few minutes.",
                    ToolTipIcon.Info);
            }
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
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _watchdog?.Dispose();
        _hook?.Dispose();
        _controller?.Dispose();
        _indicators?.Dispose();
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
