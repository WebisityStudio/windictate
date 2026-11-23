using WinDictate.Core;
using Xunit;

namespace WinDictate.Tests;

public class TranscriberTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task MissingModel_IsReportedClearly_AndNothingIsDownloaded()
    {
        string missing = Path.Combine(Path.GetTempPath(), "WinDictate.Tests", Guid.NewGuid().ToString("N"), PilotModel.FileName);
        using var transcriber = new Transcriber(missing);
        var problem = await Assert.ThrowsAsync<ModelUnavailableException>(() => transcriber.EnsureReadyAsync(null));
        Assert.Equal(ModelCheck.Missing, problem.Check);
        Assert.False(transcriber.IsReady);
        Assert.False(Directory.Exists(Path.GetDirectoryName(missing)), "nothing may be created or downloaded");
    }

    [Fact]
    public async Task ModelThatIsNotTheApprovedFile_IsNotLoaded()
    {
        string dir = Path.Combine(Path.GetTempPath(), "WinDictate.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, PilotModel.FileName);
        try
        {
            await File.WriteAllTextAsync(path, "not a model");
            using var transcriber = new Transcriber(path);
            var problem = await Assert.ThrowsAsync<ModelUnavailableException>(() => transcriber.EnsureReadyAsync(null));
            Assert.Equal(ModelCheck.WrongSize, problem.Check);
            Assert.Contains("checksum", problem.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TranscribeWithoutModel_Throws_AndReleasesTheGate()
    {
        using var transcriber = new Transcriber();
        var settings = new AppSettings();

        // Before the fix the "not loaded" throw skipped the semaphore release, so
        // the first call threw and every later call waited forever.
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            var call = transcriber.TranscribeAsync(new MemoryStream(), settings);
            var winner = await Task.WhenAny(call, Task.Delay(Patience));
            Assert.True(winner == call, $"call {attempt} hung: the transcriber's gate was never released");
            await Assert.ThrowsAsync<InvalidOperationException>(() => call);
        }
    }
}
