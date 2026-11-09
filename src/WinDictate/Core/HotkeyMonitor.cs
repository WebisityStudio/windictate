namespace WinDictate.Core;

/// <summary>Why a dictation session ended.</summary>
public enum SessionEndReason
{
    /// <summary>The user let go of the hotkey: transcribe and insert.</summary>
    Released,

    /// <summary>Ended by the app at the recording time limit: transcribe and insert.</summary>
    Stopped,

    /// <summary>Another key was pressed, the hotkey changed, or the workstation locked or is
    /// suspending: discard the audio.</summary>
    Cancelled,

    /// <summary>The hotkey is no longer physically held but its release never reached the hook
    /// (secure desktop, UAC prompt, elevated window, a dropped hook): discard the audio.</summary>
    KeysLost
}

public static class SessionEndReasonExtensions
{
    /// <summary>True when the recording should be transcribed and inserted.</summary>
    public static bool KeepsAudio(this SessionEndReason reason) =>
        reason is SessionEndReason.Released or SessionEndReason.Stopped;
}

/// <summary>
/// Hold-to-dictate state machine over raw key events.
/// A session starts when every configured modifier is held and ends when any
/// of them is released. Pressing any other key during a session cancels it and
/// poisons the combo until fully released, so AltGr-style typing (Ctrl+Alt+letter
/// on many European layouts) never produces an accidental dictation.
///
/// Key state: <see cref="OnKeyEvent"/> is called from inside the WH_KEYBOARD_LL
/// callback, and Windows updates the asynchronous key state only after that
/// callback returns. During an event, <see cref="PhysicalKeyCheck"/> therefore
/// still reports the event's own key in its previous state, so that key is always
/// taken from the event; every other key's probe is current. Methods called off
/// the hook thread (<see cref="Reset"/>, <see cref="StopSession"/>,
/// <see cref="WatchdogTick"/>) can probe any key.
///
/// Threading: OnKeyEvent runs on the keyboard-hook thread, WatchdogTick on a
/// timer thread, the rest on the UI or timer threads. One lock guards all state
/// and events are raised while it is held, so subscribers always see a session
/// start and end in order, whichever thread caused the change. Handlers must
/// return quickly and must not block, wait on another thread or write to disk
/// (they may run on the hook thread). They may call back into the monitor.
/// </summary>
public sealed class HotkeyMonitor
{
    /// <summary>A session started; the argument is its id.</summary>
    public event Action<long>? SessionStarted;

    /// <summary>The session with this id ended, for this reason.</summary>
    public event Action<long, SessionEndReason>? SessionEnded;

    /// <summary>A session started or the combo was poisoned: <see cref="WatchdogTick"/>
    /// must run until it returns false.</summary>
    public event Action? WatchdogNeeded;

    // Toggled from the UI thread, read on the keyboard-hook thread.
    private volatile bool _enabled = true;
    public bool Enabled { get => _enabled; set => _enabled = value; }
    public string ActiveHotkeyText { get; private set; } = "";

    /// <summary>
    /// Probe for the OS-level key state, used to self-heal when key-up events
    /// were swallowed (secure desktop, UAC prompt, workstation lock, RDP focus
    /// loss). Replaceable in tests.
    /// </summary>
    public Func<int, bool> PhysicalKeyCheck { get; set; } =
        vk => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>
    /// Watchdog checks in a row that must find the hotkey released before an
    /// active session is cancelled. More than one tolerates a single stale read
    /// (Windows updates a key's state just after the hook sees it).
    /// </summary>
    public int WatchdogMissesToCancel { get; set; } = 2;

    private readonly object _sync = new();
    private List<int[]> _required = new();
    private readonly HashSet<int> _down = new();
    private bool _session;
    private bool _poisoned;
    private long _sessionId;
    private int _watchdogMisses;

    public HotkeyMonitor(string hotkey)
    {
        Reconfigure(hotkey);
    }

    /// <summary>True while a session is active or the combo is poisoned.</summary>
    public bool NeedsWatchdog
    {
        get { lock (_sync) return _session || _poisoned; }
    }

