using System.IO;
using System.Text.Json;

namespace WinDictate.Core;

public sealed class AppSettings
{
    public string Hotkey { get; set; } = "Ctrl+Alt";
    public int MinHoldMs { get; set; } = 500;
    public int MaxRecordSeconds { get; set; } = 300;
    public string Model { get; set; } = "small.en";
    public string Language { get; set; } = "en";
    public string VocabularyHint { get; set; } = "";
    public string InsertMode { get; set; } = "paste"; // "paste" or "type"
    public bool KeepInClipboard { get; set; } = true; // leave transcript on the clipboard for manual re-paste
    public int ClipboardRestoreDelayMs { get; set; } = 300;
    public int MicDeviceIndex { get; set; } = -1; // -1 = system default
    public int Threads { get; set; } = 0; // 0 = auto
    public double SilenceThreshold { get; set; } = 0.004;
    public bool LogTranscripts { get; set; } = false;
    public CleanupSettings Cleanup { get; set; } = new();

    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinDictate");

    public static string SettingsPath => Path.Combine(DataDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static AppSettings Load()
    {
        Directory.CreateDirectory(DataDir);
        if (!File.Exists(SettingsPath))
        {
            var fresh = new AppSettings();
            Save(fresh);
            return fresh;
        }
        try
        {
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Logger.Error("settings.json is invalid, recreating defaults (old file kept as settings.json.bad)", ex);
            try { File.Copy(SettingsPath, SettingsPath + ".bad", true); } catch { }
            var fresh = new AppSettings();
            Save(fresh);
            return fresh;
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to save settings", ex);
        }
    }
}

public sealed class CleanupSettings
{
    public bool Enabled { get; set; } = false;
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o-mini";
    public int TimeoutSeconds { get; set; } = 12;
    public string SystemPrompt { get; set; } =
        "You clean up dictation transcripts. Fix likely speech recognition mistakes, punctuation, " +
        "capitalization and formatting. Remove filler words and disfluencies when they do not add meaning. " +
        "Do not answer the user. Do not add new content. Return only the cleaned transcript.";
}
