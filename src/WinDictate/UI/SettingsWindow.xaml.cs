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
/// Settings: the everyday choices up front in plain words, everything else
/// under Advanced. Nothing changes until Save.
/// </summary>
public partial class SettingsWindow : Window
{
    private const int MicTestSeconds = 6;
    private const double MicBarWidth = 64;

    private readonly AppSettings _settings;
    private readonly List<string> _micNames = new(); // current waveIn devices, by index
    private int _missingMicItem = -1;                // combo index of a "(not connected)" entry
    private IDictationIndicator? _preview;           // the indicator shown by the Preview button
    private AutoStart.State _autoStartWas;           // what Windows said when the window opened
    private bool _syncingModel;                      // the quality choice and the model list are being lined up

    // Microphone test: listens briefly to move the bar. Nothing is kept.
    private AudioRecorder? _micTest;
    private Task _micOpening = Task.CompletedTask; // the test device being opened; it is closed only after this
    private DispatcherTimer? _micTestTimer;
    private bool _micHeard;
    private double _micLevel;

    public event Action? Saved;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        MaxHeight = Math.Max(420, SystemParameters.WorkArea.Height - 24);
        VersionText.Text = $"WinDictate v{Logger.AppVersion}";

        PopulateModels();
        LoadInto();
        PopulateMics();
        MicRow.Tag = MicLine();
        RefreshModelLines();
        RefreshStatus();

        SaveButton.Click += (_, _) => Save();
        CloseButton.Click += (_, _) => Close();
        OpenDataButton.Click += (_, _) => OpenDataFolder();
        IndicatorPreviewButton.Click += (_, _) => PreviewIndicator();
        MicTestButton.Click += (_, _) => ToggleMicTest();
        MicBox.SelectionChanged += (_, _) => OnMicChosen();
        HotkeyBox.SelectionChanged += (_, _) => ShowShortcutKeys();
        ModelBox.SelectionChanged += (_, _) => OnModelChosen();
        QualityFast.Checked += (_, _) => OnQualityChosen(SpeechQuality.Fast);
        QualityBalanced.Checked += (_, _) => OnQualityChosen(SpeechQuality.Balanced);
        QualityAccurate.Checked += (_, _) => OnQualityChosen(SpeechQuality.Accurate);
        CleanupEnabled.Checked += (_, _) => OnCleanupChanged();
        CleanupEnabled.Unchecked += (_, _) => OnCleanupChanged();
        CleanupUrl.TextChanged += (_, _) => RefreshStatus();
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
        SelectModel(_settings.Model);
        VocabBox.Text = _settings.VocabularyHint;
        bool type = _settings.InsertMode.Equals("type", StringComparison.OrdinalIgnoreCase);
        TypeRadio.IsChecked = type;
        PasteRadio.IsChecked = !type;
        KeepClipboardBox.IsChecked = _settings.KeepInClipboard;
        TrailingSpaceBox.IsChecked = _settings.AddTrailingSpace;
        bool glow = IndicatorFactory.ParseStyle(_settings.Indicator) == IndicatorStyle.Glow;
        IndicatorGlowRadio.IsChecked = glow;
        IndicatorPillRadio.IsChecked = !glow;
        CleanupEnabled.IsChecked = _settings.Cleanup.Enabled;
        CleanupUrl.Text = _settings.Cleanup.BaseUrl;
        CleanupModel.Text = _settings.Cleanup.Model;
        CleanupKey.Password = _settings.Cleanup.ApiKey;
        CleanupFields.IsEnabled = _settings.Cleanup.Enabled;
        CleanupFields.Opacity = _settings.Cleanup.Enabled ? 1 : 0.5;
        LogTranscripts.IsChecked = _settings.LogTranscripts;

