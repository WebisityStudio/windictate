namespace WinDictate.Core;

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
/// the hook thread (<see cref="Reset"/>) can probe any key.
/// </summary>
public sealed class HotkeyMonitor
{
    public event Action? SessionStarted;
    public event Action<bool>? SessionEnded; // true = cancelled

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

    private readonly object _sync = new();
    private List<int[]> _required = new();
    private readonly HashSet<int> _down = new();
    private bool _session;
    private bool _poisoned;

    public HotkeyMonitor(string hotkey)
    {
        Reconfigure(hotkey);
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

        bool sessionWasActive;
        lock (_sync)
        {
            sessionWasActive = _session;
            _required = required;
            _down.Clear();
            _session = false;
            _poisoned = false;
            ActiveHotkeyText = string.Join("+", names);
        }
        // Cancel any in-progress dictation that was started under the old config.
        if (sessionWasActive)
            SessionEnded?.Invoke(true);
    }

    public void OnKeyEvent(int vk, bool down)
    {
        bool started = false;
        bool? endedCancelled = null;

        lock (_sync)
        {
            bool relevant = _required.Any(t => t.Contains(vk));

            if (down && !relevant && _session)
            {
                // Any non-hotkey key (Esc, AltGr letters, Ctrl+Alt+Del, ...) cancels.
                _session = false;
                _poisoned = true;
                endedCancelled = true;
            }

            if (relevant)
            {
                if (down)
                {
                    // Self-heal: forget keys whose release we never saw. Only other
                    // keys are probed; the event's own key would read stale here.
                    _down.RemoveWhere(k => k != vk && !PhysicalKeyCheck(k));
                    _down.Add(vk);
                }
                else
                {
                    _down.Remove(vk);
                }
            }

            bool allDown = _required.All(t => t.Any(v => _down.Contains(v)));

            if (!_session && allDown && !_poisoned && _enabled)
            {
                _session = true;
                started = true;
            }
            else if (_session && !allDown)
            {
                _session = false;
                endedCancelled = false;
            }

            // Fully released: lift the poison. Probing the key being released
            // would still report it as down (its async state is updated after
            // this callback), which used to keep the hotkey dead after a cancel
            // until some unrelated key was typed.
            if (_poisoned && _down.Count == 0 && !AnyRequiredDown(vk, down))
                _poisoned = false;
        }

        if (started) SessionStarted?.Invoke();
        if (endedCancelled.HasValue) SessionEnded?.Invoke(endedCancelled.Value);
    }

    /// <summary>
    /// Forget all key state without firing events. Used after a forced session
    /// stop (max recording duration). If the combo is still physically held the
    /// monitor stays poisoned until everything is released. Called off the hook
    /// thread (timer, UI), where every probe is current.
    /// </summary>
    public void Reset()
    {
        lock (_sync)
        {
            _session = false;
            _down.Clear();
            _poisoned = AnyRequiredPhysicallyDown();
        }
    }

    private bool AnyRequiredPhysicallyDown() =>
        _required.Any(t => t.Any(v => PhysicalKeyCheck(v)));

    /// <summary>Like <see cref="AnyRequiredPhysicallyDown"/>, but inside a key event:
    /// the event's own key comes from the event, the rest from probes.</summary>
    private bool AnyRequiredDown(int eventVk, bool eventDown) =>
        _required.Any(t => t.Any(v => v == eventVk ? eventDown : PhysicalKeyCheck(v)));
}
