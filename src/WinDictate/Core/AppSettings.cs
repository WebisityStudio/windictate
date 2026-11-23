using System.IO;
using System.Text.Json;

namespace WinDictate.Core;

/// <summary>
/// The user's settings, kept in <c>settings.json</c> in <see cref="DataDir"/>.
///
/// Restricted pilot: there is deliberately no setting for a speech model (one
/// fixed model ships with the app, see <see cref="PilotModel"/>), for transcript
/// history (never kept), for keeping dictations on the clipboard (the user's
/// clipboard is always put back), for AI cleanup or for starting with Windows
/// (none of these exist in this build). Settings files from other builds still
/// load: properties this build does not know are ignored, and are dropped the
/// next time settings are saved.
/// </summary>
public sealed class AppSettings
{
    public string Hotkey { get; set; } = "Ctrl+Alt";
    public int MinHoldMs { get; set; } = 500;
    public int MaxRecordSeconds { get; set; } = 300;
    public string VocabularyHint { get; set; } = "";
    public string InsertMode { get; set; } = "paste"; // "paste" or "type"
    public bool AddTrailingSpace { get; set; } = true; // so the next dictation does not run into this one
    // How the app shows it is listening: "pill" (a small glass capsule above the
    // taskbar) or "glow" (the taskbar edge lights up, no shape).
    public string Indicator { get; set; } = "pill";
    public int ClipboardRestoreDelayMs { get; set; } = 300;
    public int MicDeviceIndex { get; set; } = -1; // -1 = system default
    // Product name of the chosen microphone, resolved to its current index when
    // recording starts (indexes shift after docking). "" = system default, or a
    // settings file from before names were stored (MicDeviceIndex is used as-is).
    public string MicDeviceName { get; set; } = "";
    public int Threads { get; set; } = 0; // 0 = auto
    // Per-buffer level that always counts as voice; quieter buffers still count
    // when well above the clip's own background (see SpeechGate).
    public double SilenceThreshold { get; set; } = 0.004;
    public int MinVoicedMs { get; set; } = 120; // reject a clip only if it has less voiced audio than this

    /// <summary>
    /// Which version of the pilot-use notice the user has acknowledged ("" = none
    /// yet). Only the fact of the acknowledgement is stored, never anything the
    /// user said or dictated.
    /// </summary>
    public string NoticeAcknowledged { get; set; } = "";

    /// <summary>
    /// Settings and the event log. Local (not roaming) application data, so
    /// nothing is copied to a roaming-profile server.
    /// </summary>
    public static string DataDir =>
        DataDirOverride ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDictate");

    /// <summary>
    /// Redirects <see cref="DataDir"/> (settings and log). Set only by the unit
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

    /// <summary>Reads settings JSON. Properties missing from older files keep their defaults; unknown ones are ignored.</summary>
    internal static AppSettings Parse(string json) =>
        JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();

    internal static string Serialize(AppSettings settings) =>
        JsonSerializer.Serialize(settings, JsonOptions);

    /// <summary>Deep copy, so worker threads use a snapshot that the Settings window never edits in place.</summary>
    public AppSettings Clone() => Parse(Serialize(this));
}
