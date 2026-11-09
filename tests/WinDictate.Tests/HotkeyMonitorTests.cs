using System.Collections.Concurrent;
using WinDictate.Core;
using Xunit;

namespace WinDictate.Tests;

public class HotkeyMonitorTests
{
    private const int LCtrl = 0xA2;
    private const int RCtrl = 0xA3;
    private const int LAlt = 0xA4;
    private const int RAlt = 0xA5;
    private const int KeyE = 0x45;
    private const int Esc = 0x1B;
    private const int Del = 0x2E;

    private readonly HotkeyMonitor _monitor;
    private readonly List<string> _events = new();
    private readonly HashSet<int> _physicallyDown = new();

    public HotkeyMonitorTests()
    {
        _monitor = new HotkeyMonitor("Ctrl+Alt");
        _monitor.PhysicalKeyCheck = vk => _physicallyDown.Contains(vk);
        _monitor.SessionStarted += _ => _events.Add("start");
        _monitor.SessionEnded += (_, reason) => _events.Add(Name(reason));
    }

    private static string Name(SessionEndReason reason) => reason switch
    {
        SessionEndReason.Released => "end",
        SessionEndReason.Stopped => "stop",
        SessionEndReason.Cancelled => "cancel",
        SessionEndReason.KeysLost => "lost",
        _ => reason.ToString()
    };

    // These helpers model Windows: the low-level hook callback runs *before*
    // the asynchronous key state is updated, so while OnKeyEvent runs the probe
    // still reports the event's own key in its previous state. The physical
    // state only changes after the callback returns.

    private void Press(int vk)
    {
        _monitor.OnKeyEvent(vk, true);
        _physicallyDown.Add(vk);
    }

    private void Release(int vk)
    {
        _monitor.OnKeyEvent(vk, false);
        _physicallyDown.Remove(vk);
    }

    /// <summary>The key goes up where the hook never sees it (secure desktop, UAC, elevated window).</summary>
    private void ReleaseUnseen(int vk) => _physicallyDown.Remove(vk);

    [Fact]
    public void HoldAndRelease_StartsThenEnds()
    {
        Press(LCtrl);
        Press(LAlt);
        Release(LAlt);
        Release(LCtrl);
        Assert.Equal(new[] { "start", "end" }, _events);
    }

    [Fact]
    public void EitherSideOfModifier_Counts()
    {
        Press(RCtrl);
        Press(RAlt);
        Release(RCtrl);
        Assert.Equal(new[] { "start", "end" }, _events);
    }

    [Fact]
    public void SessionIds_IncreaseAndMatch()
    {
        var ids = new List<string>();
        _monitor.SessionStarted += id => ids.Add($"start {id}");
        _monitor.SessionEnded += (id, _) => ids.Add($"end {id}");
        for (int i = 0; i < 2; i++)
        {
            Press(LCtrl);
            Press(LAlt);
            Release(LAlt);
            Release(LCtrl);
        }
        Assert.Equal(new[] { "start 1", "end 1", "start 2", "end 2" }, ids);
    }

    [Fact]
    public void OtherKey_CancelsSession()
    {
        Press(LCtrl);
        Press(LAlt);
        Press(KeyE);
        Release(KeyE);
        Release(LAlt);
        Release(LCtrl);
        Assert.Equal(new[] { "start", "cancel" }, _events);
    }

    [Fact]
    public void Escape_CancelsSession()
    {
        Press(LCtrl);
        Press(LAlt);
        Press(Esc);
        Release(Esc);
        Release(LCtrl);
        Release(LAlt);
        Assert.Equal(new[] { "start", "cancel" }, _events);
    }

    [Fact]
    public void AltGrTyping_ProducesNoExtraSessions()
    {
        // AltGr on European layouts arrives as LCtrl+RAlt; typing letters with it
        // must cancel once and stay quiet until the combo is fully released.
        Press(LCtrl);
        Press(RAlt);
        Press(KeyE);
        Release(KeyE);
        Press(KeyE);
        Release(KeyE);
        Release(RAlt);
        Release(LCtrl);
        Assert.Equal(new[] { "start", "cancel" }, _events);
    }

