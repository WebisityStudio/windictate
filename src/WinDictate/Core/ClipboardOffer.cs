using System.Runtime.InteropServices;

namespace WinDictate.Core;

/// <summary>
/// Puts a dictation on the clipboard for one paste, using Windows' delayed
/// rendering: the clipboard only announces that text is available, and Windows
/// asks WinDictate for it at the moment an application reads it. That tells
/// WinDictate exactly when the target application has taken the text, so the
/// user's own clipboard is put back only afterwards, never while a busy
/// application is still about to paste. If the target never reads it (the
/// cursor was not in a text field), the dictation is simply left on the
/// clipboard instead.
///
/// The offer lives on its own STA thread with a message-only window, which owns
/// the clipboard while the offer is open and answers Windows' render requests.
/// A read after Ctrl+V was sent counts as the paste, whichever process makes
/// it (Teams and the new Outlook read through a separate WebView2 process). If
/// another program reads the text before Ctrl+V (a clipboard manager), later
/// reads can no longer be seen, and the caller falls back to a fixed wait. If
/// another program puts something new on the clipboard, that is never
/// overwritten. Clipboard contents are never logged.
/// </summary>
internal sealed class ClipboardOffer : IPasteOffer
{
    private static readonly TimeSpan OpenLimit = TimeSpan.FromSeconds(2);

    /// <summary>A pause after the offer appears, so programs that read every new clipboard
    /// content at once do it before Ctrl+V and are not mistaken for the paste.</summary>
    private const int SettleMs = 50;

    private readonly string _text;
    private readonly TaskCompletionSource<bool> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _readAfterPaste = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _replaced = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Queue<Action> _work = new();
    private readonly Thread _thread;
    private IntPtr _window;
    private volatile bool _replacedBySomeoneElse; // another program put new content on the clipboard
    private volatile bool _closed;
    private volatile bool _pasteSent;
    private volatile bool _readBeforePaste;  // rendered for someone else first: later reads are invisible
    private volatile bool _promised;         // the text is announced on the clipboard but not rendered yet
    private volatile bool _handedOver;       // the caller has the offer (false: it gave up waiting and typed instead)

