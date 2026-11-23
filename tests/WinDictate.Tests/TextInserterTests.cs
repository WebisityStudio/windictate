using WinDictate.Core;
using Xunit;

namespace WinDictate.Tests;

/// <summary>
/// The text inserter on its own, with a clock the test controls: how long a
/// dictation waiting on the clipboard is protected, and what happens to the
/// user's own clipboard text when a paste has to be retried.
/// </summary>
public sealed class TextInserterTests
{
    private static readonly InsertionTarget Here = new(new IntPtr(42), new IntPtr(7));
    private static readonly InsertOptions Paste = new(TypeMode: false, ClipboardRestoreDelayMs: 0);
    private static readonly InsertOptions PasteAndRestore = Paste;
    private static readonly InsertOptions Type = new(TypeMode: true, ClipboardRestoreDelayMs: 0);

    private readonly FakeTextTarget _target = new();
    private readonly TextInserter _inserter;
    private long _now = 1_000_000;
    private Action? _whilePasting; // runs while the inserter waits for the app to take the pasted text

    public TextInserterTests()
    {
        _inserter = new TextInserter(_target, delay: _ => { _whilePasting?.Invoke(); return Task.CompletedTask; },
            protectMs: 30_000, clearMs: 120_000, clock: () => _now);
    }

    /// <summary>A dictation cannot be delivered and is left on the clipboard; the user is told at once.</summary>
    private async Task UndeliveredAsync(string text)
    {
        var result = await _inserter.KeepForRecoveryAsync(text, startedAt: _now);
        Assert.Equal(InsertOutcome.CopiedOnly, result.Outcome);
        _inserter.RecoveryAnnounced();
    }

    [Fact]
    public async Task InTheHalfMinuteAfterTheNotice_LaterDictationsAreTyped_AndTheWaitingTextStays()
    {
        await UndeliveredAsync("waiting ");
        _now += 10_000;
        var result = await _inserter.InsertAsync("next ", Paste, Here, startedAt: _now);

        Assert.Equal(InsertOutcome.Inserted, result.Outcome);
        Assert.True(result.Typed);
        Assert.True(result.EarlierTextKept);
        Assert.Equal(new[] { "type:next " }, _target.Delivered);
        Assert.Equal("waiting ", _target.Clipboard); // exactly what the notice promised
    }

    [Fact]
    public async Task AfterHalfAMinute_PastingReturnsToNormal()
    {
        await UndeliveredAsync("waiting ");
        _now += 31_000;
        var result = await _inserter.InsertAsync("next ", Paste, Here, startedAt: _now);

        Assert.Equal(InsertOutcome.Inserted, result.Outcome);
        Assert.False(result.Typed);
        Assert.False(result.OnClipboard);
        Assert.Equal(new[] { "paste:next " }, _target.Delivered);
        Assert.Equal("waiting ", _target.Clipboard); // the undelivered text is put back until it is cleared
    }

    [Fact]
    public async Task ProtectionDoesNotComeBack_OnceItHasEnded()
    {
        await UndeliveredAsync("waiting ");
        _now += 31_000;
        await _inserter.InsertAsync("one ", Paste, Here, startedAt: _now);
        _inserter.RecoveryAnnounced(); // a stray announcement must not revive it
        var result = await _inserter.InsertAsync("two ", Paste, Here, startedAt: _now);

        Assert.False(result.Typed);
        Assert.Equal(new[] { "paste:one ", "paste:two " }, _target.Delivered);
        Assert.Equal("waiting ", _target.Clipboard);
    }

    [Fact]
    public async Task TheTimeToPaste_StartsWhenTheUserIsTold_NotWhenTheDictationFailed()
    {
        // The notice waited for a long recording to end before it could be shown.
        await _inserter.KeepForRecoveryAsync("waiting ", startedAt: _now);
        _now += 60_000;
        _inserter.RecoveryAnnounced();
        _now += 10_000;
        var result = await _inserter.InsertAsync("next ", Paste, Here, startedAt: _now);

        Assert.True(result.Typed);
        Assert.Equal("waiting ", _target.Clipboard);
    }

    [Fact]
    public async Task ADictationSpokenBeforeTheNotice_IsTyped_HoweverLongItWaitedInTheQueue()
    {
        long spoken = _now - 5_000;
        await UndeliveredAsync("waiting ");
        _now += 120_000; // a slow transcription
        var result = await _inserter.InsertAsync("queued ", Paste, Here, startedAt: spoken);

        Assert.True(result.Typed);
        Assert.Equal("waiting ", _target.Clipboard);
    }

    [Fact]
    public async Task CopyingSomethingElse_EndsTheProtectionAtOnce()
    {
        await UndeliveredAsync("waiting ");
        _target.UserCopies("the user's own");
        _now += 1_000;
        var result = await _inserter.InsertAsync("next ", Paste, Here, startedAt: _now);

        Assert.False(result.Typed);
        Assert.Equal("the user's own", _target.Clipboard); // put back after the paste
    }

