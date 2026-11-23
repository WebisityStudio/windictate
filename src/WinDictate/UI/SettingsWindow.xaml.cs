using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WinDictate.Core;
using WinDictate.UI.Indicators;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace WinDictate.UI;

/// <summary>
/// Settings: the everyday choices up front in plain words, the rest under
/// Advanced. In the restricted pilot the privacy behaviour (no history, the
/// clipboard always put back, no network) and the speech model are fixed and
/// only shown here. Nothing changes until Save.
/// </summary>
public partial class SettingsWindow : Window
{
    private const int MicTestSeconds = 6;
    private const double MicBarWidth = 64;

    private readonly AppSettings _settings;
    private readonly ModelCheck? _modelState;        // the model check's result, or null while it runs
    private readonly List<string> _micNames = new(); // current waveIn devices, by index
    private int _missingMicItem = -1;                // combo index of a "(not connected)" entry
    private IDictationIndicator? _preview;           // the indicator shown by the Preview button

    // Microphone test: listens briefly to move the bar. Nothing is kept.
    private AudioRecorder? _micTest;
    private Task _micOpening = Task.CompletedTask; // the test device being opened; it is closed only after this
    private DispatcherTimer? _micTestTimer;
    private bool _micHeard;
    private double _micLevel;

    public event Action? Saved;

    public SettingsWindow(AppSettings settings, ModelCheck? modelState = null)
    {
        InitializeComponent();
        _settings = settings;
        _modelState = modelState;
        MaxHeight = Math.Max(420, SystemParameters.WorkArea.Height - 24);
        VersionText.Text = $"{BuildInfo.Product} {BuildInfo.Version}";

        LoadInto();
        PopulateMics();
        MicRow.Tag = MicLine();
        RefreshStatus();

        SaveButton.Click += (_, _) => Save();
        CloseButton.Click += (_, _) => Close();
        OpenDataButton.Click += (_, _) => OpenDataFolder();
        AboutButton.Click += (_, _) => AboutWindow.ShowSingle(_modelState, _settings.Hotkey);
        IndicatorPreviewButton.Click += (_, _) => PreviewIndicator();
        MicTestButton.Click += (_, _) => ToggleMicTest();
        MicBox.SelectionChanged += (_, _) => OnMicChosen();
        HotkeyBox.SelectionChanged += (_, _) => ShowShortcutKeys();
        AdvancedToggle.Checked += (_, _) => ShowAdvanced(true);
        AdvancedToggle.Unchecked += (_, _) => ShowAdvanced(false);
        SizeChanged += (_, _) => KeepOnScreen();
        SourceInitialized += (_, _) => WindowBackdrop.TryUseGlass(this);
        Activated += (_, _) => WindowBackdrop.TryUseGlass(this); // the theme may have put its own background back
        Closed += (_, _) =>
        {
            StopMicTest(null);
            _preview?.Dispose();
            _preview = null;
        };
    }

    // ----- Loading -----

    private void LoadInto()
    {
        SelectCombo(HotkeyBox, _settings.Hotkey);
        ShowShortcutKeys();
        MinHoldBox.Text = _settings.MinHoldMs.ToString();
        VocabBox.Text = _settings.VocabularyHint;
        bool type = _settings.InsertMode.Equals("type", StringComparison.OrdinalIgnoreCase);
        TypeRadio.IsChecked = type;
        PasteRadio.IsChecked = !type;
        TrailingSpaceBox.IsChecked = _settings.AddTrailingSpace;
        bool glow = IndicatorFactory.ParseStyle(_settings.Indicator) == IndicatorStyle.Glow;
        IndicatorGlowRadio.IsChecked = glow;
        IndicatorPillRadio.IsChecked = !glow;
    }

    private void ShowShortcutKeys() =>
        ShortcutKeys.ItemsSource = SettingsLogic.HotkeyKeys((HotkeyBox.SelectedItem as ComboBoxItem)?.Content?.ToString());

