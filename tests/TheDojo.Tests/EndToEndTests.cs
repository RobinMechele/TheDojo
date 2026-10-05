using Microsoft.Extensions.Time.Testing;
using TheDojo.Cli;
using TheDojo.Core;
using TheDojo.Core.Analysis;
using TheDojo.Core.Ingest;
using TheDojo.Core.Limits;
using TheDojo.Core.Model;
using TheDojo.Core.Pricing;
using TheDojo.Tests.Fixtures;

namespace TheDojo.Tests;

/// <summary>The whole pipeline (logs on disk → scan → price → analyze → coach → report) on the sample world, and the CLI on top of it.</summary>
public sealed class EndToEndTests : IDisposable
{
    private readonly SampleWorld _world = new();

    public void Dispose() => _world.Dispose();

    private DojoService Service(PlanLimitsService? limits = null) => new(
        new LogScanner(_world.ClaudeProjects, "*.jsonl", ClaudeTranscriptParser.Parse, Path.Combine(_world.Cache, "claude.json.gz")),
        new LogScanner(_world.CopilotSessions, "events.jsonl", CopilotEventsParser.Parse, Path.Combine(_world.Cache, "copilot.json.gz")),
        _ => Task.FromResult(new PriceBook()),
        new FakeTimeProvider(SampleWorld.Now),
        limits,
        null,
        TimeZoneInfo.Utc);

    [Fact]
    public async Task SampleWorld_IsReadPricedAndCoached()
    {
        var snapshot = await Service().LoadAsync(new ReportFilter(UsageRange.Days30), null, CancellationToken.None);
        var r = snapshot.Report;

        Assert.False(r.IsEmpty);
        Assert.Contains(r.Models, m => m.Provider == Provider.Copilot);
        Assert.Contains(r.Sessions, s => s.SessionId == "sess-marathon" && s.Compactions == 2 && s.PeakContext > 400_000);
        Assert.Contains(r.Projects, p => p.Project == "shop");
        Assert.DoesNotContain(r.Projects, p => p.Project == "swift-maple"); // the worktree counts as shop
        Assert.True(r.Totals.ToolErrors > 0);
        Assert.True(r.Totals.PullRequests > 0);
        Assert.Contains(r.Subagents, s => s.Label == "Explore" && s.CostUsd > 0);
        Assert.NotEmpty(snapshot.LimitHistory); // from the /usage commands in the transcripts
        Assert.InRange(snapshot.Coach.Score, 1, 100);
        Assert.Contains(snapshot.Coach.Insights, i => i.Id == "context-bloat");

        // Every dollar lands in exactly one bucket of every breakdown.
        Assert.Equal(r.Totals.CostUsd, r.Series.Sum(p => p.CostUsd), 6);
        Assert.Equal(r.Totals.CostUsd, r.Models.Sum(m => m.CostUsd), 6);
        Assert.Equal(r.Totals.CostUsd, r.Sessions.Sum(s => s.CostUsd), 6);
        Assert.Equal(r.Totals.CostUsd, r.Heatmap.Sum(c => c.CostUsd), 6);
    }

    [Fact]
    public async Task SecondLoad_ComesFromTheCache_AndFiltersNarrowIt()
    {
        var service = Service();
        var all = await service.LoadAsync(new ReportFilter(UsageRange.Days30), null, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(_world.Cache, "claude.json.gz")));

        var copilot = await Service().LoadAsync(new ReportFilter(UsageRange.Days30, Provider.Copilot), null, CancellationToken.None);
        var billing = await service.LoadAsync(new ReportFilter(UsageRange.Days30, null, "billing-api"), null, CancellationToken.None);

