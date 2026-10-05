using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TheDojo.Core.Ingest;
using TheDojo.Core.Model;

namespace TheDojo.Core.Limits;

/// <summary>
/// Claude subscription limits, from the endpoint Claude Code's own <c>/usage</c> reads:
/// <c>GET api.anthropic.com/api/oauth/usage</c> with the OAuth token Claude Code keeps in
/// <c>~/.claude/.credentials.json</c> (or under <c>CLAUDE_CONFIG_DIR</c>). The token is only ever sent to
/// Anthropic. When it has expired, running Claude Code once refreshes it.
/// </summary>
public sealed class ClaudePlanLimitsSource(HttpClient http, string? configDir = null) : IPlanLimitsSource
{
    private static readonly Uri Endpoint = new("https://api.anthropic.com/api/oauth/usage");

    private static readonly (string Key, string Label, TimeSpan Length)[] Windows =
    [
        ("five_hour", "Session", TimeSpan.FromHours(5)),
        ("seven_day", "Weekly", TimeSpan.FromDays(7)),
        ("seven_day_opus", "Weekly Opus", TimeSpan.FromDays(7)),
        ("seven_day_sonnet", "Weekly Sonnet", TimeSpan.FromDays(7)),
    ];

    public Provider Provider => Provider.Claude;

    /// <summary>The length of a Claude window by its label, so readings from <c>/usage</c> can be paced too.</summary>
    public static TimeSpan? WindowLength(string label) =>
        Windows.FirstOrDefault(w => w.Label == label) is { Label: not null } w ? w.Length : null;

    public static string CredentialsPath(string? configDir = null) =>
        Path.Combine(configDir ?? AppIdentity.ClaudeHome, ".credentials.json");

    public async Task<ProviderLimits> FetchAsync(CancellationToken cancellationToken)
    {
        var (token, plan) = ReadCredentials(CredentialsPath(configDir));
        if (token is null)
        {
            return ProviderLimits.Failed(Provider, "Sign in to Claude Code to see your limits.", local: true);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        request.Headers.UserAgent.ParseAdd(AppIdentity.FileName + "/1.0");

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return ProviderLimits.Failed(Provider, "Claude Code's sign-in has expired. Run Claude Code once to refresh it.");
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return ProviderLimits.Failed(Provider, "Anthropic is rate limiting limit checks. Try again in a few minutes.");
        }

        if (!response.IsSuccessStatusCode)
        {
            return ProviderLimits.Failed(Provider, "Claude didn't return limits right now.");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new ProviderLimits(Provider, plan, ParseWindows(json));
    }

    internal static IReadOnlyList<LimitWindow> ParseWindows(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var windows = new List<LimitWindow>();
        foreach (var (key, label, length) in Windows)
        {
            if (Json.Obj(doc.RootElement, key) is not { } w || Json.Num(w, "utilization") is not { } used)
            {
                continue;
            }

            windows.Add(new LimitWindow(label, used, Json.Time(w, "resets_at"), length));
        }

        return windows;
    }

    internal static (string? Token, string? Plan) ReadCredentials(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return (null, null);
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (Json.Obj(doc.RootElement, "claudeAiOauth") is not { } oauth)
            {
                return (null, null);
            }

            var token = Json.Str(oauth, "accessToken");
            return (string.IsNullOrWhiteSpace(token) ? null : token, PlanName(Json.Str(oauth, "subscriptionType")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, null);
        }
    }

    private static string? PlanName(string? subscription) => subscription?.ToLowerInvariant() switch
    {
        null or "" => null,
        "max" => "Max",
        "pro" => "Pro",
        "team" => "Team",
        "enterprise" => "Enterprise",
        var other => char.ToUpperInvariant(other[0]) + other[1..],
    };
}
