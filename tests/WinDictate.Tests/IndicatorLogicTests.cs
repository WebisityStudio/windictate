using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using WinDictate.UI.Indicators;
using Xunit;

namespace WinDictate.Tests;

/// <summary>The waveform that shows the user their voice is being heard.</summary>
public class WaveformShaperTests
{
    private const double Frame = 1 / 60.0;

    /// <summary>A speech-like microphone peak at ordinary talking level.</summary>
    private static float Voice(double seconds) =>
        (float)(0.03 + 0.27 * Math.Abs(Math.Sin(seconds * 4.7) * Math.Sin(seconds * 1.9 + 0.7)));

    private static double HighestBar(float scale, int frames = 300)
    {
        var shaper = new WaveformShaper();
        double highest = 0;
        for (int f = 0; f < frames; f++)
        {
            shaper.Advance(Voice(f * Frame) * scale, Frame);
            highest = Math.Max(highest, shaper.Heights.Max());
        }
        return highest;
    }

    [Fact]
    public void StartsAtRest()
    {
        var shaper = new WaveformShaper();
        Assert.Equal(WaveformShaper.BarCount, shaper.Heights.Count);
        Assert.All(shaper.Heights, h => Assert.Equal(WaveformShaper.Floor, h));
        Assert.Equal(0, shaper.Level);
    }

    [Fact]
    public void Speech_MovesTheBars_WithinBounds_AndNotInUnison()
    {
        var shaper = new WaveformShaper();
        double lowest = 1, highest = 0;
        for (int f = 0; f < 600; f++)
        {
            shaper.Advance(Voice(f * Frame), Frame);
            lowest = Math.Min(lowest, shaper.Heights.Min());
            highest = Math.Max(highest, shaper.Heights.Max());
        }
        Assert.True(lowest >= WaveformShaper.Floor, $"lowest bar {lowest}");
        Assert.True(highest <= 1.0, $"highest bar {highest}");
        Assert.True(highest > 0.8, $"speech should lift the bars well up (highest {highest})");
        Assert.True(shaper.Heights.Distinct().Count() > 3, "bars should not all be the same height");
    }

    [Fact]
    public void Silence_BringsEveryBarBackToRest()
    {
        var shaper = new WaveformShaper();
        for (int f = 0; f < 120; f++) shaper.Advance(Voice(f * Frame), Frame);
        for (int f = 0; f < 120; f++) shaper.Advance(0, Frame);
        Assert.All(shaper.Heights, h => Assert.True(h - WaveformShaper.Floor < 0.01, $"bar at {h}"));
    }

    [Fact]
    public void RisesFasterThanItFalls()
    {
        var shaper = new WaveformShaper();
        int rise = 0, fall = 0;
        while (shaper.Level < 0.9 && rise < 1000) { shaper.Advance(1, Frame); rise++; }
        while (shaper.Level > 0.1 && fall < 1000) { shaper.Advance(0, Frame); fall++; }
        Assert.True(rise < fall, $"rise {rise} frames, fall {fall} frames");
    }

    [Fact]
    public void AQuietMicrophone_StillMovesTheBarsClearly()
    {
        // A fifth of the normal level: the old fixed gain barely moved the bars.
        Assert.True(HighestBar(scale: 0.2f) > 0.45, "a quiet microphone should still show the voice");
    }

    [Fact]
    public void RoomHiss_DoesNotLiftTheBars()
    {
        var shaper = new WaveformShaper();
        double highest = 0;
        for (int f = 0; f < 300; f++)
        {
            shaper.Advance(0.0005f, Frame);
            highest = Math.Max(highest, shaper.Heights.Max());
        }
        Assert.True(highest < 0.22, $"hiss lifted a bar to {highest}");
    }

    [Fact]
    public void AVeryLoudMicrophone_DoesNotPinEveryBarAtTheTop()
    {
        var shaper = new WaveformShaper();
        double lowestOnceSettled = 1;
        for (int f = 0; f < 300; f++)
        {
            shaper.Advance(1f, Frame);
            if (f > 60) lowestOnceSettled = Math.Min(lowestOnceSettled, shaper.Heights.Min());
        }
        Assert.True(lowestOnceSettled < 0.75, $"lowest bar {lowestOnceSettled}");
    }

    [Fact]
    public void SurvivesBadInput()
    {
        var shaper = new WaveformShaper();
        shaper.Advance(float.NaN, Frame);
        shaper.Advance(50, 5.0);
        shaper.Advance(-3, -1.0);
        Assert.All(shaper.Heights, h => Assert.InRange(h, WaveformShaper.Floor, 1.0));
    }

