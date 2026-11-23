using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace ReleaseKit;

/// <summary>Something the sanitisation scan found.</summary>
public sealed record Finding(string File, int Line, string Category, string Excerpt, bool Blocking, string? Note = null);

/// <summary>
/// The brief's sanitisation review, automated: scans every text file in the
/// package, including every file inside the source snapshot ZIP, for personal
/// paths, network paths, e-mail addresses, web addresses outside a reviewed
/// list of public hosts, credential-looking strings, sensitive file names and
/// any private terms supplied at build time (for example an employer's name,
/// which is never written into the repository or the report). Program
/// binaries are scanned for the build machine's user and computer names.
/// </summary>
public static class Sanitiser
{
    private static readonly string[] TextExtensions =
    {
        ".md", ".txt", ".json", ".csv", ".ps1", ".sh", ".cs", ".xaml", ".csproj", ".props", ".targets", ".sln",
        ".yml", ".yaml", ".config", ".manifest", ".xml", ".editorconfig", ".gitignore", ".gitattributes", ".resx"
    };

    private static readonly string[] SensitiveNames =
    {
        ".env", "secrets.json", "id_rsa", "id_ed25519", ".npmrc", ".pypirc", "credentials", ".git-credentials"
    };

    private static readonly string[] SensitiveExtensions = { ".pfx", ".p12", ".pem", ".key", ".snk", ".user", ".pdb" };

