using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace ReleaseKit;

/// <summary>One component of the runtime package (or of the build), for the SBOM.</summary>
public sealed class Component
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string Supplier { get; init; }
    public required string SourceUrl { get; init; }
    public required string Licence { get; init; }
    public required string Purpose { get; init; }
    public string Type { get; init; } = "library";        // CycloneDX: application, library, framework, machine-learning-model
    public bool Shipped { get; init; } = true;              // false: build-only, or a prerequisite installed separately
    public string Scope { get; init; } = "Runtime";         // Runtime, Build-only, Prerequisite (not included)
    public string Modified { get; init; } = "Unmodified";
    public string? Purl { get; init; }
    public string? PackageSha512 { get; init; }             // of the NuGet package, hex
    public string? Sha256 { get; init; }                    // of the file itself (the model)
    public List<string> Files { get; } = new();
    public string Ref => $"{Name}@{Version}";
}

/// <summary>
/// Builds the software bill of materials from what was actually published:
/// every file in the program folder is assigned to exactly one component (the
/// build fails otherwise), using WinDictate.deps.json, the locked package
/// versions and the packages' own metadata.
/// </summary>
public static class Sbom
{
    public const string UpdateOwner = "TO BE ASSIGNED (support and ownership template)";

    public static List<Component> Collect(ReleaseContext ctx, string runtimeDir)
    {
        string packagesRoot = NuGetPackagesRoot(ctx.Root);
        using var deps = JsonDocument.Parse(File.ReadAllText(Path.Combine(runtimeDir, "WinDictate.deps.json")));
        var target = deps.RootElement.GetProperty("targets").EnumerateObject().First(t => t.Name.EndsWith("/win-x64", StringComparison.Ordinal)).Value;
        var libraries = deps.RootElement.GetProperty("libraries");

        var components = new Dictionary<string, Component>(StringComparer.Ordinal);
        var owner = new Dictionary<string, Component>(StringComparer.OrdinalIgnoreCase); // file name -> component

        var app = new Component
        {
            Name = "WinDictate Restricted Pilot", Version = ctx.Version, Supplier = "Konrad Major (WinDictate project)",
            SourceUrl = $"{ctx.RepositoryUrl} (commit {ctx.Commit})", Licence = "MIT", Type = "application",
            Purpose = "Push-to-talk dictation: records while a shortcut is held, transcribes on the PC, inserts the text",
            Modified = "First-party code", Purl = $"pkg:github/WebisityStudio/windictate@{ctx.Commit}"
        };
        components[app.Ref] = app;

        foreach (var entry in target.EnumerateObject())
        {
            string[] nameVersion = entry.Name.Split('/');
            string id = nameVersion[0], version = nameVersion[1];
            if (id == "WinDictate") continue;
            string type = libraries.GetProperty(entry.Name).GetProperty("type").GetString()!;
            Component component = type == "runtimepack"
                ? RuntimePack(id.Replace("runtimepack.", ""), version)
                : Package(id, version, libraries.GetProperty(entry.Name), packagesRoot);
            components[component.Ref] = component;
            foreach (string kind in new[] { "runtime", "native", "resources" })
            {
                if (!entry.Value.TryGetProperty(kind, out var files)) continue;
                foreach (var file in files.EnumerateObject()) owner[Path.GetFileName(file.Name)] = component;
            }
        }

        // The speech engine's native DLLs come from Whisper.net.Runtime, which deps.json does not list.
        string whisperRuntimeVersion = LockedVersion(ctx.Root, "Whisper.net.Runtime");
        var whisperRuntime = Package("Whisper.net.Runtime", whisperRuntimeVersion, null, packagesRoot);
        components[whisperRuntime.Ref] = whisperRuntime;
        components["whisper.cpp@1.8.5"] = new Component
        {
            Name = "whisper.cpp (with ggml)", Version = "1.8.5 (commit f24588a272ae8e23280d9c220536437164e6ed28)",
            Supplier = "The ggml authors", SourceUrl = "https://github.com/ggml-org/whisper.cpp", Licence = "MIT",
            Purpose = "Native speech recognition engine, compiled into the Whisper.net.Runtime DLLs (whisper.dll, ggml-*.dll)",
            Purl = "pkg:github/ggml-org/whisper.cpp@f24588a272ae8e23280d9c220536437164e6ed28",
            Shipped = true, Scope = "Runtime (inside Whisper.net.Runtime)"
        };

        var model = new Component
        {
            Name = $"Whisper {ctx.Model.Name} model ({ctx.Model.FileName})", Version = $"revision {ctx.Model.Revision}",
            Supplier = "OpenAI (weights); whisper.cpp project (GGML conversion)", SourceUrl = ctx.Model.Url, Licence = "MIT",
            Type = "machine-learning-model", Purpose = "Fixed English speech recognition model, loaded only after its SHA-256 is checked",
            Sha256 = ctx.Model.Sha256, Purl = $"pkg:huggingface/ggerganov/whisper.cpp@{ctx.Model.Revision}"
        };
        components[model.Ref] = model;

        // Assign every shipped file to exactly one component.
        var unassigned = new List<string>();
        foreach (string file in Hashing.Files(runtimeDir))
        {
            string name = Path.GetFileName(file);
            Component? c =
                file.StartsWith("models/", StringComparison.Ordinal) ? model :
                file.StartsWith("runtimes/win-x64/", StringComparison.Ordinal) ? whisperRuntime :
                name.StartsWith("WinDictate.", StringComparison.Ordinal) || name is "LICENSE.txt" or "THIRD-PARTY-NOTICES.txt" ? app :
                owner.GetValueOrDefault(name);
            if (c == null) unassigned.Add(file);
            else c.Files.Add(file);
        }
        if (unassigned.Count > 0)
            throw new ReleaseException("These shipped files belong to no known component (the SBOM would be incomplete): " + string.Join(", ", unassigned));

        // Packages in the dependency graph that put no file in the program folder.
        foreach (var (id, version) in LockedPackages(ctx.Root))
        {
            string key = $"{id}@{version}";
            if (components.Values.Any(c => c.Name == id && c.Version == version)) continue;
            var notShipped = Package(id, version, null, packagesRoot, scope: "Not shipped (no files for win-x64)");
            components[key] = notShipped;
        }

        components["dotnet-sdk"] = new Component
        {
            Name = ".NET SDK", Version = ctx.SdkVersion, Supplier = "Microsoft / .NET Foundation", SourceUrl = "https://dotnet.microsoft.com",
            Licence = "MIT", Type = "application", Purpose = "Compiler and build tools (pinned in global.json)", Shipped = false, Scope = "Build-only"
        };
        components["vc-redist"] = new Component
        {
            Name = "Microsoft Visual C++ 2015-2022 Redistributable (x64)", Version = "14.x (latest supported)", Supplier = "Microsoft",
            SourceUrl = "https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist", Licence = "Microsoft Software License Terms",
            Type = "framework", Purpose = "C/C++ runtime the speech engine's native DLLs load (vcruntime140, vcruntime140_1, msvcp140, vcomp140)",
            Shipped = false, Scope = "Prerequisite (not included; install through the approved software route)"
        };
        foreach (var (id, version) in TestPackages(ctx.Root))
        {
            Component p = Package(id, version, null, packagesRoot, scope: "Build-only (tests)");
            components[$"test:{id}@{version}"] = new Component
            {
                Name = p.Name, Version = p.Version, Supplier = p.Supplier, SourceUrl = p.SourceUrl, Licence = p.Licence, Purl = p.Purl,
                Purpose = "Unit and Windows smoke tests; never shipped", Shipped = false, Scope = "Build-only (tests)",
                PackageSha512 = p.PackageSha512
            };
        }

        return components.Values
            .OrderBy(c => c.Shipped ? 0 : 1)
            .ThenBy(c => c.Type == "application" ? 0 : 1)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Component RuntimePack(string id, string version)
    {
        bool desktop = id.Contains("WindowsDesktop", StringComparison.Ordinal);
        return new Component
        {
            Name = id, Version = version, Supplier = "Microsoft / .NET Foundation",
            SourceUrl = desktop ? "https://github.com/dotnet/wpf, https://github.com/dotnet/winforms" : "https://github.com/dotnet/runtime",
            Licence = "MIT", Type = "framework", Purl = $"pkg:nuget/{id}@{version}",
            Purpose = desktop ? "WPF and Windows Forms user-interface frameworks (bundled, self-contained)"
                              : ".NET runtime and base class library (bundled, self-contained; unused parts such as the network stack are present but never called)"
        };
    }

    private static Component Package(string id, string version, JsonElement? library, string packagesRoot, string scope = "Runtime")
    {
        string folder = Path.Combine(packagesRoot, id.ToLowerInvariant(), version.ToLowerInvariant());
        string nuspecPath = Path.Combine(folder, id.ToLowerInvariant() + ".nuspec");
        string licence = "UNKNOWN", url = $"https://www.nuget.org/packages/{id}/{version}", supplier = "UNKNOWN", description = "";
        if (File.Exists(nuspecPath))
        {
            XDocument nuspec = XDocument.Load(nuspecPath);
            XElement? meta = nuspec.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
            string? Get(string name) => meta?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
            XElement? licenceElement = meta?.Elements().FirstOrDefault(e => e.Name.LocalName == "license");
            licence = licenceElement?.Attribute("type")?.Value == "expression" ? licenceElement.Value.Trim()
                    : licenceElement != null ? "MIT (licence file in package)" : "UNKNOWN";
            if (id.StartsWith("Whisper.net", StringComparison.Ordinal) && licence.StartsWith("MIT", StringComparison.Ordinal)) licence = "MIT";
            XElement? repo = meta?.Elements().FirstOrDefault(e => e.Name.LocalName == "repository");
            url = repo?.Attribute("url")?.Value is { Length: > 0 } repoUrl
                ? repoUrl + (repo.Attribute("commit")?.Value is { Length: > 0 } c ? $" (commit {c})" : "")
                : Get("projectUrl") ?? url;
            supplier = Get("authors") ?? supplier;
            description = Get("description") ?? "";
        }
        string? sha512 = null;
        string shaFile = Path.Combine(folder, $"{id.ToLowerInvariant()}.{version.ToLowerInvariant()}.nupkg.sha512");
        string? base64 = library?.TryGetProperty("sha512", out var s) == true ? s.GetString()?.Replace("sha512-", "") : null;
        base64 ??= File.Exists(shaFile) ? File.ReadAllText(shaFile).Trim() : null;
        if (base64 != null) sha512 = Convert.ToHexStringLower(Convert.FromBase64String(base64));

        return new Component
        {
            Name = id, Version = version, Supplier = supplier, SourceUrl = url, Licence = licence,
            Purpose = PurposeOf(id, description), Purl = $"pkg:nuget/{id}@{version}", PackageSha512 = sha512,
            Shipped = scope == "Runtime", Scope = scope
        };
    }

    private static string PurposeOf(string id, string description) => id switch
    {
        "Whisper.net" => "Managed bindings to whisper.cpp (WinDictate never uses its model downloader)",
        "Whisper.net.Runtime" => "Native whisper.cpp speech engine for win-x64 (CPU, AVX2)",
        "Whisper.net.Runtime.Metal" => "macOS GPU build of the speech engine; a dependency of Whisper.net.Runtime with no Windows files",
        "NAudio.Core" => "Audio formats and the in-memory WAV writer",
        "NAudio.WinMM" => "Microphone capture through the Windows waveIn API",
        "Microsoft.Extensions.AI.Abstractions" => "Interface definitions required by Whisper.net; not used by WinDictate",
        _ => description.Length > 140 ? description[..137] + "..." : description
    };

    private static string NuGetPackagesRoot(string root)
    {
        string? fromEnv = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;
        string listed = BuildCommand.Run(root, "dotnet", "nuget locals global-packages --list");
        int colon = listed.IndexOf(':');
        return colon > 0 ? listed[(colon + 1)..].Trim() : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
    }

    private static IEnumerable<(string Id, string Version)> LockedPackages(string root, string project = "src/WinDictate")
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, project, "packages.lock.json")));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var framework in doc.RootElement.GetProperty("dependencies").EnumerateObject())
            foreach (var package in framework.Value.EnumerateObject())
                if (package.Value.TryGetProperty("resolved", out var resolved) && seen.Add(package.Name + "@" + resolved.GetString()))
                    yield return (package.Name, resolved.GetString()!);
    }

    private static string LockedVersion(string root, string id) =>
        LockedPackages(root).FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)).Version
        ?? throw new ReleaseException($"{id} is not in src/WinDictate/packages.lock.json.");

    /// <summary>The test projects' direct packages (build-only).</summary>
    private static IEnumerable<(string Id, string Version)> TestPackages(string root)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string project in new[] { "tests/WinDictate.Tests", "tests/WinDictate.UiSmoke" })
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, project, "packages.lock.json")));
            foreach (var framework in doc.RootElement.GetProperty("dependencies").EnumerateObject())
                foreach (var package in framework.Value.EnumerateObject())
                    if (package.Value.GetProperty("type").GetString() == "Direct" &&
                        !package.Name.StartsWith("Whisper.net", StringComparison.Ordinal) &&
                        seen.Add(package.Name))
                        yield return (package.Name, package.Value.GetProperty("resolved").GetString()!);
        }
    }

    // ----- Output -----

    public static void WriteCycloneDx(List<Component> components, ReleaseContext ctx, string path)
    {
        string serial = DeterministicUuid($"{ctx.Commit}/{ctx.Version}/{ctx.Model.Sha256}");
        var root = new JsonObject
        {
            ["bomFormat"] = "CycloneDX",
            ["specVersion"] = "1.5",
            ["serialNumber"] = "urn:uuid:" + serial,
            ["version"] = 1,
            ["metadata"] = new JsonObject
            {
                ["timestamp"] = ctx.BuildDate.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["tools"] = new JsonObject
                {
                    ["components"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "application", ["name"] = "WinDictate ReleaseKit", ["version"] = ctx.Version,
                        ["description"] = "pilot/tools/ReleaseKit in the source snapshot"
                    })
                },
                ["component"] = ToJson(components.First(c => c.Type == "application" && c.Shipped)),
                ["properties"] = new JsonArray(
                    Property("windictate:source-commit", ctx.Commit),
                    Property("windictate:dotnet-sdk", ctx.SdkVersion),
                    Property("windictate:dotnet-runtime", ctx.RuntimeVersion),
                    Property("windictate:network-access", "false"),
                    Property("windictate:external-cleanup", "false"),
                    Property("windictate:transcript-history", "false"))
            },
            ["components"] = new JsonArray(components.Where(c => !(c.Type == "application" && c.Shipped)).Select(c => (JsonNode)ToJson(c)).ToArray())
        };
        File.WriteAllText(path, root.ToJsonString(Json.Readable) + "\n");
    }

    private static JsonObject ToJson(Component c)
    {
        var o = new JsonObject
        {
            ["type"] = c.Type,
            ["bom-ref"] = c.Ref,
            ["name"] = c.Name,
            ["version"] = c.Version,
            ["supplier"] = new JsonObject { ["name"] = c.Supplier },
            ["description"] = c.Purpose,
            ["scope"] = c.Shipped ? "required" : "excluded"
        };
        o["licenses"] = c.Licence.StartsWith("MIT", StringComparison.Ordinal)
            ? new JsonArray(new JsonObject { ["license"] = new JsonObject { ["id"] = "MIT" } })
            : new JsonArray(new JsonObject { ["license"] = new JsonObject { ["name"] = c.Licence } });
        if (c.Purl != null) o["purl"] = c.Purl;
        var hashes = new JsonArray();
        if (c.PackageSha512 != null) hashes.Add(new JsonObject { ["alg"] = "SHA-512", ["content"] = c.PackageSha512 });
        if (c.Sha256 != null) hashes.Add(new JsonObject { ["alg"] = "SHA-256", ["content"] = c.Sha256 });
        if (hashes.Count > 0) o["hashes"] = hashes;
        o["externalReferences"] = new JsonArray(new JsonObject { ["type"] = "vcs", ["url"] = c.SourceUrl.Split(' ')[0].TrimEnd(',') });
        o["properties"] = new JsonArray(
            Property("windictate:scope", c.Scope),
            Property("windictate:modified", c.Modified),
            Property("windictate:update-owner", UpdateOwner),
            Property("windictate:shipped-files", c.Files.Count.ToString()));
        return o;
    }

    private static JsonObject Property(string name, string value) => new() { ["name"] = name, ["value"] = value };

    public static void WriteCsv(List<Component> components, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Name,Version or commit,Supplier or project,Source URL,Licence,Runtime or build-only,Modified,Purpose,Shipped files,Package SHA-512 or file SHA-256,Update owner");
        foreach (var c in components)
        {
            sb.AppendLine(string.Join(",", new[]
            {
                c.Name, c.Version, c.Supplier, c.SourceUrl, c.Licence, c.Scope, c.Modified, c.Purpose,
                c.Files.Count.ToString(), c.Sha256 ?? c.PackageSha512 ?? "", UpdateOwner
            }.Select(Csv)));
        }
        File.WriteAllText(path, sb.ToString());
    }

    public static void WriteInventory(List<Component> components, ReleaseContext ctx, string runtimeDir, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Component inventory");
        sb.AppendLine();
        sb.AppendLine($"WinDictate Restricted Pilot {ctx.Version}, commit `{ctx.Commit}`. Generated from the published program folder by the release tool; every file in `01-Runtime/win-x64` is assigned to exactly one component below (the build fails otherwise).");
        sb.AppendLine();
        sb.AppendLine("| Component | Version | Licence | Scope | Files shipped | Purpose |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var c in components)
            sb.AppendLine($"| {Md(c.Name)} | {Md(c.Version)} | {Md(c.Licence)} | {Md(c.Scope)} | {c.Files.Count} | {Md(c.Purpose)} |");
        sb.AppendLine();
        sb.AppendLine("## Files by component");
        sb.AppendLine();
        foreach (var c in components.Where(c => c.Files.Count > 0))
        {
            long bytes = c.Files.Sum(f => new FileInfo(Path.Combine(runtimeDir, f)).Length);
            sb.AppendLine($"### {Md(c.Name)} {Md(c.Version)} ({c.Files.Count} files, {bytes / 1024.0 / 1024.0:F1} MB)");
            sb.AppendLine();
            foreach (string f in c.Files) sb.AppendLine($"- `{f}`");
            sb.AppendLine();
        }
        sb.AppendLine("SHA-256 hashes of every file are in `05-Release/SHA256SUMS.txt`.");
        File.WriteAllText(path, sb.ToString());
    }

    private static string Csv(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n') ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    private static string Md(string value) => value.Replace("|", "\\|");

    private static string DeterministicUuid(string seed)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50); // version 5 style
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // RFC 4122 variant
        string hex = Convert.ToHexStringLower(hash.AsSpan(0, 16));
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}";
    }
}
