using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ReleaseKit;

/// <summary>
/// Lays out the submission folder exactly as the brief specifies, fills the
/// document templates from pilot/ with this release's facts, and writes the
/// SBOM, licences, manifest and source record.
/// </summary>
public static class Package
{
    public static void Assemble(ReleaseContext ctx, string publish, string model, string sourceZip)
    {
        string top = ctx.SubmissionDir;
        string runtime = Path.Combine(top, "01-Runtime", "win-x64");
        string source = Path.Combine(top, "02-Source");
        string docs = Path.Combine(top, "03-Documentation");
        string compliance = Path.Combine(top, "04-Compliance");
        string licences = Path.Combine(compliance, "LICENCES");
        string release = Path.Combine(top, "05-Release");
        foreach (string dir in new[] { runtime, source, docs, licences, release }) Directory.CreateDirectory(dir);

        ctx.Say("Assembling 01-Runtime: the published program, the verified model and the install scripts");
        CopyTree(publish, runtime);
        Directory.CreateDirectory(Path.Combine(runtime, "models"));
        File.Copy(model, Path.Combine(runtime, "models", ctx.Model.FileName));
        string scripts = Path.Combine(ctx.Root, "pilot", "scripts");
        foreach (string script in Directory.GetFiles(scripts, "*.ps1"))
            File.Copy(script, Path.Combine(top, "01-Runtime", Path.GetFileName(script)));

        ctx.Say("Assembling 02-Source: the source snapshot and how to rebuild from it");
        File.Copy(sourceZip, Path.Combine(source, ctx.SourceZipName));
        File.WriteAllText(Path.Combine(source, "SOURCE_COMMIT.txt"),
            $"Repository: {ctx.RepositoryUrl}\n" +
            $"Commit: {ctx.Commit}\n" +
            $"Commit date: {ctx.CommitDate.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC\n" +
            $"Branch: restricted-pilot-build\n" +
            $"Version: {ctx.Version}\n" +
            $"Source archive: {ctx.SourceZipName} (git archive of the commit: tracked files only, no .git history)\n" +
            (ctx.Dirty ? "WARNING: built from a working tree with uncommitted changes. Not a release.\n" : ""));

        var facts = Facts(ctx, runtime);
        ctx.Say("Filling the documents with this release's facts");
        Template(ctx, facts, "pilot/docs/START-HERE.md", Path.Combine(top, "START-HERE.md"));
        Template(ctx, facts, "pilot/docs/BUILD_INSTRUCTIONS.md", Path.Combine(source, "BUILD_INSTRUCTIONS.md"));
        foreach (string doc in new[]
                 {
                     "README-PILOT.md", "INSTALLATION-INSTRUCTIONS.md", "UNINSTALL-INSTRUCTIONS.md", "TECHNICAL-ARCHITECTURE.md",
                     "DATA-FLOW-AND-PRIVACY.md", "SECURITY-CONTROLS.md", "USER-GUIDE.md", "UAT-TEST-PLAN.md",
                     "KNOWN-LIMITATIONS.md", "SUPPORT-AND-OWNERSHIP-TEMPLATE.md"
                 })
            Template(ctx, facts, $"pilot/docs/{doc}", Path.Combine(docs, doc));
        Template(ctx, facts, "pilot/compliance/OPERATIONAL-AI-FACTSHEET.md", Path.Combine(compliance, "OPERATIONAL-AI-FACTSHEET.md"));
        Template(ctx, facts, "pilot/compliance/MODEL-SOURCE-LICENCE-AND-HASH.md", Path.Combine(compliance, "MODEL-SOURCE-LICENCE-AND-HASH.md"));
        Template(ctx, facts, "pilot/release/RELEASE-CHECKLIST.md", Path.Combine(release, "RELEASE-CHECKLIST.md"));
        Template(ctx, facts, "pilot/release/CHANGELOG.md", Path.Combine(release, "CHANGELOG.md"));

        ctx.Say("Assembling 04-Compliance: SBOM, licences and notices");
        string licenceSource = Path.Combine(ctx.Root, "pilot", "compliance", "licences");
        foreach (string licence in Directory.GetFiles(licenceSource, "*.txt"))
            File.Copy(licence, Path.Combine(licences, Path.GetFileName(licence)));
        File.WriteAllText(Path.Combine(licences, "Model-LICENCE.txt"),
            $"Model: {ctx.Model.FileName} (Whisper {ctx.Model.Name}), SHA-256 {ctx.Model.Sha256}\n" +
            $"Weights: OpenAI Whisper, MIT License (text below).\n" +
            $"File format conversion: whisper.cpp project (GGML), MIT License (see WhisperCpp-LICENCE.txt).\n" +
            $"Obtained from: {ctx.Model.Url}\n\n" +
            File.ReadAllText(Path.Combine(licenceSource, "OpenAI-Whisper-LICENCE.txt")));
        File.Copy(Path.Combine(ctx.Root, "pilot", "compliance", "THIRD-PARTY-NOTICES.txt"), Path.Combine(compliance, "THIRD-PARTY-NOTICES.txt"));

        List<Component> components = Sbom.Collect(ctx, runtime);
        Sbom.WriteCycloneDx(components, ctx, Path.Combine(compliance, "SBOM.cdx.json"));
        Sbom.WriteCsv(components, Path.Combine(compliance, "SBOM.csv"));
        Sbom.WriteInventory(components, ctx, runtime, Path.Combine(compliance, "COMPONENT-INVENTORY.md"));
        int shipped = components.Count(c => c.Shipped);
        ctx.Say($"SBOM: {components.Count} components ({shipped} shipped), all {Hashing.Files(runtime).Count} program files accounted for");

        WriteManifest(ctx, runtime, Path.Combine(release, "RELEASE-MANIFEST.json"));
    }

