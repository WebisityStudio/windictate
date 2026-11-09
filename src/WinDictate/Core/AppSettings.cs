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
    public bool AddTrailingSpace { get; set; } = true; // so the next dictation does not run into this one
    public int ClipboardRestoreDelayMs { get; set; } = 300;
    public int MicDeviceIndex { get; set; } = -1; // -1 = system default
    // Product name of the chosen microphone, resolved to its current index when
    // recording starts (indexes shift after docking). "" = system default, or a
    // settings file from before names were stored (MicDeviceIndex is used as-is).
    public string MicDeviceName { get; set; } = "";
    public int Threads { get; set; } = 0; // 0 = auto
    // Per-buffer level that always counts as voice; quieter buffers still count
    // when well above the clip's own background (see SpeechGate). Same name and
    // default as before, so existing settings.json files keep their value.
    public double SilenceThreshold { get; set; } = 0.004;
    public int MinVoicedMs { get; set; } = 120; // reject a clip only if it has less voiced audio than this
    public bool LogTranscripts { get; set; } = false;
    public CleanupSettings Cleanup { get; set; } = new();

    public static string DataDir =>
        DataDirOverride ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinDictate");

    /// <summary>
    /// Redirects <see cref="DataDir"/> (settings, log, models). Set only by the unit
    /// tests, so a test run never writes to the user's profile. Not configurable
    /// from outside the assembly.
    /// </summary>
    internal static string? DataDirOverride { get; set; }

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
            return Parse(File.ReadAllText(SettingsPath));
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
            File.WriteAllText(SettingsPath, Serialize(settings));
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to save settings", ex);
        }
    }

    /// <summary>Reads settings JSON. Properties missing from older files keep their defaults.</summary>
    internal static AppSettings Parse(string json) =>
        JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();

    internal static string Serialize(AppSettings settings) =>
        JsonSerializer.Serialize(settings, JsonOptions);

    /// <summary>Deep copy, so worker threads use a snapshot that the Settings window never edits in place.</summary>
    public AppSettings Clone() => Parse(Serialize(this));
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
