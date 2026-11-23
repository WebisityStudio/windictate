using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

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

    // Documented Windows clipboard formats that keep content out of clipboard
    // history (Win+V), cloud clipboard sync and well-behaved clipboard monitors.
    internal const string ExcludeFromMonitoring = "ExcludeClipboardContentFromMonitorProcessing";
    internal const string CanIncludeInHistory = "CanIncludeInClipboardHistory";
    internal const string CanUploadToCloud = "CanUploadToCloudClipboard";

    /// <summary>Formats that together still count as "just plain text", so the text alone restores them exactly.</summary>
    private static readonly HashSet<string> PlainTextFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        DataFormats.UnicodeText, DataFormats.Text, DataFormats.OemText, DataFormats.Locale,
        DataFormats.StringFormat, // "System.String": the same text, as a .NET client sees it
        ExcludeFromMonitoring, CanIncludeInHistory, CanUploadToCloud
    };

    /// <summary>
    /// Puts text on the clipboard, rendered immediately (so it outlives the
    /// thread) and marked so that Windows clipboard history and cloud clipboard
    /// leave it out.
    /// </summary>
    public static Task<bool> TrySetTextAsync(string text) =>
        RunAsync(() =>
        {
            Clipboard.SetDataObject(PrivateText(text), true, 10, 50);
            return true;
        }, false, "Clipboard write");

    /// <summary>The text plus the "do not keep in history or sync" markers.</summary>
    internal static DataObject PrivateText(string text)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, false, text);
        // Any content in this format excludes all formats from history and sync.
        data.SetData(ExcludeFromMonitoring, false, new MemoryStream(BitConverter.GetBytes(1)));
        // Belt and braces: a DWORD 0 in each of these says "not in history" and "not to the cloud".
        data.SetData(CanIncludeInHistory, false, new MemoryStream(BitConverter.GetBytes(0)));
        data.SetData(CanUploadToCloud, false, new MemoryStream(BitConverter.GetBytes(0)));
        return data;
    }

    /// <summary>
    /// Reads the clipboard without changing it: its text, and whether it holds
    /// anything besides plain text (a picture, files, formatted text), which
    /// WinDictate could not put back exactly.
    /// </summary>
    public static Task<ClipboardSnapshot> TryReadAsync() =>
        RunAsync(() =>
        {
            IDataObject? data = Clipboard.GetDataObject();
            string[] formats = data?.GetFormats(autoConvert: false) ?? Array.Empty<string>();
            bool onlyText = formats.All(PlainTextFormats.Contains);
            string? text = Clipboard.ContainsText(TextDataFormat.UnicodeText)
                ? Clipboard.GetText(TextDataFormat.UnicodeText)
                : null;
            return new ClipboardSnapshot(true, text, onlyText);
        }, ClipboardSnapshot.Unreadable, "Clipboard read");

    public static Task<bool> TryClearAsync() =>
        RunAsync(() =>
        {
            Clipboard.Clear();
            return true;
        }, false, "Clipboard clear");

    /// <summary>The names of the formats on the clipboard right now (for the Windows tests).</summary>
    internal static Task<string[]> TryGetFormatsAsync() =>
        RunAsync(() => Clipboard.GetDataObject()?.GetFormats(autoConvert: false) ?? Array.Empty<string>(),
            Array.Empty<string>(), "Clipboard formats");

    private static async Task<T> RunAsync<T>(Func<T> operation, T failed, string what)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var result = operation();
                // Too late: the caller has already treated this call as failed.
                if (!done.TrySetResult(result))
                    Logger.Warn($"{what} completed only after it had timed out");
            }
            catch (Exception ex) when (ex is ExternalException or ThreadStateException or InvalidOperationException)
            {
                Logger.Warn($"{what} failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
                done.TrySetResult(failed);
            }
            catch (Exception ex)
            {
                // Type and code only: an exception message could quote clipboard content.
                Logger.Error($"{what} failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
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
