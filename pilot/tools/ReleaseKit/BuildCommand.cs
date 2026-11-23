using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReleaseKit;

/// <summary>The pinned speech model, as recorded in pilot/model.json.</summary>
public sealed record ModelRecord(string Name, string FileName, long SizeBytes, string Sha256, string Url, string Repository, string Revision)
{
    public static ModelRecord Load(string root)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "pilot", "model.json")));
        var m = doc.RootElement;
        var source = m.GetProperty("source");
        return new ModelRecord(
            m.GetProperty("name").GetString()!, m.GetProperty("fileName").GetString()!,
            m.GetProperty("sizeBytes").GetInt64(), m.GetProperty("sha256").GetString()!,
            source.GetProperty("url").GetString()!, source.GetProperty("repository").GetString()!,
            source.GetProperty("revision").GetString()!);
    }
}

/// <summary>Everything the later steps need to know about this release.</summary>
public sealed class ReleaseContext
{
    public required string Root { get; init; }
    public required string Version { get; init; }
    public required string Commit { get; init; }
    public required DateTimeOffset CommitDate { get; init; }
    public required DateTimeOffset BuildDate { get; init; }
    public required string SdkVersion { get; init; }
    public required bool Dirty { get; init; }
    public required string OutputRoot { get; init; }
    public required ModelRecord Model { get; init; }
    public string RepositoryUrl { get; init; } = "https://github.com/WebisityStudio/windictate";
    public string RuntimeVersion { get; set; } = "";
    public string WorkDir => Path.Combine(OutputRoot, "_work");
    public string PackageName => $"WinDictate-Restricted-Pilot-{Version}-Submission";
    public string SubmissionDir => Path.Combine(OutputRoot, PackageName);
    public string ZipPath => Path.Combine(OutputRoot, PackageName + ".zip");
    public string SourceZipName => $"WinDictate-Restricted-Pilot-{Version}-source.zip";
    public string ShortCommit => Commit[..12];
    public List<string> Log { get; } = new();

    public void Say(string line)
    {
        string stamped = $"{DateTime.UtcNow:HH:mm:ss} {line}";
        Console.WriteLine(stamped);
        Log.Add(stamped);
    }
}