    /// <summary>The values the document templates refer to as {{NAME}}.</summary>
    private static Dictionary<string, string> Facts(ReleaseContext ctx, string runtime)
    {
        var files = Hashing.Files(runtime);
        long runtimeBytes = files.Sum(f => new FileInfo(Path.Combine(runtime, f)).Length);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["VERSION"] = ctx.Version,
            ["COMMIT"] = ctx.Commit,
            ["COMMIT_SHORT"] = ctx.ShortCommit,
            ["COMMIT_DATE"] = ctx.CommitDate.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'"),
            ["BUILD_DATE"] = ctx.BuildDate.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'"),
            ["SDK_VERSION"] = ctx.SdkVersion,
            ["RUNTIME_VERSION"] = ctx.RuntimeVersion,
            ["REPO_URL"] = ctx.RepositoryUrl,
            ["PACKAGE_NAME"] = ctx.PackageName,
            ["ZIP_NAME"] = ctx.PackageName + ".zip",
            ["SOURCE_ZIP"] = ctx.SourceZipName,
            ["MODEL_NAME"] = ctx.Model.Name,
            ["MODEL_FILE"] = ctx.Model.FileName,
            ["MODEL_SIZE"] = ctx.Model.SizeBytes.ToString("N0", System.Globalization.CultureInfo.InvariantCulture),
            ["MODEL_SIZE_MB"] = (ctx.Model.SizeBytes / 1024 / 1024).ToString(),
            ["MODEL_SHA256"] = ctx.Model.Sha256,
            ["MODEL_SHA256_SHORT"] = ctx.Model.Sha256[..12],
            ["MODEL_URL"] = ctx.Model.Url,
            ["MODEL_REPOSITORY"] = ctx.Model.Repository,
            ["MODEL_REVISION"] = ctx.Model.Revision,
            ["RUNTIME_FILE_COUNT"] = files.Count.ToString(),
            ["RUNTIME_SIZE_MB"] = (runtimeBytes / 1024 / 1024).ToString(),
            ["DIRTY_WARNING"] = ctx.Dirty ? "\n> **WARNING: built from uncommitted changes. This is a dry run, not a release.**\n" : ""
        };
    }

    private static void Template(ReleaseContext ctx, Dictionary<string, string> facts, string relativeSource, string destination)
    {
        string path = Path.Combine(ctx.Root, relativeSource.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) throw new ReleaseException($"Template {relativeSource} is missing.");
        string text = Regex.Replace(File.ReadAllText(path), @"\{\{([A-Z0-9_]+)\}\}", m =>
            facts.TryGetValue(m.Groups[1].Value, out string? value)
                ? value
                : throw new ReleaseException($"{relativeSource}: unknown placeholder {m.Value}."));
        File.WriteAllText(destination, text);
    }

    private static void WriteManifest(ReleaseContext ctx, string runtime, string path)
    {
        string exe = Path.Combine(runtime, "WinDictate.exe");
        var manifest = new JsonObject
        {
            ["product"] = "WinDictate Restricted Pilot",
            ["version"] = ctx.Version,
            ["sourceRepository"] = ctx.RepositoryUrl,
            ["sourceCommit"] = ctx.Commit,
            ["sourceBranch"] = "restricted-pilot-build",
            ["sourceTreeClean"] = !ctx.Dirty,
            ["targetRuntime"] = "win-x64",
            ["selfContained"] = true,
            ["dotnetSdk"] = ctx.SdkVersion,
            ["dotnetRuntime"] = ctx.RuntimeVersion,
            ["networkAccess"] = false,
            ["externalCleanup"] = false,
            ["transcriptHistory"] = false,
            ["audioWrittenToDisk"] = false,
            ["startsWithWindows"] = false,
            ["clipboard"] = "restored after each paste; undelivered text removed after 120 seconds or on exit; marked to stay out of clipboard history and cloud clipboard",
            ["modelFile"] = ctx.Model.FileName,
            ["modelSha256"] = ctx.Model.Sha256,
            ["modelSizeBytes"] = ctx.Model.SizeBytes,
            ["modelSource"] = ctx.Model.Url,
            ["executableSha256"] = Hashing.Sha256Of(exe),
            ["programDllSha256"] = Hashing.Sha256Of(Path.Combine(runtime, "WinDictate.dll")),
            ["prerequisites"] = new JsonArray("Windows 11 x64", "CPU with AVX, AVX2 and FMA", "Microsoft Visual C++ 2015-2022 Redistributable (x64)"),
            ["buildDateUtc"] = ctx.BuildDate.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["buildTool"] = "pilot/tools/ReleaseKit (build-release.ps1)",
            ["buildHost"] = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux"
        };
        File.WriteAllText(path, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private static void CopyTree(string from, string to)
    {
        foreach (string file in Hashing.Files(from))
        {
            string target = Path.Combine(to, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(from, file.Replace('/', Path.DirectorySeparatorChar)), target);
        }
    }
}
