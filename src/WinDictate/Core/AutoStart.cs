using Microsoft.Win32;

namespace WinDictate.Core;

/// <summary>
/// "Start with Windows": a Run entry for the signed-in user only (no
/// administrator rights, nothing machine-wide). It is written only when the
/// user switches it on in Settings and removed when they switch it off. Windows
/// itself is the record of the choice, so the switch always shows what will
/// really happen at the next sign-in.
/// </summary>
internal static class AutoStart
{
    public const string EntryName = "WinDictate";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public enum State
    {
        Off,

        On,

        /// <summary>The entry exists, but the user switched it off in Task Manager or Windows' Startup apps.</summary>
        OffInWindows
    }

    public static State Read(string entryName = EntryName, string? exePath = null)
    {
        try
        {
            exePath ??= Environment.ProcessPath ?? "";
            using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (!AutoStartRules.RunCommandMatches(run?.GetValue(entryName) as string, exePath)) return State.Off;
            using RegistryKey? approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return AutoStartRules.IsSwitchedOffInWindows(approved?.GetValue(entryName) as byte[])
                ? State.OffInWindows
                : State.On;
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not read the Start with Windows setting: " + ex.Message);
            return State.Off;
        }
    }

    /// <returns>False if Windows refused the change (for example, blocked by policy).</returns>
    public static bool Set(bool enabled, string entryName = EntryName, string? exePath = null)
    {
        try
        {
            exePath ??= Environment.ProcessPath ?? "";
            using RegistryKey run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (!enabled)
            {
                run.DeleteValue(entryName, throwOnMissingValue: false);
                return true;
            }
            if (exePath.Length == 0) return false;
            run.SetValue(entryName, AutoStartRules.RunCommand(exePath), RegistryValueKind.String);
            // The user has just asked for it here, so an earlier "off" set in Task Manager no longer stands.
            using RegistryKey? approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(entryName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("Could not change the Start with Windows setting", ex);
            return false;
        }
    }
}
