using System.IO;
using System.Security.Cryptography;

namespace WinDictate.Core;

/// <summary>What a check of the speech model file found.</summary>
public enum ModelCheck
{
    Ok,
    Missing,
    WrongSize,
    WrongHash,
    Unreadable
}

/// <summary>
/// The one speech model this build uses. It ships in the <c>models</c> folder
/// next to WinDictate.exe and is never downloaded. Before it is loaded, its
/// size and SHA-256 are compared with the values below; a file that differs is
/// not loaded.
///
/// The same values are recorded in <c>pilot/model.json</c>, which the release
/// build uses to fetch and verify the file; a unit test keeps the two in step.
/// </summary>
public static class PilotModel
{
    /// <summary>Whisper small, English only, converted to the whisper.cpp (GGML) format.</summary>
    public const string Name = "small.en";

    public const string FileName = "ggml-small.en.bin";

    public const long SizeBytes = 487_614_201;

    public const string Sha256 = "c6138d6d58ecc8322097e0f987c32f1be8bb0a18532a3f88f734d1bbf9c41e5d";

    /// <summary>The language the model understands; it is English-only.</summary>
    public const string Language = "en";

    /// <summary>Where the model is expected: the <c>models</c> folder beside the program.</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "models", FileName);

    /// <summary>The first 12 characters of the hash, for display.</summary>
    public static string ShortHash => Sha256[..12];

    /// <summary>
    /// Checks the file at <paramref name="path"/> against the pinned size and
    /// hash. Reads the whole file (about half a gigabyte), so it is only called
    /// off the UI thread.
    /// </summary>
    public static ModelCheck Verify(string path, long expectedSize = SizeBytes, string expectedSha256 = Sha256)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return ModelCheck.Missing;
            if (file.Length != expectedSize) return ModelCheck.WrongSize;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1 << 20, FileOptions.SequentialScan);
            string actual = Convert.ToHexStringLower(SHA256.HashData(stream));
            return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase)
                ? ModelCheck.Ok
                : ModelCheck.WrongHash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ModelCheck.Unreadable;
        }
    }

    /// <summary>A plain explanation of a failed check, for the user and the log.</summary>
    public static string Explain(ModelCheck check) => check switch
    {
        ModelCheck.Missing => $"The speech model file ({FileName}) is missing from the program's models folder.",
        ModelCheck.WrongSize or ModelCheck.WrongHash =>
            $"The speech model file ({FileName}) is not the approved file: its checksum does not match. It may be damaged or replaced.",
        ModelCheck.Unreadable => $"The speech model file ({FileName}) could not be read.",
        _ => "The speech model file is in place and matches the approved checksum."
    };
}

/// <summary>The speech model could not be used; <see cref="Check"/> says why.</summary>
public sealed class ModelUnavailableException : Exception
{
    public ModelUnavailableException(ModelCheck check) : base(PilotModel.Explain(check)) => Check = check;

    public ModelCheck Check { get; }
}
