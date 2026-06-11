using System.Runtime.InteropServices;

namespace WinDictate.Core;

/// <summary>
/// Passive global keyboard listener. Never swallows keys; ignores injected
/// (synthetic) events so the app's own Ctrl+V paste does not feed back into it.
/// Must be installed on a thread that runs a message loop (the WPF UI thread).
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    public event Action<int, bool>? KeyEvent; // (virtualKeyCode, isDown)

    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private IntPtr _hook;

    public KeyboardHook()
    {
        _proc = Callback;
    }

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = NativeMethods.SetWindowsHookExW(
            NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandleW(null), 0);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException(
                "Failed to install keyboard hook (error " + Marshal.GetLastWin32Error() + ")");
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            bool injected = (data.flags & NativeMethods.LLKHF_INJECTED) != 0;
            if (!injected)
            {
                int msg = (int)wParam;
                bool down = msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
                bool up = msg is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;
                if (down || up)
                {
                    try { KeyEvent?.Invoke((int)data.vkCode, down); }
                    catch (Exception ex) { Logger.Error("Key handler failed", ex); }
                }
            }
        }
        return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
