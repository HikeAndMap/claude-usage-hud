using System.Net.Http.Headers;
using System.Text.Json;

namespace ClaudeUsageHUD;

/// <summary>
/// Fetches live weekly / 5-hour plan usage from Anthropic's OAuth usage endpoint with a token the user pasted
/// in (see HudLogin). Exists because Claude desktop stopped recording samples to plan-usage-history.json
/// around 2026-10-01 (see PlanUsageReader), leaving that file hours or days stale while the desktop app's own
/// Usage page keeps updating - so it must be getting the numbers from this kind of live call. The endpoint is
/// undocumented: response shape (five_hour / seven_day, each {utilization 0-100, resets_at ISO-8601}) is from
/// community tooling, so every failure is logged (status + body prefix, never the token) rather than assumed.
/// </summary>
public static class OAuthUsageClient
{
    private const string Endpoint = "https://api.anthropic.com/api/oauth/usage";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public readonly record struct Window(int Percent, DateTimeOffset? ResetAt);
    public readonly record struct Reading(Window? FiveHour, Window? SevenDay, DateTimeOffset FetchedAt);

    /// <summary>Returns a reading, or a short human-readable error (shown in the HUD tooltip) - never both.</summary>
    public static async Task<(Reading? Reading, string? Error)> FetchAsync(string token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            request.Headers.UserAgent.ParseAdd("claude-code/2.1.286");

            using HttpResponseMessage response = await Http.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                PlanUsageReader.LogDiagnostic($"oauth usage: HTTP {(int)response.StatusCode} {response.ReasonPhrase}; body={Truncate(body, 300)}");
                return (null, response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                    ? $"token rejected (HTTP {(int)response.StatusCode}) - sign in again"
                    : $"HTTP {(int)response.StatusCode}");
            }

            using JsonDocument doc = JsonDocument.Parse(body);
            Window? five = ParseWindow(doc.RootElement, "five_hour");
            Window? seven = ParseWindow(doc.RootElement, "seven_day");
            if (five == null && seven == null)
            {
                string keys = string.Join(", ", doc.RootElement.EnumerateObject().Select(p => p.Name));
                PlanUsageReader.LogDiagnostic($"oauth usage: 200 but no five_hour/seven_day; root keys: {keys}");
                return (null, "unexpected response shape");
            }
            return (new Reading(five, seven, DateTimeOffset.UtcNow), null);
        }
        catch (Exception ex)
        {
            PlanUsageReader.LogDiagnostic($"oauth usage: {ex.GetType().Name}: {ex.Message}");
            return (null, ex.GetType().Name);
        }
    }

    private static Window? ParseWindow(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement el) || el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty("utilization", out JsonElement util) || util.ValueKind != JsonValueKind.Number) return null;

        DateTimeOffset? resetAt = null;
        if (el.TryGetProperty("resets_at", out JsonElement r) && r.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(r.GetString(), out DateTimeOffset parsed))
        {
            resetAt = parsed;
        }
        return new Window((int)Math.Round(util.GetDouble()), resetAt);
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "...";
}
