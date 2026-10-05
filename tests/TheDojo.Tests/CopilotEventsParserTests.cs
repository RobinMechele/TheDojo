using System.Text.Json.Nodes;
using TheDojo.Core.Ingest;
using TheDojo.Core.Model;
using TheDojo.Tests.Fixtures;

namespace TheDojo.Tests;

public sealed class CopilotEventsParserTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 4, 10, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Directory.CreateTempSubdirectory("dojo-copilot-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private static SessionLog Parse(CopilotLog log) => CopilotEventsParser.ParseLines(log.Lines, log.SessionId);

    [Fact]
    public void PerCallEvents_GiveExactTokensCostAndQuota()
    {
        var log = new CopilotLog("s").Start(T0)
            .ModelCall(T0.AddSeconds(5), "c1", "claude-sonnet-4.6", 2_000, 18_000, 1_000, 600, 2_500_000_000, premiumLeft: 64);

        var parsed = Parse(log);

        var call = Assert.Single(parsed.Calls);
        Assert.Equal("claude-sonnet-4.6", call.Model);
        Assert.Equal(new TokenCounts(2_000, 600, 18_000, 1_000), call.Tokens);
        Assert.Equal(0.025, call.ReportedCostUsd!.Value, 9); // 2.5 AI credits at $0.01
        Assert.Equal((1800L, 600L), (call.DurationMs!.Value, call.FirstTokenMs!.Value));
        Assert.False(call.IsAggregate);
        var limit = Assert.Single(parsed.Limits);
        Assert.Equal(("Premium requests", 36.0), (limit.Window, limit.PercentUsed));
    }

    [Fact]
    public void PerCallEvents_KeepTheRunningTotalsTheyDidntLog_AsOneAggregate()
    {
        // Started in an older CLI (only a checkpoint), resumed in a newer one (per-call events).
        var log = new CopilotLog("s").Start(T0)
            .Checkpoint(T0.AddMinutes(1), 1_000_000_000)
            .ModelCall(T0.AddMinutes(2), "c1", "gpt-5.6-sol", 10, 0, 0, 5, 500_000_000)
            .Checkpoint(T0.AddMinutes(3), 1_500_000_000);

        var calls = Parse(log).Calls;

        Assert.Equal(0.015, calls.Sum(c => c.ReportedCostUsd ?? 0), 9);
        Assert.Single(calls, c => c.IsAggregate);
    }

    [Fact]
    public void Tools_HaveOutcomeDurationLinesAndSubagent()
    {
        var log = new CopilotLog("s")
            .Tool(T0, T0.AddSeconds(2), "t1", "apply_patch", new JsonObject { ["path"] = "a.ts" }, linesAdded: 12, linesRemoved: 4)
            .Tool(T0, T0.AddSeconds(1), "t2", "powershell", new JsonObject { ["command"] = "npm test" }, success: false, errorCode: "failure")
            .Tool(T0, T0.AddSeconds(1), "t3", "create", new JsonObject { ["path"] = "b.ts" }, success: false, errorCode: "rejected")
            .Tool(T0, T0.AddSeconds(4), "t4", "view", new JsonObject { ["path"] = "c.ts" }, parent: "task-1");

        var tools = Parse(log).Tools.ToDictionary(t => t.Name);

        Assert.Equal((ToolOutcome.Success, 2000L, 12, 4), (tools["apply_patch"].Outcome, tools["apply_patch"].DurationMs!.Value, tools["apply_patch"].LinesAdded, tools["apply_patch"].LinesRemoved));
        Assert.Equal(ToolOutcome.Error, tools["powershell"].Outcome);
        Assert.Equal("npm", tools["powershell"].Target);
        Assert.Equal(ToolOutcome.Denied, tools["create"].Outcome);
        Assert.Equal("task-1", tools["view"].AgentId);
        Assert.All(tools.Values, t => Assert.StartsWith("s:", t.Id)); // ids are per session: the CLI reuses them
    }

    [Fact]
    public void PromptsTurnsCompactionsAndErrors_AreRead()
    {
        var log = new CopilotLog("s").Start(T0)
            .Prompt(T0, "add a health endpoint", messageId: "m1")
            .Prompt(T0.AddMinutes(1), "/model", messageId: "m2")
            .Prompt(T0.AddMinutes(2), "continue the objective", source: "autopilot", messageId: "m3")
            .Turn(T0.AddSeconds(1), T0.AddSeconds(20), "t1", "i1")
            .Turn(T0.AddSeconds(21), T0.AddSeconds(50), "t2", "i1")
            .Compaction(T0.AddMinutes(3), T0.AddMinutes(4), 120_000, 20_000)
            .Abort(T0.AddMinutes(5))
            .Error(T0.AddMinutes(6), "authorization");

        var parsed = Parse(log);

        Assert.Equal([PromptKind.Typed, PromptKind.Command, PromptKind.Automated], parsed.Prompts.Select(p => p.Kind));
        Assert.Equal("/model", parsed.Prompts[1].Command);
        var turn = Assert.Single(parsed.Turns); // both model rounds belong to one interaction
        Assert.Equal((49_000L, 2), (turn.DurationMs, turn.MessageCount));
        var compaction = Assert.Single(parsed.Compactions);
        Assert.Equal((120_000L, 20_000L, 60_000L), (compaction.PreTokens, compaction.PostTokens, compaction.DurationMs!.Value));
        Assert.Contains(parsed.Errors, e => e.Kind == ErrorKind.Interrupt);
        Assert.Contains(parsed.Errors, e => e is { Kind: ErrorKind.Session, Code: "authorization" });
    }

    [Fact]
    public void SessionStart_GivesProjectRepositoryAndBranch_AndWorkspaceGivesTheTitle()
    {
        var path = new CopilotLog("abc").Start(T0, cwd: @"E:\src\api", repository: "acme/payments-api", branch: "dev").WriteTo(_dir, workspaceName: "Health endpoint");

        var info = Assert.Single(CopilotEventsParser.Parse(path).Sessions);

        Assert.Equal(("payments-api", "dev", "1.0.89", "Health endpoint"), (info.Project, info.Branch, info.ClientVersion, info.Title));
    }

    // ---- sessions from before per-call events: running totals only (as in Agent Smith) ----

    [Fact]
    public void RunningTotals_TurnCheckpointsIntoCostGrowth()
    {
        var log = new CopilotLog("abc").Checkpoint(T0, 500_000_000).Checkpoint(T0.AddHours(1), 1_500_000_000);

        var calls = Parse(log).Calls;

        Assert.Equal(2, calls.Length);
        Assert.Equal(0.005, calls[0].ReportedCostUsd!.Value, 9);
        Assert.Equal(0.01, calls[1].ReportedCostUsd!.Value, 9);
        Assert.All(calls, c => Assert.True(c.IsAggregate));
    }

    [Fact]
    public void RunningTotals_AttributeToTheModelThatReallyAnswered_EvenWhenSetToAuto()
    {
        var log = new CopilotLog("s")
            .Event("session.model_change", T0, new JsonObject { ["newModel"] = "auto" })
            .Checkpoint(T0.AddHours(1), 300_000_000, "gpt-5-mini", "c1", 100, 40)
            .Event("session.usage_checkpoint", T0.AddHours(2), JsonNode.Parse("{\"totalNanoAiu\":900000000,\"promptCacheBreakState\":[{\"models\":{\"gpt-5-mini\":{\"model_call_id\":\"c1\",\"prompt_tokens\":100,\"cache_read\":40},\"claude-sonnet-4.6\":{\"model_call_id\":\"c2\",\"prompt_tokens\":500,\"cache_read\":0}}}]}")!.AsObject());

        var calls = Parse(log).Calls;

        Assert.Equal(0.003, calls.Where(c => c.Model == "gpt-5-mini").Sum(c => c.ReportedCostUsd ?? 0), 9);
        Assert.Equal(0.006, calls.Where(c => c.Model == "claude-sonnet-4.6").Sum(c => c.ReportedCostUsd ?? 0), 9);
        Assert.Equal(60, calls.Where(c => c.Model == "gpt-5-mini").Sum(c => c.Tokens.Input));
        Assert.Equal(40, calls.Where(c => c.Model == "gpt-5-mini").Sum(c => c.Tokens.CacheRead));
        Assert.DoesNotContain(calls, c => c.Model == "auto");
    }

    [Fact]
    public void RunningTotals_CleanExit_GivesExactTokensAndLinesChanged()
    {
        var log = new CopilotLog("s").Checkpoint(T0, 1_000_000_000).Shutdown(T0.AddMinutes(5), 1_000_000_000, "mai-code-1.1-flash", 1_000, 9_000, 200, linesAdded: 30, linesRemoved: 2);

        var parsed = Parse(log);

        var flash = parsed.Calls.Where(c => c.Model == "mai-code-1.1-flash").ToList();
        Assert.Equal(10_200, flash.Sum(c => c.Tokens.Total));
        Assert.Equal(0.01, flash.Sum(c => c.ReportedCostUsd ?? 0), 9);
        var info = Assert.Single(parsed.Sessions);
        Assert.Equal((30, 2), (info.LinesAdded!.Value, info.LinesRemoved!.Value));
    }

    [Fact]
    public void HugeSnapshotLines_AreSkippedWithoutParsing()
    {
        var log = new CopilotLog("s").Event("model.messages_snapshot", T0, new JsonObject { ["messages"] = new string('x', 200_000) }).Prompt(T0, "hi");

        Assert.Single(Parse(log).Prompts);
    }
}
