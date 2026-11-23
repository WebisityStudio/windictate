using WinDictate.Core;
using WinDictate.UI;
using WinDictate.UI.Indicators;
using Xunit;

namespace WinDictate.Tests;

/// <summary>The wording and small decisions behind the Settings window, the pilot notice and the indicator's target name.</summary>
public sealed class SettingsLogicTests
{
    [Theory]
    [InlineData("Ctrl+Alt", new[] { "Ctrl", "Alt" })]
    [InlineData("Ctrl + Shift", new[] { "Ctrl", "Shift" })]
    [InlineData("RCtrl", new[] { "RCtrl" })]
    [InlineData("", new[] { "Ctrl", "Alt" })]
    [InlineData(null, new[] { "Ctrl", "Alt" })]
    public void Shortcut_IsShownKeyByKey(string? hotkey, string[] keys) =>
        Assert.Equal(keys, SettingsLogic.HotkeyKeys(hotkey));

    [Fact]
    public void StatusLine_SaysEverythingStaysOnThePc_AndNamesAModelProblem()
    {
        var ready = SettingsLogic.StatusLine(ModelCheck.Ok);
        Assert.True(ready.AllGood);
        Assert.Equal("Ready. Everything stays on this PC.", ready.Text);

        Assert.True(SettingsLogic.StatusLine(null).AllGood); // still checking
        Assert.Contains("Everything stays on this PC", SettingsLogic.StatusLine(null).Text);

        var missing = SettingsLogic.StatusLine(ModelCheck.Missing);
        Assert.False(missing.AllGood);
        Assert.Equal("Speech model missing: reinstall WinDictate", missing.Text);

        foreach (var broken in new[] { ModelCheck.WrongHash, ModelCheck.WrongSize, ModelCheck.Unreadable })
        {
            Assert.False(SettingsLogic.StatusLine(broken).AllGood);
            Assert.Contains("reinstall", SettingsLogic.StatusLine(broken).Text);
        }
    }

    [Fact]
    public void MicrophoneBar_IsEmptyForSilence_FullForLoud_AndRisesInBetween()
    {
        Assert.Equal(0, SettingsLogic.MeterLevel(0f));
        Assert.Equal(0, SettingsLogic.MeterLevel(float.NaN));
        Assert.Equal(0, SettingsLogic.MeterLevel(0.001f)); // quieter than the quietest speech
        Assert.Equal(1, SettingsLogic.MeterLevel(1f));
        Assert.Equal(1, SettingsLogic.MeterLevel(5f));
        double quiet = SettingsLogic.MeterLevel(0.02f), normal = SettingsLogic.MeterLevel(0.15f);
        Assert.InRange(quiet, 0.2, 0.6);
        Assert.InRange(normal, quiet + 0.2, 0.95);
    }

    [Fact]
    public void PilotNotice_IsWordForWordTheApprovedText()
    {
        Assert.Equal(
            "This pilot tool is intended for post-event dictation and drafting. " +
            "Do not use it to record live conversations, calls or meetings. " +
            "Review all transcribed text before saving, sending or submitting it. " +
            "Speech recognition may misinterpret names, numbers and specialist terminology.",
            PilotNotice.Text);
        Assert.Contains("Ctrl+Alt", PilotNotice.HowTo("Ctrl+Alt"));
        Assert.Contains("Esc", PilotNotice.HowTo("Ctrl+Alt")); // the cancel route is stated
    }

    [Theory]
    [InlineData("Microsoft Outlook", "Microsoft Office", "OUTLOOK", "Outlook")]
    [InlineData("Microsoft Word", null, "WINWORD", "Word")]
    [InlineData("Microsoft Edge", "Microsoft Edge", "msedge", "Edge")]
    [InlineData("Microsoft Teams", null, "ms-teams", "Teams")]
    [InlineData("Microsoft® Excel®", null, "EXCEL", "Excel")]
    [InlineData("Notepad", null, "notepad", "Notepad")]
    [InlineData("Windows Explorer", null, "explorer", "Explorer")]
    [InlineData("", "", "olk", "olk")]
    [InlineData(null, "Some Product", "x", "Some Product")]
    [InlineData("A description that is far too long for the pill", null, "x", "A description that is…")]
    [InlineData(null, null, null, "")]
    public void TargetApp_GetsAShortFriendlyName(string? description, string? product, string? file, string expected) =>
        Assert.Equal(expected, TargetApp.Friendly(description, product, file));
}
