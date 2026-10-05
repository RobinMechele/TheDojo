using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using TheDojo.Controls;
using TheDojo.Core;
using TheDojo.Core.Analysis;
using TheDojo.Core.Limits;
using TheDojo.Core.Model;

namespace TheDojo.ViewModels;

public enum Page
{
    Overview,
    Coach,
    Sessions,
    Models,
    Projects,
    Tools,
    Activity,
    Limits,
    Reports,
}

/// <summary>A page fed from the current snapshot. Pages only format; every number comes from Core.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    public abstract Page Page { get; }

    public abstract string Title { get; }

    public abstract string Glyph { get; }

    [ObservableProperty]
    private bool _isSelected;

    public abstract void Update(DojoSnapshot snapshot, IReadOnlyList<ProviderLimits> limits, DateTimeOffset now);

    protected static string Money(double usd) => UsageFormat.Money(usd);

    protected static string Compact(double value) => UsageFormat.Compact(value);

    protected static string Pct(double share) => UsageFormat.Percent(share);

    protected static string N(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}

public sealed partial class OverviewViewModel : PageViewModel
{
    public override Page Page => Page.Overview;

    public override string Title => "Overview";

    public override string Glyph => "";

    [ObservableProperty]
    private string _spend = "$0.00";

    [ObservableProperty]
    private string? _spendTrend;

    [ObservableProperty]
    private Brush? _spendTrendBrush;

    [ObservableProperty]
    private string _spendCaption = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<StatTile> _tiles = [];

    [ObservableProperty]
    private IReadOnlyList<BarColumn> _columns = [];

    [ObservableProperty]
    private string _chartTitle = "Spend per day";

    [ObservableProperty]
    private int _score;

    [ObservableProperty]
    private string _belt = "White";

    [ObservableProperty]
    private Brush _beltBrush = Palette.Belt("White");

    [ObservableProperty]
    private IReadOnlyList<InsightItem> _lessons = [];

    [ObservableProperty]
    private IReadOnlyList<RankRow> _models = [];

    [ObservableProperty]
    private IReadOnlyList<RankRow> _projects = [];

    [ObservableProperty]
    private IReadOnlyList<ProviderLimitsItem> _limits = [];

    [ObservableProperty]
    private bool _isEmpty;

    public IReadOnlyList<string> SeriesNames { get; } = ["Claude", "Copilot"];

    public IReadOnlyList<Brush> SeriesBrushes { get; } = [Palette.Claude, Palette.Copilot];

    public override void Update(DojoSnapshot snapshot, IReadOnlyList<ProviderLimits> limits, DateTimeOffset now)
    {
        var r = snapshot.Report;
        var t = r.Totals;
        IsEmpty = r.IsEmpty;
        Spend = Money(t.CostUsd);
        SpendCaption = $"estimated spend · {Core.Reports.MarkdownReport.RangeName(r.Filter.Range)}";
        if (t.CostTrend is { } trend)
        {
            SpendTrend = $"{UsageFormat.Trend(trend)} vs previous period";
            // Up is not good for spend.
            SpendTrendBrush = trend > 0.05 ? Palette.Warning : trend < -0.05 ? Palette.Good : Palette.Muted;
        }
        else
        {
            SpendTrend = null;
        }

        Tiles =
        [
            new StatTile("Tokens processed", Compact(t.Tokens.Total), $"{Pct(t.CacheHitRate)} read from cache"),
            new StatTile("Cache savings", Money(t.CacheSavingsUsd), "vs. full input price"),
            new StatTile("Sessions", N(t.Sessions), $"{N(t.Projects)} projects · {Money(t.CostPerSession)} each"),
            new StatTile("Prompts", N(t.Prompts), $"{Money(t.CostPerPrompt)} per prompt"),
            new StatTile("Agent time", UsageFormat.Duration(t.ActiveMs), $"{N(t.Calls)} model calls"),
            new StatTile("Tool calls", N(t.ToolCalls), $"{Pct(t.ToolErrorRate)} failed · {N(t.ToolDenials)} denied"),
            new StatTile("Code changed", $"+{Compact(t.LinesAdded)} / −{Compact(t.LinesRemoved)}", $"{N(t.PullRequests)} pull requests"),
            new StatTile("Subagents", Pct(t.SubagentShare), "of spend"),
        ];

        ChartTitle = r.Hourly ? "Spend per hour" : "Spend per day";
        Columns = [.. r.Series.Select(p => new BarColumn(
            r.Hourly ? Display.Format(p.Start, "HH:mm") : Display.Format(p.Start, "MMM d"),
            r.Hourly ? Display.Format(p.Start, "ddd HH:00") : Display.Format(p.Start, "ddd MMM d"),
            [p.ClaudeCostUsd, p.CopilotCostUsd]))];

        Score = snapshot.Coach.Score;
        Belt = snapshot.Coach.Belt;
        BeltBrush = Palette.Belt(Belt);
        Lessons = [.. snapshot.Coach.Insights.Take(3).Select(i => new InsightItem(i))];

        var maxModel = r.Models.Count == 0 ? 0 : r.Models.Max(m => m.CostUsd);
        Models = [.. r.Models.Take(6).Select(m => new RankRow(m.Model, Money(m.CostUsd), maxModel == 0 ? 0 : m.CostUsd / maxModel, Palette.For(m.Provider), $"{N(m.Calls)} calls · {Pct(m.Share)}"))];
        var maxProject = r.Projects.Count == 0 ? 0 : r.Projects.Max(p => p.CostUsd);
        Projects = [.. r.Projects.Take(6).Select(p => new RankRow(p.Project, Money(p.CostUsd), maxProject == 0 ? 0 : p.CostUsd / maxProject, Palette.Blue, $"{N(p.Sessions)} sessions · {N(p.Prompts)} prompts"))];
        Limits = [.. limits.Where(l => l.Windows.Count > 0).Select(l => new ProviderLimitsItem(l, now))];
    }
}