    public void Reconfigure(string hotkey)
    {
        var required = new List<int[]>();
        var names = new List<string>();
        foreach (var raw in (hotkey ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int[]? vks = raw.ToLowerInvariant() switch
            {
                "ctrl" or "control" => new[] { 0xA2, 0xA3 },
                "alt" => new[] { 0xA4, 0xA5 },
                "shift" => new[] { 0xA0, 0xA1 },
                "win" or "windows" => new[] { 0x5B, 0x5C },
                "lctrl" => new[] { 0xA2 },
                "rctrl" => new[] { 0xA3 },
                "lalt" => new[] { 0xA4 },
                "ralt" or "altgr" => new[] { 0xA5 },
                "lshift" => new[] { 0xA0 },
                "rshift" => new[] { 0xA1 },
                _ => null
            };
            if (vks == null)
            {
                Logger.Warn($"Unknown hotkey token '{raw}' ignored. Allowed: Ctrl, Alt, Shift, Win, LCtrl, RCtrl, LAlt, RAlt, LShift, RShift.");
                continue;
            }
            required.Add(vks);
            names.Add(raw);
        }

        if (required.Count == 0)
        {
            Logger.Warn("No valid hotkey configured, falling back to Ctrl+Alt.");
            required.Add(new[] { 0xA2, 0xA3 });
            required.Add(new[] { 0xA4, 0xA5 });
            names = new List<string> { "Ctrl", "Alt" };
        }

        lock (_sync)
        {
            // Cancel any in-progress dictation that was started under the old config.
            EndSessionLocked(SessionEndReason.Cancelled);
            _required = required;
            _down.Clear();
            _poisoned = false;
            ActiveHotkeyText = string.Join("+", names);
        }
    }

    /// <summary>A key went down or up. Called on the hook thread, inside the hook callback.</summary>
    public void OnKeyEvent(int vk, bool down)
    {
        lock (_sync)
        {
            bool relevant = _required.Any(t => t.Contains(vk));

            if (down && !relevant)
            {
                if (_session)
                {
                    // Any non-hotkey key (Esc, AltGr letters, Ctrl+Alt+Del, ...) cancels.
                    EndSessionLocked(SessionEndReason.Cancelled);
                    SetPoisonedLocked(true);
                }
            }
            else if (relevant)
            {
                if (down)
                {
                    // Self-heal: forget keys whose release we never saw. Only other
                    // keys are probed; the event's own key would read stale here.
                    int forgotten = _down.RemoveWhere(k => k != vk && !PhysicalKeyCheck(k));
                    _down.Add(vk);
                    if (forgotten > 0 && _session && !ComboDownLocked())
                    {
                        // The hotkey was let go where the hook could not see it, so
                        // what was recorded since then is not a deliberate dictation.
                        EndSessionLocked(SessionEndReason.KeysLost);
                    }
                }
                else
                {
                    _down.Remove(vk);
                }
            }

            bool comboDown = ComboDownLocked();
            if (!_session && comboDown && !_poisoned && _enabled)
                StartSessionLocked();
            else if (_session && !comboDown)
                EndSessionLocked(SessionEndReason.Released);

            // Fully released: lift the poison. Probing the key being released
            // would still report it as down (its async state is updated after
            // this callback), which used to keep the hotkey dead after a cancel
            // until some unrelated key was typed.
            if (_poisoned && _down.Count == 0 && !AnyRequiredDown(vk, down))
                _poisoned = false;
        }
    }

    /// <summary>
    /// Ends any active session as <see cref="SessionEndReason.Cancelled"/> and
    /// forgets all key state: workstation lock, suspend, unlock, resume. If a
    /// hotkey key is still physically held the combo stays poisoned until fully
    /// released. Never call from inside the hook callback.
    /// </summary>
    public void Reset()
    {
        lock (_sync)
        {
            EndSessionLocked(SessionEndReason.Cancelled);
            _down.Clear();
            SetPoisonedLocked(AnyRequiredPhysicallyDown());
        }
    }

    /// <summary>
    /// Ends session <paramref name="sessionId"/> as <see cref="SessionEndReason.Stopped"/>
    /// if it is still the active one (recording time limit). Keys still held keep
    /// the combo poisoned so they do not start another dictation straight away.
    /// Returns false if that session had already ended.
    /// </summary>
    public bool StopSession(long sessionId)
    {
        lock (_sync)
        {
            if (!_session || _sessionId != sessionId) return false;
            EndSessionLocked(SessionEndReason.Stopped);
            _down.Clear();
            SetPoisonedLocked(AnyRequiredPhysicallyDown());
            return true;
        }
    }

    /// <summary>
    /// One check of the physical key state, run off the hook thread (where probes
    /// are current) for as long as it returns true; see <see cref="HotkeyWatchdog"/>.
    /// During a session it ends the session as <see cref="SessionEndReason.KeysLost"/>
    /// once <see cref="WatchdogMissesToCancel"/> checks in a row find the hotkey no
    /// longer held: the release never reached the hook, and the microphone would
    /// otherwise stay open. While poisoned it lifts the poison as soon as every
    /// hotkey key is up, even if those key-ups were never delivered.
    /// </summary>
    public bool WatchdogTick()
    {
        lock (_sync)
        {
            if (_session)
            {
                if (_required.All(t => t.Any(v => PhysicalKeyCheck(v))))
                {
                    _watchdogMisses = 0;
                    return true;
                }
                if (++_watchdogMisses < Math.Max(1, WatchdogMissesToCancel)) return true;

                EndSessionLocked(SessionEndReason.KeysLost);
                _down.RemoveWhere(k => !PhysicalKeyCheck(k));
                SetPoisonedLocked(AnyRequiredPhysicallyDown());
                return _poisoned;
            }
            if (_poisoned)
            {
                if (AnyRequiredPhysicallyDown()) return true;
                _down.Clear();
                _poisoned = false;
            }
            return false;
        }
    }

    private void StartSessionLocked()
    {
        _session = true;
        _sessionId++;
        _watchdogMisses = 0;
        Raise(SessionStarted, _sessionId);
        Raise(WatchdogNeeded);
    }

    private void EndSessionLocked(SessionEndReason reason)
    {
        if (!_session) return;
        _session = false;
        _watchdogMisses = 0;
        Raise(SessionEnded, _sessionId, reason);
    }

    private void SetPoisonedLocked(bool poisoned)
    {
        bool newlyPoisoned = poisoned && !_poisoned;
        _poisoned = poisoned;
        if (newlyPoisoned) Raise(WatchdogNeeded);
    }

    private bool ComboDownLocked() => _required.All(t => t.Any(v => _down.Contains(v)));

    private bool AnyRequiredPhysicallyDown() =>
        _required.Any(t => t.Any(v => PhysicalKeyCheck(v)));

    /// <summary>Like <see cref="AnyRequiredPhysicallyDown"/>, but inside a key event:
    /// the event's own key comes from the event, the rest from probes.</summary>
    private bool AnyRequiredDown(int eventVk, bool eventDown) =>
        _required.Any(t => t.Any(v => v == eventVk ? eventDown : PhysicalKeyCheck(v)));

    // A throwing handler must neither leave the state machine half-updated nor
    // escape into the hook callback. The failure is logged from the thread pool
    // because handlers may be running on the hook thread.
    private static void Raise(Action? handler)
    {
        if (handler == null) return;
        try { handler(); } catch (Exception ex) { ReportHandlerFailure(ex); }
    }

    private static void Raise<T>(Action<T>? handler, T arg)
    {
        if (handler == null) return;
        try { handler(arg); } catch (Exception ex) { ReportHandlerFailure(ex); }
    }

    private static void Raise<T1, T2>(Action<T1, T2>? handler, T1 arg1, T2 arg2)
    {
        if (handler == null) return;
        try { handler(arg1, arg2); } catch (Exception ex) { ReportHandlerFailure(ex); }
    }

    private static void ReportHandlerFailure(Exception ex) =>
        ThreadPool.QueueUserWorkItem(_ => Logger.Error("Hotkey event handler failed", ex));
}
