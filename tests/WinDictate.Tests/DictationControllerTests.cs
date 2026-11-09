using System.Diagnostics;
using WinDictate.Core;
using Xunit;

namespace WinDictate.Tests;

/// <summary>
/// Drives the real DictationController with fakes for the microphone, Whisper,
/// the clipboard/focused app and the overlay. Utterance "A" is transcribed as
/// "Text A." and, with the default trailing space, inserted as "Text A. ".
/// </summary>
public sealed class DictationControllerTests : IDisposable
{
    // Short, but with slack for a busy CI runner: tests that check something does
    // NOT happen only assert inside windows well below these limits.
    private static readonly DictationTimings Fast = new()
    {
        OverlayDelayMs = 30,
        SlowMicNoticeMs = 120,
        ModifierWaitLimitMs = 1500,
        InsertPollMs = 5,
        MicOpenTimeoutMs = 1000
    };

    private static readonly InsertionTarget Field1 = new(new IntPtr(42), new IntPtr(7));
    private static readonly InsertionTarget Field2SameWindow = new(new IntPtr(42), new IntPtr(8));
    private static readonly InsertionTarget OtherWindow = new(new IntPtr(43), new IntPtr(9));

    private readonly FakeOverlay _overlay = new();
    private readonly FakePlatform _platform = new();
    private readonly FakeSpeech _speech = new();
    private readonly FakeTextTarget _target = new();
    private readonly AppSettings _settings = new() { MinHoldMs = 0, ClipboardRestoreDelayMs = 100 };
    private DictationController? _controller;
    private long _lastId;

    // Created on first use, so a test can adjust _settings first.
    private DictationController Controller =>
        _controller ??= new DictationController(_settings, _overlay, _platform, _speech, _target,
            cleanup: (text, _) => Task.FromResult(text), timings: Fast);

    public DictationControllerTests()
    {
        _target.Current = () => _platform.Target; // one simulated keyboard focus for both fakes
    }

    public void Dispose() => _controller?.Dispose();

    /// <summary>Holds the hotkey for a recording of <paramref name="utterance"/> and waits until the microphone is capturing.</summary>
    private async Task<long> BeginAsync(string utterance, bool voiced = true)
    {
        var recorder = _platform.Expect(utterance, voiced);
        long id = ++_lastId;
        Controller.OnSessionStarted(id);
        await WaitFor(() => recorder.Started, $"the microphone for {utterance}");
        return id;
    }

    private void End(long id, SessionEndReason reason = SessionEndReason.Released) =>
        Controller.OnSessionEnded(id, reason);

    private async Task DictateAsync(string utterance) => End(await BeginAsync(utterance));

