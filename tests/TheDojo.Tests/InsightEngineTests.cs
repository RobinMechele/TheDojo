using TheDojo.Core.Analysis;
using TheDojo.Core.Insights;
using TheDojo.Core.Limits;
using TheDojo.Core.Model;
using TheDojo.Core.Pricing;

namespace TheDojo.Tests;

public sealed class InsightEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 18, 0, 0, TimeSpan.Zero);
    private static readonly CostCalculator Costs = new(new PriceBook());
    private int _id;

    private ApiCall Call(string session, DateTimeOffset at, string model = "claude-sonnet-5-5", long input = 10, long output = 500, long read = 20_000, long write5m = 0, long write1h = 0, string? agent = null) =>
        new(Provider.Claude, at, session, "c" + ++_id, model, new TokenCounts(input, output, read, write5m, write1h), AgentId: agent);

    private PromptEvent Prompt(string session, DateTimeOffset at, string text = "Please refactor the payment module and keep the API stable") =>
        new(Provider.Claude, at, session, "p" + ++_id, PromptKind.Typed, text.Length, text);

    private ToolCall Tool(string session, DateTimeOffset at, string name, ToolOutcome outcome = ToolOutcome.Success, string? target = null, string? denial = null) =>
        new(Provider.Claude, at, session, "t" + ++_id, name, outcome, target, DenialKind: denial);

    private static CoachResult Coach(
        IEnumerable<ApiCall>? calls = null,
        IEnumerable<PromptEvent>? prompts = null,
        IEnumerable<ToolCall>? tools = null,
        IEnumerable<CompactionEvent>? compactions = null,
        IEnumerable<ErrorEvent>? errors = null,
        IReadOnlyList<ProviderLimits>? limits = null,
        UsageRange range = UsageRange.Days30)
    {
        var data = UsageDataset.Merge([new SessionLog([], [.. calls ?? []], [.. tools ?? []], [.. prompts ?? []], [], [.. compactions ?? []], [.. errors ?? []], [])]);
        var report = DojoAnalyzer.Build(data, Costs, new ReportFilter(range), Now, TimeZoneInfo.Utc);
        return InsightEngine.Analyze(report, data, Costs, limits, Now);
    }

    [Fact]
    public void NoActivity_NoLessonsAndNoScore()
    {
        var coach = Coach();

        Assert.Empty(coach.Insights);
        Assert.Equal(0, coach.Score);
        Assert.Equal("White", coach.Belt);
    }

    [Theory]
    [InlineData(95, "Black")]
    [InlineData(80, "Brown")]
    [InlineData(72, "Blue")]
    [InlineData(65, "Green")]
    [InlineData(50, "Orange")]
    [InlineData(41, "Yellow")]
    [InlineData(10, "White")]
    public void Belts_FollowTheScore(int score, string belt) => Assert.Equal(belt, CoachResult.BeltFor(score));

    [Fact]
    public void ContextBloat_IsAWarning_WithAnEstimatedSaving()
    {
        var calls = Enumerable.Range(0, 30).Select(i => Call("s1", Now.AddHours(-1).AddMinutes(i), "claude-opus-5-5", read: 300_000 + i * 10_000)).ToList();

        var coach = Coach(calls, [Prompt("s1", Now.AddHours(-2))]);

        var lesson = Assert.Single(coach.Insights, i => i.Id == "context-bloat");
        Assert.Equal(InsightSeverity.Warning, lesson.Severity);
        Assert.True(lesson.SavingsUsd > 0);
        Assert.Contains(coach.Parts, p => p.Name == "Context discipline" && p.Value == 0);
    }

    [Fact]
    public void QuickQuestionsOnAPremiumModel_SuggestACheaperDefault()
    {
        var calls = new List<ApiCall>();
        var prompts = new List<PromptEvent>();
        for (var i = 0; i < 10; i++)
        {
            var at = Now.AddHours(-i - 1);
            prompts.Add(Prompt("s" + i, at, "what does this function do?"));
            calls.Add(Call("s" + i, at.AddSeconds(5), "claude-fable-5-1", input: 20_000, output: 400, read: 60_000));
        }

        var lesson = Assert.Single(Coach(calls, prompts).Insights, i => i.Id == "premium-small-turns");

        Assert.Contains("10 quick questions", lesson.Title);
        Assert.True(lesson.SavingsUsd > 1);
    }

    [Fact]
    public void BigTurnsOnAPremiumModel_AreLeftAlone()
    {
        var calls = new List<ApiCall>();
        var prompts = new List<PromptEvent>();
        for (var i = 0; i < 10; i++)
        {
            var at = Now.AddHours(-i - 1);
            prompts.Add(Prompt("s" + i, at));
            calls.AddRange(Enumerable.Range(0, 8).Select(k => Call("s" + i, at.AddSeconds(5 + k), "claude-opus-5-5", output: 3_000, read: 60_000)));
        }

        Assert.DoesNotContain(Coach(calls, prompts).Insights, i => i.Id == "premium-small-turns");
    }

    [Fact]
    public void SubagentsOnAPremiumModel_SuggestAPerAgentModel()
    {
        var calls = Enumerable.Range(0, 40).Select(i => Call("s1", Now.AddMinutes(-i), "claude-opus-5-5", input: 20_000, output: 2_000, read: 100_000, agent: "a" + (i % 4))).ToList();

        var lesson = Assert.Single(Coach(calls).Insights, i => i.Id == "subagent-models");

        Assert.Contains("4 subagents", lesson.Title);
        Assert.Contains("model: sonnet", lesson.Action);
    }

    [Fact]
    public void BreaksLongerThanTheCacheLifetime_AreCountedAsRewrites()
    {
        var calls = new List<ApiCall>();
        var at = Now.AddDays(-1);
        for (var i = 0; i < 5; i++)
        {
            calls.Add(Call("s1", at, "claude-opus-5-5", read: 200_000));
            at = at.AddMinutes(30); // longer than the 5-minute cache
            calls.Add(Call("s1", at, "claude-opus-5-5", read: 0, write5m: 200_000));
            at = at.AddMinutes(1);
        }

        var lesson = Assert.Single(Coach(calls).Insights, i => i.Id == "cache-rewrites");

        Assert.StartsWith("5 breaks", lesson.Title);
    }

    [Fact]
    public void GoodCaching_IsAStrength()
    {
        var calls = Enumerable.Range(0, 40).Select(i => Call("s1", Now.AddMinutes(-i), read: 100_000, input: 100)).ToList();

        var coach = Coach(calls);

        Assert.Contains(coach.Insights, i => i is { Id: "cache-strong", Severity: InsightSeverity.Strength });
        Assert.Equal(InsightSeverity.Strength, coach.Insights[^1].Severity); // strengths come last
    }

    [Fact]
    public void FailingShellCommands_NameTheProgramsThatFail()
    {
        var tools = Enumerable.Range(0, 20).Select(i => Tool("s1", Now.AddMinutes(-i), "Bash", i < 8 ? ToolOutcome.Error : ToolOutcome.Success, i < 6 ? "npm" : "git")).ToList();

        var lesson = Assert.Single(Coach(prompts: [Prompt("s1", Now.AddHours(-1))], tools: tools).Insights, i => i.Id == "tool-failures-Bash");

        Assert.Contains("npm (6)", lesson.Action);
        Assert.Contains("8 of 20", lesson.Detail);
    }

    [Fact]
    public void ManyInterruptionsAndDenials_SuggestPlanningFirst_OrAllowingSafeCommands()
    {
        var prompts = Enumerable.Range(0, 20).Select(i => Prompt("s1", Now.AddHours(-i - 1))).ToList();
        var denied = Enumerable.Range(0, 10).Select(i => Tool("s1", Now.AddHours(-i - 1).AddMinutes(1), "Bash", ToolOutcome.Denied, denial: "permission-rule")).ToList();

        var lesson = Assert.Single(Coach(prompts: prompts, tools: denied).Insights, i => i.Id == "overrides");

        Assert.Contains("10 calls were blocked by permission rules", lesson.Action);
    }

    [Fact]
    public void ReReadingTheSameFiles_IsSpotted()
    {
        var tools = Enumerable.Range(0, 30).Select(i => Tool("s1", Now.AddMinutes(-i), "Read", target: @"E:\src\Ledger.cs")).ToList();

        var lesson = Assert.Single(Coach(prompts: [Prompt("s1", Now.AddHours(-1))], tools: tools).Insights, i => i.Id == "file-rereads");

        Assert.Contains("Ledger.cs", lesson.Detail);
        Assert.Contains("29 extra reads", lesson.Title);
    }

    [Fact]
    public void SessionsCompactedTwice_SuggestSplittingWork()
    {
        var compactions = new[]
        {
            new CompactionEvent(Provider.Claude, Now.AddHours(-3), "s1", "c1", false, 900_000, 30_000),
            new CompactionEvent(Provider.Claude, Now.AddHours(-2), "s1", "c2", false, 900_000, 30_000),
        };

        Assert.Single(Coach(prompts: [Prompt("s1", Now.AddHours(-4))], compactions: compactions).Insights, i => i.Id == "long-sessions");
    }

    [Fact]
    public void ALimitOnPaceToRunOut_IsAWarning()
    {
        var limits = new[]
        {
            new ProviderLimits(Provider.Claude, "Max",
            [
                new LimitWindow("Weekly", 83, Now.AddDays(2), TimeSpan.FromDays(7)), // 5/7 elapsed (71%), 83% used: ahead
                new LimitWindow("Session", 20, Now.AddHours(1), TimeSpan.FromHours(5)), // on track
            ]),
        };

        var lesson = Assert.Single(Coach(limits: limits).Insights);

        Assert.Equal(InsightSeverity.Warning, lesson.Severity);
        Assert.Contains("weekly limit: 83% used", lesson.Title);
    }

    [Fact]
    public void SpendJump_IsCalledOut()
    {
        var calls = new[] { Call("s1", Now.AddDays(-1), "claude-opus-5-5", input: 0, read: 0, output: 1_000_000), Call("s0", Now.AddDays(-9), "claude-opus-5-5", input: 0, read: 0, output: 100_000) };

        var lesson = Assert.Single(Coach(calls, range: UsageRange.Days7).Insights, i => i.Id == "spend-up");

        Assert.Contains("900%", lesson.Title);
    }

    [Fact]
    public void Score_WeighsTheDisciplines()
    {
        var calls = Enumerable.Range(0, 20).Select(i => Call("s1", Now.AddMinutes(-i), read: 90_000, input: 10_000)).ToList();
        var tools = Enumerable.Range(0, 10).Select(i => Tool("s1", Now.AddMinutes(-i), "Read")).ToList();

        var coach = Coach(calls, [Prompt("s1", Now.AddHours(-1))], tools);

        Assert.Equal(["Caching", "Context discipline", "Model fit", "Tool success", "Flow"], coach.Parts.Select(p => p.Name));
        Assert.Equal(100, coach.Score); // 90% cache hit earns full marks; nothing else lost points
        Assert.Equal("Black", coach.Belt);
    }
}
