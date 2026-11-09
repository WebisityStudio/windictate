using WinDictate.Core;
using Xunit;

namespace WinDictate.Tests;

public class TranscriberTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

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