    [Fact]
    public void PoisonedCombo_DoesNotRestart_UntilFullyReleased()
    {
        Press(LCtrl);
        Press(LAlt);
        Press(KeyE);              // cancel + poison
        Release(KeyE);
        Release(LAlt);
        Press(LAlt);              // still poisoned: Ctrl never released
        Release(LAlt);
        Release(LCtrl);           // fully released: poison clears
        Press(LCtrl);
        Press(LAlt);
        Assert.Equal(new[] { "start", "cancel", "start" }, _events);
    }

    [Theory]
    [InlineData(LAlt, LCtrl)]
    [InlineData(LCtrl, LAlt)]
    public void CancelThenFullRelease_NextHoldStartsAgain_EveryTime(int releasedFirst, int releasedLast)
    {
        // Regression: the final key-up probed the key being released, which the
        // OS still reports as down inside the hook, so the poison never cleared
        // and every later hold did nothing until an unrelated key was typed.
        for (int round = 0; round < 3; round++)
        {
            Press(LCtrl);
            Press(LAlt);
            Press(Esc);
            Release(Esc);
            Release(releasedFirst);
            Release(releasedLast);
        }
        Assert.Equal(new[] { "start", "cancel", "start", "cancel", "start", "cancel" }, _events);
    }

    [Fact]
    public void Disabled_ProducesNothing()
    {
        _monitor.Enabled = false;
        Press(LCtrl);
        Press(LAlt);
        Release(LAlt);
        Release(LCtrl);
        Assert.Empty(_events);
    }

    [Fact]
    public void StaleKey_IsPrunedOnNextKeyDown()
    {
        // Simulates a key-up swallowed by a secure desktop (Ctrl+Alt+Del, UAC):
        // the monitor saw Ctrl go down, never saw it come up, and the OS now
        // reports it as released. The next key event must not start a session.
        Press(LCtrl);
        ReleaseUnseen(LCtrl);
        Press(LAlt);
        Assert.Empty(_events);
    }

    [Fact]
    public void SwallowedRelease_NoticedOnNextKeyDown_EndsAsKeysLost()
    {
        // Both key-ups went to a secure desktop; the next Ctrl press reveals it.
        // The session must not end normally (that would insert what was heard).
        Press(LCtrl);
        Press(LAlt);
        ReleaseUnseen(LAlt);
        ReleaseUnseen(LCtrl);
        Press(LCtrl);
        Assert.Equal(new[] { "start", "lost" }, _events);

        Press(LAlt);                      // a fresh hold works straight away
        Assert.Equal(new[] { "start", "lost", "start" }, _events);
    }

    [Fact]
    public void Reset_CancelsSession_AndPoisonsWhileStillHeld()
    {
        Press(LCtrl);
        Press(LAlt);
        _monitor.Reset();                 // e.g. workstation locked or suspending
        Assert.Equal(new[] { "start", "cancel" }, _events);

        _monitor.OnKeyEvent(LCtrl, true); // auto-repeat of the still-held keys
        _monitor.OnKeyEvent(LAlt, true);
        Release(LAlt);
        Release(LCtrl);
        Assert.Equal(new[] { "start", "cancel" }, _events); // no restart, no spurious end

        Press(LCtrl);
        Press(LAlt);
        Assert.Equal(new[] { "start", "cancel", "start" }, _events);
    }

    [Fact]
    public void Reset_WithKeysUp_DoesNotPoison()
    {
        _monitor.Reset();
        Assert.False(_monitor.NeedsWatchdog);
        Press(LCtrl);
        Press(LAlt);
        Assert.Equal(new[] { "start" }, _events);
    }

    [Fact]
    public void StopSession_EndsAsStopped_AndRearmsAfterFullRelease()
    {
        long id = 0;
        _monitor.SessionStarted += started => id = started;
        Press(LCtrl);
        Press(LAlt);

        Assert.True(_monitor.StopSession(id)); // recording time limit
        Assert.Equal(new[] { "start", "stop" }, _events);

        Release(LCtrl);                        // no auto-repeat: last key-up must lift the poison
        Release(LAlt);
        Assert.Equal(new[] { "start", "stop" }, _events);

        Press(LCtrl);
        Press(LAlt);
        Assert.Equal(new[] { "start", "stop", "start" }, _events);
    }

