using System.Security.Cryptography;
using WinDictate.Core;
using Xunit;

namespace WinDictate.Tests;

/// <summary>The model check and the system checks that run before anything native is loaded.</summary>
public sealed class PilotModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "WinDictate.Tests", "model-" + Guid.NewGuid().ToString("N"));

    public PilotModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private (string Path, long Size, string Sha256) FakeModel(string content)
    {
        string path = Path.Combine(_dir, "model.bin");
        File.WriteAllText(path, content);
        byte[] bytes = File.ReadAllBytes(path);
        return (path, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    [Fact]
    public void TheApprovedFile_Passes()
    {
        var (path, size, hash) = FakeModel("approved");
        Assert.Equal(ModelCheck.Ok, PilotModel.Verify(path, size, hash));
        Assert.Equal(ModelCheck.Ok, PilotModel.Verify(path, size, hash.ToUpperInvariant())); // case does not matter
    }

    [Fact]
    public void AMissingFile_IsReportedAsMissing() =>
        Assert.Equal(ModelCheck.Missing, PilotModel.Verify(Path.Combine(_dir, "absent.bin")));

    [Fact]
    public void AFileOfTheWrongSize_FailsBeforeItIsRead()
    {
        var (path, size, hash) = FakeModel("approved");
        Assert.Equal(ModelCheck.WrongSize, PilotModel.Verify(path, size + 1, hash));
    }

    [Fact]
    public void AFileOfTheRightSizeButOtherContent_FailsTheChecksum()
    {
        var (path, size, _) = FakeModel("approved");
        var (_, _, otherHash) = (path, size, Convert.ToHexStringLower(SHA256.HashData("tampered"u8.ToArray())));
        Assert.Equal(ModelCheck.WrongHash, PilotModel.Verify(path, size, otherHash));
    }

    [Fact]
    public void EveryFailure_HasAPlainExplanation()
    {
        foreach (ModelCheck check in Enum.GetValues<ModelCheck>())
            Assert.Contains(check == ModelCheck.Ok ? "matches" : PilotModel.FileName, PilotModel.Explain(check));
        Assert.Equal(64, PilotModel.Sha256.Length);
        Assert.Equal(PilotModel.Sha256[..12], PilotModel.ShortHash);
        Assert.EndsWith(Path.Combine("models", PilotModel.FileName), PilotModel.DefaultPath);
    }

    [Fact]
    public void UnsupportedProcessor_GetsAPlainMessage()
    {
        string? message = SystemCheck.Problem(cpuSupported: false, Array.Empty<string>());
        Assert.NotNull(message);
        Assert.Contains("processor is not supported", message);
        Assert.Contains("AVX2", message);
    }

    [Fact]
    public void MissingVisualCppRuntime_GetsAPlainMessageNamingTheFiles()
    {
        string? message = SystemCheck.Problem(cpuSupported: true, new[] { "msvcp140.dll", "vcomp140.dll" });
        Assert.NotNull(message);
        Assert.Contains("Visual C++ 2015-2022 Redistributable (x64)", message);
        Assert.Contains("msvcp140.dll, vcomp140.dll", message);
    }

    [Fact]
    public void ASupportedPc_HasNoProblem()
    {
        Assert.Null(SystemCheck.Problem(cpuSupported: true, Array.Empty<string>()));
        Assert.Equal(new[] { "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "vcomp140.dll" },
            SystemCheck.RequiredRuntimeLibraries);
    }
}