    private ClipboardOffer(string text)
    {
        _text = text;
        _thread = new Thread(Run) { IsBackground = true, Name = "WinDictate paste offer" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    /// <summary>Puts <paramref name="text"/> on the clipboard as an offer for one paste.</summary>
    /// <returns>The offer, or null if the clipboard could not be taken (then nothing changed).</returns>
    public static async Task<IPasteOffer?> OpenAsync(string text)
    {
        var offer = new ClipboardOffer(text);
        offer._thread.Start();
        var opened = await Task.WhenAny(offer._opened.Task, Task.Delay(OpenLimit)).ConfigureAwait(false);
        if (opened != offer._opened.Task || !await offer._opened.Task.ConfigureAwait(false))
        {
            offer.Dispose();
            return null;
        }
        await Task.Delay(SettleMs).ConfigureAwait(false);
        offer._handedOver = true;
        return offer;
    }

    public void PasteSent() => _pasteSent = true;

    public async Task<PasteRead> WaitForReadAsync(int timeoutMs)
    {
        if (_readBeforePaste && !_readAfterPaste.Task.IsCompleted)
        {
            Logger.Info("Another program read the clipboard before the paste; the paste's own read cannot be seen");
            return PasteRead.Unknown;
        }
        await Task.WhenAny(_readAfterPaste.Task, _replaced.Task, Task.Delay(Math.Max(0, timeoutMs))).ConfigureAwait(false);
        if (_readAfterPaste.Task.IsCompleted) return PasteRead.Read;
        return _replacedBySomeoneElse ? PasteRead.Replaced : PasteRead.NotRead;
    }

    public Task<ReplaceResult> ReplaceWithAsync(string? text) => OnThread(() =>
    {
        if (_replacedBySomeoneElse) return ReplaceResult.LeftAlone; // someone else's content is on the clipboard now
        if (!OpenClipboardWithRetry()) return ReplaceResult.Failed;
        try
        {
            // Checked with the clipboard open, so nobody can put something new on it in between.
            if (_replacedBySomeoneElse || Win32.GetClipboardOwner() != _window) return ReplaceResult.LeftAlone;
            if (!Win32.EmptyClipboard()) return ReplaceResult.Failed;
            _promised = false;
            if (text != null && !PutText(text)) return ReplaceResult.Failed;
            PutPrivacyMarkers();
            return ReplaceResult.Replaced;
        }
        finally
        {
            Win32.CloseClipboard();
        }
    }, ReplaceResult.Failed);

    public Task<bool> KeepAsync() => OnThread(() =>
    {
        if (_replacedBySomeoneElse) return false;
        if (!OpenClipboardWithRetry()) return false;
        try
        {
            if (_replacedBySomeoneElse || Win32.GetClipboardOwner() != _window) return false;
            if (!_promised) return true; // already read, so already ordinary clipboard text
            // Rendering it now turns the announcement into ordinary clipboard text,
            // which stays after this offer's window is gone.
            if (!PutText(_text)) return false;
            _promised = false;
            return true;
        }
        finally
        {
            Win32.CloseClipboard();
        }
    }, false);

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        IntPtr window = _window;
        if (window != IntPtr.Zero) Win32.PostMessageW(window, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        _opened.TrySetResult(false);
    }

    // ----- The offer's own thread -----

    private void Run()
    {
        try
        {
            _window = Win32.CreateMessageWindow(this);
            if (_window == IntPtr.Zero || !OpenClipboardWithRetry())
            {
                _opened.TrySetResult(false);
                if (_window != IntPtr.Zero) Win32.DestroyWindow(_window);
                return;
            }
            bool ok = false;
            try
            {
                if (Win32.EmptyClipboard())
                {
                    // Delayed rendering: no data yet. Windows asks this window for it when
                    // someone reads it. (SetClipboardData returns NULL even when this works,
                    // so check that the text is announced instead.)
                    Win32.SetClipboardData(Win32.CF_UNICODETEXT, IntPtr.Zero);
                    ok = Win32.IsClipboardFormatAvailable(Win32.CF_UNICODETEXT);
                    if (ok)
                    {
                        _promised = true;
                        PutPrivacyMarkers();
                    }
                    else
                    {
                        Logger.Warn($"Could not offer text on the clipboard (error {Marshal.GetLastWin32Error()}); it is now empty");
                    }
                }
            }
            finally
            {
                Win32.CloseClipboard();
            }
            if (!ok || _closed)
            {
                // Not offered, or the caller stopped waiting meanwhile and typed the text:
                // destroying the window withdraws an offer nobody will follow up.
                _opened.TrySetResult(false);
                Win32.DestroyWindow(_window);
                return;
            }
            _opened.TrySetResult(true);

            while (Win32.GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                Win32.TranslateMessage(ref message);
                Win32.DispatchMessageW(ref message);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Paste offer failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
            _opened.TrySetResult(false);
        }
        finally
        {
            _closed = true;
            Win32.Forget(_window);
            lock (_work)
            {
                // Anything still queued would wait forever: run it here, on this thread.
                while (_work.Count > 0) _work.Dequeue()();
            }
        }
    }

    /// <summary>Window messages for the offer's window, on the offer's thread.</summary>
    internal IntPtr HandleMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case Win32.WM_RENDERFORMAT:
                // Someone is reading the text (the clipboard is open for them: do not open it).
                if ((uint)wParam.ToInt64() == Win32.CF_UNICODETEXT)
                {
                    if (PutText(_text)) _promised = false;
                    if (_pasteSent) _readAfterPaste.TrySetResult();
                    else _readBeforePaste = true;
                }
                return IntPtr.Zero;

            case Win32.WM_RENDERALLFORMATS:
                // The window is going away while the text is only announced. If the caller
                // has the offer, render it, so the clipboard is never left with a promise
                // nobody can keep. If the caller gave up waiting for the offer (and typed
                // the text instead), withdraw it.
                if (_promised && Win32.OpenClipboard(window))
                {
                    try
                    {
                        if (Win32.GetClipboardOwner() == window)
                        {
                            if (_handedOver)
                            {
                                if (PutText(_text)) _promised = false;
                            }
                            else
                            {
                                Win32.EmptyClipboard();
                                _promised = false;
                                Logger.Warn("Withdrew a clipboard offer that was ready too late");
                            }
                        }
                    }
                    finally
                    {
                        Win32.CloseClipboard();
                    }
                }
                return IntPtr.Zero;

            case Win32.WM_DESTROYCLIPBOARD:
                // Emptied by whoever is putting something new on it (or by us, replacing it).
                _promised = false;
                if (!Win32.InOwnReplace)
                {
                    _replacedBySomeoneElse = true;
                    _replaced.TrySetResult();
                }
                return IntPtr.Zero;

            case Win32.WM_APP_RUN:
                lock (_work)
                {
                    while (_work.Count > 0) _work.Dequeue()();
                }
                return IntPtr.Zero;

            case Win32.WM_CLOSE:
                Win32.DestroyWindow(window);
                return IntPtr.Zero;

            case Win32.WM_DESTROY:
                Win32.PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return Win32.DefWindowProcW(window, message, wParam, lParam);
    }

    /// <summary>Runs <paramref name="action"/> on the offer's thread (clipboard calls belong there).</summary>
    private Task<T> OnThread<T>(Func<T> action, T failed)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_closed || _window == IntPtr.Zero)
        {
            done.TrySetResult(failed);
            return done.Task;
        }
        lock (_work)
        {
            _work.Enqueue(() =>
            {
                try
                {
                    Win32.InOwnReplace = true;
                    done.TrySetResult(action());
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Paste offer step failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
                    done.TrySetResult(failed);
                }
                finally
                {
                    Win32.InOwnReplace = false;
                }
            });
        }
        if (!Win32.PostMessageW(_window, Win32.WM_APP_RUN, IntPtr.Zero, IntPtr.Zero))
        {
            // The window is gone: run whatever is queued here instead.
            lock (_work)
            {
                while (_work.Count > 0) _work.Dequeue()();
            }
        }
        return done.Task;
    }

