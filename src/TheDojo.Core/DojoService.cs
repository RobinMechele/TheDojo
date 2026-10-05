using TheDojo.Core.Analysis;
using TheDojo.Core.Ingest;
using TheDojo.Core.Insights;
using TheDojo.Core.Limits;
using TheDojo.Core.Model;
using TheDojo.Core.Pricing;

namespace TheDojo.Core;

/// <summary>Where the agents' logs are. Defaults to this machine's Claude Code and Copilot CLI folders.</summary>
public sealed record DataSources(string ClaudeProjects, string CopilotSessions)
{
    public static DataSources Default() => new(
        Path.Combine(AppIdentity.ClaudeHome, "projects"),
        Path.Combine(AppIdentity.CopilotHome, "session-state"));

    public static DataSources From(string? claudeHome, string? copilotHome) => new(
        Path.Combine(claudeHome ?? AppIdentity.ClaudeHome, "projects"),
        Path.Combine(copilotHome ?? AppIdentity.CopilotHome, "session-state"));
}

/// <summary>Everything a page needs for one filter: the report, the coach's verdict and the session data behind them.</summary>
public sealed record DojoSnapshot(DojoReport Report, CoachResult Coach, UsageDataset Data, CostCalculator Costs, IReadOnlyList<LimitReading> LimitHistory);

/// <summary>What the app and the CLI read: every agent session on this machine, priced and analyzed.</summary>
public interface IDojoService
{
    Task<DojoSnapshot> LoadAsync(ReportFilter filter, IReadOnlyList<ProviderLimits>? limits, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProviderLimits>> FetchLimitsAsync(bool manual, CancellationToken cancellationToken);
}

public sealed class DojoService : IDojoService
{
    private readonly LogScanner _claude;
    private readonly LogScanner _copilot;
    private readonly Func<CancellationToken, Task<PriceBook>> _prices;
    private readonly PlanLimitsService? _limits;
    private readonly LimitHistoryStore? _history;
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _zone;
    private PriceBook? _priceBook;

    public DojoService(
        LogScanner claude,
        LogScanner copilot,
        Func<CancellationToken, Task<PriceBook>> prices,
        TimeProvider? time = null,
        PlanLimitsService? limits = null,
        LimitHistoryStore? history = null,
        TimeZoneInfo? zone = null)
    {
        _claude = claude;
        _copilot = copilot;
        _prices = prices;
        _limits = limits;
        _history = history;
        _time = time ?? TimeProvider.System;
        _zone = zone ?? TimeZoneInfo.Local;
    }

    /// <param name="offline">No network: built-in prices only, and no live limit checks.</param>
    public static DojoService CreateDefault(DataSources? sources = null, bool offline = false, string? cacheFolder = null)
    {
        sources ??= DataSources.Default();
        var cache = cacheFolder ?? AppIdentity.LocalFolder;
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var history = new LimitHistoryStore(Path.Combine(cache, "limit-history.jsonl"));
        return new DojoService(
            new LogScanner(sources.ClaudeProjects, "*.jsonl", ClaudeTranscriptParser.Parse, Path.Combine(cache, "scan-claude.json.gz")),
            new LogScanner(sources.CopilotSessions, "events.jsonl", CopilotEventsParser.Parse, Path.Combine(cache, "scan-copilot.json.gz")),
            offline ? _ => Task.FromResult(new PriceBook()) : ct => PriceBook.LoadAsync(http, Path.Combine(cache, "model-rates.json"), TimeProvider.System, ct),
            limits: offline ? null : PlanLimitsService.CreateDefault(history),
            history: history);
    }

    public async Task<UsageDataset> LoadDatasetAsync(ReportFilter filter, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        // Files written before the range can't hold events in it; "All" reads everything.
        var since = filter.Range == UsageRange.All ? DateTimeOffset.MinValue : DojoAnalyzer.Start(filter.Range, now, _zone) - TimeSpan.FromDays(DojoAnalyzer.Days(filter.Range));
        var claude = filter.Provider is null or Provider.Claude ? _claude.ScanAsync(since, cancellationToken) : Task.FromResult<IReadOnlyList<SessionLog>>([]);
        var copilot = filter.Provider is null or Provider.Copilot ? _copilot.ScanAsync(since, cancellationToken) : Task.FromResult<IReadOnlyList<SessionLog>>([]);
        await Task.WhenAll(claude, copilot).ConfigureAwait(false);
        return UsageDataset.Merge(claude.Result.Concat(copilot.Result));
    }

    public async Task<DojoSnapshot> LoadAsync(ReportFilter filter, IReadOnlyList<ProviderLimits>? limits, CancellationToken cancellationToken)
    {
        var dataTask = LoadDatasetAsync(filter, cancellationToken);
        var pricesTask = _priceBook is null ? _prices(cancellationToken) : Task.FromResult(_priceBook);
        await Task.WhenAll(dataTask, pricesTask).ConfigureAwait(false);
        _priceBook = pricesTask.Result;

        var data = dataTask.Result;
        var costs = new CostCalculator(_priceBook);
        var now = _time.GetUtcNow();
        var report = DojoAnalyzer.Build(data, costs, filter, now, _zone);
        var coach = InsightEngine.Analyze(report, data, costs, limits, now);
        var history = data.Limits.Concat(_history?.Read() ?? []).OrderBy(l => l.Timestamp).ToList();
        return new DojoSnapshot(report, coach, data, costs, history);
    }

    public async Task<IReadOnlyList<ProviderLimits>> FetchLimitsAsync(bool manual, CancellationToken cancellationToken) =>
        _limits is null ? [] : await _limits.FetchAllAsync(cancellationToken, manual).ConfigureAwait(false);
}