public sealed partial class CoachViewModel : PageViewModel
{
    public override Page Page => Page.Coach;

    public override string Title => "Coach";

    public override string Glyph => "";

    [ObservableProperty]
    private int _score;

    [ObservableProperty]
    private string _belt = "White";

    [ObservableProperty]
    private Brush _beltBrush = Palette.Belt("White");

    [ObservableProperty]
    private string _nextBelt = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<ScorePartItem> _parts = [];

    [ObservableProperty]
    private IReadOnlyList<InsightItem> _insights = [];

    [ObservableProperty]
    private string _savings = string.Empty;

    [ObservableProperty]
    private bool _isEmpty;

    public override void Update(DojoSnapshot snapshot, IReadOnlyList<ProviderLimits> limits, DateTimeOffset now)
    {
        var coach = snapshot.Coach;
        Score = coach.Score;
        Belt = coach.Belt;
        BeltBrush = Palette.Belt(coach.Belt);
        var next = new[] { 40, 50, 60, 70, 80, 90 }.FirstOrDefault(s => s > coach.Score);
        NextBelt = next == 0 ? "Highest belt. Keep the form." : $"{next - coach.Score} points to {Core.Insights.CoachResult.BeltFor(next)} belt";
        Parts = [.. coach.Parts.Select(p => new ScorePartItem(p))];
        Insights = [.. coach.Insights.Select(i => new InsightItem(i))];
        Savings = coach.PotentialSavingsUsd > 0.005 ? $"≈ {Money(coach.PotentialSavingsUsd)} could be saved by acting on these lessons" : string.Empty;
        IsEmpty = coach.Insights.Count == 0;
    }
}

public sealed partial class ModelsViewModel : PageViewModel
{
    public override Page Page => Page.Models;

    public override string Title => "Models";

    public override string Glyph => "";

    [ObservableProperty]
    private IReadOnlyList<ModelItem> _rows = [];

    [ObservableProperty]
    private IReadOnlyList<RankRow> _efforts = [];

    [ObservableProperty]
    private IReadOnlyList<RankRow> _contexts = [];

    [ObservableProperty]
    private IReadOnlyList<RankRow> _stopReasons = [];

    [ObservableProperty]
    private IReadOnlyList<StatTile> _tiles = [];