/// <summary>
/// The whole release, in the order the brief sets out: clean commit, locked
/// restore, tests, self-contained publish, fixed model, source snapshot,
/// package structure, SBOM, manifest, sanitisation, hashes, final ZIP.
/// </summary>
public static class BuildCommand
{
    public static async Task<int> RunAsync(Options options)
    {
        string root = FindRoot();
        string output = Path.GetFullPath(options.Required("out"));
        bool allowDirty = options.Flag("allow-dirty");

        string status = Run(root, "git", "status --porcelain --untracked-files=normal").Trim();
        bool dirty = status.Length > 0;
        if (dirty && !allowDirty)
            throw new ReleaseException("The working tree has uncommitted changes. A release is built from a commit only:\n" + status);

        string commit = Run(root, "git", "rev-parse HEAD").Trim();
        var commitDate = DateTimeOffset.Parse(Run(root, "git", "log -1 --format=%cI HEAD").Trim(), CultureInfo.InvariantCulture);
        string pinnedSdk = PinnedSdk(root);
        string sdk = Run(root, "dotnet", "--version").Trim();
        if (sdk != pinnedSdk)
            throw new ReleaseException($"The .NET SDK in use is {sdk}, but global.json pins {pinnedSdk}.");

        var ctx = new ReleaseContext
        {
            Root = root,
            Version = ProjectVersion(root),
            Commit = commit,
            CommitDate = commitDate,
            BuildDate = options.Get("build-date") is { } fixedDate
                ? DateTimeOffset.Parse(fixedDate, CultureInfo.InvariantCulture)
                : DateTimeOffset.UtcNow,
            SdkVersion = sdk,
            Dirty = dirty,
            OutputRoot = output,
            Model = ModelRecord.Load(root)
        };

        ctx.Say($"WinDictate Restricted Pilot {ctx.Version} from commit {commit}{(dirty ? " (UNCOMMITTED CHANGES: dry run only)" : "")}");
        ctx.Say($".NET SDK {sdk}; output {output}");

        if (Directory.Exists(ctx.SubmissionDir) || File.Exists(ctx.ZipPath))
            throw new ReleaseException($"{ctx.PackageName} already exists in {output}. A frozen package is never overwritten: use an empty output folder.");
        Directory.CreateDirectory(ctx.WorkDir);

        ctx.Say("Restoring packages in locked mode (fails if any version differs from packages.lock.json)");
        Run(root, "dotnet", "restore WinDictate.sln --locked-mode", ctx);
        Run(root, "dotnet", "restore tests/WinDictate.UiSmoke/WinDictate.UiSmoke.csproj --locked-mode", ctx);

        if (!options.Flag("skip-tests"))
        {
            ctx.Say("Running the unit tests (includes the guards against network code, cleanup endpoints, history and audio files)");
            Run(root, "dotnet", "test tests/WinDictate.Tests/WinDictate.Tests.csproj -c Release --no-restore", ctx);
        }
        else ctx.Say("Unit tests SKIPPED (--skip-tests): this package is not a release candidate");

        string publish = Path.Combine(ctx.WorkDir, "publish");
        ctx.Say("Publishing the self-contained win-x64 program");
        Run(root, "dotnet",
            "publish src/WinDictate/WinDictate.csproj -c Release -r win-x64 --self-contained true --no-restore " +
            "-p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -p:ContinuousIntegrationBuild=true " +
            "-p:Deterministic=true -p:RestoreLockedMode=true " +
            $"-p:SourceRevisionId={commit} -p:BuildDateUtc={ctx.BuildDate.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ} " +
            $"-o \"{publish}\"", ctx);
        ctx.RuntimeVersion = RuntimePackVersion(publish);
        CheckPublishOutput(publish, ctx);

        string model = await ModelFile.ObtainAsync(ctx, options.Get("model"));

        string sourceZip = Path.Combine(ctx.WorkDir, ctx.SourceZipName);
        ctx.Say("Archiving the exact source of the commit (tracked files only, no .git history)");
        Run(root, "git", $"archive --format=zip --prefix=WinDictate-Restricted-Pilot-{ctx.Version}-source/ -o \"{sourceZip}\" {commit}", ctx);

        Package.Assemble(ctx, publish, model, sourceZip);

        ctx.Say("Sanitisation scan of the whole package");
        var findings = Sanitiser.Scan(ctx.SubmissionDir, options.Get("deny-file"));
        Sanitiser.WriteReport(findings, Path.Combine(ctx.SubmissionDir, "05-Release", "SANITISATION-REPORT.md"), ctx);
        int blocking = findings.Count(f => f.Blocking);
        if (blocking > 0)
            throw new ReleaseException($"Sanitisation found {blocking} blocking item(s); see 05-Release/SANITISATION-REPORT.md.");

        File.WriteAllLines(Path.Combine(ctx.SubmissionDir, "05-Release", "BUILD-LOG.txt"), ctx.Log);
        ctx.Say("Hashing every file (05-Release/SHA256SUMS.txt)");
        Hashing.WriteSums(ctx.SubmissionDir);
        Hashing.Verify(ctx.SubmissionDir);

        ctx.Say("Creating the final ZIP");
        Hashing.CreateZip(ctx.SubmissionDir, ctx.ZipPath, ctx.CommitDate);
        string zipHash = Hashing.Sha256Of(ctx.ZipPath);
        File.WriteAllText(ctx.ZipPath + ".sha256", $"{zipHash}  {Path.GetFileName(ctx.ZipPath)}\n");
        ctx.Say($"{Path.GetFileName(ctx.ZipPath)}  SHA-256 {zipHash}  ({new FileInfo(ctx.ZipPath).Length / 1024 / 1024} MB)");
        if (dirty) ctx.Say("REMINDER: built from uncommitted changes. Do not submit this package.");
        return 0;
    }

