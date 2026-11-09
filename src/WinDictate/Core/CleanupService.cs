using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace WinDictate.Core;

/// <summary>
/// Optional transcript cleanup through any OpenAI-compatible chat completions
/// endpoint (public API or an internal enterprise gateway). Disabled by default.
/// On any failure the raw local transcript is used, so dictation keeps working
/// fully offline.
/// </summary>
public static class CleanupService
{
    private static readonly HttpClient Http = new();

    public static async Task<string> CleanAsync(string transcript, CleanupSettings cleanup)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(3, cleanup.TimeoutSeconds)));
            using var request = BuildRequest(transcript, cleanup);
            using var response = await Http.SendAsync(request, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                Logger.Warn($"Cleanup API returned {(int)response.StatusCode}, using raw transcript");
                return transcript;
            }

            using var doc = JsonDocument.Parse(body);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            return string.IsNullOrWhiteSpace(content) ? transcript : content.Trim();
        }
        catch (Exception ex)
        {
            Logger.Warn("Cleanup failed, using raw transcript: " + ex.Message);
            return transcript;
        }
    }

    internal static HttpRequestMessage BuildRequest(string transcript, CleanupSettings cleanup)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, cleanup.BaseUrl.TrimEnd('/') + "/chat/completions");
        // Keyless gateways get no Authorization header at all, rather than an
        // empty "Bearer " that some gateways reject or log as a failed login.
        if (!string.IsNullOrWhiteSpace(cleanup.ApiKey))
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + cleanup.ApiKey);

        var payload = JsonSerializer.Serialize(new
        {
            model = cleanup.Model,
            temperature = 0.2,
            messages = new object[]
            {
                new { role = "system", content = cleanup.SystemPrompt },
                new { role = "user", content = transcript }
            }
        });
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        return request;
    }
}