    public override void Update(DojoSnapshot snapshot, IReadOnlyList<ProviderLimits> limits, DateTimeOffset now)
    {
        var r = snapshot.Report;
        Rows = [.. r.Models.Select(m => new ModelItem(m))];
        var effortMax = r.Efforts.Count == 0 ? 0 : r.Efforts.Max(e => e.CostUsd);
        Efforts = [.. r.Efforts.Select(e => new RankRow(e.Label, Money(e.CostUsd), effortMax == 0 ? 0 : e.CostUsd / effortMax, Palette.Violet, $"{N(e.Count)} calls"))];
        var ctxMax = r.ContextBuckets.Count == 0 ? 0 : r.ContextBuckets.Max(b => b.CostUsd);
        Contexts = [.. r.ContextBuckets.Select(b => new RankRow(b.Label, Money(b.CostUsd), ctxMax == 0 ? 0 : b.CostUsd / ctxMax, b.MinTokens >= 200_000 ? Palette.Warning : Palette.Blue, $"{N(b.Calls)} calls"))];
        var stopMax = r.StopReasons.Count == 0 ? 0 : r.StopReasons.Max(s => s.Count);
        StopReasons = [.. r.StopReasons.Select(s => new RankRow(s.Label, N(s.Count), stopMax == 0 ? 0 : (double)s.Count / stopMax, Palette.Aqua))];
        var t = r.Totals;
        Tiles =
        [
            new StatTile("Input (uncached)", Compact(t.Tokens.Input)),
            new StatTile("Cache reads", Compact(t.Tokens.CacheRead), Pct(t.CacheHitRate) + " of prompt"),
            new StatTile("Cache writes", Compact(t.Tokens.CacheWrite), $"{Compact(t.Tokens.CacheWrite1h)} on the 1-hour tier"),
            new StatTile("Output", Compact(t.Tokens.Output), $"{Pct(t.ReasoningShare)} thinking"),
            new StatTile("Web", $"{N(t.WebSearches)} / {N(t.WebFetches)}", "searches / fetches"),
        ];
    }
}

public sealed record ModelItem(ModelRow Row)
{
    public string Model => Row.Model;

    public string Provider => Row.Provider.ToString();

    public Brush Brush => Palette.For(Row.Provider);

    public int Calls => Row.Calls;

    public long Tokens => Row.Tokens.Total;

    public string TokensText => UsageFormat.Compact(Row.Tokens.Total);

    public double CacheHitRate => Row.CacheHitRate;

    public string CacheText => Row.Tokens.Context == 0 ? "—" : UsageFormat.Percent(Row.CacheHitRate);

    public long AverageContext => Row.AverageContext;

    public string AverageContextText => Row.Calls == 0 ? "—" : UsageFormat.Compact(Row.AverageContext);

    public string AverageOutputText => Row.Calls == 0 ? "—" : UsageFormat.Compact(Row.AverageOutput);

    public double Cost => Row.CostUsd;

    public string CostText => Row.Priced ? UsageFormat.Money(Row.CostUsd) : "not priced";

    public double Share => Row.Share;

    public string ShareText => UsageFormat.Percent(Row.Share);

    public int SubagentCalls => Row.SubagentCalls;
}

public sealed partial class ProjectsViewModel : PageViewModel
{
    public override Page Page => Page.Projects;

    public override string Title => "Projects";

    public override string Glyph => "";

    [ObservableProperty]
    private IReadOnlyList<ProjectItem> _rows = [];

    public override void Update(DojoSnapshot snapshot, IReadOnlyList<ProviderLimits> limits, DateTimeOffset now)
    {
        var max = snapshot.Report.Projects.Count == 0 ? 0 : snapshot.Report.Projects.Max(p => p.CostUsd);
        Rows = [.. snapshot.Report.Projects.Select(p => new ProjectItem(p, max))];
    }
}

public sealed record ProjectItem(ProjectRow Row, double MaxCost)
{
    public string Project => Row.Project;

    public int Sessions => Row.Sessions;

    public int Prompts => Row.Prompts;

    public double Cost => Row.CostUsd;

    public string CostText => UsageFormat.Money(Row.CostUsd);

    public double Share => MaxCost == 0 ? 0 : Row.CostUsd / MaxCost;

    public string PerPrompt => Row.Prompts == 0 ? "—" : UsageFormat.Money(Row.CostUsd / Row.Prompts);

    public long Tokens => Row.Tokens;

    public string TokensText => UsageFormat.Compact(Row.Tokens);

    public string Lines => $"+{UsageFormat.Compact(Row.LinesAdded)} / −{UsageFormat.Compact(Row.LinesRemoved)}";

    public int PullRequests => Row.PullRequests;

    public DateTimeOffset LastActive => Row.LastActive;

    public string LastActiveText => Display.Format(Row.LastActive, "MMM d, HH:mm");
}

