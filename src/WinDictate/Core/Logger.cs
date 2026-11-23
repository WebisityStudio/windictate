using System.IO;

namespace WinDictate.Core;

/// <summary>
/// Minimal event log (windictate.log in the data folder, rotated at 1 MB with
/// one older copy kept). It records operational events only: versions, model
/// checks, microphone and hotkey problems, durations, word counts and error
/// types. It never records audio, transcripts, clipboard contents, text sent
/// to other applications, or window titles.
/// </summary>
public static class Logger
{
    private static readonly object Sync = new();

    public static string LogPath => Path.Combine(AppSettings.DataDir, "windictate.log");

    public static string AppVersion => BuildInfo.Version;

    public static void Init()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            var fi = new FileInfo(LogPath);
            if (fi.Exists && fi.Length > 1_000_000)
            {
                File.Copy(LogPath, LogPath + ".old", true);
                File.Delete(LogPath);
            }
        }
        catch { }
        string build = BuildInfo.IsReleaseBuild ? "commit " + BuildInfo.ShortCommit : "developer build";
        Info($"{BuildInfo.Product} {AppVersion} ({build}) starting on {Environment.OSVersion.VersionString}, " +
             $"{Environment.ProcessorCount} logical processors");
    }

    public static void Info(string message) => WriteLine("INFO ", message);
    public static void Warn(string message) => WriteLine("WARN ", message);

    public static void Error(string message, Exception? ex = null) =>
        WriteLine("ERROR", ex == null ? message : message + " :: " + ex);

    private static void WriteLine(string level, string message)
    {
        lock (Sync)
        {
            try
            {
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