    [Fact]
    public void IsDeterministic_AndResets()
    {
        var a = new WaveformShaper();
        var b = new WaveformShaper();
        for (int f = 0; f < 200; f++)
        {
            a.Advance(Voice(f * Frame), Frame);
            b.Advance(Voice(f * Frame), Frame);
        }
        Assert.Equal(a.Heights, b.Heights);
        a.Reset();
        Assert.All(a.Heights, h => Assert.Equal(WaveformShaper.Floor, h));
        Assert.Equal(0, a.Level);
    }
}

/// <summary>Where the indicator sits: centred on the top edge of the taskbar, on whichever monitor.</summary>
public class IndicatorPlacementTests
{
    private static readonly PixelRect Laptop = new(0, 0, 1920, 1128); // 1920x1200 at 150%, 72 px taskbar

    [Fact]
    public void Laptop_CentredAndRestingOnTheTaskbar()
    {
        var (x, y) = IndicatorPlacement.BottomCentre(Laptop, widthPx: 600, heightPx: 126);
        Assert.Equal((660, 1002), (x, y));
        Assert.Equal(Laptop.Bottom, y + 126);
    }

    [Fact]
    public void MonitorAtNegativeCoordinates()
    {
        // A second monitor to the left of, and higher than, the primary one.
        var left = new PixelRect(-2560, -200, 0, 1200);
        Assert.Equal((-1480, 1116), IndicatorPlacement.BottomCentre(left, 400, 84));
    }

    [Fact]
    public void OddWidths_RoundTheSameWayOnEitherSideOfZero()
    {
        Assert.Equal((-1161, 956), IndicatorPlacement.BottomCentre(new PixelRect(-1921, 0, 0, 1040), 400, 84));
        Assert.Equal((760, 956), IndicatorPlacement.BottomCentre(new PixelRect(0, 0, 1921, 1040), 400, 84));
    }

    [Theory]
    [InlineData(1.5, 563)]  // the pilot laptops
    [InlineData(1.0, 845)]
    [InlineData(2.0, 422)]
    public void EdgeGlow_SpansTheSameShareOfTheScreen_AtAnyScaling(double dpiScale, double expectedDip) =>
        Assert.Equal(expectedDip, IndicatorPlacement.FractionalWidthDip(Laptop, dpiScale, 0.44, minimumDip: 360));

    [Fact]
    public void EdgeGlow_IsNeverNarrowerThanItsMinimum() =>
        Assert.Equal(360, IndicatorPlacement.FractionalWidthDip(new PixelRect(0, 0, 640, 440), 1.0, 0.44, minimumDip: 360));

    [Fact]
    public void ToleratesAZeroDpiScale() =>
        Assert.Equal(845, IndicatorPlacement.FractionalWidthDip(Laptop, 0, 0.44, minimumDip: 360));
}

/// <summary>
/// The controller reports in words; the indicators stay wordless unless there
/// is a problem or a wait. These pin down the translation.
/// </summary>
public class IndicatorCuesTests
{
    [Theory]
    [InlineData("Transcribing…")]
    [InlineData("Cleaning up…")]
    [InlineData("Inserting…")]
    [InlineData(null)]
    public void OrdinaryProgress_IsWordless(string? status) =>
        Assert.Equal(CueAction.Working, IndicatorCues.ForStatus(status).Action);

    [Theory]
    [InlineData("Opening microphone…", "Opening microphone")]
    [InlineData("Loading speech model…", "Getting ready")]
    [InlineData("Downloading small.en model…", "Getting ready")]
    [InlineData("Downloading small.en model… 42%", "Getting ready 42%")]
    public void Waits_GetAShortLine(string status, string expected)
    {
        IndicatorCue cue = IndicatorCues.ForStatus(status);
        Assert.Equal((CueAction.Notice, expected, NoticeKind.Busy), (cue.Action, cue.Text, cue.Kind));
    }

    [Theory]
    [InlineData("Inserted 12 words")]
    [InlineData("Inserted 1 word · in clipboard")]
    [InlineData("Inserted 3 words · earlier dictation still in clipboard")]
    public void Success_IsAWordlessTick(string result) =>
        Assert.Equal(CueAction.Done, IndicatorCues.ForResult(result, success: true).Action);

