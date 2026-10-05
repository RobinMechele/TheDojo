using System.Globalization;
using System.Text;
using TheDojo.Core.Analysis;
using TheDojo.Core.Insights;
using TheDojo.Core.Limits;

namespace TheDojo.Core.Reports;

/// <summary>A shareable report of a period: the numbers, the lessons, and where the money went. Paths and prompts stay out unless asked for.</summary>
public static class MarkdownReport
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Write(DojoReport report, CoachResult coach, IReadOnlyList<ProviderLimits>? limits = null, bool includePrompts = false, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        DateTime Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, zone).DateTime;
        var t = report.Totals;
        var md = new StringBuilder();
        md.AppendLine($"# {AppIdentity.ProductName} — AI usage report");
        md.AppendLine();
        md.AppendLine($"**Period:** {Local(report.From):yyyy-MM-dd HH:mm} → {Local(report.To):yyyy-MM-dd HH:mm} ({RangeName(report.Filter.Range)})  ");
        if (report.Filter.Provider is { } provider)
        {
            md.AppendLine($"**Agent:** {provider}  ");
        }

        if (report.Filter.Project is { } project)
        {
            md.AppendLine($"**Project:** {project}  ");
        }

        md.AppendLine($"**Dojo score:** {coach.Score}/100 — {coach.Belt} belt");
        md.AppendLine();

        md.AppendLine("## Summary");
        md.AppendLine();
        md.AppendLine("| Metric | Value |");
        md.AppendLine("|---|---|");
        Row(md, "Spend", UsageFormat.Money(t.CostUsd) + (t.CostTrend is { } trend ? $" ({UsageFormat.Trend(trend)} vs previous period)" : string.Empty));
        Row(md, "Tokens processed", UsageFormat.Compact(t.Tokens.Total));
        Row(md, "Input / cache read / cache write / output", $"{UsageFormat.Compact(t.Tokens.Input)} / {UsageFormat.Compact(t.Tokens.CacheRead)} / {UsageFormat.Compact(t.Tokens.CacheWrite)} / {UsageFormat.Compact(t.Tokens.Output)}");
        Row(md, "Cache hit rate", UsageFormat.Percent(t.CacheHitRate) + $" (saved {UsageFormat.Money(t.CacheSavingsUsd)})");
        Row(md, "Sessions / prompts / model calls", $"{t.Sessions} / {t.Prompts} / {t.Calls}");
        Row(md, "Cost per prompt / per session", $"{UsageFormat.Money(t.CostPerPrompt)} / {UsageFormat.Money(t.CostPerSession)}");
        Row(md, "Active agent time", UsageFormat.Duration(t.ActiveMs));
        Row(md, "Tool calls (errors, denied)", $"{t.ToolCalls} ({t.ToolErrors}, {t.ToolDenials})");
        Row(md, "Interruptions / compactions / API errors", $"{t.Interrupts} / {t.Compactions} / {t.ApiErrors}");
        Row(md, "Subagent share of spend", UsageFormat.Percent(t.SubagentShare));
        Row(md, "Thinking share of output", UsageFormat.Percent(t.ReasoningShare));
        Row(md, "Lines added / removed", $"{t.LinesAdded.ToString("N0", Invariant)} / {t.LinesRemoved.ToString("N0", Invariant)}");
        Row(md, "Pull requests", t.PullRequests.ToString(Invariant));
        md.AppendLine();

        if (coach.Parts.Count > 0)
        {
            md.AppendLine("## Dojo score");
            md.AppendLine();
            md.AppendLine("| Discipline | Score | Why |");
            md.AppendLine("|---|---|---|");
            foreach (var part in coach.Parts)
            {
                md.AppendLine($"| {part.Name} | {part.Value * 100:0}/100 | {Escape(part.Explanation)} |");
            }

            md.AppendLine();
        }

        md.AppendLine("## Lessons");
        md.AppendLine();
        if (coach.Insights.Count == 0)
        {
            md.AppendLine("Not enough activity in this period to draw lessons from.");
        }

        foreach (var insight in coach.Insights)
        {
            var badge = insight.Severity switch
            {
                InsightSeverity.Warning => "⚠️",
                InsightSeverity.Tip => "💡",
                _ => "✅",
            };
            md.AppendLine($"### {badge} {insight.Title}");
            md.AppendLine();
            md.AppendLine(insight.Detail);
            md.AppendLine();
            md.AppendLine($"**Try:** {insight.Action}" + (insight.SavingsUsd is > 0 and var s ? $" *(≈ {UsageFormat.Money(s)} to save)*" : string.Empty));
            md.AppendLine();
        }

        if (limits is { Count: > 0 })
        {
            md.AppendLine("## Plan limits");
            md.AppendLine();
            md.AppendLine("| Agent | Window | Used | Resets |");
            md.AppendLine("|---|---|---|---|");
            foreach (var p in limits)
            {
                foreach (var w in p.Windows)
                {
                    md.AppendLine($"| {p.Provider}{(p.Plan is null ? string.Empty : $" ({p.Plan})")} | {w.Label} | {w.PercentUsed:0}% | {(w.ResetsAt is { } at ? Local(at).ToString("ddd HH:mm", Invariant) : "—")} |");
                }
            }

            md.AppendLine();
        }

        Table(md, "Models", ["Model", "Calls", "Tokens", "Cache hit", "Avg context", "Spend", "Share"],
            report.Models.Select(m => new[] { m.Model, m.Calls.ToString(Invariant), UsageFormat.Compact(m.Tokens.Total), UsageFormat.Percent(m.CacheHitRate), UsageFormat.Compact(m.AverageContext), UsageFormat.Money(m.CostUsd), UsageFormat.Percent(m.Share) }));

        Table(md, "Projects", ["Project", "Sessions", "Prompts", "Spend", "Lines +/−", "PRs"],
            report.Projects.Select(p => new[] { p.Project, p.Sessions.ToString(Invariant), p.Prompts.ToString(Invariant), UsageFormat.Money(p.CostUsd), $"+{p.LinesAdded}/−{p.LinesRemoved}", p.PullRequests.ToString(Invariant) }));

        Table(md, "Most expensive sessions", ["Session", "Project", "Started", "Prompts", "Peak context", "Spend"],
            report.Sessions.OrderByDescending(s => s.CostUsd).Take(10).Select(s => new[] { s.Title, s.Project, Local(s.Start).ToString("MM-dd HH:mm", Invariant), s.Prompts.ToString(Invariant), UsageFormat.Compact(s.PeakContext), UsageFormat.Money(s.CostUsd) }));

        Table(md, "Tools", ["Tool", "Calls", "Errors", "Denied", "Avg time"],
            report.Tools.Take(15).Select(x => new[] { x.Name, x.Calls.ToString(Invariant), x.Errors.ToString(Invariant), x.Denied.ToString(Invariant), x.AverageMs is { } ms ? UsageFormat.Duration(ms) : "—" }));

        Table(md, "Subagents", ["Type", "Runs", "Spend"],
            report.Subagents.Select(s => new[] { s.Label, s.Count.ToString(Invariant), UsageFormat.Money(s.CostUsd) }));

        Table(md, "Context size of calls", ["Context", "Calls", "Spend"],
            report.ContextBuckets.Where(b => b.Calls > 0).Select(b => new[] { b.Label, b.Calls.ToString(Invariant), UsageFormat.Money(b.CostUsd) }));

        if (includePrompts)
        {
            Table(md, "Most expensive prompts", ["When", "Project", "Prompt", "Calls", "Spend"],
                report.TopPrompts.Select(p => new[] { Local(p.Timestamp).ToString("MM-dd HH:mm", Invariant), p.Project, p.Preview, p.Calls.ToString(Invariant), UsageFormat.Money(p.CostUsd) }));
        }

        md.AppendLine($"<sub>Generated by {AppIdentity.ProductName} on {Local(report.To):yyyy-MM-dd HH:mm}. Costs are estimates at public API prices (Copilot: reported AI credits); subscription plans bill differently.</sub>");
        return md.ToString();
    }

    public static string RangeName(UsageRange range) => range switch
    {
        UsageRange.Past24Hours => "past 24 hours",
        UsageRange.Days7 => "7 days",
        UsageRange.Days30 => "30 days",
        UsageRange.Days90 => "90 days",
        _ => "all time",
    };

    private static void Row(StringBuilder md, string name, string value) => md.AppendLine($"| {name} | {Escape(value)} |");

    private static void Table(StringBuilder md, string title, string[] headers, IEnumerable<string[]> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0)
        {
            return;
        }

        md.AppendLine("## " + title);
        md.AppendLine();
        md.AppendLine("| " + string.Join(" | ", headers) + " |");
        md.AppendLine("|" + string.Concat(headers.Select(_ => "---|")));
        foreach (var row in list)
        {
            md.AppendLine("| " + string.Join(" | ", row.Select(Escape)) + " |");
        }

        md.AppendLine();
    }

    private static string Escape(string text) => text.Replace("|", "\\|").Replace("\n", " ");
}