    [Fact]
    public async Task ASecondUndeliveredDictation_IsAddedWhileProtected_ButReplacesStaleTextLater()
    {
        await UndeliveredAsync("one ");
        _now += 5_000;
        var soon = await _inserter.KeepForRecoveryAsync("two ", startedAt: _now);
        Assert.Equal(2, soon.RecoveryCount);
        Assert.Equal("one two ", _target.Clipboard);
        _inserter.RecoveryAnnounced();

        _now += 60_000; // long after: the user has had their time with "one two"
        var later = await _inserter.KeepForRecoveryAsync("three ", startedAt: _now);
        Assert.Equal(1, later.RecoveryCount);
        Assert.Equal("three ", _target.Clipboard);
    }

    [Fact]
    public async Task TypeMode_WhileTextWaits_LeavesItAlone()
    {
        await UndeliveredAsync("waiting ");
        var result = await _inserter.InsertAsync("next ", Type, Here, startedAt: _now + 1);

        Assert.True(result.EarlierTextKept);
        Assert.False(result.OnClipboard);
        Assert.Equal("waiting ", _target.Clipboard);
    }

    [Fact]
    public async Task HalfSentPaste_IsKeptOnTheClipboard_AndReportedAsPossiblyPasted()
    {
        _target.PasteStopsPartWay = true;
        var result = await _inserter.InsertAsync("text ", PasteAndRestore, Here, startedAt: _now);

        Assert.Equal(InsertOutcome.CopiedOnly, result.Outcome);
        Assert.Equal(RecoveryReason.MaybePasted, result.Reason);
        Assert.Equal("text ", _target.Clipboard); // stays, so the user can check and paste it
    }

    [Fact]
    public async Task RetryAfterAKeyWasHeld_PutsBackWhatTheUserCopiedInBetween()
    {
        _target.UserCopies("old copy");
        bool held = true;
        _target.KeysHeld = () => held;

        var first = await _inserter.InsertAsync("text ", PasteAndRestore, Here, startedAt: _now);
        Assert.Equal(InsertOutcome.KeysHeld, first.Outcome);
        Assert.Equal("old copy", _target.Clipboard);

        _target.UserCopies("new copy"); // while the app waited for the key to be released
        held = false;
        var retry = await _inserter.InsertAsync("text ", PasteAndRestore, Here, startedAt: _now);

        Assert.Equal(InsertOutcome.Inserted, retry.Outcome);
        Assert.Equal(new[] { "paste:text " }, _target.Delivered);
        Assert.Equal("new copy", _target.Clipboard);
    }

    [Fact]
    public async Task RetryAfterAKeyWasHeld_StillKnowsTheUsersText_WhenPuttingItBackFailed()
    {
        _target.UserCopies("old copy");
        bool held = true;
        _target.KeysHeld = () => held;
        bool refuse = true;
        _target.RejectWrite = text => text == "old copy" && refuse;

        await _inserter.InsertAsync("text ", PasteAndRestore, Here, startedAt: _now);
        Assert.Equal("text ", _target.Clipboard); // the restore failed: the transcript is still there

        held = false;
        refuse = false;
        await _inserter.InsertAsync("text ", PasteAndRestore, Here, startedAt: _now);
        Assert.Equal("old copy", _target.Clipboard); // not mistaken for the user's own text
    }

    [Fact]
    public async Task Paste_PutsTheUsersTextBack()
    {
        _target.UserCopies("mine");
        var result = await _inserter.InsertAsync("text ", Paste, Here, startedAt: _now);

        Assert.Equal(InsertOutcome.Inserted, result.Outcome);
        Assert.False(result.OnClipboard);
        Assert.Equal(new[] { "paste:text " }, _target.Delivered);
        Assert.Equal("mine", _target.Clipboard);
    }

    [Fact]
    public async Task Paste_WithAPictureOnTheClipboard_TypesInstead_AndNeverTouchesTheClipboard()
    {
        _target.UserCopiesPicture();
        long before = _target.ClipboardSequence;
        var result = await _inserter.InsertAsync("text ", Paste, Here, startedAt: _now);

        Assert.True(result.Typed);
        Assert.True(result.TypedToSpareClipboard);
        Assert.Equal(new[] { "type:text " }, _target.Delivered);
        Assert.Equal(before, _target.ClipboardSequence);
        Assert.True(_target.ClipboardHasOtherContent);
    }

    [Fact]
    public async Task Paste_WhenTheClipboardCannotBeRead_TypesInstead()
    {
        _target.ClipboardFails = true;
        var result = await _inserter.InsertAsync("text ", Paste, Here, startedAt: _now);
        Assert.True(result.Typed);
        Assert.Equal(new[] { "type:text " }, _target.Delivered);
    }

    [Fact]
    public async Task Restore_LeavesAlone_WhatSomeoneElseCopiedDuringThePaste()
    {
        _target.UserCopies("mine");
        _whilePasting = () => _target.UserCopies("copied meanwhile");
        await _inserter.InsertAsync("text ", Paste, Here, startedAt: _now);
        Assert.Equal("copied meanwhile", _target.Clipboard);
    }

