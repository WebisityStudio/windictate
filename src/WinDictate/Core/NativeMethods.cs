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

    // ----- Foreground window info -----

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);

    public static string GetForegroundWindowTitle()
    {
        try
        {
            var hwnd = GetForegroundWindow();
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

    public static void SendCtrlV()
    {
        var inputs = new[]
        {
            KeyInput(VK_CONTROL, false),
            KeyInput(VK_V, false),
            KeyInput(VK_V, true),
            KeyInput(VK_CONTROL, true)
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public static void SendUnicodeText(string text)
    {
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
                Flush(batch);
                Thread.Sleep(2);
            }
        }
        Flush(batch);

        static void Flush(List<INPUT> batch)
        {
            if (batch.Count == 0) return;
            SendInput((uint)batch.Count, batch.ToArray(), Marshal.SizeOf<INPUT>());
            batch.Clear();
        }
    }

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
