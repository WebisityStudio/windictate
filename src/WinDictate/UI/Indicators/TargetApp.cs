using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WinDictate.UI.Indicators;

/// <summary>
/// The name of the application a dictation is going into ("Outlook", "Word",
/// "Edge"), shown beside the waveform so the user can see where the text will
/// land before letting go. Taken from the program's own description, never
/// from the window title (which can name a document or a customer), and never
/// logged.
/// </summary>
internal static class TargetApp
{
    private const int MaxLength = 22;

    /// <summary>A short name for the program that owns <paramref name="window"/>, or "" if unknown.</summary>
    public static string NameOf(IntPtr window)
    {
        if (window == IntPtr.Zero) return "";
        try
        {
            GetWindowThreadProcessId(window, out uint processId);
            if (processId == 0) return "";
            string? path = ImagePath(processId);
            if (string.IsNullOrEmpty(path)) return "";
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
            return Friendly(info.FileDescription, info.ProductName, Path.GetFileNameWithoutExtension(path));
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return "";
        }
    }

    /// <summary>
    /// Shortens a program's description to something that fits the pill:
    /// "Microsoft Outlook" becomes "Outlook". Pure, so it is unit-tested.
    /// </summary>
    public static string Friendly(string? description, string? product, string? fileName)
    {
        string name = FirstUseful(description) ?? FirstUseful(product) ?? FirstUseful(fileName) ?? "";
        foreach (string prefix in new[] { "Microsoft® ", "Microsoft ", "Windows " })
        {
            if (name.Length > prefix.Length + 2 && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[prefix.Length..];
                break;
            }
        }
        name = name.Replace("®", "", StringComparison.Ordinal).Replace("™", "", StringComparison.Ordinal).Trim();
        return name.Length <= MaxLength ? name : name[..(MaxLength - 1)].TrimEnd() + "…";
    }

    private static string? FirstUseful(string? value)
    {
        string trimmed = (value ?? "").Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string? ImagePath(uint processId)
    {
        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