        Assert.All(copilot.Report.Sessions, s => Assert.Equal(Provider.Copilot, s.Provider));
        Assert.All(billing.Report.Sessions, s => Assert.Equal("billing-api", s.Project));
        Assert.True(billing.Report.Totals.CostUsd < all.Report.Totals.CostUsd);
    }

    [Fact]
    public async Task Cli_Summary_PrintsTheNumbersAndLessons()
    {
        var output = new StringWriter();
        var code = await DojoCli.RunAsync(["summary", "--range", "30d"], output, new StringWriter(), CancellationToken.None, _ => Service());

        Assert.Equal(0, code);
        var text = output.ToString();
        Assert.Contains("The Dojo · 30 days", text);
        Assert.Contains("Spend", text);
        Assert.Contains("claude-opus-5-5", text);
        Assert.Contains("Lessons", text);
    }

    [Fact]
    public async Task Cli_Report_AndExport_WriteFiles()
    {
        var md = Path.Combine(_world.Root, "out", "report.md");
        var csv = Path.Combine(_world.Root, "out", "sessions.csv");

        Assert.Equal(0, await DojoCli.RunAsync(["report", "--out", md, "--offline"], new StringWriter(), new StringWriter(), CancellationToken.None, _ => Service()));
        Assert.Equal(0, await DojoCli.RunAsync(["export", "sessions", "--out", csv], new StringWriter(), new StringWriter(), CancellationToken.None, _ => Service()));

        Assert.Contains("## Lessons", File.ReadAllText(md));
        Assert.True(File.ReadAllLines(csv).Length > 10);
    }

    [Theory]
    [InlineData("summary", "--range", "yesterday")]
    [InlineData("summary", "--provider", "gemini")]
    [InlineData("summary", "--bogus")]
    [InlineData("dance")]
    public async Task Cli_BadArguments_ExitWithUsage(params string[] args)
    {
        var error = new StringWriter();

        var code = await DojoCli.RunAsync(args, new StringWriter(), error, CancellationToken.None, _ => Service());

        Assert.Equal(2, code);
        Assert.Contains("Usage:", error.ToString());
    }

    [Fact]
    public void Cli_ParsesEveryOption()
    {
        var o = DojoCli.Parse(["report", "--range", "all", "--provider", "COPILOT", "--project", "shop", "--format", "json", "--out", "x.json", "--prompts", "--offline", "--claude-home", "c", "--copilot-home", "p", "--cache", "k"]);

        Assert.Equal(("report", UsageRange.All, Provider.Copilot, "shop"), (o.Command, o.Filter.Range, o.Filter.Provider, o.Filter.Project));
        Assert.Equal(("json", "x.json", true, true), (o.Format, o.Out, o.Prompts, o.Offline));
        Assert.Equal(("c", "p", "k"), (o.ClaudeHome, o.CopilotHome, o.Cache));
        Assert.Equal("summary", DojoCli.Parse([]).Command);
    }
}

/// <summary>
/// Against this machine's real logs, local only (<c>DOJO_REAL_DATA=1</c>): the estimates must match Claude Code's
/// own cost for the sessions that report it.
/// </summary>
public sealed class RealDataTests
{
    [Fact]
    public async Task Estimates_MatchClaudeCodesOwnSessionCost()
    {
        if (Environment.GetEnvironmentVariable("DOJO_REAL_DATA") is not ("1" or "true"))
        {
            return;
        }

        var root = Path.Combine(AppIdentity.ClaudeHome, "projects");
        var data = UsageDataset.Merge(await new LogScanner(root, "*.jsonl", ClaudeTranscriptParser.Parse).ScanAsync(DateTimeOffset.MinValue, CancellationToken.None));
        var costs = new CostCalculator(new PriceBook());
        var compared = data.Sessions.Values
            .Where(s => s.ReportedCostUsd > 0.01)
            .Select(s => (Reported: s.ReportedCostUsd!.Value, Mine: data.Calls.Where(c => c.SessionId == s.SessionId).Sum(c => costs.Cost(c).Usd)))
            .Where(x => x.Mine > 0)
            .ToList();

        Assert.NotEmpty(compared);
        var close = compared.Count(x => Math.Abs(x.Mine - x.Reported) <= Math.Max(0.01, x.Reported * 0.05));
        Assert.True(close >= compared.Count * 0.8, $"{close} of {compared.Count} sessions within 5%");
    }
}
