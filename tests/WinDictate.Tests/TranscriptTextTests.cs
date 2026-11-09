using WinDictate.Core;
using Xunit;

namespace WinDictate.Tests;

public class TranscriptTextTests
{
    [Theory]
    // Dictated content in brackets must survive (the reported regression).
    [InlineData("Transfer 5,000 pounds [GBP] to the client's ISA (not the current account).",
                "Transfer 5,000 pounds [GBP] to the client's ISA (not the current account).")]
    [InlineData("Rates [see appendix B] apply.", "Rates [see appendix B] apply.")]
    [InlineData("Options (a), (b) and (c).", "Options (a), (b) and (c).")]
    [InlineData("Call foo() now.", "Call foo() now.")]
    [InlineData("This is *important* to note.", "This is *important* to note.")]
    [InlineData("Multiply 2*3*4 please.", "Multiply 2*3*4 please.")]
    [InlineData("Fees (music lessons included) apply.", "Fees (music lessons included) apply.")]
    public void KeepsDictatedBrackets(string raw, string expected) =>
        Assert.Equal(expected, TranscriptText.StripAnnotations(raw));

    [Theory]
    // Whisper non-speech annotations are removed, with no stray spaces left.
    [InlineData("[BLANK_AUDIO]", "")]
    [InlineData(" [MUSIC] ", "")]
    [InlineData("[ Silence ]", "")]
    [InlineData("[NO SPEECH]", "")]
    [InlineData("(speaking in foreign language)", "")]
    [InlineData("Hello [BLANK_AUDIO] world.", "Hello world.")]
    [InlineData("Hello (coughing) world.", "Hello world.")]
    [InlineData("I agree (laughs).", "I agree.")]
    [InlineData("Can you hear me (static)?", "Can you hear me?")]
    [InlineData("*sighs* Fine, let's go.", "Fine, let's go.")]
    [InlineData("Well, *clears throat*, as I said.", "Well, as I said.")]
    [InlineData("Hello. (laughs). Next point.", "Hello. Next point.")]
    [InlineData("(upbeat music) Welcome back.", "Welcome back.")]
    [InlineData("[Music playing] ♪ la la ♪", "la la")]
    [InlineData("Yes [ Silence ] [BLANK_AUDIO] no.", "Yes no.")]
    [InlineData("Sign here (inaudible), then date it.", "Sign here, then date it.")]
    [InlineData("  lots   of    spaces  ", "lots of spaces")]
    public void RemovesNonSpeechAnnotations(string raw, string expected) =>
        Assert.Equal(expected, TranscriptText.StripAnnotations(raw));

    [Fact]
    public void EmptyInput_IsEmpty()
    {
        Assert.Equal("", TranscriptText.StripAnnotations(""));
        Assert.Equal("", TranscriptText.StripAnnotations("   "));
    }
}
