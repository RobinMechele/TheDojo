using System.Globalization;
using System.Text;
using TheDojo.Core.Analysis;
using TheDojo.Core.Model;
using TheDojo.Core.Pricing;

namespace TheDojo.Core.Reports;

/// <summary>Raw data for spreadsheets: one row per model call, per session or per tool call (RFC 4180, invariant culture).</summary>
public static class CsvExport
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Calls(UsageDataset data, CostCalculator costs, DojoReport report)
    {
        var csv = new StringBuilder();
        Line(csv, "timestamp", "provider", "session", "project", "model", "input", "output", "cache_read", "cache_write_5m", "cache_write_1h", "reasoning",
            "context", "cost_usd", "priced", "aggregate", "effort", "speed", "stop_reason", "subagent_type", "skill", "tools", "duration_ms");
        foreach (var c in InScope(data.Calls, data, report))
        {
            var cost = costs.Cost(c);
            Line(csv, c.Timestamp.ToString("O", Invariant), c.Provider.ToString(), c.SessionId, Project(data, c.SessionId), c.Model,
                N(c.Tokens.Input), N(c.Tokens.Output), N(c.Tokens.CacheRead), N(c.Tokens.CacheWrite5m), N(c.Tokens.CacheWrite1h), N(c.Tokens.Reasoning),
                N(c.Tokens.Context), cost.Usd.ToString("0.######", Invariant), cost.Priced ? "1" : "0", c.IsAggregate ? "1" : "0",
                c.Effort, c.Speed, c.StopReason, c.IsSubagent ? c.AgentType ?? "subagent" : null, c.Skill, c.Tools is null ? null : string.Join(';', c.Tools),
                c.DurationMs?.ToString(Invariant));
        }

        return csv.ToString();
    }

    public static string Sessions(DojoReport report)
    {
        var csv = new StringBuilder();
        Line(csv, "session", "provider", "title", "project", "branch", "start", "end", "active_ms", "prompts", "calls", "cost_usd", "tokens", "cache_hit_rate",
            "peak_context", "compactions", "tool_calls", "tool_errors", "denials", "interrupts", "models", "subagent_cost_usd", "lines_added", "lines_removed", "pull_requests");
        foreach (var s in report.Sessions)
        {
            Line(csv, s.SessionId, s.Provider.ToString(), s.Title, s.Project, s.Branch, s.Start.ToString("O", Invariant), s.End.ToString("O", Invariant),
                N(s.ActiveMs), N(s.Prompts), N(s.Calls), s.CostUsd.ToString("0.####", Invariant), N(s.Tokens.Total), s.CacheHitRate.ToString("0.###", Invariant),
                N(s.PeakContext), N(s.Compactions), N(s.ToolCalls), N(s.ToolErrors), N(s.Denials), N(s.Interrupts), string.Join(';', s.Models),
                s.SubagentCostUsd.ToString("0.####", Invariant), N(s.LinesAdded), N(s.LinesRemoved), N(s.PullRequests));
        }

        return csv.ToString();
    }

    public static string Tools(UsageDataset data, DojoReport report)
    {
        var csv = new StringBuilder();
        Line(csv, "timestamp", "provider", "session", "project", "tool", "outcome", "target", "mcp_server", "denial", "duration_ms", "lines_added", "lines_removed", "subagent", "error");
        foreach (var t in InScope(data.Tools, data, report))
        {
            Line(csv, t.Timestamp.ToString("O", Invariant), t.Provider.ToString(), t.SessionId, Project(data, t.SessionId), t.Name, t.Outcome.ToString(), t.Target, t.McpServer,
                t.DenialKind, t.DurationMs?.ToString(Invariant), N(t.LinesAdded), N(t.LinesRemoved), t.AgentId is null ? "0" : "1", t.ErrorText);
        }

        return csv.ToString();
    }

    private static IEnumerable<T> InScope<T>(IEnumerable<T> events, UsageDataset data, DojoReport report)
        where T : IAgentEvent =>
        events.Where(e => e.Timestamp >= report.From && e.Timestamp <= report.To
            && (report.Filter.Provider is null || e.Provider == report.Filter.Provider)
            && (report.Filter.Project is null || string.Equals(Project(data, e.SessionId), report.Filter.Project, StringComparison.OrdinalIgnoreCase)));

    private static string Project(UsageDataset data, string sessionId) =>
        data.Sessions.TryGetValue(sessionId, out var s) ? s.Project : "(unknown)";

    private static string N(long value) => value.ToString(Invariant);

    private static void Line(StringBuilder csv, params string?[] fields)
    {
        csv.AppendJoin(',', fields.Select(Quote));
        csv.Append("\r\n");
    }

    internal static string Quote(string? field)
    {
        if (string.IsNullOrEmpty(field))
        {
            return string.Empty;
        }

        return field.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;
    }
}
