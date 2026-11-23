using System.Reflection;

namespace WinDictate.Core;

/// <summary>
/// What this build is: product, version, source commit and build date, read
/// from attributes the build writes into the program (see WinDictate.csproj).
/// A developer build has no commit or date recorded and says so.
/// </summary>
public static class BuildInfo
{
    private static readonly Assembly App = typeof(BuildInfo).Assembly;

    public static string Product =>
        App.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "WinDictate";

    /// <summary>Semantic version, for example 0.2.0.</summary>
    public static string Version => App.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>The full source commit the build was made from, or "" for a developer build.</summary>
    public static string Commit => Metadata("SourceCommit");

    public static string ShortCommit => Commit.Length >= 12 ? Commit[..12] : Commit;

    /// <summary>When the release build was made (UTC, ISO 8601), or "" for a developer build.</summary>
    public static string BuildDateUtc => Metadata("BuildDateUtc");

    public static bool IsReleaseBuild => Commit.Length > 0;

    private static string Metadata(string key) =>
        App.GetCustomAttributes<AssemblyMetadataAttribute>()
           .FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.Ordinal))?.Value ?? "";
}
