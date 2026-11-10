namespace WinDictate.Core;

/// <summary>
/// The rules behind "Start with Windows", free of the registry so they can be
/// unit-tested on any OS. See <see cref="AutoStart"/> for where they are applied.
/// </summary>
internal static class AutoStartRules
{
    /// <summary>The command Windows runs at sign-in: the app's path in quotes, no arguments.</summary>
    public static string RunCommand(string exePath) => "\"" + exePath.Trim().Trim('"') + "\"";

    /// <summary>
    /// True when a Run entry starts this very program. An entry left behind by a
    /// copy of the app in another folder does not count, so the switch shows "off"
    /// and turning it on points Windows at the copy that is running.
    /// </summary>
    public static bool RunCommandMatches(string? command, string exePath)
    {
        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(exePath)) return false;
        string value = command.Trim();
        string program;
        if (value.StartsWith('"'))
        {
            int close = value.IndexOf('"', 1);
            program = close > 0 ? value[1..close] : value.Trim('"');
        }
        else
        {
            program = value;
        }
        return string.Equals(program.Trim(), exePath.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Windows records "switched off in Task Manager / Startup apps" beside the Run
    /// entry, as a small binary value whose first byte is odd when it is off.
    /// No value at all means nobody has switched it off.
    /// </summary>
    public static bool IsSwitchedOffInWindows(byte[]? startupApproved) =>
        startupApproved is { Length: > 0 } && (startupApproved[0] & 1) == 1;
}