    [Fact]
    public void StopSession_IgnoresSessionsThatAlreadyEnded()
    {
        long first = 0;
        _monitor.SessionStarted += started => first = first == 0 ? started : first;
        Press(LCtrl);
        Press(LAlt);
        Release(LAlt);                         // session 1 ended normally
        Press(LAlt);                           // session 2 is running

        Assert.False(_monitor.StopSession(first)); // a late time-limit timer for session 1
        Assert.Equal(new[] { "start", "end", "start" }, _events);
    }

    [Fact]
    public void Reconfigure_CancelsActiveSession()
    {
        Press(LCtrl);
        Press(LAlt);
        _monitor.Reconfigure("Ctrl+Shift");
        Assert.Equal(new[] { "start", "cancel" }, _events);
        Assert.Equal("Ctrl+Shift", _monitor.ActiveHotkeyText);
    }

    [Fact]
    public void InvalidHotkey_FallsBackToCtrlAlt()
    {
        var monitor = new HotkeyMonitor("Foo+Bar");
        Assert.Equal("Ctrl+Alt", monitor.ActiveHotkeyText);
    }

    [Fact]
    public void SingleSidedToken_Works()
    {
        var events = new List<string>();
        var monitor = new HotkeyMonitor("RCtrl");
        monitor.PhysicalKeyCheck = _physicallyDown.Contains;
        monitor.SessionStarted += _ => events.Add("start");
        monitor.SessionEnded += (_, reason) => events.Add(Name(reason));

        monitor.OnKeyEvent(RCtrl, true);
        _physicallyDown.Add(RCtrl);
        monitor.OnKeyEvent(RCtrl, false);
        _physicallyDown.Remove(RCtrl);
        Assert.Equal(new[] { "start", "end" }, events);
    }

    [Fact]
    public void ThrowingHandler_DoesNotBreakTheStateMachine()
    {
        _monitor.SessionStarted += _ => throw new InvalidOperationException("handler bug");
        Press(LCtrl);
        Press(LAlt);
        Release(LAlt);
        Assert.Equal(new[] { "start", "end" }, _events);
    }

    // ----- Watchdog (bug 5): key-ups that never reach the hook -----

    [Fact]
    public void Watchdog_EndsSession_WhenReleaseIsNeverDelivered()
    {
        Press(LCtrl);
        Press(LAlt);
        ReleaseUnseen(LAlt);              // e.g. Ctrl+Alt+Del screen or a UAC prompt
        ReleaseUnseen(LCtrl);

        Assert.True(_monitor.WatchdogTick());  // one stale read is tolerated
        Assert.Equal(new[] { "start" }, _events);
        Assert.False(_monitor.WatchdogTick()); // second miss: cancel, nothing left to watch
        Assert.Equal(new[] { "start", "lost" }, _events);

        Press(LCtrl);                     // the next hold is a normal new session
        Press(LAlt);
        Assert.Equal(new[] { "start", "lost", "start" }, _events);
    }

    [Fact]
    public void Watchdog_KeepsSession_WhileTheHotkeyIsHeld()
    {
        Press(LCtrl);
        Press(LAlt);
        for (int i = 0; i < 50; i++)
            Assert.True(_monitor.WatchdogTick());
        Release(LAlt);
        Assert.Equal(new[] { "start", "end" }, _events);
    }

    [Fact]
    public void Watchdog_ToleratesASingleStaleRead()
    {
        Press(LCtrl);
        Press(LAlt);
        ReleaseUnseen(LAlt);              // one probe reads the key as up...
        Assert.True(_monitor.WatchdogTick());
        _physicallyDown.Add(LAlt);        // ...then it reads down again
        Assert.True(_monitor.WatchdogTick());
        ReleaseUnseen(LAlt);
        Assert.True(_monitor.WatchdogTick()); // the miss count started over
        Assert.Equal(new[] { "start" }, _events);
    }

