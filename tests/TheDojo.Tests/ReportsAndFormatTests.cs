using System.Text.Json;
using TheDojo.Core;
using TheDojo.Core.Analysis;
using TheDojo.Core.Insights;
using TheDojo.Core.Ingest;
using TheDojo.Core.Model;
using TheDojo.Core.Pricing;
using TheDojo.Core.Reports;

namespace TheDojo.Tests;

public sealed class ReportsAndFormatTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 18, 0, 0, TimeSpan.Zero);
    private static readonly CostCalculator Costs = new(new PriceBook());

    private static (DojoReport Report, CoachResult Coach, UsageDataset Data) Sample()
    {
        var data = UsageDataset.Merge([new SessionLog(
            [new SessionInfo(Provider.Claude, "s1", Title: "Fix | pipes", Cwd: @"E:\src\shop")],
            [new ApiCall(Provider.Claude, Now.AddHours(-1), "s1", "c1", "claude-sonnet-5-5", new TokenCounts(10, 1_000, 50_000), Tools: ["Read", "Edit"])],
            [new ToolCall(Provider.Claude, Now.AddHours(-1), "s1", "t1", "Bash", ToolOutcome.Error, "dotnet", ErrorText: "error, \"quoted\"\nnext line")],
            [new PromptEvent(Provider.Claude, Now.AddHours(-2), "s1", "p1", PromptKind.Typed, 20, "secret project plans")],
            [], [], [], [])]);
        var report = DojoAnalyzer.Build(data, Costs, new ReportFilter(UsageRange.Days7), Now, TimeZoneInfo.Utc);
        return (report, InsightEngine.Analyze(report, data, Costs, null, Now), data);
    }

    [Fact]
    public void Markdown_HasTheSummaryAndTables_AndLeavesPromptsOutUnlessAsked()
    {
        var (report, coach, _) = Sample();

        var md = MarkdownReport.Write(report, coach, zone: TimeZoneInfo.Utc);

        Assert.StartsWith("# The Dojo", md);
        Assert.Contains("**Period:** 2026-09-29 00:00 → 2026-10-05 18:00 (7 days)", md);
        Assert.Contains("| Spend | $0.02 |", md);
        Assert.Contains("## Models", md);
        Assert.Contains("Fix \\| pipes", md); // pipes in cells are escaped
        Assert.DoesNotContain("secret project plans", md);
        Assert.Contains("secret project plans", MarkdownReport.Write(report, coach, includePrompts: true, zone: TimeZoneInfo.Utc));
    }

    [Fact]
    public void Json_IsValidAndCamelCased()
    {
        var (report, coach, _) = Sample();

        using var doc = JsonDocument.Parse(JsonReport.Write(report, coach));

        Assert.Equal("The Dojo", doc.RootElement.GetProperty("generator").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("report").GetProperty("totals").GetProperty("calls").GetInt32());
        Assert.Equal("days7", doc.RootElement.GetProperty("report").GetProperty("filter").GetProperty("range").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("report").GetProperty("topPrompts").GetArrayLength());
    }

    [Fact]
    public void Csv_QuotesFieldsThatNeedIt_AndHasARowPerRecord()
    {
        var (report, _, data) = Sample();

        var calls = CsvExport.Calls(data, Costs, report).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var tools = CsvExport.Tools(data, report);
        var sessions = CsvExport.Sessions(report).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, calls.Length);
        Assert.StartsWith("timestamp,provider,session,project,model,input,output", calls[0]);
        Assert.Contains("claude-sonnet-5-5,10,1000,50000", calls[1]);
        Assert.Contains("Read;Edit", calls[1]);
        Assert.Contains("\"error, \"\"quoted\"\"\nnext line\"", tools);
        Assert.Equal(2, sessions.Length);
        Assert.Contains("\"Fix | pipes\"".Replace("\"", string.Empty), sessions[1]);
    }

    [Theory]
    [InlineData(476_000_000, "476M")]
    [InlineData(6_460, "6.46K")]
    [InlineData(47_600_000, "47.6M")]
    [InlineData(0, "0")]
    [InlineData(1_234_000_000, "1.23B")]
    [InlineData(-2_500, "-2.5K")]
    public void Compact_KeepsThreeSignificantDigits(double value, string expected) => Assert.Equal(expected, UsageFormat.Compact(value));

    [Theory]
    [InlineData(1080.76, "$1,080.76")]
    [InlineData(0.004, "$0.004")]
    [InlineData(0, "$0.00")]
    [InlineData(-3.5, "-$3.50")]
    public void Money_IsCultureIndependent(double usd, string expected) => Assert.Equal(expected, UsageFormat.Money(usd));

    [Theory]
    [InlineData(45, "45s")]
    [InlineData(12 * 60, "12m")]
    [InlineData(2 * 3600 + 3 * 60, "2h 3m")]
    [InlineData(3 * 86400 + 4 * 3600, "3d 4h")]
    [InlineData(0, "0s")]
    public void Duration_IsShort(int seconds, string expected) => Assert.Equal(expected, UsageFormat.Duration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Percent_AndTrend()
    {
        Assert.Equal("98%", UsageFormat.Percent(0.981));
        Assert.Equal("3.2%", UsageFormat.Percent(0.032));
        Assert.Equal("+25%", UsageFormat.Trend(0.25));
        Assert.Equal("-8%", UsageFormat.Trend(-0.08));
    }

    [Theory]
    [InlineData("cd src && dotnet test", "dotnet")]
    [InlineData("FOO=1 npm run build | tee log", "npm")]
    [InlineData("\"C:\\Program Files\\Git\\bin\\git.exe\" status", "git")]
    [InlineData("Set-Location x; Get-ChildItem", "Get-ChildItem")]
    [InlineData("   ", null)]
    public void ShellProgram_IsTheCommandThatRuns(string command, string? expected) => Assert.Equal(expected, ToolTargets.Program(command));

    [Theory]
    [InlineData("mcp__jira__create_issue", "jira")]
    [InlineData("mcp__ccd_session__mark_chapter", "ccd_session")]
    [InlineData("Bash", null)]
    public void McpServer_ComesFromTheToolName(string tool, string? server) => Assert.Equal(server, ToolTargets.McpServer(tool));
}
