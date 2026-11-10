using WinDictate.Core;
using WinDictate.UI;
using Xunit;

namespace WinDictate.Tests;

/// <summary>The wording and small decisions behind the Settings window.</summary>
public sealed class SettingsLogicTests
{
    [Theory]
    [InlineData("base.en", "Fast")]
    [InlineData("small.en", "Balanced")]
    [InlineData("SMALL.EN", "Balanced")]
    [InlineData("medium.en", "Accurate")]
    public void ThePlainQualityChoices_StandForOneModelEach(string model, string quality)
    {
        Assert.Equal(quality, SettingsLogic.QualityOf(model).ToString());
        Assert.Equal(model.ToLowerInvariant(), SettingsLogic.ModelFor(SettingsLogic.QualityOf(model)!.Value));
    }

    [Theory]
    [InlineData("tiny.en")]
    [InlineData("large-v3-turbo")]
    [InlineData("my-own-model")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherModels_AreNoneOfTheThreeChoices(string? model) =>
        Assert.Null(SettingsLogic.QualityOf(model));

    [Fact]
    public void QualityLine_SaysWhatTheChoiceMeans_AndWarnsOfADownload()
    {
        Assert.Equal("Balanced suits most laptops", SettingsLogic.QualityLine("small.en", downloaded: true));
        Assert.Equal("Balanced suits most laptops. Downloads once, about 466 MB", SettingsLogic.QualityLine("small.en", downloaded: false));
        Assert.Equal("Using tiny.en, chosen under Advanced", SettingsLogic.QualityLine("tiny.en", downloaded: true));
        Assert.Equal("Using my-own-model, chosen under Advanced. Downloads once on first use", SettingsLogic.QualityLine("my-own-model", downloaded: false));
        Assert.Equal("On this PC. Works with no internet", SettingsLogic.ModelLine("medium.en", downloaded: true));
        Assert.Equal("Downloads once, about 1.5 GB", SettingsLogic.ModelLine("medium.en", downloaded: false));
    }

    [Theory]
    [InlineData("Ctrl+Alt", new[] { "Ctrl", "Alt" })]
    [InlineData("Ctrl + Shift", new[] { "Ctrl", "Shift" })]
    [InlineData("RCtrl", new[] { "RCtrl" })]
    [InlineData("", new[] { "Ctrl", "Alt" })]
    [InlineData(null, new[] { "Ctrl", "Alt" })]
    public void Shortcut_IsShownKeyByKey(string? hotkey, string[] keys) =>
        Assert.Equal(keys, SettingsLogic.HotkeyKeys(hotkey));

    [Fact]
    public void StatusLine_OnlyClaimsEverythingStaysOnThePc_WhenItDoes()
    {
        var ready = SettingsLogic.StatusLine(modelDownloaded: true, cleanupOn: false, cleanupUrl: "https://api.openai.com/v1");
        Assert.True(ready.AllGood);
        Assert.Equal("Ready. Everything stays on this PC.", ready.Text);

        var cleanup = SettingsLogic.StatusLine(modelDownloaded: true, cleanupOn: true, cleanupUrl: "https://api.openai.com/v1");
        Assert.False(cleanup.AllGood);
        Assert.Equal("AI cleanup is on: text is sent to api.openai.com", cleanup.Text);

        var odd = SettingsLogic.StatusLine(modelDownloaded: true, cleanupOn: true, cleanupUrl: "not a url");
        Assert.Equal("AI cleanup is on: text is sent to the cleanup service", odd.Text);

        // Cleanup switched on but with no address does nothing, so nothing leaves the PC.
        Assert.True(SettingsLogic.StatusLine(true, cleanupOn: true, cleanupUrl: "  ").AllGood);

        var noModel = SettingsLogic.StatusLine(modelDownloaded: false, cleanupOn: false, cleanupUrl: "");
        Assert.False(noModel.AllGood);
        Assert.Equal("The speech model downloads on first use", noModel.Text);
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
    public void StartWithWindows_WritesTheProgramPathInQuotes()
    {
        Assert.Equal("\"C:\\Tools\\Win Dictate\\WinDictate.exe\"", AutoStartRules.RunCommand("C:\\Tools\\Win Dictate\\WinDictate.exe"));
        Assert.Equal("\"C:\\Tools\\WinDictate.exe\"", AutoStartRules.RunCommand("\"C:\\Tools\\WinDictate.exe\""));
    }

    [Theory]
    [InlineData("\"C:\\Tools\\WinDictate.exe\"", true)]
    [InlineData("\"c:\\tools\\windictate.EXE\"", true)]           // Windows paths ignore case
    [InlineData("C:\\Tools\\WinDictate.exe", true)]               // written without quotes
    [InlineData("\"C:\\Tools\\WinDictate.exe\" --minimised", true)]
    [InlineData("\"C:\\Old place\\WinDictate.exe\"", false)]      // a copy somewhere else
    [InlineData("\"C:\\Tools\\Other.exe\"", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void StartWithWindows_CountsOnlyAnEntryForThisCopyOfTheApp(string? command, bool expected) =>
        Assert.Equal(expected, AutoStartRules.RunCommandMatches(command, "C:\\Tools\\WinDictate.exe"));

    [Fact]
    public void StartWithWindows_RespectsBeingSwitchedOffInTaskManager()
    {
        Assert.False(AutoStartRules.IsSwitchedOffInWindows(null));
        Assert.False(AutoStartRules.IsSwitchedOffInWindows(Array.Empty<byte>()));
        Assert.False(AutoStartRules.IsSwitchedOffInWindows(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));
        Assert.True(AutoStartRules.IsSwitchedOffInWindows(new byte[] { 0x03, 0, 0, 0, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x01 }));
        Assert.False(AutoStartRules.IsSwitchedOffInWindows(new byte[] { 0x06 }));
        Assert.True(AutoStartRules.IsSwitchedOffInWindows(new byte[] { 0x07 }));
    }
}