    private static async Task WaitFor(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > timeoutMs) Assert.Fail($"Timed out waiting for {what}");
            await Task.Delay(5);
        }
    }

    /// <summary>Gives the controller time to do something it must not do.</summary>
    private static Task Settle(int ms = 150) => Task.Delay(ms);

    private bool OverlayShowed(string call) => _overlay.Calls.Contains(call);

    // ----- Bug 3: back-to-back dictations -----

    [Fact]
    public async Task Dictation_IsPasted_WithTrailingSpace_AndKeptOnClipboard()
    {
        await DictateAsync("A");
        await WaitFor(() => _target.Delivered.Length == 1, "the insertion");

        Assert.Equal(new[] { "paste:Text A. " }, _target.Delivered);
        Assert.Equal("Text A. ", _target.Clipboard);
        await WaitFor(() => OverlayShowed("flash:Inserted 2 words · in clipboard"), "the result");
    }

    [Fact]
    public async Task NewRecording_StartsWhileTheLastOneIsStillBeingTranscribed()
    {
        var a = _speech.Hold("A");
        await DictateAsync("A");
        await WaitFor(() => OverlayShowed("status:Transcribing…"), "transcription of A");

        long b = await BeginAsync("B"); // used to be ignored while busy
        Assert.Equal(2, _platform.Created.Length);
        End(b);

        a.SetResult();
        await WaitFor(() => _target.Delivered.Length == 2, "both insertions");
        Assert.Equal(new[] { "paste:Text A. ", "paste:Text B. " }, _target.Delivered);
    }

    [Fact]
    public async Task Results_AreInsertedInTheOrderSpoken()
    {
        var a = _speech.Hold("A"); // A is slow to transcribe, B and C are instant
        await DictateAsync("A");
        await DictateAsync("B");
        await DictateAsync("C");
        await Settle();
        Assert.Empty(_target.Delivered);

        a.SetResult();
        await WaitFor(() => _target.Delivered.Length == 3, "three insertions");
        Assert.Equal(new[] { "paste:Text A. ", "paste:Text B. ", "paste:Text C. " }, _target.Delivered);
    }

    [Fact]
    public async Task NothingIsInserted_WhileARecordingIsInProgress()
    {
        var a = _speech.Hold("A");
        await DictateAsync("A");
        long b = await BeginAsync("B");
        a.SetResult();
        await WaitFor(() => _speech.Transcribed.Contains("A"), "transcription of A");
        await Settle();
        Assert.Empty(_target.Delivered); // would have become Ctrl+Alt+V in the app being dictated into

        End(b);
        await WaitFor(() => _target.Delivered.Length == 2, "both insertions");
        Assert.Equal(new[] { "paste:Text A. ", "paste:Text B. " }, _target.Delivered);
    }

    [Fact]
    public async Task NothingIsInserted_WhileModifierKeysAreHeld()
    {
        _platform.ModifiersHeld = true;
        await DictateAsync("A");
        await WaitFor(() => _speech.Transcribed.Contains("A"), "transcription of A");
        await Settle(100);
        Assert.Empty(_target.Delivered);

        _platform.ModifiersHeld = false;
        await WaitFor(() => _target.Delivered.Length == 1, "the insertion");
        Assert.Equal(new[] { "paste:Text A. " }, _target.Delivered);
    }

    [Fact]
    public async Task KeyPressedJustBeforeSending_WaitsAndRetries_AndRestoresTheUsersClipboard()
    {
        _settings.KeepInClipboard = false;
        _target.UserCopies("my own text");
        int checks = 0;
        _target.KeysHeld = () => Interlocked.Increment(ref checks) <= 3; // a key goes down right at the last moment

        await DictateAsync("A");
        await WaitFor(() => _target.Delivered.Length == 1, "the insertion");
        Assert.Equal(new[] { "paste:Text A. " }, _target.Delivered);
        await WaitFor(() => _target.Clipboard == "my own text", "the clipboard restore");
    }

    [Fact]
    public async Task ModifiersHeldTooLong_TextStaysOnClipboard_EvenWithKeepInClipboardOff()
    {
        _settings.KeepInClipboard = false;
        _target.UserCopies("my own text");
        _platform.ModifiersHeld = true;

        await DictateAsync("A");
        await WaitFor(() => OverlayShowed("notice:Not inserted · 2 words in clipboard — press Ctrl+V"), "the notice");
        Assert.Empty(_target.Delivered);
        Assert.Equal("Text A. ", _target.Clipboard);
    }

    [Fact]
    public async Task ClipboardUnavailable_InPasteMode_FallsBackToTyping()
    {
        _target.ClipboardFails = true;
        await DictateAsync("A");
        await WaitFor(() => _target.Delivered.Length == 1, "the insertion");
        Assert.Equal(new[] { "type:Text A. " }, _target.Delivered);
        await WaitFor(() => OverlayShowed("flash:Inserted 2 words"), "the result");
    }

    [Fact]
    public async Task InputBlocked_TextStaysOnClipboard_AndOverlaySaysSo()
    {
        _target.InputBlocked = true; // e.g. an elevated window has focus
        await DictateAsync("A");
        await WaitFor(() => OverlayShowed("notice:Not inserted · 2 words in clipboard — press Ctrl+V"), "the notice");
        Assert.Empty(_target.Delivered);
        Assert.Equal("Text A. ", _target.Clipboard);
    }

    [Fact]
    public async Task TwoUndeliverableDictations_AreBothKeptOnClipboard()
    {
        _target.InputBlocked = true;
        await DictateAsync("A");
        await WaitFor(() => _target.Clipboard == "Text A. ", "A on the clipboard");
        await DictateAsync("B");
        await WaitFor(() => OverlayShowed("notice:Not inserted · 2 dictations in clipboard — press Ctrl+V"), "the notice");
        Assert.Equal("Text A. Text B. ", _target.Clipboard);
    }

    [Fact]
    public async Task LaterSuccess_DoesNotOverwriteAnUndeliveredDictation()
    {
        _target.InputBlocked = true;
        await DictateAsync("A");
        await WaitFor(() => _target.Clipboard == "Text A. ", "A on the clipboard");

        _target.InputBlocked = false;
        await DictateAsync("B");
        await WaitFor(() => _target.Delivered.Length == 1, "B's insertion");
        // B is typed, not pasted, so A is never replaced (no clipboard swap to race with).
        Assert.Equal(new[] { "type:Text B. " }, _target.Delivered);
        Assert.Equal("Text A. ", _target.Clipboard);
    }

    [Fact]
    public async Task UndeliveredDictation_StopsBeingProtected_OnceTheUserCopiesSomethingElse()
    {
        _target.InputBlocked = true;
        await DictateAsync("A");
        await WaitFor(() => _target.Clipboard == "Text A. ", "A on the clipboard");

        _target.InputBlocked = false;
        _target.UserCopies("something else");
        await DictateAsync("B");
        await WaitFor(() => _target.Delivered.Length == 1, "B's insertion");
        Assert.Equal("Text B. ", _target.Clipboard); // normal keep-on-clipboard behaviour again
    }

    [Fact]
    public async Task InsertionAndClipboardBothImpossible_OverlaySaysSo()
    {
        _target.InputBlocked = true;
        _target.ClipboardFails = true;
        await DictateAsync("A");
        await WaitFor(() => OverlayShowed("notice:Couldn't insert or copy — please dictate again"), "the notice");
        Assert.Empty(_target.Delivered);
    }

    [Fact]
    public async Task TranscriptionFailure_IsReported_AndLaterDictationsStillWork()
    {
        lock (_speech.Failing) _speech.Failing.Add("A");
        await DictateAsync("A");
        await WaitFor(() => OverlayShowed("notice:Error, see log"), "the error notice");

        await DictateAsync("B");
        await WaitFor(() => _target.Delivered.Length == 1, "B's insertion");
        Assert.Equal(new[] { "paste:Text B. " }, _target.Delivered);
    }

    [Fact]
    public async Task WorkstationLockedAfterADictation_ItIsKeptOnClipboard_NotTypedAfterUnlock()
    {
        var a = _speech.Hold("A");
        await DictateAsync("A");
        Controller.SetWorkstationLocked(true);
        a.SetResult();
        await WaitFor(() => _speech.Transcribed.Contains("A"), "transcription of A");
        await Settle();
        Assert.Empty(_target.Delivered);

        Controller.SetWorkstationLocked(false);
        await WaitFor(() => OverlayShowed("notice:Not inserted · 2 words in clipboard — press Ctrl+V"), "the notice");
        Assert.Empty(_target.Delivered);
        Assert.Equal("Text A. ", _target.Clipboard);

        await DictateAsync("B"); // dictations made after the unlock are inserted as usual...
        await WaitFor(() => _target.Delivered.Length == 1, "B's insertion");
        Assert.Equal(new[] { "type:Text B. " }, _target.Delivered); // ...typed, so A stays on the clipboard
        Assert.Equal("Text A. ", _target.Clipboard);
    }

    [Fact]
    public async Task WhileRecording_EarlierResultsDoNotTakeOverTheOverlay()
    {
        var a = _speech.Hold("A");
        await DictateAsync("A");
        int listeningBefore = _overlay.Calls.Count(c => c == "listening");
        long b = await BeginAsync("B");
        await WaitFor(() => _overlay.Calls.Count(c => c == "listening") == listeningBefore + 1, "B's listening overlay");
        int shown = _overlay.Calls.Length;

        a.SetResult();
        await WaitFor(() => _speech.Transcribed.Contains("A"), "transcription of A");
        await Settle();
        Assert.Equal(shown, _overlay.Calls.Length); // "Listening…" stays up while B records

        End(b);
        await WaitFor(() => _target.Delivered.Length == 2, "both insertions");
    }

    // ----- Discarded sessions -----

    [Fact]
    public async Task CancelledDictation_IsDiscarded()
    {
        End(await BeginAsync("A"), SessionEndReason.Cancelled);
        await WaitFor(() => OverlayShowed("flash:Cancelled"), "the cancel feedback");
        await Settle();
        Assert.Empty(_speech.Transcribed);
        Assert.Empty(_target.Delivered);
        Assert.True(_platform.Created[0].Stopped && _platform.Created[0].Disposed, "microphone closed");
    }

    [Fact]
    public async Task KeysLost_DiscardsTheAudio_WithANotice()
    {
        End(await BeginAsync("A"), SessionEndReason.KeysLost);
        await WaitFor(() => OverlayShowed("flash:Cancelled · hotkey interrupted"), "the notice");
        await Settle();
        Assert.Empty(_speech.Transcribed);
        Assert.Empty(_target.Delivered);
    }

    [Fact]
    public async Task TimeLimitStop_IsStillTranscribedAndInserted()
    {
        End(await BeginAsync("A"), SessionEndReason.Stopped);
        await WaitFor(() => _target.Delivered.Length == 1, "the insertion");
    }

    [Fact]
    public async Task QuickTap_VanishesSilently()
    {
        _settings.MinHoldMs = 10_000;
        End(await BeginAsync("A"));
        await WaitFor(() => _overlay.Calls.LastOrDefault() == "hide", "the overlay to hide");
        await Settle();
        Assert.Empty(_speech.Transcribed);
        Assert.Empty(_target.Delivered);
    }

    [Fact]
    public async Task SilentClip_SaysNoSpeechDetected_WithoutTranscribing()
    {
        End(await BeginAsync("A", voiced: false));
        await WaitFor(() => OverlayShowed("flash:No speech detected"), "the feedback");
        Assert.Empty(_speech.Transcribed);
    }

    // ----- Bug 7: trailing space setting -----

    [Fact]
    public async Task TrailingSpaceOff_InsertsTheTextAsIs()
    {
        _settings.AddTrailingSpace = false;
        await DictateAsync("A");
        await WaitFor(() => _target.Delivered.Length == 1, "the insertion");
        Assert.Equal(new[] { "paste:Text A." }, _target.Delivered);
    }

    [Fact]
    public async Task TypeMode_TypesTheText_AndKeepsABackupCopy()
    {
        _settings.InsertMode = "type";
        await DictateAsync("A");
        await WaitFor(() => _target.Delivered.Length == 1, "the insertion");
        Assert.Equal(new[] { "type:Text A. " }, _target.Delivered);
        await WaitFor(() => _target.Clipboard == "Text A. ", "the backup copy");
    }

    [Fact]
    public async Task KeepInClipboardOff_RestoresTheUsersClipboard()
    {
        _settings.KeepInClipboard = false;
        _target.UserCopies("my own text");
        await DictateAsync("A");
        await WaitFor(() => _target.Delivered.Length == 1, "the insertion");
        Assert.Equal(new[] { "paste:Text A. " }, _target.Delivered);
        await WaitFor(() => _target.Clipboard == "my own text", "the clipboard restore");
    }

    // ----- Bug 10: "Listening…" only once the microphone is really capturing -----

    [Fact]
    public async Task Listening_IsShownOnlyOnceTheMicrophoneCaptures()
    {
        var recorder = _platform.Expect("A");
        recorder.StartCapturingImmediately = false; // device open, no audio yet
        Controller.OnSessionStarted(1);
        await WaitFor(() => recorder.Started, "the device to open");
        await Task.Delay(Fast.OverlayDelayMs * 4);
        Assert.DoesNotContain("listening", _overlay.Calls);

        recorder.BeginCapture();
        await WaitFor(() => OverlayShowed("listening"), "the listening overlay");
    }

    [Fact]
    public async Task SlowMicrophone_SaysItIsOpening_ThenListening()
    {
        var recorder = _platform.Expect("A");
        recorder.OpenGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Controller.OnSessionStarted(1);
        await WaitFor(() => OverlayShowed("status:Opening microphone…"), "the opening notice");
        Assert.DoesNotContain("listening", _overlay.Calls);

        recorder.OpenGate.SetResult();
        await WaitFor(() => OverlayShowed("listening"), "the listening overlay");
        var calls = _overlay.Calls.ToList();
        Assert.True(calls.IndexOf("status:Opening microphone…") < calls.IndexOf("listening"));
    }

    [Fact]
    public async Task ReleasedBeforeTheMicrophoneCaptured_SaysSo_AndClosesIt()
    {
        var recorder = _platform.Expect("A");
        recorder.OpenGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Controller.OnSessionStarted(1);
        End(1);
        recorder.OpenGate.SetResult();

        await WaitFor(() => OverlayShowed("flash:Microphone not ready"), "the feedback");
        await WaitFor(() => recorder.Stopped && recorder.Disposed, "the microphone to close");
        Assert.Empty(_speech.Transcribed);
        Assert.DoesNotContain("listening", _overlay.Calls);
    }

    [Fact]
    public async Task MicrophoneFailure_SaysNoMicrophone()
    {
        var recorder = _platform.Expect("A");
        recorder.ThrowOnStart = true;
        Controller.OnSessionStarted(1);
        await WaitFor(() => OverlayShowed("flash:No microphone"), "the feedback");
        End(1);
        await Settle();
        Assert.Empty(_speech.Transcribed);
        Assert.Single(_overlay.Calls, c => c == "flash:No microphone");
    }

    [Fact]
    public async Task ReleasedBeforeTheOverlayDelay_NeverShowsListening()
    {
        var recorder = _platform.Expect("A");
        recorder.StartCapturingImmediately = false;
        var timings = Fast with { OverlayDelayMs = 5000 }; // the tap must end long before this
        _controller = new DictationController(_settings, _overlay, _platform, _speech, _target,
            cleanup: (text, _) => Task.FromResult(text), timings: timings);
        Controller.OnSessionStarted(1);
        await WaitFor(() => recorder.Started, "the device to open");
        recorder.BeginCapture();
        End(1);                                  // released before the overlay delay
        await WaitFor(() => _target.Delivered.Length == 1, "the dictation to finish");
        Assert.DoesNotContain("listening", _overlay.Calls);
    }

    // ----- Review follow-ups: target check, model, stalls, partial delivery -----

    [Fact]
    public async Task FocusMovedToAnotherWindow_TextIsKeptOnClipboard_NotPastedThere()
    {
        var a = _speech.Hold("A");
        await DictateAsync("A");
        _platform.Target = OtherWindow; // the user clicks into another app while A transcribes
        a.SetResult();
        await WaitFor(() => OverlayShowed("notice:Focus moved · 2 words in clipboard — press Ctrl+V"), "the notice");
        Assert.Empty(_target.Delivered);
        Assert.Equal("Text A. ", _target.Clipboard);
    }

    [Fact]
    public async Task FocusMovedToAnotherField_TextIsKeptOnClipboard()
    {
        var a = _speech.Hold("A");
        await DictateAsync("A");
        _platform.Target = Field2SameWindow; // same window, different control
        a.SetResult();
        await WaitFor(() => OverlayShowed("notice:Focus moved · 2 words in clipboard — press Ctrl+V"), "the notice");
        Assert.Empty(_target.Delivered);
    }

    [Fact]
    public async Task BackToBack_InDifferentWindows_EachGoesOnlyWhereItWasDictated()
    {
        var a = _speech.Hold("A");
        await DictateAsync("A");          // dictated into Field1
        _platform.Target = OtherWindow;   // user moves on and dictates B elsewhere
        await DictateAsync("B");
        a.SetResult();

        await WaitFor(() => _target.Delivered.Length == 1, "B's insertion");
        Assert.Equal(new[] { "type:Text B. " }, _target.Delivered); // typed: A is waiting on the clipboard
        Assert.Equal("Text A. ", _target.Clipboard);                 // A is not lost and not misplaced
    }

    [Fact]
    public async Task ModelThatCannotLoad_DictationKeepsUsingTheLoadedModel()
    {
        _speech.FailEnsure = true;        // e.g. offline, new model not downloaded
        Controller.UpdateSettings(new AppSettings { MinHoldMs = 0, Model = "base.en" });
        await WaitFor(() => _speech.EnsureCalls.Count == 1, "the background model load");

        await DictateAsync("A");
        await WaitFor(() => _target.Delivered.Length == 1, "the insertion");
        Assert.Equal(new[] { "paste:Text A. " }, _target.Delivered);
        Assert.Single(_speech.EnsureCalls); // the dictation itself did not try (and fail) to download
    }

    [Fact]
    public async Task NoModelLoadedYet_TheFirstDictationLoadsIt()
    {
        _speech.Ready = false;
        await DictateAsync("A");
        await WaitFor(() => _target.Delivered.Length == 1, "the insertion");
        Assert.Single(_speech.EnsureCalls);
    }

    [Fact]
    public async Task MissedResumeEvent_DoesNotStallLaterDictations()
    {
        Controller.SetSuspended(true); // the matching resume never arrives
        await DictateAsync("A");
        await WaitFor(() => _target.Delivered.Length == 1, "the insertion");
        Assert.Equal(new[] { "paste:Text A. " }, _target.Delivered);
    }

    [Fact]
    public async Task UnlockAfterAMissedResume_ReleasesTheWaitingDictation()
    {
        var a = _speech.Hold("A");
        await DictateAsync("A");
        Controller.SetSuspended(true);
        Controller.SetWorkstationLocked(true);
        a.SetResult();
        await WaitFor(() => _speech.Transcribed.Contains("A"), "transcription of A");
        Controller.SetWorkstationLocked(false); // no resume event, just the unlock

        await WaitFor(() => OverlayShowed("notice:Not inserted · 2 words in clipboard — press Ctrl+V"), "the notice");
        Assert.Equal("Text A. ", _target.Clipboard);
    }

    [Fact]
    public async Task HungMicrophone_DoesNotHoldUpLaterDictations()
    {
        var hung = _platform.Expect("A");
        hung.OpenGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Controller.OnSessionStarted(++_lastId);
        End(_lastId);
        await WaitFor(() => OverlayShowed("flash:Microphone not responding"), "the feedback");

        await DictateAsync("B");
        await WaitFor(() => _target.Delivered.Length == 1, "B's insertion");
        Assert.Equal(new[] { "paste:Text B. " }, _target.Delivered);

        hung.OpenGate.SetResult(); // the driver finally returns: the device is closed
        await WaitFor(() => hung.Stopped && hung.Disposed, "the late microphone to close");
    }

    [Fact]
    public async Task TypingStoppedPartWay_SaysSo_AndKeepsTheFullText()
    {
        _settings.InsertMode = "type";
        _target.TypingStopsPartWay = true;
        await DictateAsync("A");
        await WaitFor(() => OverlayShowed("notice:Partly typed · full text in clipboard — check, then Ctrl+V"), "the notice");
        Assert.Equal("Text A. ", _target.Clipboard);
    }

    [Fact]
    public async Task ClipboardRestoreFailure_IsReported()
    {
        _settings.KeepInClipboard = false;
        _target.UserCopies("my own text");
        _target.RejectWrite = text => text == "my own text";
        await DictateAsync("A");
        await WaitFor(() => OverlayShowed("flash:Inserted 2 words · clipboard not restored"), "the result");
    }

    [Fact]
    public async Task KeyPressedJustBeforeSending_UsersClipboardSurvivesAFailedRestore()
    {
        _settings.KeepInClipboard = false;
        _target.UserCopies("my own text");
        int sends = 0;
        _target.KeysHeld = () => Interlocked.Increment(ref sends) == 1; // first paste attempt refused
        int restores = 0;
        _target.RejectWrite = text => text == "my own text" && Interlocked.Increment(ref restores) == 1;

        await DictateAsync("A");
        await WaitFor(() => _target.Delivered.Length == 1, "the insertion");
        // The retry must not mistake the transcript for the user's own clipboard text.
        await WaitFor(() => _target.Clipboard == "my own text", "the user's clipboard back");
    }
}
