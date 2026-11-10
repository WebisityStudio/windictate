namespace WinDictate.Core;

/// <summary>
/// Maps the saved microphone choice to a current waveIn device index. waveIn
/// indexes shift whenever devices come and go (docking, Bluetooth headsets),
/// so the device's name is the stable part of the choice.
/// </summary>
public static class MicrophoneSelector
{
    /// <summary>The WAVE_MAPPER index: whatever Windows uses as the default input.</summary>
    public const int SystemDefault = -1;

    /// <param name="savedName">Saved product name; empty when none is stored (default, or settings older than names).</param>
    /// <param name="savedIndex">Saved waveIn index, -1 for the system default.</param>
    /// <param name="currentDevices">Product names of the devices present now, by waveIn index.</param>
    /// <returns>The index to record from, or <see cref="SystemDefault"/>.</returns>
    public static int Resolve(string? savedName, int savedIndex, IReadOnlyList<string> currentDevices)
    {
        if (!string.IsNullOrWhiteSpace(savedName))
        {
            // Still at the saved index (this also picks the right one of two
            // identically named devices when nothing has moved).
            if (IsValid(savedIndex, currentDevices) && SameName(currentDevices[savedIndex], savedName))
                return savedIndex;
            for (int i = 0; i < currentDevices.Count; i++)
                if (SameName(currentDevices[i], savedName))
                    return i;
            return SystemDefault; // the chosen microphone is not connected right now
        }

        // Settings written before names were stored: keep the old index-only behaviour.
        return IsValid(savedIndex, currentDevices) ? savedIndex : SystemDefault;
    }

    /// <summary>True when a name is saved but no current device has it.</summary>
    public static bool IsMissing(string? savedName, IReadOnlyList<string> currentDevices) =>
        !string.IsNullOrWhiteSpace(savedName) && !currentDevices.Any(d => SameName(d, savedName));

    private static bool IsValid(int index, IReadOnlyList<string> devices) => index >= 0 && index < devices.Count;

    private static bool SameName(string a, string b) =>
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