        _autoStartWas = AutoStart.Read();
        AutoStartBox.IsChecked = _autoStartWas == AutoStart.State.On;
        AutoStartRow.Tag = _autoStartWas == AutoStart.State.OffInWindows
            ? "Switched off in Windows' Startup apps. Turn it on here to allow it again"
            : "Ready as soon as you sign in";
    }

    private void ShowShortcutKeys() =>
        ShortcutKeys.ItemsSource = SettingsLogic.HotkeyKeys((HotkeyBox.SelectedItem as ComboBoxItem)?.Content?.ToString());

    /// <summary>The line at the top: says "everything stays on this PC" only while that is true.</summary>
    private void RefreshStatus()
    {
        SettingsStatus status = SettingsLogic.StatusLine(
            File.Exists(Transcriber.ModelPath(SelectedModel())),
            CleanupEnabled.IsChecked == true,
            CleanupUrl.Text);
        StatusText.Text = status.Text;
        StatusText.ToolTip = status.Text;
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
            status.AllGood ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush");
    }

    private void OnCleanupChanged()
    {
        bool on = CleanupEnabled.IsChecked == true;
        CleanupFields.IsEnabled = on;
        CleanupFields.Opacity = on ? 1 : 0.5;
        RefreshStatus();
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

    // ----- Speech model -----

    private void PopulateModels()
    {
        ModelBox.Items.Clear();
        foreach (ModelInfo model in SettingsLogic.Models)
            ModelBox.Items.Add(new ComboBoxItem { Content = $"{model.Name} ({model.Summary})", Tag = model.Name });
    }

    private string SelectedModel() => (ModelBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "small.en";

    private void SelectModel(string model)
    {
        _syncingModel = true;
        try
        {
            ComboBoxItem? match = ModelBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, model, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                // A model named by hand in settings.json: keep it rather than replace it on save.
                match = new ComboBoxItem { Content = model, Tag = model };
                ModelBox.Items.Add(match);
            }
            ModelBox.SelectedItem = match;
            ShowQuality(SettingsLogic.QualityOf(model));
        }
        finally
        {
            _syncingModel = false;
        }
    }

    private void ShowQuality(SpeechQuality? quality)
    {
        QualityFast.IsChecked = quality == SpeechQuality.Fast;
        QualityBalanced.IsChecked = quality == SpeechQuality.Balanced;
        QualityAccurate.IsChecked = quality == SpeechQuality.Accurate;
    }

    private void OnQualityChosen(SpeechQuality quality)
    {
        if (_syncingModel) return;
        SelectModel(SettingsLogic.ModelFor(quality));
        RefreshModelLines();
        RefreshStatus();
    }

    private void OnModelChosen()
    {
        if (_syncingModel) return;
        _syncingModel = true;
        try { ShowQuality(SettingsLogic.QualityOf(SelectedModel())); }
        finally { _syncingModel = false; }
        RefreshModelLines();
        RefreshStatus();
    }

    private void RefreshModelLines()
    {
        string model = SelectedModel();
        bool downloaded = File.Exists(Transcriber.ModelPath(model));
        QualityRow.Tag = SettingsLogic.QualityLine(model, downloaded);
        ModelRow.Tag = SettingsLogic.ModelLine(model, downloaded);
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
        _settings.Model = SelectedModel();
        _settings.VocabularyHint = VocabBox.Text.Trim();
        _settings.InsertMode = TypeRadio.IsChecked == true ? "type" : "paste";
        _settings.KeepInClipboard = KeepClipboardBox.IsChecked == true;
        _settings.AddTrailingSpace = TrailingSpaceBox.IsChecked == true;
        _settings.Indicator = SelectedIndicator() == IndicatorStyle.Glow ? "glow" : "pill";
        SaveMicChoice();
        _settings.Cleanup.Enabled = CleanupEnabled.IsChecked == true;
        _settings.Cleanup.BaseUrl = CleanupUrl.Text.Trim();
        _settings.Cleanup.Model = CleanupModel.Text.Trim();
        _settings.Cleanup.ApiKey = CleanupKey.Password;
        _settings.LogTranscripts = LogTranscripts.IsChecked == true;

        AppSettings.Save(_settings);
        SaveAutoStart();
        Saved?.Invoke();
        Close();
    }

    /// <summary>Touches Windows' startup list only when the switch was actually changed.</summary>
    private void SaveAutoStart()
    {
        bool want = AutoStartBox.IsChecked == true;
        if (want == (_autoStartWas == AutoStart.State.On)) return;
        if (AutoStart.Set(want)) return;
        MessageBox.Show(this,
            "Windows did not allow the \"Start with Windows\" change. Your other settings were saved.",
            "WinDictate", MessageBoxButton.OK, MessageBoxImage.Warning);
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
