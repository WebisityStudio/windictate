using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinDictate.Core;
using Xunit;

namespace WinDictate.Tests;

/// <summary>
/// Static checks on the application's source that fail the build if anything
/// the restricted pilot leaves out comes back: network code (a cleanup service,
/// a model download), endpoint or key settings, transcript history, audio
/// files, or a start-with-Windows registry entry. They read the source files
/// under src/WinDictate, so they run on any OS as part of every build.
/// The Windows smoke tests check the compiled program the same way.
/// </summary>
public sealed class PilotGuardTests
{
    private static readonly string Root = RepoRoot();
    private static readonly string AppSource = Path.Combine(Root, "src", "WinDictate");

    private static IEnumerable<(string Path, string Text)> SourceFiles() =>
        Directory.EnumerateFiles(AppSource, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => f.EndsWith(".cs") || f.EndsWith(".xaml") || f.EndsWith(".csproj") || f.EndsWith(".manifest") || f.EndsWith(".json"))
            .Select(f => (Path.GetRelativePath(Root, f), File.ReadAllText(f)));

    private static void AssertAbsent(string what, Regex pattern, Func<string, bool>? allowedIn = null)
    {
        var hits = new List<string>();
        foreach (var (path, text) in SourceFiles())
        {
            if (allowedIn?.Invoke(path) == true) continue;
            foreach (Match match in pattern.Matches(text))
            {
                int line = text[..match.Index].Count(c => c == '\n') + 1;
                hits.Add($"{path}:{line}: {match.Value}");
            }
        }
        Assert.True(hits.Count == 0, $"{what} must not be in the restricted pilot:\n" + string.Join("\n", hits));
    }

    private static Regex Words(params string[] words) =>
        new(string.Join("|", words.Select(Regex.Escape)), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void SourceIsFound() =>
        Assert.True(SourceFiles().Count() > 20, $"expected the app's source under {AppSource}");

    [Fact]
    public void NoNetworkCode() =>
        AssertAbsent("Network code", Words(
            "HttpClient", "HttpRequestMessage", "HttpWebRequest", "WebClient", "WebRequest", "System.Net.Http",
            "System.Net.Sockets", "TcpClient", "UdpClient", "ClientWebSocket", "new Socket(", "Dns.Get",
            "WhisperGgmlDownloader", "SmtpClient", "FtpWebRequest"));

    [Fact]
    public void NoRemoteCleanupEndpointOrKeys() =>
        AssertAbsent("A remote AI cleanup endpoint or API key", Words(
            "chat/completions", "openai", "api.openai", "ApiKey", "api_key", "Bearer ", "Authorization",
            "CleanupService", "CleanupSettings", "BaseUrl"));

    [Fact]
    public void NoWebAddresses_ExceptXamlAndManifestSchemas() =>
        AssertAbsent("A web address", new Regex(@"https?://(?!schemas\.microsoft\.com/)[^\s""'<>]+", RegexOptions.IgnoreCase));

    [Fact]
    public void NoModelDownload() =>
        AssertAbsent("Model download code", Words("huggingface", "resolve/main", "DownloadModel", ".part\""));

    [Fact]
    public void NoTranscriptHistory() =>
        AssertAbsent("Transcript history", Words("transcription-history", "LogTranscripts", "AppendHistory", "History.tsv"));

    [Fact]
    public void NoStartWithWindowsEntry() =>
        AssertAbsent("A start-with-Windows entry", Words(@"CurrentVersion\Run", "StartupApproved", "AutoStart"));

    [Fact]
    public void NoAudioFiles() =>
        AssertAbsent("Writing audio to a file", Words(".wav\"", ".pcm\"", "WaveFileWriter(\"", "CreateWaveFile", "WaveFileWriter.Create"));

    /// <summary>Only settings, the log and opening (never writing) the model touch files.</summary>
    [Fact]
    public void FilesAreWrittenOnlyForSettingsAndTheLog()
    {
        var writes = new Regex(@"File\.(Write|Append|Create|Copy|Move|Open)\w*\(|new FileStream\(|new StreamWriter\(", RegexOptions.CultureInvariant);
        var allowed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.Combine("src", "WinDictate", "Core", "AppSettings.cs")] = 2,  // settings.json, and a copy of a broken one
            [Path.Combine("src", "WinDictate", "Core", "Logger.cs")] = 2,       // the log, and its one rotated copy
            [Path.Combine("src", "WinDictate", "Core", "PilotModel.cs")] = 1,   // reads the model to check it (FileAccess.Read)
            [Path.Combine("src", "WinDictate", "App.xaml.cs")] = 1,             // creates an empty log before opening it
        };
        var problems = new List<string>();
        foreach (var (path, text) in SourceFiles())
        {
            int count = writes.Matches(text).Count;
            int limit = allowed.TryGetValue(path, out int n) ? n : 0;
            if (count > limit) problems.Add($"{path}: {count} file operations (allowed {limit})");
        }
        Assert.True(problems.Count == 0, "Unexpected file writing:\n" + string.Join("\n", problems));
        Assert.Contains("FileAccess.Read", File.ReadAllText(Path.Combine(AppSource, "Core", "PilotModel.cs")));
    }

    [Fact]
    public void TheModelPinnedInTheApp_MatchesTheReleaseRecord()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "pilot", "model.json")));
        var model = doc.RootElement;
        Assert.Equal(PilotModel.FileName, model.GetProperty("fileName").GetString());
        Assert.Equal(PilotModel.SizeBytes, model.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(PilotModel.Sha256, model.GetProperty("sha256").GetString());
        Assert.Equal(PilotModel.Name, model.GetProperty("name").GetString());
        string url = model.GetProperty("source").GetProperty("url").GetString()!;
        Assert.Contains(model.GetProperty("source").GetProperty("revision").GetString()!, url); // pinned, not "main"
        Assert.EndsWith("/" + PilotModel.FileName, url);
    }

    [Fact]
    public void ThePackageVersionsAreLocked()
    {
        string lockFile = Path.Combine(AppSource, "packages.lock.json");
        Assert.True(File.Exists(lockFile), "src/WinDictate/packages.lock.json must be committed");
        string csproj = File.ReadAllText(Path.Combine(AppSource, "WinDictate.csproj"));
        Assert.DoesNotContain("Version=\"*", csproj);
        Assert.Contains("\"version\": \"10.0.404\"", File.ReadAllText(Path.Combine(Root, "global.json")));
    }

    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
}