    [Theory]
    [InlineData("No speech detected", "Didn't catch that", NoticeKind.Warning)]
    [InlineData("No microphone", "No microphone found", NoticeKind.Warning)]
    [InlineData("Microphone not ready", "Microphone wasn't ready", NoticeKind.Warning)]
    [InlineData("Cancelled", "Cancelled", NoticeKind.Info)]
    [InlineData("Cancelled · hotkey interrupted", "Cancelled", NoticeKind.Info)]
    [InlineData("Not inserted · 12 words in clipboard — press Ctrl+V", "Copied. Press Ctrl+V", NoticeKind.Info)]
    [InlineData("Not inserted · 2 dictations in clipboard — press Ctrl+V", "Copied. Press Ctrl+V", NoticeKind.Info)]
    [InlineData("Focus moved · 12 words in clipboard — press Ctrl+V", "Focus moved. Press Ctrl+V", NoticeKind.Info)]
    [InlineData("Partly typed · full text in clipboard — check, then Ctrl+V", "Partly typed. Check, then Ctrl+V", NoticeKind.Warning)]
    [InlineData("Microphone not responding", "Microphone not responding", NoticeKind.Warning)]
    [InlineData("Couldn't insert or copy — please dictate again", "Couldn't insert. Dictate again", NoticeKind.Warning)]
    [InlineData("Error, see log", "Something went wrong", NoticeKind.Warning)]
    public void Problems_GetOnePlainLine(string result, string expected, NoticeKind kind)
    {
        IndicatorCue cue = IndicatorCues.ForResult(result, success: false);
        Assert.Equal((CueAction.Notice, expected, kind), (cue.Action, cue.Text, cue.Kind));
    }

    [Fact]
    public void InsertedButClipboardNotRestored_SaysSo()
    {
        // The words went in, but the dictation is still on the clipboard when the user chose otherwise.
        IndicatorCue cue = IndicatorCues.ForResult("Inserted 12 words · clipboard not restored", success: true);
        Assert.Equal((CueAction.Notice, "Inserted. Clipboard not restored", NoticeKind.Info), (cue.Action, cue.Text, cue.Kind));
    }

    [Fact]
    public void AnUnknownProblem_IsShown_NotSwallowed()
    {
        IndicatorCue cue = IndicatorCues.ForResult("Disk is full.", success: false);
        Assert.Equal((CueAction.Notice, "Disk is full", NoticeKind.Warning), (cue.Action, cue.Text, cue.Kind));

        IndicatorCue longCue = IndicatorCues.ForResult(
            "A very long message that nobody planned for and that would not fit in the pill", success: false);
        Assert.True(longCue.Text.Length <= 40 && longCue.Text.EndsWith('…'), longCue.Text);

        Assert.Equal("Something went wrong", IndicatorCues.ForResult("", success: false).Text);
    }

    /// <summary>
    /// Guard against drift: every message the controller can send must have a
    /// deliberate quiet wording. If someone adds a message there, this fails
    /// until IndicatorCues is given a rule for it.
    /// </summary>
    [Fact]
    public void EveryMessageTheControllerSends_HasADeliberateWording()
    {
        string source = File.ReadAllText(ControllerSourcePath());
        var calls = new Regex(@"\b(SessionFeedback|PipelineNotice|PipelineFlash|PipelineStatus|_overlay\.ShowStatus|_overlay\.Flash)\s*\(([^;]*);",
            RegexOptions.Singleline);
        var literal = new Regex(@"\$?""((?:[^""\\]|\\.)*)""");

        var failures = new List<string>();
        int checkedCount = 0;
        foreach (Match call in calls.Matches(source))
        {
            bool isStatus = call.Groups[1].Value.EndsWith("Status", StringComparison.Ordinal);
            foreach (Match text in literal.Matches(call.Groups[2].Value))
            {
                string message = text.Groups[1].Value;
                int brace = message.IndexOf('{');
                if (brace >= 0) message = message[..brace]; // interpolated: keep the fixed start
                if (message.Trim().Length < 3) continue;
                checkedCount++;

                if (isStatus) continue; // any status has a wording: a wait line, or the wordless working state
                if (message.StartsWith("Inserted", StringComparison.Ordinal)) continue; // success: wordless tick
                if (!IndicatorCues.Recognises(message)) failures.Add(message);
            }
        }

        Assert.True(checkedCount >= 12, $"expected to find the controller's messages, found {checkedCount}");
        Assert.True(failures.Count == 0, "No quiet wording for: " + string.Join(" | ", failures));
    }

    private static string ControllerSourcePath([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "src", "WinDictate", "Core", "DictationController.cs"));
}