    /// <summary>The repository root: the folder holding global.json and WinDictate.sln.</summary>
    public static string FindRoot()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "global.json")) && File.Exists(Path.Combine(dir.FullName, "WinDictate.sln")))
                return dir.FullName;
        }
        throw new ReleaseException("Run from inside the WinDictate repository (global.json and WinDictate.sln not found).");
    }

    public static string PinnedSdk(string root)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "global.json")));
        return doc.RootElement.GetProperty("sdk").GetProperty("version").GetString()!;
    }

    public static string ProjectVersion(string root)
    {
        string csproj = File.ReadAllText(Path.Combine(root, "src", "WinDictate", "WinDictate.csproj"));
        Match m = Regex.Match(csproj, @"<Version>([0-9]+\.[0-9]+\.[0-9]+)</Version>");
        return m.Success ? m.Groups[1].Value : throw new ReleaseException("No <Version> in WinDictate.csproj.");
    }

    /// <summary>The .NET runtime version the publish bundled, from WinDictate.deps.json.</summary>
    private static string RuntimePackVersion(string publish)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(publish, "WinDictate.deps.json")));
        foreach (var library in doc.RootElement.GetProperty("libraries").EnumerateObject())
        {
            if (library.Name.StartsWith("runtimepack.Microsoft.NETCore.App.Runtime.win-x64/", StringComparison.Ordinal))
                return library.Name[(library.Name.IndexOf('/') + 1)..];
        }
        throw new ReleaseException("The publish output does not record its .NET runtime pack.");
    }

    /// <summary>The program folder must hold exactly what the pilot ships, and nothing that was ruled out.</summary>
    private static void CheckPublishOutput(string publish, ReleaseContext ctx)
    {
        string[] required = { "WinDictate.exe", "WinDictate.dll", "LICENSE.txt", "THIRD-PARTY-NOTICES.txt",
                              Path.Combine("runtimes", "win-x64", "whisper.dll"), Path.Combine("runtimes", "win-x64", "ggml-cpu-whisper.dll") };
        foreach (string file in required)
            if (!File.Exists(Path.Combine(publish, file))) throw new ReleaseException($"The publish output is missing {file}.");
        foreach (string unwanted in new[] { Path.Combine("runtimes", "win-x86"), Path.Combine("runtimes", "win-arm64") })
            if (Directory.Exists(Path.Combine(publish, unwanted))) throw new ReleaseException($"The publish output still has {unwanted}.");
        string[] pdbs = Directory.GetFiles(publish, "*.pdb", SearchOption.AllDirectories);
        if (pdbs.Length > 0) throw new ReleaseException("Debug symbol files are in the publish output: " + string.Join(", ", pdbs.Select(Path.GetFileName)));
        int files = Directory.GetFiles(publish, "*", SearchOption.AllDirectories).Length;
        ctx.Say($"Program folder: {files} files, .NET runtime {ctx.RuntimeVersion}");
    }

    /// <summary>Runs a command in <paramref name="workingDir"/>; throws if it fails. Returns its standard output.</summary>
    public static string Run(string workingDir, string file, string arguments, ReleaseContext? ctx = null)
    {
        var start = new ProcessStartInfo(file, arguments)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        ctx?.Log.Add($"$ {file} {arguments}");
        using var process = Process.Start(start) ?? throw new ReleaseException($"Could not start {file}.");
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) { stdout.AppendLine(e.Data); if (ctx != null) Echo(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) { stderr.AppendLine(e.Data); if (ctx != null) Echo(e.Data); } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        if (ctx != null) ctx.Log.Add($"  exit code {process.ExitCode}");
        if (process.ExitCode != 0)
            throw new ReleaseException($"'{file} {arguments}' failed with exit code {process.ExitCode}.\n{Tail(stderr)}{Tail(stdout)}");
        return stdout.ToString();

        static void Echo(string line)
        {
            if (line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("warning", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Passed!", StringComparison.Ordinal) || line.Contains("Failed!", StringComparison.Ordinal))
                Console.WriteLine("    " + line.Trim());
        }

        static string Tail(StringBuilder text)
        {
            string[] lines = text.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 25))) + "\n";
        }
    }
}

/// <summary>The pinned model: a given local copy, the build cache, or a fresh download; always checked.</summary>
public static class ModelFile
{
    public static async Task<string> ObtainAsync(ReleaseContext ctx, string? given)
    {
        ModelRecord model = ctx.Model;
        string path = given != null
            ? Path.GetFullPath(given)
            : Path.Combine(ctx.Root, ".model-cache", model.FileName);

        if (!File.Exists(path))
        {
            if (given != null) throw new ReleaseException($"--model {given} does not exist.");
            ctx.Say($"Downloading {model.FileName} ({model.SizeBytes / 1024 / 1024} MB) from the pinned revision {model.Revision[..12]}…");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string partial = path + ".part";
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
            using (var response = await http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync();
                await using var target = File.Create(partial);
                await source.CopyToAsync(target);
            }
            File.Move(partial, path, overwrite: true);
        }

        ctx.Say($"Checking {model.FileName}: size and SHA-256 against pilot/model.json");
        long size = new FileInfo(path).Length;
        if (size != model.SizeBytes)
            throw new ReleaseException($"{path} is {size} bytes; the pinned model is {model.SizeBytes} bytes.");
        string hash = Hashing.Sha256Of(path);
        if (!string.Equals(hash, model.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new ReleaseException($"{path} has SHA-256 {hash}; the pinned model is {model.Sha256}.");
        ctx.Say($"Model verified: SHA-256 {hash}");
        return path;
    }
}
