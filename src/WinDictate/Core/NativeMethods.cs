using System.Runtime.InteropServices;
using System.Text;

namespace WinDictate.Core;

internal static class NativeMethods
{
    // ----- Low-level keyboard hook -----

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;
    public const uint LLKHF_INJECTED = 0x10;

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    // ----- Message loop (keyboard hook thread) -----

    public const uint WM_APP = 0x8000;
    public const uint PM_NOREMOVE = 0x0000;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    /// <summary>Returns &gt; 0 for a message, 0 for WM_QUIT, -1 on error.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    // ----- Foreground window info -----

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO pgui);

    /// <summary>
    /// The foreground window and, when Windows exposes one, its focused control.
    /// Cheap and sends no window messages, so it is safe on the keyboard-hook thread.
    /// (Browsers and other single-window apps report one control for all fields.)
    /// </summary>
    public static InsertionTarget CurrentInsertionTarget()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return default;
        uint thread = GetWindowThreadProcessId(window, out _);
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        IntPtr focus = thread != 0 && GetGUIThreadInfo(thread, ref info) ? info.hwndFocus : IntPtr.Zero;
        return new InsertionTarget(window, focus);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);

    /// <summary>
    /// Short title of a window for the overlay. For a window of this process
    /// GetWindowText sends WM_GETTEXT, so call it on the UI thread (never on the
    /// keyboard-hook thread); for other processes it reads the caption directly.
    /// </summary>
    public static string GetWindowTitle(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return "the active window";
            var sb = new StringBuilder(256);
            GetWindowTextW(hwnd, sb, sb.Capacity);
            var title = sb.ToString().Trim();
            if (title.Length == 0) return "the active window";
            if (title.Length > 40) title = title[..40] + "…";
            return title;
        }
        catch
        {
            return "the active window";
        }
    }

    // ----- SendInput -----

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;

    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_V = 0x56;
    public const ushort VK_RETURN = 0x0D;
    public const ushort VK_TAB = 0x09;

    private static INPUT KeyInput(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } }
    };

    private static INPUT UnicodeInput(char c, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) } }
    };

    /// <summary>True while Shift, Ctrl, Alt or either Win key is physically held. Not for use inside the hook callback.</summary>
    public static bool AnyModifierHeld()
    {
        foreach (int vk in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }) // Shift, Ctrl, Alt, LWin, RWin
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) return true;
        return false;
    }

    /// <summary>How much of a SendInput call Windows accepted.</summary>
    public enum InputDelivery { All, None, Partial }

    /// <summary>Sends Ctrl+V and says how much of it Windows accepted (it may accept
    /// none while, for example, a secure desktop is active; UIPI blocking may not be
    /// reported). Partial means V went down, so the paste has probably happened.
    /// No key is left logically held down.</summary>
    public static InputDelivery SendCtrlV()
    {
        var inputs = new[]
        {
            KeyInput(VK_CONTROL, false),
            KeyInput(VK_V, false),
            KeyInput(VK_V, true),
            KeyInput(VK_CONTROL, true)
        };
        int size = Marshal.SizeOf<INPUT>();
        uint sent = SendInput((uint)inputs.Length, inputs, size);
        var (result, keysToRelease) = AfterCtrlV(sent);
        if (keysToRelease.Length > 0)
        {
            int error = Marshal.GetLastPInvokeError(); // keep the reason for the caller's log
            var release = Array.ConvertAll(keysToRelease, vk => KeyInput(vk, true));
            SendInput((uint)release.Length, release, size);
            Marshal.SetLastPInvokeError(error);
        }
        return result;
    }

    /// <summary>
    /// What it means when Windows accepted only the first <paramref name="accepted"/>
    /// of Ctrl+V's four key events (Ctrl down, V down, V up, Ctrl up): whether the
    /// paste may have happened (it does once V has gone down), and which keys must
    /// still be released so that none stays logically held.
    /// </summary>
    internal static (InputDelivery Result, ushort[] KeysToRelease) AfterCtrlV(uint accepted) => accepted switch
    {
        0 => (InputDelivery.None, Array.Empty<ushort>()),
        1 => (InputDelivery.None, new[] { VK_CONTROL }),           // only Ctrl went down
        2 => (InputDelivery.Partial, new[] { VK_V, VK_CONTROL }),  // V is down as well
        3 => (InputDelivery.Partial, new[] { VK_CONTROL }),        // V went down and came up
        _ => (InputDelivery.All, Array.Empty<ushort>())
    };

    /// <summary>Types text as Unicode key events and says how much of it Windows accepted.
    /// Stops at the first rejected batch rather than typing a garbled remainder.</summary>
    public static InputDelivery SendUnicodeText(string text)
    {
        bool anySent = false;
        var batch = new List<INPUT>(128);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') continue;
                c = '\n';
            }
            if (c == '\n')
            {
                batch.Add(KeyInput(VK_RETURN, false));
                batch.Add(KeyInput(VK_RETURN, true));
            }
            else if (c == '\t')
            {
                batch.Add(KeyInput(VK_TAB, false));
                batch.Add(KeyInput(VK_TAB, true));
            }
            else
            {
                batch.Add(UnicodeInput(c, false));
                batch.Add(UnicodeInput(c, true));
            }
            if (batch.Count >= 120)
            {
                uint flushed = Flush(batch, out bool complete);
                anySent |= flushed > 0;
                if (!complete) return anySent ? InputDelivery.Partial : InputDelivery.None;
                Thread.Sleep(2);
            }
        }
        uint last = Flush(batch, out bool allSent);
        anySent |= last > 0;
        if (allSent) return InputDelivery.All;
        return anySent ? InputDelivery.Partial : InputDelivery.None;

        static uint Flush(List<INPUT> batch, out bool complete)
        {
            complete = true;
            if (batch.Count == 0) return 0;
            uint sent = SendInput((uint)batch.Count, batch.ToArray(), Marshal.SizeOf<INPUT>());
            complete = sent == batch.Count;
            batch.Clear();
            return sent;
        }
    }

    // ----- Clipboard -----

    /// <summary>Changes whenever the clipboard's contents change (per window station).</summary>
    [DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();

    // ----- Window styles (overlay) -----

    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_NOACTIVATE = 0x08000000;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_TRANSPARENT = 0x00000020;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