    private bool OpenClipboardWithRetry()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (Win32.OpenClipboard(_window)) return true;
            Thread.Sleep(15); // another program has it open for a moment
        }
        return false;
    }

    /// <summary>Puts <paramref name="text"/> on the open clipboard as Unicode text.</summary>
    private static bool PutText(string text)
    {
        IntPtr memory = Win32.CopyToGlobal(text);
        if (memory == IntPtr.Zero) return false;
        if (Win32.SetClipboardData(Win32.CF_UNICODETEXT, memory) != IntPtr.Zero) return true;
        Win32.GlobalFree(memory); // not taken by the clipboard
        return false;
    }

    /// <summary>Marks the content so Windows clipboard history and cloud clipboard leave it out.</summary>
    private static void PutPrivacyMarkers()
    {
        foreach (var (format, value) in new[]
                 {
                     (ClipboardAccess.ExcludeFromMonitoring, 1),
                     (ClipboardAccess.CanIncludeInHistory, 0),
                     (ClipboardAccess.CanUploadToCloud, 0)
                 })
        {
            uint id = Win32.RegisterClipboardFormatW(format);
            if (id == 0) continue;
            IntPtr memory = Win32.CopyToGlobal(BitConverter.GetBytes(value));
            if (memory != IntPtr.Zero && Win32.SetClipboardData(id, memory) == IntPtr.Zero) Win32.GlobalFree(memory);
        }
    }

    // ----- Win32 -----

    private static class Win32
    {
        public const uint CF_UNICODETEXT = 13;
        public const uint WM_DESTROY = 0x0002;
        public const uint WM_CLOSE = 0x0010;
        public const uint WM_RENDERFORMAT = 0x0305;
        public const uint WM_RENDERALLFORMATS = 0x0306;
        public const uint WM_DESTROYCLIPBOARD = 0x0307;
        public const uint WM_APP_RUN = 0x8000 + 0x31; // WM_APP + n
        private static readonly IntPtr HWND_MESSAGE = new(-3);
        private const uint GMEM_MOVEABLE = 0x0002;

        // Set while the offer's own thread is emptying the clipboard to replace its content.
        [ThreadStatic] public static bool InOwnReplace;

        private static readonly object Sync = new();
        private static readonly Dictionary<IntPtr, ClipboardOffer> Offers = new();
        private static readonly WndProc Procedure = Dispatch; // kept alive for the process lifetime
        private static ushort _class;

        private delegate IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        public static IntPtr CreateMessageWindow(ClipboardOffer offer)
        {
            lock (Sync)
            {
                if (_class == 0)
                {
                    var wc = new WNDCLASSEXW
                    {
                        cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                        lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Procedure),
                        hInstance = GetModuleHandleW(null),
                        lpszClassName = "WinDictatePasteOffer"
                    };
                    _class = RegisterClassExW(ref wc);
                    if (_class == 0) return IntPtr.Zero;
                }
            }
            // The window procedure looks the offer up by window handle, which only exists
            // once CreateWindowEx returns: early messages go to DefWindowProc.
            IntPtr window = CreateWindowExW(0, "WinDictatePasteOffer", null, 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
            if (window != IntPtr.Zero)
                lock (Sync) Offers[window] = offer;
            return window;
        }

        public static void Forget(IntPtr window)
        {
            if (window == IntPtr.Zero) return;
            lock (Sync) Offers.Remove(window);
        }

        private static IntPtr Dispatch(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
        {
            ClipboardOffer? offer;
            lock (Sync) Offers.TryGetValue(window, out offer);
            return offer != null ? offer.HandleMessage(window, message, wParam, lParam) : DefWindowProcW(window, message, wParam, lParam);
        }

        public static IntPtr CopyToGlobal(string text)
        {
            byte[] bytes = new byte[(text.Length + 1) * 2];
            System.Text.Encoding.Unicode.GetBytes(text, 0, text.Length, bytes, 0);
            return CopyToGlobal(bytes);
        }

        public static IntPtr CopyToGlobal(byte[] bytes)
        {
            IntPtr memory = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
            if (memory == IntPtr.Zero) return IntPtr.Zero;
            IntPtr target = GlobalLock(memory);
            if (target == IntPtr.Zero)
            {
                GlobalFree(memory);
                return IntPtr.Zero;
            }
            Marshal.Copy(bytes, 0, target, bytes.Length);
            GlobalUnlock(memory);
            return memory;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEXW
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowExW(uint exStyle, string className, string? windowName, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyWindow(IntPtr window);

        [DllImport("user32.dll")]
        public static extern IntPtr DefWindowProcW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern int GetMessageW(out MSG message, IntPtr window, uint min, uint max);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TranslateMessage(ref MSG message);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessageW(ref MSG message);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern void PostQuitMessage(int exitCode);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenClipboard(IntPtr owner);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetClipboardData(uint format, IntPtr memory);

        [DllImport("user32.dll")]
        public static extern IntPtr GetClipboardOwner();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern uint RegisterClipboardFormatW(string format);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr memory);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalUnlock(IntPtr memory);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GlobalFree(IntPtr memory);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string? moduleName);
    }
}
