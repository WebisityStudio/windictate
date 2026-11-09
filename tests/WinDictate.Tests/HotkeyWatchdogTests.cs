using System.Collections.Concurrent;
using WinDictate.Core;
using Xunit;

namespace WinDictate.Tests;

public class HotkeyWatchdogTests
{
    private const int LCtrl = 0xA2;
    private const int LAlt = 0xA4;

    [Fact]
    public async Task EndsASessionWhoseReleaseNeverArrives_OnItsOwnTimer()
    {
        var keyboard = new ConcurrentDictionary<int, byte>();
        var monitor = new HotkeyMonitor("Ctrl+Alt") { PhysicalKeyCheck = keyboard.ContainsKey };
        var ended = new TaskCompletionSource<SessionEndReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.SessionEnded += (_, reason) => ended.TrySetResult(reason);
        using var watchdog = new HotkeyWatchdog(monitor, intervalMs: 20);

        monitor.OnKeyEvent(LCtrl, true); keyboard[LCtrl] = 0;
        monitor.OnKeyEvent(LAlt, true); keyboard[LAlt] = 0;
        keyboard.Clear(); // both key-ups land on the secure desktop

        var finished = await Task.WhenAny(ended.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(finished == ended.Task, "the watchdog never ended the session");
        Assert.Equal(SessionEndReason.KeysLost, await ended.Task);
        Assert.False(monitor.NeedsWatchdog);
    }

    [Fact]
    public async Task LeavesAHeldSessionAlone()
    {
        var keyboard = new ConcurrentDictionary<int, byte>();
        var monitor = new HotkeyMonitor("Ctrl+Alt") { PhysicalKeyCheck = keyboard.ContainsKey };
        int ends = 0;
        monitor.SessionEnded += (_, _) => Interlocked.Increment(ref ends);
        using var watchdog = new HotkeyWatchdog(monitor, intervalMs: 10);

        monitor.OnKeyEvent(LCtrl, true); keyboard[LCtrl] = 0;
        monitor.OnKeyEvent(LAlt, true); keyboard[LAlt] = 0;
        await Task.Delay(300);

        Assert.Equal(0, Volatile.Read(ref ends));
        Assert.True(monitor.NeedsWatchdog);
    }
}
