using System.Runtime.InteropServices;

namespace WinDictate.Core;

/// <summary>
/// Passive global keyboard listener. Never swallows keys; ignores injected
/// (synthetic) events so the app's own Ctrl+V paste does not feed back into it.
///
/// The hook runs on its own high-priority thread with its own message loop, so
/// keystrokes anywhere on the machine never wait for WinDictate's UI thread.
/// Windows silently removes a low-level hook whose callback overruns
/// LowLevelHooksTimeout, so the callback only reads the key and hands it to
/// <see cref="KeyEvent"/>; that handler (the hotkey state machine) must stay
/// cheap and must never block, log to disk or wait on another thread.
/// Windows gives no notice when a hook is removed, so <see cref="Reinstall"/>
/// registers it afresh whenever it may have been lost (unlock, resume).
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    /// <summary>(virtualKeyCode, isDown), raised on the hook thread inside the hook callback.</summary>
    public event Action<int, bool>? KeyEvent;

    private const uint WM_APP_REINSTALL = NativeMethods.WM_APP + 1;
    private const uint WM_APP_STOP = NativeMethods.WM_APP + 2;

    private static readonly int VkCodeOffset =
        (int)Marshal.OffsetOf<NativeMethods.KBDLLHOOKSTRUCT>(nameof(NativeMethods.KBDLLHOOKSTRUCT.vkCode));
    private static readonly int FlagsOffset =
        (int)Marshal.OffsetOf<NativeMethods.KBDLLHOOKSTRUCT>(nameof(NativeMethods.KBDLLHOOKSTRUCT.flags));

    // Kept alive for the life of the hook: Windows only holds a raw function pointer.
    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private Thread? _thread;
    private volatile uint _threadId;
    private IntPtr _hook; // only touched on the hook thread

    public KeyboardHook()
    {
        _proc = Callback;
    }

    /// <summary>
    /// Starts the hook thread and installs the hook. Throws if Windows refuses it
    /// (endpoint security or group policy can block low-level hooks).
    /// </summary>
    public void Start()
    {
        if (_thread != null) return;
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => Run(started))
        {
            IsBackground = true,
            Name = "WinDictate keyboard hook",
            // Whisper keeps every core busy while transcribing; the hook must still
            // answer within LowLevelHooksTimeout.
            Priority = ThreadPriority.Highest
        };
        thread.Start();
        int error = started.Task.GetAwaiter().GetResult();
        if (error != 0)
        {
            thread.Join(1000);
            throw new InvalidOperationException("Failed to install keyboard hook (error " + error + ")");
        }
        _thread = thread;
    }

    /// <summary>
    /// Registers the hook again (new hook first, then the old one is removed, so
    /// no key is missed). Safe to call from any thread; returns immediately.
    /// </summary>
    public void Reinstall()
    {
        uint threadId = _threadId;
        if (threadId == 0) return;
        if (!NativeMethods.PostThreadMessageW(threadId, WM_APP_REINSTALL, IntPtr.Zero, IntPtr.Zero))
            InBackground(() => Logger.Warn("Could not reach the keyboard hook thread to re-install the hook"));
    }

    private void Run(TaskCompletionSource<int> started)
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        // Create this thread's message queue before anyone can post to it.
        NativeMethods.PeekMessageW(out _, IntPtr.Zero, 0, 0, NativeMethods.PM_NOREMOVE);

        _hook = SetHook();
        if (_hook == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            _threadId = 0;
            started.TrySetResult(error == 0 ? -1 : error);
            return;
        }
        started.TrySetResult(0);

        // The low-level hook callback is delivered while this thread waits in GetMessage.
        int result;
        while ((result = NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0)) > 0)
        {
            if (msg.hwnd == IntPtr.Zero && msg.message == WM_APP_STOP) break;
            if (msg.hwnd == IntPtr.Zero && msg.message == WM_APP_REINSTALL)
            {
                ReinstallOnHookThread();
                continue;
            }
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessageW(ref msg);
        }
        if (result < 0)
        {
            int error = Marshal.GetLastWin32Error();
            InBackground(() => Logger.Error($"Keyboard hook message loop failed (error {error}); hotkey stopped"));
        }

        _threadId = 0;
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr SetHook() =>
        NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandleW(null), 0);

    private void ReinstallOnHookThread()
    {
        var fresh = SetHook();
        if (fresh == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            InBackground(() => Logger.Warn($"Keyboard hook re-install failed (error {error}), keeping the current hook"));
            return;
        }
        var old = _hook;
        _hook = fresh;
        // Fails harmlessly if Windows had already removed the old hook.
        if (old != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(old);
        InBackground(() => Logger.Info("Keyboard hook re-installed"));
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            // Read the two fields in place: no allocation, nothing that can block.
            int flags = Marshal.ReadInt32(lParam, FlagsOffset);
            if ((flags & (int)NativeMethods.LLKHF_INJECTED) == 0)
            {
                int msg = (int)wParam;
                bool down = msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
                bool up = msg is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;
                if (down || up)
                {
                    int vk = Marshal.ReadInt32(lParam, VkCodeOffset);
                    try { KeyEvent?.Invoke(vk, down); }
                    catch (Exception ex) { InBackground(() => Logger.Error("Key handler failed", ex)); }
                }
            }
        }
        return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    /// <summary>Logging writes to disk, so never do it on the hook thread itself.</summary>
    private static void InBackground(Action action) =>
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { action(); } catch { }
        });

    public void Dispose()
    {
        var thread = _thread;
        if (thread == null) return;
        _thread = null;
        uint threadId = _threadId;
        if (threadId != 0)
            NativeMethods.PostThreadMessageW(threadId, WM_APP_STOP, IntPtr.Zero, IntPtr.Zero);
        thread.Join(2000);
    }
}
