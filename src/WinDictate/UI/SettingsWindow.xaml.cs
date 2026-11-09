using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using WinDictate.Core;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace WinDictate.UI;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly List<string> _micNames = new(); // current waveIn devices, by index
    private int _missingMicItem = -1;                // combo index of a "(not connected)" entry
    public event Action? Saved;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        SubtitleText.Text = $"Local push-to-talk dictation · v{Logger.AppVersion}";
        LoadInto();
        PopulateMics();
        UpdateModelStatus();

        SaveButton.Click += (_, _) => Save();
        CloseButton.Click += (_, _) => Close();
        OpenDataButton.Click += (_, _) => OpenDataFolder();
        ModelBox.SelectionChanged += (_, _) => UpdateModelStatus();
    }

    private void LoadInto()
    {
        SelectCombo(HotkeyBox, _settings.Hotkey);
        MinHoldBox.Text = _settings.MinHoldMs.ToString();
        SelectModel(_settings.Model);
        VocabBox.Text = _settings.VocabularyHint;
        PasteRadio.IsChecked = !_settings.InsertMode.Equals("type", StringComparison.OrdinalIgnoreCase);
        TypeRadio.IsChecked = _settings.InsertMode.Equals("type", StringComparison.OrdinalIgnoreCase);
        KeepClipboardBox.IsChecked = _settings.KeepInClipboard;
        CleanupEnabled.IsChecked = _settings.Cleanup.Enabled;
        CleanupUrl.Text = _settings.Cleanup.BaseUrl;
        CleanupModel.Text = _settings.Cleanup.Model;
        CleanupKey.Password = _settings.Cleanup.ApiKey;
        LogTranscripts.IsChecked = _settings.LogTranscripts;
    }

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

    private void UpdateModelStatus()
    {
        var model = SelectedModel();
        var path = Transcriber.ModelPath(model);
        if (File.Exists(path))
        {
            long mb = new FileInfo(path).Length / 1024 / 1024;
            ModelStatusText.Text = $"✓ {model} is downloaded ({mb} MB) — works offline.";
        }
        else
        {
            ModelStatusText.Text = $"⤓ {model} will download on first use.";
        }
    }

    private void Save()
    {
        if (!int.TryParse(MinHoldBox.Text, out int minHold) || minHold < 0 || minHold > 5000)
        {
            MessageBox.Show("Minimum hold must be a number between 0 and 5000.", "WinDictate",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.Hotkey = (HotkeyBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Ctrl+Alt";
        _settings.MinHoldMs = minHold;
        _settings.Model = SelectedModel();
        _settings.VocabularyHint = VocabBox.Text.Trim();
        _settings.InsertMode = TypeRadio.IsChecked == true ? "type" : "paste";
        _settings.KeepInClipboard = KeepClipboardBox.IsChecked == true;
        SaveMicChoice();
        _settings.Cleanup.Enabled = CleanupEnabled.IsChecked == true;
        _settings.Cleanup.BaseUrl = CleanupUrl.Text.Trim();
        _settings.Cleanup.Model = CleanupModel.Text.Trim();
        _settings.Cleanup.ApiKey = CleanupKey.Password;
        _settings.LogTranscripts = LogTranscripts.IsChecked == true;

        AppSettings.Save(_settings);
        Saved?.Invoke();
        Close();
    }

    private string SelectedModel() => SelectedModelFrom(ModelBox.SelectedItem as ComboBoxItem);

    private static string SelectedModelFrom(ComboBoxItem? item)
    {
        var text = item?.Content?.ToString() ?? "small.en";
        return text.Split(' ')[0].Trim();
    }

    private void SelectModel(string model)
    {
        foreach (ComboBoxItem item in ModelBox.Items)
        {
            if (SelectedModelFrom(item).Equals(model, StringComparison.OrdinalIgnoreCase))
            {
                ModelBox.SelectedItem = item;
                return;
            }
        }
        ModelBox.SelectedIndex = 2; // small.en
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
