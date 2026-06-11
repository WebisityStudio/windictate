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

    private readonly HotkeyMonitor _monitor;
    private readonly List<string> _events = new();
    private readonly HashSet<int> _physicallyDown = new();

    public HotkeyMonitorTests()
    {
        _monitor = new HotkeyMonitor("Ctrl+Alt");
        _monitor.PhysicalKeyCheck = vk => _physicallyDown.Contains(vk);
        _monitor.SessionStarted += () => _events.Add("start");
        _monitor.SessionEnded += cancelled => _events.Add(cancelled ? "cancel" : "end");
    }

    private void Press(int vk)
    {
        _physicallyDown.Add(vk);
        _monitor.OnKeyEvent(vk, true);
    }

    private void Release(int vk)
    {
        _physicallyDown.Remove(vk);
        _monitor.OnKeyEvent(vk, false);
    }

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
        _physicallyDown.Remove(LCtrl);   // key-up happened where no hook runs
        Press(LAlt);
        Assert.Empty(_events);
    }

    [Fact]
    public void Reset_DropsSessionWithoutFiringEnd_AndPoisonsWhileStillHeld()
    {
        Press(LCtrl);
        Press(LAlt);
        Assert.Equal(new[] { "start" }, _events);

        _monitor.Reset();                 // e.g. max recording duration reached

        _monitor.OnKeyEvent(LCtrl, true); // auto-repeat of the still-held key
        _monitor.OnKeyEvent(LAlt, true);
        Assert.Equal(new[] { "start" }, _events); // no restart while still held

        Release(LAlt);
        Release(LCtrl);
        Assert.Equal(new[] { "start" }, _events); // no spurious end either

        Press(LCtrl);
        Press(LAlt);
        Assert.Equal(new[] { "start", "start" }, _events);
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
        monitor.SessionStarted += () => events.Add("start");
        monitor.SessionEnded += c => events.Add(c ? "cancel" : "end");

        _physicallyDown.Add(RCtrl);
        monitor.OnKeyEvent(RCtrl, true);
        _physicallyDown.Remove(RCtrl);
        monitor.OnKeyEvent(RCtrl, false);
        Assert.Equal(new[] { "start", "end" }, events);
    }
}
