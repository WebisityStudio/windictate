namespace WinDictate.Core;

/// <summary>
/// Runs <see cref="HotkeyMonitor.WatchdogTick"/> on a timer thread, never on the
/// keyboard-hook thread, so it keeps working even if the hook stops delivering
/// events. It only runs while the monitor needs it (a session is active or the
/// combo is poisoned), so an idle app does not keep waking the CPU.
/// </summary>
public sealed class HotkeyWatchdog : IDisposable
{
    public const int DefaultIntervalMs = 100;

    private readonly HotkeyMonitor _monitor;
    private readonly int _intervalMs;
    private readonly System.Threading.Timer _timer;
    private volatile bool _disposed;

    public HotkeyWatchdog(HotkeyMonitor monitor, int intervalMs = DefaultIntervalMs)
    {
        _monitor = monitor;
        _intervalMs = Math.Max(10, intervalMs);
        _timer = new System.Threading.Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        _monitor.WatchdogNeeded += Arm;
        if (_monitor.NeedsWatchdog) Arm();
    }

    // Raised under the monitor's lock, possibly on the hook thread: only schedule.
    private void Arm()
    {
        if (_disposed) return;
        try { _timer.Change(_intervalMs, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    private void Tick()
    {
        if (_disposed) return;
        bool again;
        try
        {
            again = _monitor.WatchdogTick();
        }
        catch (Exception ex)
        {
            Logger.Error("Hotkey watchdog check failed", ex);
            again = true;
        }
        // One-shot re-arming: a start that races with an idle tick arms the timer
        // itself, so the watchdog can never be left off while it is needed.
        if (again) Arm();
    }

    public void Dispose()
    {
        _disposed = true;
        _monitor.WatchdogNeeded -= Arm;
        _timer.Dispose();
    }
}
