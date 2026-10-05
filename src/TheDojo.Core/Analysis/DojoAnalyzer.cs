using TheDojo.Core.Model;
using TheDojo.Core.Pricing;

namespace TheDojo.Core.Analysis;

/// <summary>Folds a <see cref="UsageDataset"/> into the numbers every page shows. Pure, so it is testable without any files.</summary>
public static class DojoAnalyzer
{
    private static readonly string[] ShellTools = ["bash", "powershell", "shell", "read_powershell", "run_in_terminal"];

    private static readonly (string Label, long Min, long Max)[] ContextRanges =
    [
        ("< 25K", 0, 25_000),
        ("25K–50K", 25_000, 50_000),
        ("50K–100K", 50_000, 100_000),
        ("100K–200K", 100_000, 200_000),
        ("200K–500K", 200_000, 500_000),
        ("500K+", 500_000, long.MaxValue),
    ];

    public static int Days(UsageRange range) => range switch
    {
        UsageRange.Past24Hours => 1,
        UsageRange.Days7 => 7,
        UsageRange.Days30 => 30,
        UsageRange.Days90 => 90,
        _ => 0,
    };

    /// <summary>The earliest instant a range covers. "All" starts at the first local day with any data.</summary>
    public static DateTimeOffset Start(UsageRange range, DateTimeOffset now, TimeZoneInfo zone, DateTimeOffset? earliest = null)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        if (range == UsageRange.Past24Hours)
        {
            // 24 hourly buckets ending with the current hour, so the window lines up with the chart.
            var firstHour = new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0).AddHours(-23);
            return new DateTimeOffset(firstHour, zone.GetUtcOffset(firstHour));
        }

        DateTime first;
        if (range == UsageRange.All)
        {
            first = earliest is { } e && e < now ? TimeZoneInfo.ConvertTime(e, zone).Date : local.Date;
        }
        else
        {
            first = local.Date.AddDays(-(Days(range) - 1));
        }

        return new DateTimeOffset(first, zone.GetUtcOffset(first));
    }

    public static DojoReport Build(UsageDataset data, CostCalculator costs, ReportFilter filter, DateTimeOffset now, TimeZoneInfo zone)
    {
        var earliest = Earliest(data);
        var from = Start(filter.Range, now, zone, earliest);
        var hourly = filter.Range == UsageRange.Past24Hours;

        string ProjectOf(string sessionId) => data.Sessions.TryGetValue(sessionId, out var s) ? s.Project : "(unknown)";
        bool Matches(IAgentEvent e) =>
            (filter.Provider is null || e.Provider == filter.Provider)
            && (filter.Project is null || string.Equals(ProjectOf(e.SessionId), filter.Project, StringComparison.OrdinalIgnoreCase));
        bool InScope(IAgentEvent e) => e.Timestamp >= from && e.Timestamp <= now && Matches(e);

        var calls = data.Calls.Where(InScope).Select(c => (Call: c, Cost: costs.Cost(c))).ToList();
        var tools = data.Tools.Where(InScope).ToList();
        var prompts = data.Prompts.Where(InScope).ToList();
        var turns = data.Turns.Where(InScope).ToList();
        var compactions = data.Compactions.Where(InScope).ToList();
        var errors = data.Errors.Where(InScope).ToList();

        var previousCost = 0.0;
        if (filter.Range != UsageRange.All)
        {
            var length = now - from;
            previousCost = data.Calls.Where(c => c.Timestamp >= from - length && c.Timestamp < from && Matches(c)).Sum(c => costs.Cost(c).Usd);
        }

        var sessions = BuildSessions(data, calls, tools, prompts, turns, compactions, errors, ProjectOf);
        var promptRows = AttributePrompts(prompts, calls, tools, ProjectOf);

        var tokens = calls.Aggregate(new TokenCounts(), (sum, c) => sum + c.Call.Tokens);
        var totals = new ReportTotals(
            CostUsd: calls.Sum(c => c.Cost.Usd),
            PreviousCostUsd: previousCost,
            Tokens: tokens,
            Calls: calls.Count(c => !c.Call.IsAggregate),
            Sessions: sessions.Count,
            Projects: sessions.Select(s => s.Project).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            Prompts: prompts.Count(p => p.Kind != PromptKind.Automated),
            AutomatedPrompts: prompts.Count(p => p.Kind == PromptKind.Automated),
            ToolCalls: tools.Count,
            ToolErrors: tools.Count(t => t.Outcome == ToolOutcome.Error),
            ToolDenials: tools.Count(t => t.Outcome == ToolOutcome.Denied),
            Interrupts: errors.Count(e => e.Kind == ErrorKind.Interrupt) + tools.Count(t => t.Outcome == ToolOutcome.Interrupted),
            Compactions: compactions.Count,
            ApiErrors: errors.Count(e => e.Kind is ErrorKind.Api or ErrorKind.Session),
            LinesAdded: sessions.Sum(s => s.LinesAdded),
            LinesRemoved: sessions.Sum(s => s.LinesRemoved),
            PullRequests: sessions.Sum(s => s.PullRequests),
            ActiveMs: sessions.Sum(s => s.ActiveMs),
            CacheSavingsUsd: calls.Sum(c => c.Cost.CacheSavingsUsd),
            SubagentCostUsd: calls.Where(c => c.Call.IsSubagent).Sum(c => c.Cost.Usd),
            WebSearches: calls.Sum(c => c.Call.WebSearches),
            WebFetches: calls.Sum(c => c.Call.WebFetches),
            UnpricedTokens: calls.Where(c => !c.Cost.Priced).Sum(c => c.Call.Tokens.Total));

        return new DojoReport(
            filter,
            from,
            now,
            hourly,
            totals,
            BuildSeries(calls, prompts, from, now, zone, hourly),
            BuildModels(calls, totals.CostUsd),
            BuildProjects(sessions),
            sessions,
            BuildTools(tools),
            BuildMcp(calls, tools),
            BuildSubagents(calls, tools),
            BuildSkills(calls, tools),
            Group(tools.Where(t => ShellTools.Contains(t.Name.ToLowerInvariant()) && t.Target is not null), t => t.Target!, g => new BreakdownRow(g.Key, g.Count(), Errors: g.Count(t => t.Outcome == ToolOutcome.Error))),
            Group(calls.Where(c => !c.Call.IsAggregate), c => c.Call.Effort ?? "default", g => new BreakdownRow(g.Key, g.Count(), g.Sum(c => c.Cost.Usd), g.Sum(c => c.Call.Tokens.Total))),
            Group(calls.Where(c => c.Call.StopReason is not null), c => c.Call.StopReason!, g => new BreakdownRow(g.Key, g.Count(), g.Sum(c => c.Cost.Usd))),
            Group(errors, e => $"{e.Kind} · {e.Code}", g => new BreakdownRow(g.Key, g.Count())),
            Group(prompts.Where(p => p.Command is not null), p => p.Command!, g => new BreakdownRow(g.Key, g.Count())),
            BuildContextBuckets(calls),
            BuildHeatmap(calls, prompts, zone),
            [.. promptRows.OrderByDescending(p => p.CostUsd).Take(15)],
            [.. data.Sessions.Values.Select(s => s.Project).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>The first event of any kind (lists are sorted by time), or null for no data.</summary>
    public static DateTimeOffset? Earliest(UsageDataset data) =>
        new[] { data.Calls.FirstOrDefault()?.Timestamp, data.Prompts.FirstOrDefault()?.Timestamp, data.Tools.FirstOrDefault()?.Timestamp }
            .Where(t => t is not null)
            .Min();

    /// <summary>Everything about one session: its timeline of calls (context growth), prompts with their cost, tools, compactions and errors.</summary>
    public static SessionDetail? Session(UsageDataset data, CostCalculator costs, string sessionId)
    {
        if (!data.Sessions.ContainsKey(sessionId))
        {
            return null;
        }

        string ProjectOf(string id) => data.Sessions.TryGetValue(id, out var s) ? s.Project : "(unknown)";
        var calls = data.Calls.Where(c => c.SessionId == sessionId).Select(c => (Call: c, Cost: costs.Cost(c))).ToList();
        var tools = data.Tools.Where(t => t.SessionId == sessionId).ToList();
        var prompts = data.Prompts.Where(p => p.SessionId == sessionId).ToList();
        var turns = data.Turns.Where(t => t.SessionId == sessionId).ToList();
        var compactions = data.Compactions.Where(c => c.SessionId == sessionId).ToList();
        var errors = data.Errors.Where(e => e.SessionId == sessionId).ToList();

        var row = BuildSessions(data, calls, tools, prompts, turns, compactions, errors, ProjectOf).FirstOrDefault();
        if (row is null)
        {
            return null;
        }

        return new SessionDetail(
            row,
            [.. calls.Where(c => !c.Call.IsAggregate).Select(c => new CallPoint(c.Call.Timestamp, c.Call.Model, c.Call.Tokens.Context, c.Call.Tokens.Output, c.Cost.Usd, c.Call.IsSubagent, c.Call.Tools ?? []))],
            AttributePrompts(prompts, calls, tools, ProjectOf),
            BuildTools(tools),
            compactions,
            errors,
            data.Sessions[sessionId].PullRequests ?? []);
    }

    private static List<SessionRow> BuildSessions(
        UsageDataset data,
        List<(ApiCall Call, CallCost Cost)> calls,
        List<ToolCall> tools,
        List<PromptEvent> prompts,
        List<TurnEvent> turns,
        List<CompactionEvent> compactions,
        List<ErrorEvent> errors,
        Func<string, string> projectOf)
    {
        var callsBy = calls.ToLookup(c => c.Call.SessionId);
        var toolsBy = tools.ToLookup(t => t.SessionId);
        var promptsBy = prompts.ToLookup(p => p.SessionId);
        var turnsBy = turns.ToLookup(t => t.SessionId);
        var compactionsBy = compactions.ToLookup(c => c.SessionId);
        var errorsBy = errors.ToLookup(e => e.SessionId);

        var ids = callsBy.Select(g => g.Key).Union(promptsBy.Select(g => g.Key)).Union(toolsBy.Select(g => g.Key)).ToList();
        var rows = new List<SessionRow>(ids.Count);
        foreach (var id in ids)
        {
            var info = data.Sessions.TryGetValue(id, out var s) ? s : new SessionInfo(Provider.Claude, id);
            var sessionCalls = callsBy[id].ToList();
            var sessionTools = toolsBy[id].ToList();
            var sessionPrompts = promptsBy[id].ToList();

            var times = sessionCalls.Select(c => c.Call.Timestamp).Concat(sessionPrompts.Select(p => p.Timestamp)).Concat(sessionTools.Select(t => t.Timestamp)).Order().ToList();
            if (times.Count == 0)
            {
                continue;
            }

            var turnMs = turnsBy[id].Sum(t => t.DurationMs);
            var active = turnMs > 0 ? turnMs : EstimateActiveMs(times);
            var toolLinesAdded = sessionTools.Sum(t => t.LinesAdded);
            var toolLinesRemoved = sessionTools.Sum(t => t.LinesRemoved);
            var sessionErrors = errorsBy[id].ToList();

            rows.Add(new SessionRow(
                info.Provider,
                id,
                info.Title ?? FirstPrompt(sessionPrompts) ?? "(untitled session)",
                projectOf(id),
                info.Branch,
                times[0],
                times[^1],
                active,
                sessionPrompts.Count(p => p.Kind != PromptKind.Automated),
                sessionCalls.Count(c => !c.Call.IsAggregate),
                sessionCalls.Sum(c => c.Cost.Usd),
                sessionCalls.Aggregate(new TokenCounts(), (sum, c) => sum + c.Call.Tokens),
                sessionCalls.Where(c => !c.Call.IsSubagent).Select(c => c.Call.Tokens.Context).DefaultIfEmpty(0).Max(),
                compactionsBy[id].Count(),
                sessionTools.Count,
                sessionTools.Count(t => t.Outcome == ToolOutcome.Error),
                sessionTools.Count(t => t.Outcome == ToolOutcome.Denied),
                sessionErrors.Count(e => e.Kind == ErrorKind.Interrupt) + sessionTools.Count(t => t.Outcome == ToolOutcome.Interrupted),
                [.. sessionCalls.Where(c => c.Call.Tokens.Total > 0 || c.Cost.Usd > 0).GroupBy(c => c.Call.Model).OrderByDescending(g => g.Sum(c => c.Cost.Usd)).Select(g => g.Key)],
                sessionCalls.Where(c => c.Call.IsSubagent).Sum(c => c.Cost.Usd),
                toolLinesAdded > 0 || toolLinesRemoved > 0 ? toolLinesAdded : info.LinesAdded ?? 0,
                toolLinesAdded > 0 || toolLinesRemoved > 0 ? toolLinesRemoved : info.LinesRemoved ?? 0,
                info.PullRequests?.Length ?? 0,
                info.ClientVersion));
        }

        rows.Sort((a, b) => b.End.CompareTo(a.End));
        return rows;
    }

    private static string? FirstPrompt(List<PromptEvent> prompts) =>
        prompts.FirstOrDefault(p => p.Kind == PromptKind.Typed)?.Preview is { Length: > 0 } preview
            ? preview.Length > 80 ? preview[..79] + "…" : preview
            : null;

    /// <summary>Time with activity: gaps between events shorter than five minutes (longer ones are the engineer away).</summary>
    internal static long EstimateActiveMs(IReadOnlyList<DateTimeOffset> sortedTimes)
    {
        long ms = 0;
        for (var i = 1; i < sortedTimes.Count; i++)
        {
            var gap = (sortedTimes[i] - sortedTimes[i - 1]).TotalMilliseconds;
            if (gap is > 0 and < 5 * 60 * 1000)
            {
                ms += (long)gap;
            }
        }

        return ms;
    }

    /// <summary>
    /// Gives each call and tool to the prompt that set it off: the latest prompt of the same session at or before it.
    /// Work before a session's first prompt (a resumed session finishing up) belongs to no prompt.
    /// </summary>
    internal static List<PromptRow> AttributePrompts(
        List<PromptEvent> prompts,
        List<(ApiCall Call, CallCost Cost)> calls,
        List<ToolCall> tools,
        Func<string, string> projectOf)
    {
        var rows = new List<PromptRow>(prompts.Count);
        var callsBy = calls.Where(c => c.Call.Tokens.Total > 0 || c.Cost.Usd > 0).ToLookup(c => c.Call.SessionId);
        var toolsBy = tools.ToLookup(t => t.SessionId);

        foreach (var group in prompts.GroupBy(p => p.SessionId))
        {
            var list = group.OrderBy(p => p.Timestamp).ToList();
            var starts = list.Select(p => p.Timestamp).ToList();
            var callCount = new int[list.Count];
            var cost = new double[list.Count];
            var output = new long[list.Count];
            var toolCount = new int[list.Count];

            foreach (var (call, callCost) in callsBy[group.Key])
            {
                if (Owner(starts, call.Timestamp) is { } i)
                {
                    callCount[i] += call.IsAggregate ? 0 : 1;
                    cost[i] += callCost.Usd;
                    output[i] += call.Tokens.Output;
                }
            }

            foreach (var tool in toolsBy[group.Key])
            {
                if (Owner(starts, tool.Timestamp) is { } i)
                {
                    toolCount[i]++;
                }
            }

            for (var i = 0; i < list.Count; i++)
            {
                var p = list[i];
                rows.Add(new PromptRow(p.Provider, p.Timestamp, p.SessionId, projectOf(p.SessionId), p.Kind, p.Preview, callCount[i], cost[i], toolCount[i], output[i]));
            }
        }

        rows.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return rows;
    }

    private static int? Owner(List<DateTimeOffset> starts, DateTimeOffset at)
    {
        var index = starts.BinarySearch(at);
        if (index < 0)
        {
            index = ~index - 1;
        }
        else
        {
            // Several prompts at the same instant: the last of them.
            while (index + 1 < starts.Count && starts[index + 1] == at)
            {
                index++;
            }
        }

        return index >= 0 ? index : null;
    }

    private static List<SeriesPoint> BuildSeries(List<(ApiCall Call, CallCost Cost)> calls, List<PromptEvent> prompts, DateTimeOffset from, DateTimeOffset now, TimeZoneInfo zone, bool hourly)
    {
        DateTime BucketOf(DateTimeOffset at)
        {
            var local = TimeZoneInfo.ConvertTime(at, zone).DateTime;
            return hourly ? new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0) : local.Date;
        }

        // Pre-create every bucket so quiet days still draw as zero.
        var buckets = new SortedDictionary<DateTime, (double Cost, long Tokens, int Calls, int Prompts, double Claude, double Copilot)>();
        var first = BucketOf(from);
        var last = BucketOf(now);
        for (var b = first; b <= last; b = hourly ? b.AddHours(1) : b.AddDays(1))
        {
            buckets[b] = default;
        }

        foreach (var (call, cost) in calls)
        {
            var key = BucketOf(call.Timestamp);
            if (buckets.TryGetValue(key, out var v))
            {
                buckets[key] = (v.Cost + cost.Usd, v.Tokens + call.Tokens.Total, v.Calls + (call.IsAggregate ? 0 : 1), v.Prompts,
                    v.Claude + (call.Provider == Provider.Claude ? cost.Usd : 0), v.Copilot + (call.Provider == Provider.Copilot ? cost.Usd : 0));
            }
        }

        foreach (var prompt in prompts.Where(p => p.Kind != PromptKind.Automated))
        {
            var key = BucketOf(prompt.Timestamp);
            if (buckets.TryGetValue(key, out var v))
            {
                buckets[key] = v with { Prompts = v.Prompts + 1 };
            }
        }

        return [.. buckets.Select(kv => new SeriesPoint(kv.Key, kv.Value.Cost, kv.Value.Tokens, kv.Value.Calls, kv.Value.Prompts, kv.Value.Claude, kv.Value.Copilot))];
    }

    private static List<ModelRow> BuildModels(List<(ApiCall Call, CallCost Cost)> calls, double totalCost) =>
        [.. calls
            .Where(c => c.Call.Tokens.Total > 0 || c.Cost.Usd > 0)
            .GroupBy(c => (c.Call.Provider, c.Call.Model))
            .Select(g =>
            {
                var real = g.Where(c => !c.Call.IsAggregate).ToList();
                var cost = g.Sum(c => c.Cost.Usd);
                return new ModelRow(
                    g.Key.Provider,
                    g.Key.Model,
                    ClaudePricing.Family(g.Key.Model),
                    real.Count,
                    g.Aggregate(new TokenCounts(), (sum, c) => sum + c.Call.Tokens),
                    cost,
                    totalCost > 0 ? cost / totalCost : 0,
                    real.Count == 0 ? 0 : (long)real.Average(c => c.Call.Tokens.Context),
                    real.Count == 0 ? 0 : (long)real.Average(c => c.Call.Tokens.Output),
                    real.Count(c => c.Call.IsSubagent),
                    g.All(c => c.Cost.Priced));
            })
            .OrderByDescending(m => m.CostUsd)
            .ThenByDescending(m => m.Tokens.Total)];

    private static List<ProjectRow> BuildProjects(List<SessionRow> sessions) =>
        [.. sessions
            .GroupBy(s => s.Project, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProjectRow(
                g.Key,
                g.Count(),
                g.Sum(s => s.Prompts),
                g.Sum(s => s.Calls),
                g.Sum(s => s.CostUsd),
                g.Sum(s => s.Tokens.Total),
                g.Sum(s => s.LinesAdded),
                g.Sum(s => s.LinesRemoved),
                g.Sum(s => s.PullRequests),
                g.Max(s => s.End)))
            .OrderByDescending(p => p.CostUsd)];

    private static List<ToolRow> BuildTools(List<ToolCall> tools) =>
        [.. tools
            .GroupBy(t => t.Name)
            .Select(g =>
            {
                var durations = g.Where(t => t.DurationMs is not null && t.Outcome != ToolOutcome.Unknown).Select(t => t.DurationMs!.Value).Order().ToList();
                return new ToolRow(
                    g.Key,
                    g.Select(t => t.McpServer).FirstOrDefault(m => m is not null),
                    g.Count(),
                    g.Count(t => t.Outcome == ToolOutcome.Error),
                    g.Count(t => t.Outcome == ToolOutcome.Denied),
                    g.Count(t => t.Outcome == ToolOutcome.Interrupted),
                    durations.Count == 0 ? null : (long)durations.Average(),
                    durations.Count == 0 ? null : durations[Math.Min(durations.Count - 1, (int)Math.Ceiling(durations.Count * 0.95) - 1)],
                    g.Sum(t => t.LinesAdded),
                    g.Sum(t => t.LinesRemoved),
                    g.Count(t => t.AgentId is not null));
            })
            .OrderByDescending(t => t.Calls)];

    private static List<BreakdownRow> BuildMcp(List<(ApiCall Call, CallCost Cost)> calls, List<ToolCall> tools)
    {
        var costBy = calls.Where(c => c.Call.McpServer is not null).GroupBy(c => c.Call.McpServer!).ToDictionary(g => g.Key, g => g.Sum(c => c.Cost.Usd));
        return [.. tools
            .Where(t => t.McpServer is not null)
            .GroupBy(t => t.McpServer!)
            .Select(g => new BreakdownRow(g.Key, g.Count(), costBy.GetValueOrDefault(g.Key), Errors: g.Count(t => t.Outcome == ToolOutcome.Error)))
            .OrderByDescending(r => r.Count)];
    }

    /// <summary>Subagents by type: how many were started, and what their own calls cost.</summary>
    private static List<BreakdownRow> BuildSubagents(List<(ApiCall Call, CallCost Cost)> calls, List<ToolCall> tools)
    {
        var spawned = tools.Where(t => t.Name.ToLowerInvariant() is "agent" or "task").GroupBy(t => t.Target ?? "general-purpose", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var spent = calls.Where(c => c.Call.IsSubagent).GroupBy(c => c.Call.AgentType ?? "subagent", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (Cost: g.Sum(c => c.Cost.Usd), Tokens: g.Sum(c => c.Call.Tokens.Total), Agents: g.Select(c => c.Call.AgentId).Distinct().Count()), StringComparer.OrdinalIgnoreCase);

        return [.. spawned.Keys.Union(spent.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(type => new BreakdownRow(
                type,
                Math.Max(spawned.GetValueOrDefault(type), spent.TryGetValue(type, out var s) ? s.Agents : 0),
                spent.TryGetValue(type, out var c) ? c.Cost : 0,
                spent.TryGetValue(type, out var t) ? t.Tokens : 0))
            .OrderByDescending(r => r.CostUsd)
            .ThenByDescending(r => r.Count)];
    }

    private static List<BreakdownRow> BuildSkills(List<(ApiCall Call, CallCost Cost)> calls, List<ToolCall> tools)
    {
        var used = tools.Where(t => t.Name == "Skill" && t.Target is not null).GroupBy(t => t.Target!).ToDictionary(g => g.Key, g => g.Count());
        var spent = calls.Where(c => c.Call.Skill is not null).GroupBy(c => c.Call.Skill!).ToDictionary(g => g.Key, g => (Cost: g.Sum(c => c.Cost.Usd), Tokens: g.Sum(c => c.Call.Tokens.Total)));
        return [.. used.Keys.Union(spent.Keys)
            .Select(skill => new BreakdownRow(skill, used.GetValueOrDefault(skill), spent.TryGetValue(skill, out var s) ? s.Cost : 0, spent.TryGetValue(skill, out var t) ? t.Tokens : 0))
            .OrderByDescending(r => r.CostUsd)];
    }

    private static List<ContextBucket> BuildContextBuckets(List<(ApiCall Call, CallCost Cost)> calls)
    {
        var real = calls.Where(c => !c.Call.IsAggregate && c.Call.Tokens.Context > 0).ToList();
        return [.. ContextRanges.Select(r =>
        {
            var inRange = real.Where(c => c.Call.Tokens.Context >= r.Min && c.Call.Tokens.Context < r.Max).ToList();
            return new ContextBucket(r.Label, r.Min, r.Max, inRange.Count, inRange.Sum(c => c.Cost.Usd));
        })];
    }

    private static List<HeatCell> BuildHeatmap(List<(ApiCall Call, CallCost Cost)> calls, List<PromptEvent> prompts, TimeZoneInfo zone)
    {
        var cells = new (double Cost, int Prompts, int Calls)[7, 24];
        foreach (var (call, cost) in calls)
        {
            var at = TimeZoneInfo.ConvertTime(call.Timestamp, zone);
            ref var cell = ref cells[(int)at.DayOfWeek, at.Hour];
            cell.Cost += cost.Usd;
            cell.Calls += call.IsAggregate ? 0 : 1;
        }

        foreach (var prompt in prompts.Where(p => p.Kind != PromptKind.Automated))
        {
            var at = TimeZoneInfo.ConvertTime(prompt.Timestamp, zone);
            cells[(int)at.DayOfWeek, at.Hour].Prompts++;
        }

        var result = new List<HeatCell>(7 * 24);
        for (var d = 0; d < 7; d++)
        {
            for (var h = 0; h < 24; h++)
            {
                result.Add(new HeatCell((DayOfWeek)d, h, cells[d, h].Cost, cells[d, h].Prompts, cells[d, h].Calls));
            }
        }

        return result;
    }

    private static List<BreakdownRow> Group<T>(IEnumerable<T> items, Func<T, string> key, Func<IGrouping<string, T>, BreakdownRow> row) =>
        [.. items.GroupBy(key).Select(row).OrderByDescending(r => r.CostUsd).ThenByDescending(r => r.Count)];
}
