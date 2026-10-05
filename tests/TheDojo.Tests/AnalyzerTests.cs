using TheDojo.Core.Analysis;
using TheDojo.Core.Model;
using TheDojo.Core.Pricing;

namespace TheDojo.Tests;

public sealed class AnalyzerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 18, 0, 0, TimeSpan.Zero);
    private static readonly CostCalculator Costs = new(new PriceBook());

    private static ApiCall Call(string session, DateTimeOffset at, string model = "claude-sonnet-5-5", long input = 0, long output = 0, long read = 0, long write = 0,
        string? agent = null, Provider provider = Provider.Claude, double? cost = null, string? effort = null, bool aggregate = false) =>
        new(provider, at, session, Guid.NewGuid().ToString(), model, new TokenCounts(input, output, read, write), cost, aggregate, Effort: effort, AgentId: agent, AgentType: agent is null ? null : "Explore");

    private static PromptEvent Prompt(string session, DateTimeOffset at, string text = "do it", PromptKind kind = PromptKind.Typed, Provider provider = Provider.Claude) =>
        new(provider, at, session, Guid.NewGuid().ToString(), kind, text.Length, text, kind == PromptKind.Command ? text : null);

    private static ToolCall Tool(string session, DateTimeOffset at, string name = "Bash", ToolOutcome outcome = ToolOutcome.Success, long ms = 1000, string? target = null, int added = 0) =>
        new(Provider.Claude, at, session, Guid.NewGuid().ToString(), name, outcome, target, DurationMs: ms, LinesAdded: added);

    private static UsageDataset Data(
        IEnumerable<ApiCall>? calls = null,
        IEnumerable<PromptEvent>? prompts = null,
        IEnumerable<ToolCall>? tools = null,
        IEnumerable<TurnEvent>? turns = null,
        IEnumerable<SessionInfo>? sessions = null,
        IEnumerable<ErrorEvent>? errors = null,
        IEnumerable<CompactionEvent>? compactions = null) =>
        UsageDataset.Merge([new SessionLog([.. sessions ?? []], [.. calls ?? []], [.. tools ?? []], [.. prompts ?? []], [.. turns ?? []], [.. compactions ?? []], [.. errors ?? []], [])]);

    private static DojoReport Build(UsageDataset data, UsageRange range = UsageRange.Days30, Provider? provider = null, string? project = null) =>
        DojoAnalyzer.Build(data, Costs, new ReportFilter(range, provider, project), Now, TimeZoneInfo.Utc);

    [Fact]
    public void Totals_SumTokensCostAndCacheSavings_AndCountOnlyRealCalls()
    {
        var data = Data(
            calls:
            [
                Call("s1", Now.AddHours(-1), input: 1_000_000, output: 100_000, read: 2_000_000), // 2 + 1 + 0.4
                Call("s2", Now.AddDays(-2), provider: Provider.Copilot, model: "gpt-6-luna", cost: 0.5, aggregate: true),
                Call("s3", Now.AddDays(-40), output: 1_000_000), // outside 30 days
            ],
            prompts: [Prompt("s1", Now.AddHours(-1).AddSeconds(-5)), Prompt("s1", Now.AddMinutes(-30), "/model", PromptKind.Command), Prompt("s1", Now.AddMinutes(-20), "ping", PromptKind.Automated)]);

        var t = Build(data).Totals;

        Assert.Equal(3.9, t.CostUsd, 9);
        Assert.Equal(1, t.Calls);
        Assert.Equal(2, t.Sessions);
        Assert.Equal(2, t.Prompts);
        Assert.Equal(1, t.AutomatedPrompts);
        Assert.Equal(2_000_000 * (2.0 - 0.2) / 1e6, t.CacheSavingsUsd, 9);
        Assert.Equal(2_000_000 / 3_000_000.0, t.CacheHitRate, 9);
    }

    [Fact]
    public void Trend_ComparesWithThePreviousPeriodOfTheSameLength()
    {
        var data = Data(calls: [Call("s1", Now.AddDays(-1), output: 100_000), Call("s0", Now.AddDays(-10), output: 50_000)]);

        var t = Build(data, UsageRange.Days7).Totals;

        Assert.Equal(1.0, t.CostUsd, 9);
        Assert.Equal(0.5, t.PreviousCostUsd, 9);
        Assert.Equal(1.0, t.CostTrend!.Value, 9);
    }

    [Fact]
    public void Series_HasABucketForEveryDay_SplitByAgent()
    {
        var data = Data(calls: [Call("s1", Now.AddHours(-1), output: 100_000), Call("c1", Now.AddDays(-3), provider: Provider.Copilot, model: "gpt", cost: 0.25)]);

        var report = Build(data);

        Assert.False(report.Hourly);
        Assert.Equal(30, report.Series.Count);
        Assert.Equal(1.0, report.Series[^1].ClaudeCostUsd, 9);
        Assert.Equal(0.25, report.Series[^4].CopilotCostUsd, 9);
        Assert.Equal(1.25, report.Series.Sum(p => p.CostUsd), 9);
    }

    [Fact]
    public void Past24Hours_BucketsByHour_EndingWithTheCurrentHour()
    {
        var report = Build(Data(calls: [Call("s1", Now.AddMinutes(-30), output: 10)]), UsageRange.Past24Hours);

        Assert.True(report.Hourly);
        Assert.Equal(24, report.Series.Count);
        Assert.Equal(17, report.Series[^2].Start.Hour);
        Assert.Equal(10, report.Series[^2].Tokens);
    }

    [Fact]
    public void All_StartsAtTheFirstDayWithData()
    {
        var report = Build(Data(prompts: [Prompt("s1", Now.AddDays(-100))], calls: [Call("s1", Now.AddDays(-50), output: 1)]), UsageRange.All);

        Assert.Equal(Now.AddDays(-100).Date, report.From.Date);
        Assert.Equal(101, report.Series.Count);
    }

    [Fact]
    public void Models_AreRankedBySpend_WithShareAverageContextAndSubagentCalls()
    {
        var data = Data(calls:
        [
            Call("s1", Now.AddHours(-1), "claude-opus-5-5", input: 10, read: 99_990, output: 1_000),
            Call("s1", Now.AddHours(-1), "claude-opus-5-5", input: 10, read: 199_990, output: 1_000, agent: "a1"),
            Call("s1", Now.AddHours(-1), "claude-haiku-4-5", output: 10),
        ]);

        var models = Build(data).Models;

        Assert.Equal(["claude-opus-5-5", "claude-haiku-4-5"], models.Select(m => m.Model));
        Assert.Equal(2, models[0].Calls);
        Assert.Equal(1, models[0].SubagentCalls);
        Assert.Equal(150_000, models[0].AverageContext);
        Assert.Equal(1.0, models.Sum(m => m.Share), 9);
        Assert.Equal("Opus", models[0].Family);
    }

    [Fact]
    public void Sessions_UseTurnTimeForActiveTime_AndPeakMainThreadContext()
    {
        var info = new SessionInfo(Provider.Claude, "s1", Title: "Fix it", Cwd: @"E:\src\shop", Branch: "main", PullRequests: ["p1", "p2"]);
        var data = Data(
            sessions: [info],
            calls: [Call("s1", Now.AddHours(-2), read: 100_000), Call("s1", Now.AddHours(-1), read: 300_000, agent: "a1"), Call("s1", Now.AddMinutes(-50), read: 150_000)],
            prompts: [Prompt("s1", Now.AddHours(-2).AddSeconds(-1))],
            tools: [Tool("s1", Now.AddHours(-1), "Edit", added: 7), Tool("s1", Now.AddHours(-1), outcome: ToolOutcome.Error)],
            turns: [new TurnEvent(Provider.Claude, Now.AddHours(-1), "s1", "t1", 90_000, 10)],
            compactions: [new CompactionEvent(Provider.Claude, Now.AddHours(-1), "s1", "c1", false, 500_000, 20_000)]);

        var s = Assert.Single(Build(data).Sessions);

        Assert.Equal(("Fix it", "shop", "main"), (s.Title, s.Project, s.Branch));
        Assert.Equal(90_000, s.ActiveMs);
        Assert.Equal(150_000, s.PeakContext); // the subagent's 300K is its own context, not the session's
        Assert.Equal((2, 1, 1, 7, 2), (s.ToolCalls, s.ToolErrors, s.Compactions, s.LinesAdded, s.PullRequests));
    }

    [Fact]
    public void ActiveTime_WithoutTurnRecords_CountsOnlyShortGaps()
    {
        var times = new[] { Now, Now.AddMinutes(2), Now.AddMinutes(4), Now.AddHours(3), Now.AddHours(3).AddMinutes(1) };

        Assert.Equal((long)TimeSpan.FromMinutes(5).TotalMilliseconds, DojoAnalyzer.EstimateActiveMs(times));
    }

    [Fact]
    public void Prompts_OwnTheCallsAndToolsUntilTheNextPrompt()
    {
        var data = Data(
            prompts: [Prompt("s1", Now.AddHours(-3), "big refactor"), Prompt("s1", Now.AddHours(-1), "small fix")],
            calls:
            [
                Call("s1", Now.AddHours(-4), output: 1_000_000), // before the first prompt: nobody's
                Call("s1", Now.AddHours(-3), output: 100_000),
                Call("s1", Now.AddHours(-2), output: 100_000, agent: "a1"),
                Call("s1", Now.AddMinutes(-30), output: 10_000),
            ],
            tools: [Tool("s1", Now.AddHours(-2)), Tool("s1", Now.AddMinutes(-20))]);

        var top = Build(data).TopPrompts;

        Assert.Equal(["big refactor", "small fix"], top.Select(p => p.Preview));
        Assert.Equal((2, 2.0, 1), (top[0].Calls, top[0].CostUsd, top[0].ToolCalls));
        Assert.Equal((1, 0.1, 1), (top[1].Calls, Math.Round(top[1].CostUsd, 9), top[1].ToolCalls));
    }

    [Fact]
    public void Tools_HaveErrorRatesAndDurationPercentiles()
    {
        var tools = Enumerable.Range(1, 20).Select(i => Tool("s1", Now.AddMinutes(-i), "Bash", i <= 5 ? ToolOutcome.Error : ToolOutcome.Success, ms: i * 100, target: i % 2 == 0 ? "git" : "dotnet"))
            .Append(Tool("s1", Now.AddMinutes(-1), "Read", ToolOutcome.Denied));

        var report = Build(Data(prompts: [Prompt("s1", Now.AddHours(-1))], tools: tools));

        var bash = report.Tools.First();
        Assert.Equal(("Bash", 20, 5, 0.25), (bash.Name, bash.Calls, bash.Errors, bash.ErrorRate));
        Assert.Equal(1050, bash.AverageMs);
        Assert.Equal(1900, bash.P95Ms);
        Assert.Equal(1, report.Totals.ToolDenials);
        Assert.Equal(["dotnet", "git"], report.Programs.Select(p => p.Label).Order());
        Assert.Equal(3, report.Programs.Single(p => p.Label == "dotnet").Errors);
    }

    [Fact]
    public void Filters_ByAgentAndProject()
    {
        var data = Data(
            sessions: [new SessionInfo(Provider.Claude, "s1", Cwd: @"E:\src\shop"), new SessionInfo(Provider.Claude, "s2", Cwd: @"E:\src\api"), new SessionInfo(Provider.Copilot, "c1", Cwd: @"E:\src\shop")],
            calls: [Call("s1", Now.AddHours(-1), output: 100_000), Call("s2", Now.AddHours(-1), output: 200_000), Call("c1", Now.AddHours(-1), provider: Provider.Copilot, model: "gpt", cost: 0.3)]);

        Assert.Equal(1.0, Build(data, project: "shop", provider: Provider.Claude).Totals.CostUsd, 9);
        Assert.Equal(1.3, Build(data, project: "SHOP").Totals.CostUsd, 9);
        Assert.Equal(0.3, Build(data, provider: Provider.Copilot).Totals.CostUsd, 9);
        Assert.Equal(["api", "shop"], Build(data, project: "shop").AllProjects);
    }

    [Fact]
    public void ContextBuckets_AndHeatmap_SpreadTheSpend()
    {
        var monday9 = new DateTimeOffset(2026, 10, 5, 9, 15, 0, TimeSpan.Zero);
        var report = Build(Data(
            calls: [Call("s1", monday9, read: 10_000, output: 1), Call("s1", monday9, read: 250_000, output: 1)],
            prompts: [Prompt("s1", monday9.AddMinutes(-1))]));

        Assert.Equal(1, report.ContextBuckets.Single(b => b.Label == "< 25K").Calls);
        Assert.Equal(1, report.ContextBuckets.Single(b => b.MinTokens == 200_000).Calls);
        var cell = report.Heatmap.Single(c => c is { Day: DayOfWeek.Monday, Hour: 9 });
        Assert.Equal((2, 1), (cell.Calls, cell.Prompts));
        Assert.Equal(7 * 24, report.Heatmap.Count);
    }

    [Fact]
    public void SessionDetail_HasATimelineOfContextGrowth()
    {
        var data = Data(calls: [Call("s1", Now.AddHours(-2), read: 50_000), Call("s1", Now.AddHours(-1), read: 90_000), Call("s1", Now.AddMinutes(-1), read: 1, agent: "a")],
            prompts: [Prompt("s1", Now.AddHours(-3))]);

        var detail = DojoAnalyzer.Session(data, Costs, "s1")!;

        Assert.Equal([50_000L, 90_000L, 1L], detail.Timeline.Select(p => p.Context));
        Assert.True(detail.Timeline[^1].IsSubagent);
        Assert.Single(detail.Prompts);
        Assert.Null(DojoAnalyzer.Session(data, Costs, "missing"));
    }

    [Fact]
    public void EmptyData_GivesAnEmptyReport()
    {
        var report = Build(UsageDataset.Empty);

        Assert.True(report.IsEmpty);
        Assert.Equal(0, report.Totals.CostPerPrompt);
        Assert.Equal(30, report.Series.Count);
    }
}