public sealed partial class ToolsViewModel : PageViewModel
{
    public override Page Page => Page.Tools;

    public override string Title => "Tools & agents";

    public override string Glyph => "";

    [ObservableProperty]
    private IReadOnlyList<ToolItem> _tools = [];

    [ObservableProperty]
    private IReadOnlyList<RankRow> _programs = [];

    [ObservableProperty]
    private IReadOnlyList<RankRow> _subagents = [];

    [ObservableProperty]
    private IReadOnlyList<RankRow> _mcpServers = [];

    [ObservableProperty]
    private IReadOnlyList<RankRow> _skills = [];

    [ObservableProperty]
    private IReadOnlyList<RankRow> _commands = [];

    [ObservableProperty]
    private IReadOnlyList<RankRow> _errors = [];

    [ObservableProperty]
    private IReadOnlyList<StatTile> _tiles = [];

    public override void Update(DojoSnapshot snapshot, IReadOnlyList<ProviderLimits> limits, DateTimeOffset now)
    {
        var r = snapshot.Report;
        var t = r.Totals;
        var max = r.Tools.Count == 0 ? 0 : r.Tools.Max(x => x.Calls);
        Tools = [.. r.Tools.Select(x => new ToolItem(x, max))];
        Programs = Rank(r.Programs.Take(12), p => N(p.Count), p => p.Count, Palette.Blue, p => p.Errors > 0 ? $"{N(p.Errors)} failed" : null);
        Subagents = Rank(r.Subagents, s => Money(s.CostUsd), s => s.CostUsd, Palette.Violet, s => $"{N(s.Count)} runs");
        McpServers = Rank(r.McpServers, m => N(m.Count), m => m.Count, Palette.Aqua, m => m.Errors > 0 ? $"{N(m.Errors)} failed" : "calls");
        Skills = Rank(r.Skills, s => s.CostUsd > 0 ? Money(s.CostUsd) : N(s.Count), s => Math.Max(s.CostUsd, s.Count), Palette.Aqua, s => $"{N(s.Count)} uses");
        Commands = Rank(r.Commands.Take(12), c => N(c.Count), c => c.Count, Palette.Blue);
        Errors = Rank(r.Errors.Take(10), e => N(e.Count), e => e.Count, Palette.Warning);
        Tiles =
        [
            new StatTile("Tool calls", N(t.ToolCalls), $"{Pct(t.ToolErrorRate)} failed"),
            new StatTile("Denied", N(t.ToolDenials), "by you or a rule"),
            new StatTile("Interruptions", N(t.Interrupts), "you stopped the agent"),
            new StatTile("Compactions", N(t.Compactions), "context filled up"),
            new StatTile("API errors", N(t.ApiErrors), "retried or failed"),
        ];
    }

    private static List<RankRow> Rank(IEnumerable<BreakdownRow> rows, Func<BreakdownRow, string> value, Func<BreakdownRow, double> measure, Brush brush, Func<BreakdownRow, string?>? detail = null)
    {
        var list = rows.ToList();
        var max = list.Count == 0 ? 0 : list.Max(measure);
        return [.. list.Select(r => new RankRow(r.Label, value(r), max == 0 ? 0 : measure(r) / max, brush, detail?.Invoke(r)))];
    }
}

public sealed partial class ActivityViewModel : PageViewModel
{
    public override Page Page => Page.Activity;

    public override string Title => "Activity";

    public override string Glyph => "";

    private DojoReport? _report;

    /// <summary>0 = spend, 1 = prompts.</summary>
    [ObservableProperty]
    private int _metricIndex;

    [ObservableProperty]
    private IReadOnlyList<HeatmapCell> _cells = [];

    [ObservableProperty]
    private IReadOnlyList<BarColumn> _promptColumns = [];

    [ObservableProperty]
    private IReadOnlyList<PromptItem> _topPrompts = [];

    [ObservableProperty]
    private IReadOnlyList<StatTile> _tiles = [];

    public IReadOnlyList<string> PromptSeries { get; } = ["Prompts"];

    public IReadOnlyList<Brush> PromptBrushes { get; } = [Palette.Blue];

    partial void OnMetricIndexChanged(int value) => BuildCells();