    /// <summary>The line at the top: where things stand.</summary>
    private void RefreshStatus()
    {
        SettingsStatus status = SettingsLogic.StatusLine(_modelState);
        StatusText.Text = status.Text;
        StatusText.ToolTip = status.Text;
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
            status.AllGood ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush");
    }

    // ----- Advanced -----

    private void ShowAdvanced(bool show)
    {
        AdvancedPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        // Once the window has grown (or started scrolling), bring the new part into view.
        Dispatcher.InvokeAsync(() => AdvancedPanel.BringIntoView(), DispatcherPriority.Loaded);
    }

    /// <summary>The window grows downwards when Advanced opens; keep its bottom edge on the screen.</summary>
    private void KeepOnScreen()
    {
        if (!IsLoaded || double.IsNaN(Top)) return;
        Rect area = SystemParameters.WorkArea;
        if (Top + ActualHeight > area.Bottom)
            Top = Math.Max(area.Top, area.Bottom - ActualHeight);
    }

    // ----- Indicator preview -----

    private IndicatorStyle SelectedIndicator() =>
        IndicatorGlowRadio.IsChecked == true ? IndicatorStyle.Glow : IndicatorStyle.Pill;

    /// <summary>Plays a pretend dictation on the chosen indicator. Records nothing, inserts nothing.</summary>
    private void PreviewIndicator()
    {
        try
        {
            _preview?.Dispose();
            _preview = IndicatorFactory.Create(SelectedIndicator());
            IndicatorDemo.Play(_preview);
        }
        catch (Exception ex)
        {
            Logger.Error("Indicator preview failed", ex);
        }
    }

    // ----- Microphone -----

    private void PopulateMics()
    {
        MicBox.Items.Clear();
        _micNames.Clear();
        _missingMicItem = -1;
        MicBox.Items.Add("System default");
        try { _micNames.AddRange(AudioRecorder.DeviceNames()); }
        catch (Exception ex) { Logger.Warn("Could not list microphones: " + ex.Message); }
        for (int i = 0; i < _micNames.Count; i++)
            MicBox.Items.Add($"{i}: {_micNames[i]}");

        if (MicrophoneSelector.IsMissing(_settings.MicDeviceName, _micNames))
        {
            // Keep the choice while the device is unplugged, so saving other
            // settings does not quietly switch it to the system default.
            _missingMicItem = MicBox.Items.Add($"{_settings.MicDeviceName} (not connected)");
            MicBox.SelectedIndex = _missingMicItem;
            return;
        }
        int idx = MicrophoneSelector.Resolve(_settings.MicDeviceName, _settings.MicDeviceIndex, _micNames);
        MicBox.SelectedIndex = idx >= 0 ? idx + 1 : 0;
    }

    /// <summary>The everyday row names the microphone in use; it is chosen under Advanced.</summary>
    private string MicLine()
    {
        int item = MicBox.SelectedIndex;
        if (_missingMicItem >= 0 && item == _missingMicItem)
            return $"{_settings.MicDeviceName} is not connected: using the system default";
        return item > 0 && item - 1 < _micNames.Count ? _micNames[item - 1] : "System default";
    }

    private void OnMicChosen()
    {
        StopMicTest(null);
        MicRow.Tag = MicLine();
    }

    private void SaveMicChoice()
    {
        int item = MicBox.SelectedIndex;
        if (_missingMicItem >= 0 && item == _missingMicItem)
            return; // chosen microphone is unplugged: keep it as saved
        if (item <= 0 || item - 1 >= _micNames.Count)
        {
            _settings.MicDeviceIndex = MicrophoneSelector.SystemDefault;
            _settings.MicDeviceName = "";
        }
        else
        {
            _settings.MicDeviceIndex = item - 1;
            _settings.MicDeviceName = _micNames[item - 1];
        }
    }

