using System.IO;

namespace WinDictate.Core;

/// <summary>
/// Minimal event log. Never logs transcript contents or clipboard data unless
/// the user explicitly enables LogTranscripts in settings.
/// </summary>
public static class Logger
{
    private static readonly object Sync = new();

    public static string LogPath => Path.Combine(AppSettings.DataDir, "windictate.log");

    public static string AppVersion =>
        typeof(Logger).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

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
        Info("WinDictate " + AppVersion + " starting");
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