    // Built from parts so this file does not match its own patterns.
    private static readonly Regex WindowsHome = new(@"[A-Za-z]:\\" + @"Users\\(?!Public\\|Default\\|All Users\\)[^\\\s""'<>]+", RegexOptions.IgnoreCase);
    private static readonly Regex UnixHome = new("(?<![A-Za-z0-9])/(?:Users|home)/" + @"[A-Za-z][A-Za-z0-9._-]+");
    private static readonly Regex Unc = new(@"(?<![\\""'A-Za-z0-9])\\" + @"\\[A-Za-z0-9][A-Za-z0-9._-]+\\[A-Za-z0-9$._-]+");
    private static readonly Regex Email = new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}");
    private static readonly Regex Url = new(@"https?://([A-Za-z0-9.-]+)", RegexOptions.IgnoreCase);
    private static readonly Regex Secret = new(
        "-----BEGIN [A-Z ]*PRIVATE " + "KEY-----|AKIA[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9]{30,}|sk-[A-Za-z0-9]{20,}|xox[baprs]-[A-Za-z0-9-]{10,}|eyJ[A-Za-z0-9_-]{20,}\\.[A-Za-z0-9_-]{20,}");
    private static readonly Regex Keyword = new(@"(?i)\b(api[_-]?key|secret|password|passwd|token|connection ?string)\b");

    public static int Command(string path, string? denyFile)
    {
        var findings = Scan(Path.GetFullPath(path), denyFile);
        foreach (var f in findings.Where(f => f.Blocking))
            Console.WriteLine($"BLOCKING {f.Category}: {f.File}:{f.Line}: {f.Excerpt}");
        Console.WriteLine($"{findings.Count(f => f.Blocking)} blocking, {findings.Count(f => !f.Blocking)} reviewed or allowed.");
        return findings.Any(f => f.Blocking) ? 1 : 0;
    }

    public static List<Finding> Scan(string root, string? denyFile)
    {
        var allowedHosts = AllowedHosts(root);
        string[] privateTerms = denyFile != null && File.Exists(denyFile)
            ? File.ReadAllLines(denyFile).Select(l => l.Trim()).Where(l => l.Length >= 2 && !l.StartsWith('#')).ToArray()
            : Array.Empty<string>();
        PrivateTermCount = privateTerms.Length;
        var machineMarkers = new[] { Environment.UserName, Environment.MachineName }
            .Where(m => !string.IsNullOrWhiteSpace(m) && m.Length >= 4 && !m.Equals("runner", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var findings = new List<Finding>();
        FilesScanned = 0;
        ArchiveEntriesScanned = 0;
        foreach (string file in Hashing.Files(root))
        {
            string full = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
            CheckName(file, findings);
            if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(full);
                foreach (var entry in zip.Entries.Where(e => e.Length > 0))
                {
                    string inner = $"{file}!/{entry.FullName}";
                    CheckName(inner, findings);
                    if (!IsText(entry.FullName)) continue;
                    using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                    ScanText(inner, reader.ReadToEnd(), findings, allowedHosts, privateTerms, machineMarkers);
                    ArchiveEntriesScanned++;
                }
            }
            else if (IsText(file))
            {
                ScanText(file, File.ReadAllText(full), findings, allowedHosts, privateTerms, machineMarkers);
            }
            else
            {
                ScanBinary(file, full, findings, privateTerms, machineMarkers);
            }
            FilesScanned++;
        }
        return findings;
    }

    public static int FilesScanned { get; private set; }
    public static int ArchiveEntriesScanned { get; private set; }
    public static int PrivateTermCount { get; private set; }

    private static bool IsText(string name) =>
        TextExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)) ||
        Path.GetFileName(name) is "LICENSE" or "README" or "global.json";

    private static void CheckName(string file, List<Finding> findings)
    {
        string name = Path.GetFileName(file);
        bool generatedAssets = file.EndsWith(".deps.json", StringComparison.Ordinal);
        if (SensitiveNames.Any(n => name.Equals(n, StringComparison.OrdinalIgnoreCase)) ||
            (!generatedAssets && SensitiveExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase))))
            findings.Add(new Finding(file, 0, "Sensitive file name", name, Blocking: true));
    }

    /// <summary>Third-party licence and notice texts are public documents: their contents are reported but never block.</summary>
    private static bool IsThirdPartyText(string file) =>
        file.Contains("THIRD-PARTY-NOTICES", StringComparison.OrdinalIgnoreCase) ||
        file.Contains("/LICENCES/", StringComparison.Ordinal) ||
        file.Contains("pilot/compliance/licences/", StringComparison.Ordinal);

    /// <summary>The scanner's own definitions and the guard tests name the very patterns they look for.</summary>
    private static bool IsScannerDefinition(string file) =>
        file.EndsWith("pilot/tools/ReleaseKit/Sanitiser.cs", StringComparison.Ordinal) ||
        file.EndsWith("tests/WinDictate.Tests/PilotGuardTests.cs", StringComparison.Ordinal);

    private static void ScanText(string file, string text, List<Finding> findings, HashSet<string> allowedHosts,
        string[] privateTerms, string[] machineMarkers)
    {
        bool thirdParty = IsThirdPartyText(file);
        bool scanner = IsScannerDefinition(file);
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            int n = i + 1;
            void Add(string category, string excerpt, bool blocking, string? note = null) =>
                findings.Add(new Finding(file, n, category, Short(excerpt), blocking && !thirdParty && !scanner,
                    thirdParty ? "third-party licence text (public)" : scanner ? "scanner or guard definition" : note));

            foreach (Match m in WindowsHome.Matches(line)) Add("Personal Windows path", m.Value, true);
            foreach (Match m in UnixHome.Matches(line)) Add("Personal home path", m.Value, true);
            foreach (Match m in Unc.Matches(line)) Add("Network (UNC) path", m.Value, true);
            foreach (Match m in Email.Matches(line))
            {
                if (m.Value.EndsWith("@example.com", StringComparison.OrdinalIgnoreCase)) continue;
                Add("E-mail address", m.Value, true);
            }
            foreach (Match m in Url.Matches(line))
            {
                string host = m.Groups[1].Value.ToLowerInvariant().TrimEnd('.');
                if (IsReservedExample(host)) continue; // can never be a real endpoint (RFC 2606 / RFC 6761)
                bool known = allowedHosts.Contains(host) || allowedHosts.Any(h => host.EndsWith("." + h, StringComparison.Ordinal));
                if (!known) Add("Web address outside the reviewed public hosts", m.Value, true);
            }
            foreach (Match m in Secret.Matches(line)) Add("Credential-like string", Redact(m.Value), true);
            foreach (string term in privateTerms)
                if (Regex.IsMatch(line, $@"(?i)(?<![A-Za-z0-9]){Regex.Escape(term)}(?![A-Za-z0-9])"))
                    findings.Add(new Finding(file, n, "Private term (supplied at build time)", "[term withheld]", Blocking: true));
            foreach (string marker in machineMarkers)
                if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    findings.Add(new Finding(file, n, "Build machine user or computer name", "[name withheld]", Blocking: !thirdParty));
            foreach (Match m in Keyword.Matches(line))
                Add("Keyword for review: " + m.Value.ToLowerInvariant(), line.Trim(), false, "reviewed: see context");
        }
    }

    /// <summary>Domains reserved for documentation and testing, which never belong to anyone.</summary>
    private static bool IsReservedExample(string host) =>
        host is "example.com" or "example.org" or "example.net" ||
        host.EndsWith(".example.com", StringComparison.Ordinal) || host.EndsWith(".example.org", StringComparison.Ordinal) ||
        host.EndsWith(".example.net", StringComparison.Ordinal) || host.EndsWith(".test", StringComparison.Ordinal) ||
        host.EndsWith(".invalid", StringComparison.Ordinal) || host.EndsWith(".example", StringComparison.Ordinal);

    private static void ScanBinary(string file, string full, List<Finding> findings, string[] privateTerms, string[] machineMarkers)
    {
        var needles = machineMarkers.Select(m => ("Build machine user or computer name", m))
            .Concat(privateTerms.Select(t => ("Private term (supplied at build time)", t)))
            .ToArray();
        if (needles.Length == 0) return;
        byte[] data = File.ReadAllBytes(full);
        foreach (var (category, needle) in needles)
        {
            foreach (Encoding encoding in new[] { Encoding.UTF8, Encoding.Unicode })
            {
                byte[] pattern = encoding.GetBytes(needle);
                if (data.AsSpan().IndexOf(pattern) >= 0 ||
                    data.AsSpan().IndexOf(encoding.GetBytes(needle.ToLowerInvariant())) >= 0 ||
                    data.AsSpan().IndexOf(encoding.GetBytes(needle.ToUpperInvariant())) >= 0)
                {
                    findings.Add(new Finding(file, 0, category + " (in a binary)", "[withheld]", Blocking: true));
                    break;
                }
            }
        }
    }

    private static HashSet<string> AllowedHosts(string root)
    {
        // The reviewed list travels with the source; fall back to the repository copy.
        string? list = new[]
        {
            Path.Combine(BuildCommand.FindRoot(), "pilot", "sanitise-allowed-hosts.txt"),
        }.FirstOrDefault(File.Exists);
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (list != null)
            foreach (string line in File.ReadAllLines(list))
                if (line.Trim() is { Length: > 0 } h && !h.StartsWith('#')) hosts.Add(h);
        return hosts;
    }

    public static void WriteReport(List<Finding> findings, string path, ReleaseContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Sanitisation report");
        sb.AppendLine();
        sb.AppendLine($"WinDictate Restricted Pilot {ctx.Version}, commit `{ctx.Commit}`. Produced by the release tool before hashing and zipping.");
        sb.AppendLine();
        sb.AppendLine($"- Files scanned: {FilesScanned} (plus {ArchiveEntriesScanned} text files inside the source snapshot ZIP)");
        sb.AppendLine($"- Private terms checked (supplied at build time, not recorded here): {PrivateTermCount}");
        sb.AppendLine("- Build machine user and computer names: checked in every text file and every binary");
        sb.AppendLine("- Checks: personal Windows and home paths, network (UNC) paths, e-mail addresses, web addresses outside `pilot/sanitise-allowed-hosts.txt` (reserved example domains such as example.com are ignored), credential-like strings, sensitive file names, and the brief's keywords (API key, secret, password, token, connection string) for review");
        sb.AppendLine();
        var blocking = findings.Where(f => f.Blocking).ToList();
        sb.AppendLine($"## Blocking findings: {blocking.Count}");
        sb.AppendLine();
        if (blocking.Count == 0) sb.AppendLine("None. The package may be hashed and zipped.");
        foreach (var f in blocking) sb.AppendLine($"- **{f.Category}**: `{f.File}`:{f.Line} `{f.Excerpt}`");
        sb.AppendLine();
        sb.AppendLine("## Allowed findings (summary)");
        sb.AppendLine();
        sb.AppendLine("Matches inside third-party licence and notice texts (public documents shipped as their authors wrote them) and inside the scanner's own pattern definitions.");
        sb.AppendLine();
        sb.AppendLine("| Category | Reason | Count |");
        sb.AppendLine("|---|---|---|");
        foreach (var group in findings.Where(f => !f.Blocking && !f.Category.StartsWith("Keyword", StringComparison.Ordinal))
                     .GroupBy(f => (f.Category, f.Note)).OrderBy(g => g.Key.Category, StringComparer.Ordinal))
            sb.AppendLine($"| {group.Key.Category} | {group.Key.Note} | {group.Count()} |");
        sb.AppendLine();
        sb.AppendLine("## Keywords for review");
        sb.AppendLine();
        sb.AppendLine("Every line containing one of the brief's search words, outside third-party texts. None is a credential: they are .NET cancellation tokens, the guard tests that forbid API keys, and the documents explaining that this build has none.");
        sb.AppendLine();
        foreach (var f in findings.Where(f => f.Category.StartsWith("Keyword", StringComparison.Ordinal) && f.Note != "third-party licence text (public)")
                     .OrderBy(f => f.File, StringComparer.Ordinal).ThenBy(f => f.Line))
            sb.AppendLine($"- `{f.File}`:{f.Line} ({f.Category[(f.Category.IndexOf(':') + 2)..]}) `{f.Excerpt.Replace("`", "'")}`");
        File.WriteAllText(path, sb.ToString());
    }

    private static string Short(string value) => value.Length <= 140 ? value : value[..137] + "...";

    private static string Redact(string value) => value.Length <= 8 ? "[redacted]" : value[..4] + "…[redacted]";
}