    /// <summary>
    /// Starts or stops the microphone test: listens for a few seconds so the bar
    /// can show that the chosen microphone hears something. The sound is only
    /// measured; nothing is transcribed, stored or sent anywhere.
    /// </summary>
    private void ToggleMicTest()
    {
        if (_micTest != null)
        {
            StopMicTest(_micHeard ? "It hears you" : null);
            return;
        }

        int item = MicBox.SelectedIndex;
        if (_missingMicItem >= 0 && item == _missingMicItem)
        {
            MicRow.Tag = "That microphone is not connected. Pick another under Advanced";
            return;
        }
        int device = item > 0 && item - 1 < _micNames.Count ? item - 1 : MicrophoneSelector.SystemDefault;

        var recorder = new AudioRecorder();
        _micTest = recorder;
        _micHeard = false;
        _micLevel = 0;
        MicTestButton.Content = "Stop";
        MicRow.Tag = "Say something: the bar should move";
        recorder.LevelChanged += peak => Dispatcher.InvokeAsync(() => OnMicTestLevel(recorder, peak));

        // Opening a device can take a moment (Bluetooth headsets), so not on the UI thread.
        Task opening = Task.Run(() => recorder.Start(device));
        opening.ContinueWith(t =>
        {
            Logger.Warn("Microphone test could not start: " + t.Exception?.GetBaseException().Message);
            Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_micTest, recorder)) StopMicTest("Could not open this microphone");
            });
        }, TaskContinuationOptions.OnlyOnFaulted);
        _micOpening = opening; // whenever the test ends, the device is closed once this has finished

        _micTestTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(MicTestSeconds) };
        _micTestTimer.Tick += (_, _) =>
            StopMicTest(_micHeard ? "It hears you" : "No sound heard. Check it, or pick another under Advanced");
        _micTestTimer.Start();
    }

    private void OnMicTestLevel(AudioRecorder recorder, float peak)
    {
        if (!ReferenceEquals(_micTest, recorder)) return;
        if (peak >= SettingsLogic.HeardPeak) _micHeard = true;
        // Rise at once, fall gently, so the bar is easy to read.
        _micLevel = Math.Max(SettingsLogic.MeterLevel(peak), _micLevel * 0.85);
        MicLevelBar.Width = MicBarWidth * _micLevel;
    }

    /// <param name="line">What to say about the test; null to go back to naming the microphone.</param>
    private void StopMicTest(string? line)
    {
        _micTestTimer?.Stop();
        _micTestTimer = null;
        AudioRecorder? recorder = _micTest;
        _micTest = null;
        if (recorder == null) return;

        MicTestButton.Content = "Test";
        MicLevelBar.Width = 0;
        MicRow.Tag = line ?? MicLine();
        _micOpening.ContinueWith(_ => recorder.Dispose(), TaskScheduler.Default);
        _micOpening = Task.CompletedTask;
    }

    // ----- Saving -----

    private void Save()
    {
        if (!int.TryParse(MinHoldBox.Text, out int minHold) || minHold < 0 || minHold > 5000)
        {
            AdvancedToggle.IsChecked = true; // show the field the message is about
            MessageBox.Show(this, "\"Ignore quick taps\" must be a number between 0 and 5000.", "WinDictate",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.Hotkey = (HotkeyBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Ctrl+Alt";
        _settings.MinHoldMs = minHold;
        _settings.VocabularyHint = VocabBox.Text.Trim();
        _settings.InsertMode = TypeRadio.IsChecked == true ? "type" : "paste";
        _settings.AddTrailingSpace = TrailingSpaceBox.IsChecked == true;
        _settings.Indicator = SelectedIndicator() == IndicatorStyle.Glow ? "glow" : "pill";
        SaveMicChoice();

        AppSettings.Save(_settings);
        Saved?.Invoke();
        Close();
    }

    private static void SelectCombo(System.Windows.Controls.ComboBox box, string value)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if ((item.Content?.ToString() ?? "").Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
        // Keep hand-edited combos from settings.json (e.g. "RCtrl") instead of
        // silently clobbering them with the first preset on save.
        var custom = new ComboBoxItem { Content = value };
        box.Items.Add(custom);
        box.SelectedItem = custom;
    }

    private static void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            Process.Start(new ProcessStartInfo { FileName = AppSettings.DataDir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not open data folder: " + ex.Message);
        }
    }
}