    public override void Update(DojoSnapshot snapshot, IReadOnlyList<ProviderLimits> limits, DateTimeOffset now)
    {
        _report = snapshot.Report;
        BuildCells();
        var r = snapshot.Report;
        PromptColumns = [.. r.Series.Select(p => new BarColumn(
            r.Hourly ? Display.Format(p.Start, "HH:mm") : Display.Format(p.Start, "MMM d"),
            r.Hourly ? Display.Format(p.Start, "ddd HH:00") : Display.Format(p.Start, "ddd MMM d"),
            [p.Prompts]))];
        TopPrompts = [.. r.TopPrompts.Select(p => new PromptItem(p))];

        var busiest = r.Heatmap.MaxBy(c => c.CostUsd);
        var activeDays = r.Series.Count(p => p.Calls > 0 || p.Prompts > 0);
        var turns = snapshot.Data.Turns.Where(x => x.Timestamp >= r.From && x.Timestamp <= r.To).Select(x => x.DurationMs).Order().ToList();
        Tiles =
        [
            new StatTile(r.Hourly ? "Active hours" : "Active days", N(activeDays), $"of {N(r.Series.Count)}"),
            new StatTile("Busiest slot", busiest is { CostUsd: > 0 } b ? $"{Day(b.Day)} {b.Hour:00}:00" : "—", busiest is { CostUsd: > 0 } bb ? Money(bb.CostUsd) + " spent" : null),
            new StatTile("Typical wait", turns.Count == 0 ? "—" : UsageFormat.Duration(turns[turns.Count / 2]), "median prompt → answer"),
            new StatTile("Longest turn", turns.Count == 0 ? "—" : UsageFormat.Duration(turns[^1])),
            new StatTile("Automated prompts", N(r.Totals.AutomatedPrompts), "scheduled, background, autopilot"),
        ];
    }

    private void BuildCells()
    {
        if (_report is null)
        {
            return;
        }

        Cells = [.. _report.Heatmap.Select(c =>
        {
            var row = ((int)c.Day + 6) % 7; // Monday first
            var value = MetricIndex == 0 ? c.CostUsd : c.Prompts;
            return new HeatmapCell(row, c.Hour, value, $"{Day(c.Day)} {c.Hour:00}:00–{c.Hour + 1:00}:00",
                $"{Money(c.CostUsd)} · {N(c.Prompts)} prompts · {N(c.Calls)} calls");
        })];
    }

    private static string Day(DayOfWeek day) => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(day);
}

public sealed partial class LimitsViewModel : PageViewModel
{
    public override Page Page => Page.Limits;

    public override string Title => "Limits";

    public override string Glyph => "";

    [ObservableProperty]
    private IReadOnlyList<ProviderLimitsItem> _providers = [];

    [ObservableProperty]
    private IReadOnlyList<LineSeries> _history = [];

    [ObservableProperty]
    private bool _hasHistory;

    [ObservableProperty]
    private string _checked = "Checking your limits…";

    public override void Update(DojoSnapshot snapshot, IReadOnlyList<ProviderLimits> limits, DateTimeOffset now)
    {
        Providers = [.. limits.Select(l => new ProviderLimitsItem(l, now))];
        Checked = limits.Count == 0 ? "Checking your limits…" : $"Checked at {Display.Format(now, "HH:mm")}. Live from your Claude Code and Copilot CLI sign-ins.";

        // Colour follows the agent: Claude windows in Claude orange first, Copilot in Copilot blue first.
        var claude = new[] { Palette.Claude, Palette.Violet, Palette.Aqua };
        var copilot = new[] { Palette.Copilot, Palette.Aqua, Palette.Violet };
        var from = snapshot.Report.From;
        var groups = snapshot.LimitHistory.Where(l => l.Timestamp >= from).GroupBy(l => (l.Provider, l.Window)).OrderBy(g => g.Key.Provider).ThenBy(g => g.Key.Window).ToList();
        History = [.. groups.Select(g =>
        {
            var index = groups.Where(x => x.Key.Provider == g.Key.Provider).ToList().IndexOf(g);
            var brushes = g.Key.Provider == Provider.Claude ? claude : copilot;
            return new LineSeries($"{g.Key.Provider} {g.Key.Window}", brushes[index % brushes.Length],
                [.. g.OrderBy(l => l.Timestamp).Select(l => new LinePoint(l.Timestamp, l.PercentUsed))]);
        })];
        HasHistory = History.Any(s => s.Points.Count > 1);
    }
}
