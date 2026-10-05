using TheDojo.Core.Model;

namespace TheDojo.Core.Analysis;

public enum UsageRange
{
    Past24Hours,
    Days7,
    Days30,
    Days90,
    All,
}

/// <summary>Which slice of the history a report covers.</summary>
public sealed record ReportFilter(UsageRange Range, Provider? Provider = null, string? Project = null);

public sealed record ReportTotals(
    double CostUsd,
    double PreviousCostUsd,
    TokenCounts Tokens,
    int Calls,
    int Sessions,
    int Projects,
    int Prompts,
    int AutomatedPrompts,
    int ToolCalls,
    int ToolErrors,
    int ToolDenials,
    int Interrupts,
    int Compactions,
    int ApiErrors,
    int LinesAdded,
    int LinesRemoved,
    int PullRequests,
    long ActiveMs,
    double CacheSavingsUsd,
    double SubagentCostUsd,
    int WebSearches,
    int WebFetches,
    long UnpricedTokens)
{
    public double CacheHitRate => Tokens.CacheHitRate;

    public double CostPerPrompt => Prompts == 0 ? 0 : CostUsd / Prompts;

    public double CostPerSession => Sessions == 0 ? 0 : CostUsd / Sessions;

    public double ToolErrorRate => ToolCalls == 0 ? 0 : (double)ToolErrors / ToolCalls;

    public double SubagentShare => CostUsd <= 0 ? 0 : SubagentCostUsd / CostUsd;

    public double ReasoningShare => Tokens.Output == 0 ? 0 : (double)Tokens.Reasoning / Tokens.Output;

    /// <summary>Change against the previous period of the same length (e.g. +0.25 = 25% more), or null without one.</summary>
    public double? CostTrend => PreviousCostUsd > 0 ? CostUsd / PreviousCostUsd - 1 : null;

    public static ReportTotals Empty { get; } = new(0, 0, new TokenCounts(), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}

/// <summary>One bucket of the spend chart: an hour (past 24 hours) or a local day.</summary>
public sealed record SeriesPoint(DateTime Start, double CostUsd, long Tokens, int Calls, int Prompts, double ClaudeCostUsd, double CopilotCostUsd);

public sealed record ModelRow(
    Provider Provider,
    string Model,
    string Family,
    int Calls,
    TokenCounts Tokens,
    double CostUsd,
    double Share,
    long AverageContext,
    long AverageOutput,
    int SubagentCalls,
    bool Priced)
{
    public double CacheHitRate => Tokens.CacheHitRate;
}

public sealed record ProjectRow(
    string Project,
    int Sessions,
    int Prompts,
    int Calls,
    double CostUsd,
    long Tokens,
    int LinesAdded,
    int LinesRemoved,
    int PullRequests,
    DateTimeOffset LastActive);

public sealed record SessionRow(
    Provider Provider,
    string SessionId,
    string Title,
    string Project,
    string? Branch,
    DateTimeOffset Start,
    DateTimeOffset End,
    long ActiveMs,
    int Prompts,
    int Calls,
    double CostUsd,
    TokenCounts Tokens,
    long PeakContext,
    int Compactions,
    int ToolCalls,
    int ToolErrors,
    int Denials,
    int Interrupts,
    string[] Models,
    double SubagentCostUsd,
    int LinesAdded,
    int LinesRemoved,
    int PullRequests,
    string? ClientVersion)
{
    public double CostPerPrompt => Prompts == 0 ? CostUsd : CostUsd / Prompts;

    public double CacheHitRate => Tokens.CacheHitRate;

    public TimeSpan WallTime => End - Start;
}

public sealed record ToolRow(
    string Name,
    string? McpServer,
    int Calls,
    int Errors,
    int Denied,
    int Interrupted,
    long? AverageMs,
    long? P95Ms,
    int LinesAdded,
    int LinesRemoved,
    int SubagentCalls)
{
    public double ErrorRate => Calls == 0 ? 0 : (double)Errors / Calls;
}

/// <summary>A generic "label → how many, how much" row: MCP servers, subagent types, skills, programs, efforts, stop reasons, errors, commands.</summary>
public sealed record BreakdownRow(string Label, int Count, double CostUsd = 0, long Tokens = 0, int Errors = 0);

public sealed record ContextBucket(string Label, long MinTokens, long MaxTokens, int Calls, double CostUsd);

/// <summary>Spend by local day of the week and hour of the day.</summary>
public sealed record HeatCell(DayOfWeek Day, int Hour, double CostUsd, int Prompts, int Calls);

/// <summary>A prompt and everything it set off until the next one: its calls, their cost and the tools they ran.</summary>
public sealed record PromptRow(
    Provider Provider,
    DateTimeOffset Timestamp,
    string SessionId,
    string Project,
    PromptKind Kind,
    string Preview,
    int Calls,
    double CostUsd,
    int ToolCalls,
    long Output);

public sealed record DojoReport(
    ReportFilter Filter,
    DateTimeOffset From,
    DateTimeOffset To,
    bool Hourly,
    ReportTotals Totals,
    IReadOnlyList<SeriesPoint> Series,
    IReadOnlyList<ModelRow> Models,
    IReadOnlyList<ProjectRow> Projects,
    IReadOnlyList<SessionRow> Sessions,
    IReadOnlyList<ToolRow> Tools,
    IReadOnlyList<BreakdownRow> McpServers,
    IReadOnlyList<BreakdownRow> Subagents,
    IReadOnlyList<BreakdownRow> Skills,
    IReadOnlyList<BreakdownRow> Programs,
    IReadOnlyList<BreakdownRow> Efforts,
    IReadOnlyList<BreakdownRow> StopReasons,
    IReadOnlyList<BreakdownRow> Errors,
    IReadOnlyList<BreakdownRow> Commands,
    IReadOnlyList<ContextBucket> ContextBuckets,
    IReadOnlyList<HeatCell> Heatmap,
    IReadOnlyList<PromptRow> TopPrompts,
    IReadOnlyList<string> AllProjects)
{
    public bool IsEmpty => Totals.Calls == 0 && Totals.Prompts == 0 && Totals.CostUsd == 0;
}

/// <summary>One model call on a session's timeline: how full the context was and what it cost.</summary>
public sealed record CallPoint(DateTimeOffset Timestamp, string Model, long Context, long Output, double CostUsd, bool IsSubagent, string[] Tools);

public sealed record SessionDetail(
    SessionRow Session,
    IReadOnlyList<CallPoint> Timeline,
    IReadOnlyList<PromptRow> Prompts,
    IReadOnlyList<ToolRow> Tools,
    IReadOnlyList<CompactionEvent> Compactions,
    IReadOnlyList<ErrorEvent> Errors,
    IReadOnlyList<string> PullRequests);
