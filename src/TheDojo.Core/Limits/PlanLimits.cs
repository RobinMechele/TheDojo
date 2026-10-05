using TheDojo.Core.Model;

namespace TheDojo.Core.Limits;

/// <summary>Whether usage is running ahead of, or behind, an even spread over the limit window.</summary>
public enum LimitPace
{
    Unknown,
    OnTrack,
    Ahead,
}

/// <summary>
/// One rate-limit window of a subscription: Claude's 5-hour session and weekly caps, or Copilot's monthly
/// premium-request / AI-credit allowance. <paramref name="PercentUsed"/> is 0-100.
/// </summary>
public sealed record LimitWindow(
    string Label,
    double PercentUsed,
    DateTimeOffset? ResetsAt,
    TimeSpan? Length = null,
    string? Detail = null)
{
    public double PercentLeft => Math.Clamp(100 - PercentUsed, 0, 100);

    /// <summary>Share of the window already elapsed (0-1), or null when its reset or length is unknown.</summary>
    public double? ElapsedAt(DateTimeOffset now)
    {
        if (ResetsAt is not { } resets || Length is not { } length || length <= TimeSpan.Zero)
        {
            return null;
        }

        return Math.Clamp(1 - (resets - now) / length, 0, 1);
    }

    /// <summary>Usage above the elapsed share of the window means the limit would run out before it resets.</summary>
    public LimitPace PaceAt(DateTimeOffset now) =>
        ElapsedAt(now) is not { } elapsed ? LimitPace.Unknown
        : PercentUsed / 100 > elapsed ? LimitPace.Ahead
        : LimitPace.OnTrack;

    /// <summary>
    /// When the limit runs out at the current average rate, or null when it won't before the reset (or can't tell).
    /// </summary>
    public DateTimeOffset? ProjectedExhaustion(DateTimeOffset now)
    {
        if (ElapsedAt(now) is not { } elapsed || elapsed <= 0 || PercentUsed <= 0 || ResetsAt is not { } resets || Length is not { } length)
        {
            return null;
        }

        var start = resets - length;
        var perSecond = PercentUsed / (now - start).TotalSeconds;
        var at = now + TimeSpan.FromSeconds(PercentLeft / perSecond);
        return at < resets ? at : null;
    }
}

/// <summary>What one provider reports for the signed-in account (<paramref name="Error"/> is set instead of windows when it can't).</summary>
public sealed record ProviderLimits(
    Provider Provider,
    string? Plan,
    IReadOnlyList<LimitWindow> Windows,
    string? Error = null,
    string? Note = null)
{
    /// <summary>True when nothing was asked of the remote service (e.g. not signed in), so it needn't be rate-limited.</summary>
    public bool Local { get; init; }

    public static ProviderLimits Failed(Provider provider, string error, bool local = false) =>
        new(provider, null, [], error) { Local = local };
}

/// <summary>Reads the live rate-limit state of one provider's account.</summary>
public interface IPlanLimitsSource
{
    Provider Provider { get; }

    /// <summary>Never throws for expected failures (signed out, offline); reports them in <see cref="ProviderLimits.Error"/>.</summary>
    Task<ProviderLimits> FetchAsync(CancellationToken cancellationToken);
}

/// <summary>Fetches every provider's limits side by side, and records each good reading in the limit history.</summary>
/// <remarks>
/// The providers rate-limit these endpoints (Anthropic answers 429 when they are polled), so a reading is reused
/// for <see cref="AutoInterval"/> (<see cref="ManualInterval"/> after an explicit refresh), failures included, and
/// when a re-check fails the last good reading stays on screen with a note.
/// </remarks>
public sealed class PlanLimitsService(IEnumerable<IPlanLimitsSource> sources, TimeProvider? time = null, LimitHistoryStore? history = null)
{
    public static readonly TimeSpan AutoInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ManualInterval = TimeSpan.FromMinutes(1);

    private sealed class State
    {
        public ProviderLimits? Shown;
        public ProviderLimits? LastGood;
        public DateTimeOffset GoodAt;
        public DateTimeOffset CheckedAt;
    }

    private readonly IReadOnlyList<IPlanLimitsSource> _sources = sources.ToList();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<Provider, State> _state = [];
    private readonly object _lock = new();

    public static PlanLimitsService CreateDefault(LimitHistoryStore? history = null)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        return new PlanLimitsService(
        [
            new ClaudePlanLimitsSource(http),
            new CopilotPlanLimitsSource(http),
        ], history: history);
    }

    /// <param name="manual">True when the user pressed refresh (a shorter wait than the automatic check).</param>
    public async Task<IReadOnlyList<ProviderLimits>> FetchAllAsync(CancellationToken cancellationToken = default, bool manual = false)
    {
        var tasks = _sources.Select(async source =>
        {
            var now = _time.GetUtcNow();
            State state;
            lock (_lock)
            {
                state = _state.TryGetValue(source.Provider, out var s) ? s : _state[source.Provider] = new State();
                if (state.Shown is { } reuse && now - state.CheckedAt < (manual ? ManualInterval : AutoInterval))
                {
                    return reuse;
                }
            }

            var fresh = await FetchSafelyAsync(source, cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                if (fresh.Error is null)
                {
                    state.LastGood = fresh;
                    state.GoodAt = now;
                    history?.Append(fresh.Windows.Select(w => new LimitReading(fresh.Provider, now, w.Label, w.PercentUsed, w.ResetsAt)));
                }
                else if (state.LastGood is { } good)
                {
                    var minutes = (int)Math.Max(1, (now - state.GoodAt).TotalMinutes);
                    fresh = good with { Note = $"{fresh.Error} Showing the reading from {minutes} min ago." };
                }

                state.Shown = fresh;
                // Only a real request to the service counts against its limit; "not signed in" can be retried at once.
                state.CheckedAt = fresh.Local ? DateTimeOffset.MinValue : now;
                return fresh;
            }
        });

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task<ProviderLimits> FetchSafelyAsync(IPlanLimitsSource source, CancellationToken cancellationToken)
    {
        try
        {
            return await source.FetchAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderLimits.Failed(source.Provider, "Timed out reaching the service.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return ProviderLimits.Failed(source.Provider, "Couldn't read the limits right now.");
        }
    }
}
