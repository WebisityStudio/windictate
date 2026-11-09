using WinDictate.Core;
using Xunit;

namespace WinDictate.Tests;

public class MicrophoneSelectorTests
{
    private const string Laptop = "Microphone (Realtek(R) Audio)";
    private const string Headset = "Headset (Jabra Evolve2 65)";
    private const string DockMic = "Microphone (USB Audio Device)";

    [Fact]
    public void ChosenMicrophone_IsFound_AfterIndexesShift()
    {
        // Chosen while undocked as index 1; docking inserts a device before it.
        var undocked = new[] { Laptop, Headset };
        var docked = new[] { Laptop, DockMic, Headset };
        Assert.Equal(1, MicrophoneSelector.Resolve(Headset, 1, undocked));
        Assert.Equal(2, MicrophoneSelector.Resolve(Headset, 1, docked));
    }

    [Fact]
    public void ChosenMicrophone_Missing_FallsBackToSystemDefault()
    {
        var devices = new[] { Laptop, DockMic };
        Assert.Equal(MicrophoneSelector.SystemDefault, MicrophoneSelector.Resolve(Headset, 1, devices));
        Assert.True(MicrophoneSelector.IsMissing(Headset, devices));
    }

    [Fact]
    public void IdenticalNames_PreferTheSavedIndex()
    {
        var devices = new[] { Laptop, Headset, Headset };
        Assert.Equal(2, MicrophoneSelector.Resolve(Headset, 2, devices));
        Assert.Equal(1, MicrophoneSelector.Resolve(Headset, 0, devices)); // index moved: first match
    }

    [Fact]
    public void NameMatch_IgnoresCaseAndSurroundingSpaces() =>
        Assert.Equal(1, MicrophoneSelector.Resolve("  headset (JABRA Evolve2 65) ", 0, new[] { Laptop, Headset }));

    [Fact]
    public void SystemDefault_StaysDefault()
    {
        Assert.Equal(MicrophoneSelector.SystemDefault, MicrophoneSelector.Resolve("", -1, new[] { Laptop, Headset }));
        Assert.False(MicrophoneSelector.IsMissing("", new[] { Laptop }));
    }

    [Fact]
    public void IndexOnlySettings_KeepWorking()
    {
        // settings.json from v0.1.3 holds only MicDeviceIndex.
        var settings = AppSettings.Parse("""{ "MicDeviceIndex": 1 }""");
        Assert.Equal("", settings.MicDeviceName);
        Assert.Equal(1, MicrophoneSelector.Resolve(settings.MicDeviceName, settings.MicDeviceIndex, new[] { Laptop, Headset }));
        // ...and an index that no longer exists falls back to the default instead of failing.
        Assert.Equal(MicrophoneSelector.SystemDefault,
            MicrophoneSelector.Resolve(settings.MicDeviceName, settings.MicDeviceIndex, new[] { Laptop }));
    }

    [Fact]
    public void NoDevices_UsesSystemDefault() =>
        Assert.Equal(MicrophoneSelector.SystemDefault, MicrophoneSelector.Resolve(Headset, 0, Array.Empty<string>()));
}
