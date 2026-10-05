using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using TheDojo.Core.Ingest;
using TheDojo.Core.Model;

namespace TheDojo.Core.Limits;

/// <summary>
/// GitHub Copilot's AI-credit (or premium-request) allowance. It is asked from the Copilot CLI itself
/// (<see cref="CopilotCliQuota"/>), so it belongs to the account the CLI is signed in with. Without the CLI it
/// falls back to the endpoint the Copilot editors use, <c>GET api.github.com/copilot_internal/user</c>, with a
/// GitHub token from the environment (<c>COPILOT_GITHUB_TOKEN</c>, <c>GH_TOKEN</c>, <c>GITHUB_TOKEN</c>) or the
/// GitHub CLI (<c>gh auth token</c>); that token is only ever sent to GitHub.
/// </summary>
public sealed class CopilotPlanLimitsSource(
    HttpClient http,
    Func<CancellationToken, Task<string?>>? ghToken = null,
    Func<string, string?>? environment = null,
    Func<CancellationToken, Task<string?>>? cliQuota = null,
    TimeProvider? time = null) : IPlanLimitsSource
{
    private static readonly Uri Endpoint = new("https://api.github.com/copilot_internal/user");
    private static readonly string[] TokenVariables = ["COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN"];

    // Copilot's allowance resets monthly; the exact length only feeds the pace hint.
    public static readonly TimeSpan MonthLength = TimeSpan.FromDays(30);

    private readonly Func<CancellationToken, Task<string?>> _ghToken = ghToken ?? GhAuthTokenAsync;
    private readonly Func<CancellationToken, Task<string?>> _cliQuota = cliQuota ?? CopilotCliQuota.ReadAsync;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Func<string, string?> _environment = environment ?? Environment.GetEnvironmentVariable;

    public Provider Provider => Provider.Copilot;

    public async Task<ProviderLimits> FetchAsync(CancellationToken cancellationToken)
    {
        if (await _cliQuota(cancellationToken).ConfigureAwait(false) is { } fromCli
            && ParseCli(fromCli, _time.GetUtcNow()) is { } limits)
        {
            return limits;
        }

        var token = await ResolveTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return ProviderLimits.Failed(Provider, "Sign in to the Copilot CLI (or gh auth login) to see your Copilot limits.", local: true);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.TryAddWithoutValidation("Authorization", "token " + token);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Editor-Version", "vscode/1.100.0");
        request.Headers.UserAgent.ParseAdd(AppIdentity.FileName + "/1.0");

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return ProviderLimits.Failed(Provider, "This GitHub account has no Copilot subscription to read limits from.");
        }

        if (!response.IsSuccessStatusCode)
        {
            return ProviderLimits.Failed(Provider, "GitHub didn't return Copilot limits right now.");
        }

        return Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    internal static ProviderLimits Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var plan = PlanName(Json.Str(root, "copilot_plan"));
        var resets = Json.Time(root, "quota_reset_date_utc") ?? Json.Time(root, "quota_reset_date");

        var windows = new List<LimitWindow>();
        if (Json.Obj(root, "quota_snapshots") is { } snapshots)
        {
            // Plans without a premium allowance (Free) report it with no quota; their credits sit in "chat".
            AddWindow(windows, snapshots, "premium_interactions", "AI credits", "Premium requests", resets);
            AddWindow(windows, snapshots, "chat", "Chat credits", "Chat messages", resets);
        }

        return new ProviderLimits(Provider.Copilot, plan, windows);
    }

    /// <summary>The CLI's <c>account.getQuota</c> result, or null when it holds no quota snapshots.</summary>
    internal static ProviderLimits? ParseCli(string json, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(json);
        if (Json.Obj(doc.RootElement, "quotaSnapshots") is not { } snapshots)
        {
            return null;
        }

        // The CLI reports a reset date of "now" for credit billing; allowances renew on the 1st (UTC).
        var resets = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
        var windows = new List<LimitWindow>();
        AddCliWindow(windows, snapshots, "premium_interactions", "AI credits", "Premium requests", resets);
        AddCliWindow(windows, snapshots, "chat", "Chat credits", "Chat messages", resets);
        return new ProviderLimits(Provider.Copilot, null, windows);
    }

    private static void AddCliWindow(List<LimitWindow> windows, JsonElement snapshots, string name, string creditLabel, string requestLabel, DateTimeOffset resets)
    {
        if (Json.Obj(snapshots, name) is not { } snapshot
            || Json.Bool(snapshot, "isUnlimitedEntitlement")
            || Json.Prop(snapshot, "hasQuota") is { ValueKind: JsonValueKind.False }
            || Json.Num(snapshot, "entitlementRequests") is not > 0
            || Json.Num(snapshot, "remainingPercentage") is not { } left)
        {
            return;
        }

        var entitlement = Json.Num(snapshot, "entitlementRequests")!.Value;
        var credits = Json.Bool(snapshot, "tokenBasedBilling");
        var remaining = entitlement * left / 100;
        windows.Add(new LimitWindow(credits ? creditLabel : requestLabel, 100 - left, resets, MonthLength,
            $"{Math.Max(0, remaining):0.#} of {entitlement:0.#} {(credits ? "credits" : "requests")} left"));
    }

    private static void AddWindow(List<LimitWindow> windows, JsonElement snapshots, string name, string creditLabel, string requestLabel, DateTimeOffset? resets)
    {
        if (Json.Obj(snapshots, name) is not { } snapshot
            || Json.Bool(snapshot, "unlimited")
            || Json.Prop(snapshot, "has_quota") is { ValueKind: JsonValueKind.False })
        {
            return;
        }

        var entitlement = Json.Num(snapshot, "entitlement");
        if (entitlement is 0)
        {
            return;
        }

        var remaining = Json.Num(snapshot, "remaining");
        var percentLeft = Json.Num(snapshot, "percent_remaining")
            ?? (entitlement is > 0 && remaining is not null ? remaining / entitlement * 100 : null);
        if (percentLeft is not { } left)
        {
            return;
        }

        var credits = Json.Bool(snapshot, "token_based_billing");
        var detail = entitlement is > 0 && remaining is not null
            ? $"{Math.Max(0, remaining.Value):0.#} of {entitlement.Value:0.#} {(credits ? "credits" : "requests")} left"
            : null;
        windows.Add(new LimitWindow(credits ? creditLabel : requestLabel, 100 - left, resets, MonthLength, detail));
    }

    private static string? PlanName(string? plan) => plan?.ToLowerInvariant() switch
    {
        null or "" => null,
        "individual" => "Pro",
        "individual_pro" => "Pro+",
        "business" => "Business",
        "enterprise" => "Enterprise",
        "free" or "copilot_free" => "Free",
        var other => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(other.Replace('_', ' ')),
    };

    private async Task<string?> ResolveTokenAsync(CancellationToken cancellationToken)
    {
        foreach (var name in TokenVariables)
        {
            if (_environment(name) is { Length: > 0 } fromEnv)
            {
                return fromEnv.Trim();
            }
        }

        return await _ghToken(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> GhAuthTokenAsync(CancellationToken cancellationToken)
    {
        if (CommandResolver.Resolve("gh") is not { } gh)
        {
            return null;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(gh, "auth token")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var token = output.Trim();
            return process.ExitCode == 0 && token.Length > 0 ? token : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }
}