    [Fact]
    public async Task UndeliveredText_IsRemovedWhenItsTimeIsUp_AndTheUsersTextComesBack()
    {
        _target.UserCopies("mine");
        await UndeliveredAsync("waiting ");
        Assert.Equal("waiting ", _target.Clipboard);

        _now += 60_000;
        Assert.False(await _inserter.ClearExpiredRecoveryAsync()); // not yet
        Assert.Equal("waiting ", _target.Clipboard);

        _now += 61_000;
        Assert.True(await _inserter.ClearExpiredRecoveryAsync());
        Assert.Equal("mine", _target.Clipboard);
        Assert.False(await _inserter.ClearExpiredRecoveryAsync()); // only once
    }

    [Fact]
    public async Task UndeliveredText_IsRemoved_EvenWhenWhatWasThereCannotBePutBack()
    {
        _target.UserCopiesPicture();
        await UndeliveredAsync("waiting ");
        _now += 121_000;
        Assert.True(await _inserter.ClearExpiredRecoveryAsync());
        Assert.Null(_target.Clipboard); // cleared: the dictation does not linger
    }

    [Fact]
    public async Task UndeliveredText_IsLeftToTheUser_OnceTheyHaveCopiedSomethingElse()
    {
        await UndeliveredAsync("waiting ");
        _target.UserCopies("theirs now");
        _now += 121_000;
        Assert.False(await _inserter.ClearExpiredRecoveryAsync());
        Assert.Equal("theirs now", _target.Clipboard);
    }

    [Fact]
    public async Task UndeliveredText_SurvivesLaterPastes_UntilItIsCleared()
    {
        _target.UserCopies("mine");
        await UndeliveredAsync("waiting ");
        _now += 31_000; // protection over: later dictations are pasted again
        await _inserter.InsertAsync("later ", Paste, Here, startedAt: _now);
        Assert.Equal(new[] { "paste:later " }, _target.Delivered);
        Assert.Equal("waiting ", _target.Clipboard);

        _now += 121_000;
        Assert.True(await _inserter.ClearExpiredRecoveryAsync());
        Assert.Equal("mine", _target.Clipboard); // still recognised as ours, and the user's text returns
    }

    [Fact]
    public async Task OlderUndeliveredText_NeverComesBack_WhenANewerOneReplacesIt()
    {
        _target.UserCopies("mine");
        await UndeliveredAsync("older ");
        _now += 31_000; // the older text is no longer protected, but still on the clipboard

        var other = new InsertionTarget(new IntPtr(43), new IntPtr(9));
        _target.Current = () => other; // focus moves away before the next paste
        var result = await _inserter.InsertAsync("newer ", Paste, Here, startedAt: _now);
        Assert.Equal(RecoveryReason.TargetChanged, result.Reason);
        Assert.Equal("newer ", _target.Clipboard);
        _inserter.RecoveryAnnounced();

        _now += 121_000;
        Assert.True(await _inserter.ClearExpiredRecoveryAsync());
        Assert.Equal("mine", _target.Clipboard); // the user's own text, not the older dictation
    }

    [Fact]
    public async Task AFailedRestore_IsTriedAgainLater_SoTheTranscriptDoesNotLinger()
    {
        _target.UserCopies("mine");
        bool refuse = true;
        _target.RejectWrite = text => text == "mine" && refuse;
        var result = await _inserter.InsertAsync("text ", Paste, Here, startedAt: _now);
        Assert.True(result.ClipboardNotRestored);
        Assert.Equal("text ", _target.Clipboard);

        refuse = false;
        _now += 121_000;
        Assert.True(await _inserter.ClearExpiredRecoveryAsync());
        Assert.Equal("mine", _target.Clipboard);
    }

    [Fact]
    public async Task ClearingNow_RemovesUndeliveredTextRegardlessOfTime()
    {
        _target.UserCopies("mine");
        await UndeliveredAsync("waiting ");
        Assert.True(await _inserter.ClearRecoveryNowAsync());
        Assert.Equal("mine", _target.Clipboard);
    }

    [Fact]
    public async Task Offer_PutsTextOnTheClipboard_AndItIsRemovedLater()
    {
        _target.UserCopies("mine");
        Assert.True(await _inserter.OfferAsync("again "));
        Assert.Equal("again ", _target.Clipboard);
        _now += 121_000;
        Assert.True(await _inserter.ClearExpiredRecoveryAsync());
        Assert.Equal("mine", _target.Clipboard);
    }

    [Theory]
    [InlineData(0u, "None", new ushort[0])]
    [InlineData(1u, "None", new[] { NativeMethods.VK_CONTROL })]
    [InlineData(2u, "Partial", new[] { NativeMethods.VK_V, NativeMethods.VK_CONTROL })]
    [InlineData(3u, "Partial", new[] { NativeMethods.VK_CONTROL })]
    [InlineData(4u, "All", new ushort[0])]
    public void HalfAcceptedCtrlV_ReleasesEveryKeyStillDown_AndSaysIfThePasteMayHaveHappened(
        uint accepted, string expected, ushort[] keysToRelease)
    {
        var (result, release) = NativeMethods.AfterCtrlV(accepted);
        Assert.Equal(expected, result.ToString());
        Assert.Equal(keysToRelease, release);
    }
}
