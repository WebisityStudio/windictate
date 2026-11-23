using System.IO.Compression;
using System.Security.Cryptography;

namespace ReleaseKit;

/// <summary>SHA-256 sums for every file, their verification, and a reproducible ZIP.</summary>
public static class Hashing
{
    public const string SumsFile = "SHA256SUMS.txt";

    public static string Sha256Of(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>All files under <paramref name="root"/>, as forward-slash relative paths, in a stable order.</summary>
    public static List<string> Files(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Writes 05-Release/SHA256SUMS.txt: one line per file, "hash  path" (the
    /// format `sha256sum -c` and `shasum -a 256 -c` read), every file except itself.
    /// </summary>
    public static void WriteSums(string submission)
    {
        string sumsPath = Path.Combine(submission, "05-Release", SumsFile);
        var lines = Files(submission)
            .Where(f => f != $"05-Release/{SumsFile}")
            .Select(f => $"{Sha256Of(Path.Combine(submission, f))}  {f}");
        File.WriteAllText(sumsPath, string.Join("\n", lines) + "\n");
    }

    /// <summary>Checks every line of SHA256SUMS.txt, and that no file is missing from it.</summary>
    public static void Verify(string submission)
    {
        string sumsPath = Path.Combine(submission, "05-Release", SumsFile);
        if (!File.Exists(sumsPath)) throw new ReleaseException($"{sumsPath} not found.");
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(sumsPath).Where(l => l.Length > 0))
        {
            int gap = line.IndexOf("  ", StringComparison.Ordinal);
            if (gap != 64) throw new ReleaseException($"Malformed line in {SumsFile}: {line}");
            string expected = line[..64];
            string file = line[(gap + 2)..];
            string full = Path.Combine(submission, file.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) throw new ReleaseException($"{file} is listed in {SumsFile} but missing.");
            string actual = Sha256Of(full);
            if (actual != expected) throw new ReleaseException($"{file}: SHA-256 is {actual}, {SumsFile} says {expected}.");
            listed.Add(file);
        }
        var unlisted = Files(submission).Where(f => f != $"05-Release/{SumsFile}" && !listed.Contains(f)).ToList();
        if (unlisted.Count > 0) throw new ReleaseException("Files not covered by SHA256SUMS.txt: " + string.Join(", ", unlisted));
        Console.WriteLine($"All {listed.Count} files match {SumsFile}.");
    }

    public static int VerifyCommand(string path)
    {
        string full = Path.GetFullPath(path);
        if (!File.Exists(Path.Combine(full, "05-Release", SumsFile)))
        {
            // Pointed at the folder holding the submission folder.
            string? inner = Directory.GetDirectories(full, "WinDictate-Restricted-Pilot-*-Submission").FirstOrDefault();
            if (inner == null) throw new ReleaseException($"No 05-Release/{SumsFile} under {full}.");
            full = inner;
        }
        Verify(full);
        return 0;
    }

    /// <summary>
    /// Zips the submission folder with its own name as the top-level folder.
    /// Entries are sorted and stamped with the commit time, so the same input
    /// gives the same archive. The model (already compressed data) is stored.
    /// </summary>
    public static void CreateZip(string submission, string zipPath, DateTimeOffset timestamp)
    {
        string top = Path.GetFileName(submission.TrimEnd(Path.DirectorySeparatorChar));
        var stamp = new DateTimeOffset(timestamp.UtcDateTime.Year, timestamp.UtcDateTime.Month, timestamp.UtcDateTime.Day,
            timestamp.UtcDateTime.Hour, timestamp.UtcDateTime.Minute, timestamp.UtcDateTime.Second - timestamp.UtcDateTime.Second % 2, TimeSpan.Zero);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (string file in Files(submission))
        {
            bool stored = file.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            var entry = zip.CreateEntry($"{top}/{file}", stored ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
            entry.LastWriteTime = stamp;
            using var input = File.OpenRead(Path.Combine(submission, file));
            using var output = entry.Open();
            input.CopyTo(output);
        }
    }
}
