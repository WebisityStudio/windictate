using System.Runtime.InteropServices;

namespace WinDictate.Core;

/// <summary>
/// Clipboard calls, each on its own short-lived STA thread with a time limit,
/// never on the WPF dispatcher or the keyboard-hook thread. OLE clipboard calls
/// can block: SetDataObject retries while another app holds the clipboard, and
/// emptying or reading it sends messages to the current owner, which can hang
/// if that app is unresponsive. A call that runs out of time is reported as
/// failed and its thread is abandoned (background thread, ends with the app).
/// Clipboard contents are never logged.
/// </summary>
internal static class ClipboardAccess
{
    private static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(2);

    /// <summary>Puts text on the clipboard (rendered immediately, so it outlives the thread).</summary>
    public static Task<bool> TrySetTextAsync(string text) =>
        RunAsync(() =>
        {
            System.Windows.Forms.Clipboard.SetDataObject(text, true, 10, 50);
            return true;
        }, false, "Clipboard write");

    /// <summary>Reads the clipboard's text. Ok is false if the clipboard could not be read.</summary>
    public static Task<(bool Ok, string? Text)> TryGetTextAsync() =>
        RunAsync<(bool, string?)>(() =>
            System.Windows.Forms.Clipboard.ContainsText()
                ? (true, System.Windows.Forms.Clipboard.GetText())
                : (true, null),
            (false, null), "Clipboard read");

    public static Task<bool> TryClearAsync() =>
        RunAsync(() =>
        {
            System.Windows.Forms.Clipboard.Clear();
            return true;
        }, false, "Clipboard clear");

    private static async Task<T> RunAsync<T>(Func<T> operation, T failed, string what)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { done.TrySetResult(operation()); }
            catch (Exception ex) when (ex is ExternalException or ThreadStateException or InvalidOperationException)
            {
                Logger.Warn($"{what} failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
                done.TrySetResult(failed);
            }
            catch (Exception ex)
            {
                Logger.Error($"{what} failed", ex);
                done.TrySetResult(failed);
            }
        })
        {
            IsBackground = true,
            Name = "WinDictate clipboard"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var finished = await Task.WhenAny(done.Task, Task.Delay(TimeLimit)).ConfigureAwait(false);
        if (finished == done.Task) return await done.Task.ConfigureAwait(false);
        Logger.Warn($"{what} timed out after {TimeLimit.TotalSeconds:0} s (another app may be holding the clipboard)");
        return failed;
    }
}
