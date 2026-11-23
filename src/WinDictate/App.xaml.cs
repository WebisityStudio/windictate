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
    /// <summary>A second copy started with this argument asks the running one to exit (for uninstall and IT scripts).</summary>
    internal const string ExitArgument = "--exit";

    private const string SingleInstanceName = "WinDictate.SingleInstance.9b1f";
    private const string ExitSignalName = @"Local\WinDictate.Exit.9b1f";

    private Mutex? _singleInstance;
    private EventWaitHandle? _exitSignal;
    private RegisteredWaitHandle? _exitWait;
    private KeyboardHook? _hook;
    private HotkeyMonitor? _hotkeys;
    private HotkeyWatchdog? _watchdog;
    private DictationController? _controller;
    private Transcriber? _transcriber;
    private IndicatorPresenter? _indicators;
    private NotifyIcon? _tray;
    private AppSettings _settings = new();
    private Icon? _idleIcon;
    private Icon? _busyIcon;
    private ModelCheck? _modelProblem; // UI thread only

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(a => string.Equals(a, ExitArgument, StringComparison.OrdinalIgnoreCase)))
        {
            Shutdown(AskRunningCopyToExit() ? 0 : 1);
            return;
        }

        _singleInstance = new Mutex(true, SingleInstanceName, out bool isNew);
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

        // Can this PC run the speech engine at all? Checked before anything native loads.
        string? problem = SystemCheck.Problem(SystemCheck.CpuSupported(), SystemCheck.MissingRuntimeLibraries());
        if (problem != null)
        {
            Logger.Error("Cannot start: " + problem);
            MessageBox.Show(problem, "WinDictate", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        _settings = AppSettings.Load();

        // The pilot-use notice: dictation only starts once it has been acknowledged.
        if (!PilotNotice.IsAcknowledged(_settings))
        {
            if (!PilotNoticeWindow.AskOnFirstRun(_settings.Hotkey))
            {
                Logger.Info("Pilot notice not acknowledged; exiting");
                Shutdown();
                return;
            }
            _settings.NoticeAcknowledged = PilotNotice.Version;
            AppSettings.Save(_settings);
            Logger.Info($"Pilot notice version {PilotNotice.Version} acknowledged");
        }

        // The listening indicator (glass pill or edge glow); its windows are only
        // created on screen the first time something is shown.
        _indicators = new IndicatorPresenter(Dispatcher, IndicatorFactory.ParseStyle(_settings.Indicator));

        OnStatus("Starting…"); // before the model warm-up can report "Ready"
        _transcriber = new Transcriber();
        _controller = new DictationController(_settings, _indicators,
            new WindowsDictationPlatform(), _transcriber, new WindowsTextTarget());
        _controller.StatusChanged += OnStatus;
        _controller.ModelUnavailable += (check, message) => Dispatcher.BeginInvoke(() => OnModelUnavailable(check, message));
        _controller.Start();

        _hotkeys = new HotkeyMonitor(_settings.Hotkey);
        _hotkeys.SessionStarted += id => _controller!.OnSessionStarted(id);
        _hotkeys.SessionEnded += (id, reason) =>
        {
            _controller!.OnSessionEnded(id, reason);
            // A release that never arrived may mean Windows dropped the hook.
            if (reason == SessionEndReason.KeysLost) _hook?.Reinstall();
        };
        // Time limit reached or microphone lost: the monitor ends the session and
        // ignores the still-held keys until they are released.
        _controller.SessionStopRequested += id => _hotkeys!.StopSession(id);

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

        bool hookInstalled = true;
        try
        {
            _hook.Start();
        }
        catch (Exception ex)
        {
            hookInstalled = false;
            Logger.Error("Keyboard hook failed to install", ex);
            MessageBox.Show("WinDictate could not register its keyboard shortcut, so dictation will not start. " +
                "Security software or a group policy may block low-level keyboard hooks.\n\n" + ex.Message,
                "WinDictate", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        ListenForExitRequests();
        SetupTray();
        if (!hookInstalled) OnStatus("Shortcut unavailable");
        Logger.Info($"Ready. Hotkey = {_hotkeys.ActiveHotkeyText}, hook {(hookInstalled ? "installed" : "NOT installed")}");
    }

    /// <summary>
    /// Run as <c>WinDictate.exe --exit</c>: asks a running copy to exit cleanly
    /// and waits up to 10 seconds for it to finish.
    /// </summary>
    /// <returns>True when no copy is running any more.</returns>
    private static bool AskRunningCopyToExit()
    {
        if (EventWaitHandle.TryOpenExisting(ExitSignalName, out EventWaitHandle? signal))
        {
            using (signal) signal.Set();
        }
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (!Mutex.TryOpenExisting(SingleInstanceName, out Mutex? running)) return true;
            running.Dispose();
            Thread.Sleep(100);
        }
        return false;
    }

    private void ListenForExitRequests()
    {
        try
        {
            _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitSignalName);
            _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitSignal, (_, _) =>
                Dispatcher.BeginInvoke(() =>
                {
                    Logger.Info($"Exit requested ({ExitArgument})");
                    Shutdown();
                }), null, Timeout.Infinite, executeOnlyOnce: true);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not listen for exit requests: {ex.GetType().Name}");
        }
    }

    /// <summary>The model is missing or not the approved file: nothing can work, so say so and exit.</summary>
    private void OnModelUnavailable(ModelCheck check, string message)
    {
        _modelProblem = check;
        MessageBox.Show("WinDictate cannot start dictation.\n\n" + message +
            "\n\nAsk IT to reinstall WinDictate Restricted Pilot. Expected file: models\\" + PilotModel.FileName +
            " (SHA-256 " + PilotModel.Sha256 + ").", "WinDictate", MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(3);
    }

    private ModelCheck? ModelState =>
        _modelProblem ?? (_transcriber?.IsReady == true ? ModelCheck.Ok : null);

    private void SetupTray()
    {
        _idleIcon = IconFactory.Create(false);
        _busyIcon = IconFactory.Create(true);

        var menu = new ContextMenuStrip();
        var header = new ToolStripMenuItem($"{BuildInfo.Product} {BuildInfo.Version}") { Enabled = false };
        menu.Items.Add(header);
        menu.Items.Add(new ToolStripSeparator());

        var hotkeyInfo = new ToolStripMenuItem(HotkeyHint()) { Enabled = false };
        menu.Items.Add(hotkeyInfo);

        var enabledItem = new ToolStripMenuItem("Enabled") { Checked = true, CheckOnClick = true };
        enabledItem.CheckedChanged += (_, _) =>
        {
            _hotkeys!.Enabled = enabledItem.Checked;
            OnStatus(enabledItem.Checked ? "Ready" : "Paused");
        };
        menu.Items.Add(enabledItem);

        // For a paste that went nowhere (the cursor was not in a text box): the last
        // dictation stays in memory for two minutes and can be copied from here.
        var copyLastItem = new ToolStripMenuItem("Copy last dictation");
        copyLastItem.Click += async (_, _) =>
        {
            if (_controller == null || !await _controller.CopyLastDictationAsync())
                Logger.Info("Copy last dictation: nothing to copy");
        };
        menu.Items.Add(copyLastItem);
        menu.Opening += (_, _) => copyLastItem.Enabled = _controller?.CanCopyLastDictation == true;

        menu.Items.Add(new ToolStripSeparator());

        var settingsItem = new ToolStripMenuItem("Settings…");
        settingsItem.Click += (_, _) => OpenSettings(hotkeyInfo);
        menu.Items.Add(settingsItem);

        var aboutItem = new ToolStripMenuItem("About…");
        aboutItem.Click += (_, _) => AboutWindow.ShowSingle(ModelState, _hotkeys!.ActiveHotkeyText);
        menu.Items.Add(aboutItem);

        var logItem = new ToolStripMenuItem("Open log");
        logItem.Click += (_, _) => OpenFile(Logger.LogPath);
        menu.Items.Add(logItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => Shutdown();
        menu.Items.Add(exitItem);

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

    private string HotkeyHint() => $"Hold {_hotkeys!.ActiveHotkeyText} to dictate · Esc cancels";

    private void OpenSettings(ToolStripMenuItem hotkeyInfo)
    {
        var existing = Windows.OfType<SettingsWindow>().FirstOrDefault();
        if (existing != null) { existing.Activate(); return; }

        var win = new SettingsWindow(_settings, ModelState);
        win.Saved += () =>
        {
            _hotkeys!.Reconfigure(_settings.Hotkey);
            hotkeyInfo.Text = HotkeyHint();
            _controller!.UpdateSettings(_settings);
            _indicators!.UseStyle(IndicatorFactory.ParseStyle(_settings.Indicator));
            Logger.Info($"Settings updated. Hotkey = {_hotkeys.ActiveHotkeyText}, insert = {_settings.InsertMode}, indicator = {_settings.Indicator}");
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

    private string? _lastStatus; // UI thread only

    private void OnStatus(string status)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _lastStatus = status;
            if (_tray == null) return;
            var text = $"WinDictate - {status}";
            _tray.Text = text.Length > 63 ? text[..63] : text;
            bool busy = status is "Listening…" or "Transcribing…" or "Inserting…" or "Loading speech model…";
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
            Logger.Warn("Could not open file: " + ex.GetType().Name);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstance != null) Logger.Info("Shutting down");
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _exitWait?.Unregister(null);
        _exitSignal?.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _watchdog?.Dispose();
        _hook?.Dispose();
        _controller?.Dispose(); // also removes undelivered text from the clipboard
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
