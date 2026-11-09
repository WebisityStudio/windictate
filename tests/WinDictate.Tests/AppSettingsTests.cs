using WinDictate.Core;
using Xunit;

namespace WinDictate.Tests;

public class AppSettingsTests
{
    // A settings.json as written by v0.1.3 (AppSettings.Save writes every property).
    private const string V013SettingsJson = """
        {
          "Hotkey": "Ctrl+Alt",
          "MinHoldMs": 500,
          "MaxRecordSeconds": 300,
          "Model": "small.en",
          "Language": "en",
          "VocabularyHint": "",
          "InsertMode": "paste",
          "KeepInClipboard": true,
          "ClipboardRestoreDelayMs": 300,
          "MicDeviceIndex": 2,
          "Threads": 0,
          "SilenceThreshold": 0.006,
          "LogTranscripts": false,
          "Cleanup": {
            "Enabled": false,
            "BaseUrl": "https://api.openai.com/v1",
            "ApiKey": "",
            "Model": "gpt-4o-mini",
            "TimeoutSeconds": 12,
            "SystemPrompt": "x"
          }
        }
        """;

    [Fact]
    public void V013File_KeepsItsValues_AndGetsDefaultsForNewSettings()
    {
        var settings = AppSettings.Parse(V013SettingsJson);

        Assert.Equal(0.006, settings.SilenceThreshold);   // user's value survives
        Assert.Equal(2, settings.MicDeviceIndex);
        Assert.Equal(new AppSettings().MinVoicedMs, settings.MinVoicedMs);
        Assert.Equal("", settings.MicDeviceName);
        Assert.True(settings.AddTrailingSpace);            // new setting is on for existing users too
    }

    [Fact]
    public void Defaults_AddTrailingSpace() => Assert.True(new AppSettings().AddTrailingSpace);

    [Fact]
    public void RoundTrip_PreservesValues()
    {
        var original = new AppSettings { SilenceThreshold = 0.002, MinVoicedMs = 200, Hotkey = "Ctrl+Shift" };
        var copy = AppSettings.Parse(AppSettings.Serialize(original));
        Assert.Equal(0.002, copy.SilenceThreshold);
        Assert.Equal(200, copy.MinVoicedMs);
        Assert.Equal("Ctrl+Shift", copy.Hotkey);
    }
}
