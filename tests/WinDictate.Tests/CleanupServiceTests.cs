using WinDictate.Core;
using Xunit;

namespace WinDictate.Tests;

public class CleanupServiceTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankKey_SendsNoAuthorizationHeader(string key)
    {
        using var request = CleanupService.BuildRequest("hello", new CleanupSettings { ApiKey = key });
        Assert.False(request.Headers.Contains("Authorization"));
    }

    [Fact]
    public void Key_IsSentAsBearerToken()
    {
        using var request = CleanupService.BuildRequest("hello", new CleanupSettings { ApiKey = "test-key" });
        Assert.Equal("Bearer test-key", string.Join(",", request.Headers.GetValues("Authorization")));
    }

    [Fact]
    public void Request_GoesToChatCompletions()
    {
        using var request = CleanupService.BuildRequest("hello",
            new CleanupSettings { BaseUrl = "https://gateway.example.internal/v1/" });
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://gateway.example.internal/v1/chat/completions", request.RequestUri!.ToString());
    }
}