    [Fact]
    public void Watchdog_PartialRelease_CancelsAndWaitsForTheRest()
    {
        Press(LCtrl);
        Press(LAlt);
        ReleaseUnseen(LAlt);              // Alt released on the secure desktop, Ctrl still held
        _monitor.WatchdogTick();
        Assert.True(_monitor.WatchdogTick()); // cancelled, but poisoned while Ctrl is held
        Assert.Equal(new[] { "start", "lost" }, _events);

        Press(LAlt);                      // Ctrl never released: no new session yet
        Release(LAlt);
        Release(LCtrl);
        Press(LCtrl);
        Press(LAlt);
        Assert.Equal(new[] { "start", "lost", "start" }, _events);
    }

    [Fact]
    public void Watchdog_LiftsPoison_WhenTheKeyUpsWereSwallowed()
    {
        // Ctrl+Alt+Del: the Del press reaches the hook (cancel + poison), then the
        // secure desktop takes the key-ups. Without the watchdog the next hold failed.
        Press(LCtrl);
        Press(LAlt);
        Press(Del);
        ReleaseUnseen(Del);
        ReleaseUnseen(LAlt);
        ReleaseUnseen(LCtrl);
        Assert.Equal(new[] { "start", "cancel" }, _events);
        Assert.True(_monitor.NeedsWatchdog);

        Assert.False(_monitor.WatchdogTick()); // everything is up: poison lifted
        Assert.False(_monitor.NeedsWatchdog);

        Press(LCtrl);
        Press(LAlt);
        Assert.Equal(new[] { "start", "cancel", "start" }, _events);
    }

    [Fact]
    public void Watchdog_StaysQuiet_AfterANormalRelease()
    {
        Press(LCtrl);
        Press(LAlt);
        Release(LAlt);
        Release(LCtrl);
        Assert.False(_monitor.WatchdogTick());
        Assert.False(_monitor.NeedsWatchdog);
        Assert.Equal(new[] { "start", "end" }, _events);
    }

    [Fact]
    public void WatchdogNeeded_IsRaisedForSessionsAndPoison()
    {
        int needed = 0;
        _monitor.WatchdogNeeded += () => needed++;
        Press(LCtrl);
        Press(LAlt);                      // session starts
        Assert.Equal(1, needed);
        Press(Esc);                       // cancel + poison
        Assert.Equal(2, needed);
    }

    [Fact]
    public async Task Events_StayInOrder_WhenHookAndWatchdogThreadsRace()
    {
        var keyboard = new ConcurrentDictionary<int, byte>();
        var monitor = new HotkeyMonitor("Ctrl+Alt")
        {
            PhysicalKeyCheck = keyboard.ContainsKey,
            WatchdogMissesToCancel = 1
        };
        var log = new List<(string Kind, long Id)>(); // handlers run under the monitor's lock
        monitor.SessionStarted += id => log.Add(("start", id));
        monitor.SessionEnded += (id, _) => log.Add(("end", id));

        using var stop = new CancellationTokenSource();
        var watchdog = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested) monitor.WatchdogTick();
        });
        for (int i = 0; i < 20_000; i++)
        {
            monitor.OnKeyEvent(LCtrl, true); keyboard[LCtrl] = 0;
            monitor.OnKeyEvent(LAlt, true); keyboard[LAlt] = 0;
            if (i % 3 == 0)
            {
                keyboard.TryRemove(LAlt, out _);   // releases the hook never sees
                keyboard.TryRemove(LCtrl, out _);
            }
            else
            {
                monitor.OnKeyEvent(LAlt, false); keyboard.TryRemove(LAlt, out _);
                monitor.OnKeyEvent(LCtrl, false); keyboard.TryRemove(LCtrl, out _);
            }
        }
        stop.Cancel();
        await watchdog;
        monitor.Reset();

        Assert.NotEmpty(log);
        Assert.True(log.Count % 2 == 0, "every session that started also ended");
        long previous = 0;
        for (int i = 0; i < log.Count; i += 2)
        {
            Assert.Equal("start", log[i].Kind);
            Assert.Equal("end", log[i + 1].Kind);
            Assert.Equal(log[i].Id, log[i + 1].Id);
            Assert.True(log[i].Id > previous);
            previous = log[i].Id;
        }
    }
}
