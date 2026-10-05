using System.Text.Json.Nodes;
using TheDojo.Core.Ingest;
using TheDojo.Core.Model;
using TheDojo.Tests.Fixtures;

namespace TheDojo.Tests;

public sealed class ClaudeTranscriptParserTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 4, 10, 0, 0, TimeSpan.Zero);

    private static SessionLog Parse(ClaudeLog log) => ClaudeTranscriptParser.ParseLines(log.Lines, log.SessionId);

    [Fact]
    public void Reply_IsCountedOnce_EvenThoughEveryContentBlockRepeatsItsUsage()
    {
        var log = new ClaudeLog("s1").Reply(T0, "claude-opus-5-5", "m1", input: 5, output: 300, cacheRead: 40_000, write5m: 1_000, write1h: 2_000, thinking: 120,
            tools: [Tool.Read("t1", "a.cs"), Tool.Bash("t2", "dotnet test")]);

        var call = Assert.Single(Parse(log).Calls);

        Assert.Equal(3, log.Lines.Count); // text + two tool blocks
        Assert.Equal(new TokenCounts(5, 300, 40_000, 1_000, 2_000, 120), call.Tokens);
        Assert.Equal(["Read", "Bash"], call.Tools!);
        Assert.Equal("tool_use", call.StopReason);
        Assert.Equal("medium", call.Effort);
        Assert.Equal("m1:req_m1", call.Id);
    }

    [Fact]
    public void Reply_WithoutTierBreakdown_CountsCacheWritesAsFiveMinute()
    {
        var line = "{\"type\":\"assistant\",\"timestamp\":\"2026-10-04T10:00:00Z\",\"sessionId\":\"s1\",\"requestId\":\"r1\",\"message\":{\"id\":\"m1\",\"model\":\"claude-sonnet-5\",\"usage\":{\"input_tokens\":1,\"cache_creation_input_tokens\":500,\"cache_read_input_tokens\":0,\"output_tokens\":2}}}";

        var call = Assert.Single(ClaudeTranscriptParser.ParseLines([line]).Calls);

        Assert.Equal(500, call.Tokens.CacheWrite5m);
        Assert.Equal(0, call.Tokens.CacheWrite1h);
    }

    [Fact]
    public void SyntheticAndBrokenLines_AreSkipped_ButApiErrorMessagesAreKept()
    {
        var log = new ClaudeLog("s1")
            .Raw("not json")
            .Raw("{\"type\":\"assistant\",\"timestamp\":\"2026-10-04T10:00:00Z\",\"message\":{\"model\":\"<synthetic>\",\"usage\":{\"output_tokens\":0}}}")
            .Raw("{\"type\":\"assistant\",\"timestamp\":\"2026-10-04T10:00:00Z\",\"isApiErrorMessage\":true,\"error\":\"server_error\",\"message\":{\"model\":\"<synthetic>\",\"content\":[{\"type\":\"text\",\"text\":\"API Error: Connection closed\"}]}}");

        var parsed = Parse(log);

        Assert.Empty(parsed.Calls);
        var error = Assert.Single(parsed.Errors);
        Assert.Equal(ErrorKind.Api, error.Kind);
        Assert.Equal("server_error", error.Code);
    }

    [Fact]
    public void Tools_AreMatchedToTheirResults_WithOutcomeDurationAndChangedLines()
    {
        var log = new ClaudeLog("s1")
            .Reply(T0, "claude-sonnet-5-5", "m1", tools: [Tool.Edit("ok", @"E:\src\shop\a.cs"), Tool.Bash("fail", "cd src && dotnet build"), Tool.Bash("deny", "rm -rf x"), Tool.Read("lost", "b.cs")])
            .ToolResult(T0.AddSeconds(3), "ok", toolUseResult: ClaudeLog.Patch("+one", "+two", "-three", " context"))
            .ToolResult(T0.AddSeconds(8), "fail", "error CS1002", isError: true)
            .ToolResult(T0.AddSeconds(9), "deny", "The user doesn't want to proceed with this tool use.", isError: true, denialKind: "user-rejected");

        var tools = Parse(log).Tools.ToDictionary(t => t.Id);

        Assert.Equal(ToolOutcome.Success, tools["ok"].Outcome);
        Assert.Equal(3_000, tools["ok"].DurationMs);
        Assert.Equal((2, 1), (tools["ok"].LinesAdded, tools["ok"].LinesRemoved));
        Assert.Equal(@"E:\src\shop\a.cs", tools["ok"].Target);

        Assert.Equal(ToolOutcome.Error, tools["fail"].Outcome);
        Assert.Equal("dotnet", tools["fail"].Target);
        Assert.Equal("error CS1002", tools["fail"].ErrorText);

        Assert.Equal(ToolOutcome.Denied, tools["deny"].Outcome);
        Assert.Equal("user-rejected", tools["deny"].DenialKind);

        Assert.Equal(ToolOutcome.Unknown, tools["lost"].Outcome); // the session ended while it ran
    }

    [Fact]
    public void RejectedToolWithoutDenialKind_IsStillADenial_AndInterruptedOnesAreInterrupts()
    {
        var log = new ClaudeLog("s1")
            .Reply(T0, "claude-sonnet-5-5", "m1", tools: [Tool.Bash("a", "ls"), Tool.Bash("b", "ls")])
            .ToolResult(T0.AddSeconds(1), "a", "The user doesn't want to proceed with this tool use. The tool use was rejected.", isError: true)
            .ToolResult(T0.AddSeconds(2), "b", "stopped", toolUseResult: new JsonObject { ["interrupted"] = true, ["stdout"] = string.Empty });

        var tools = Parse(log).Tools.ToDictionary(t => t.Id);

        Assert.Equal(ToolOutcome.Denied, tools["a"].Outcome);
        Assert.Equal(ToolOutcome.Interrupted, tools["b"].Outcome);
    }

    [Fact]
    public void CreatedFile_CountsEveryLineAsAdded()
    {
        var log = new ClaudeLog("s1")
            .Reply(T0, "claude-sonnet-5-5", "m1", tools: [Tool.Call("w", "Write", new JsonObject { ["file_path"] = "new.cs", ["content"] = "x" })])
            .ToolResult(T0.AddSeconds(1), "w", toolUseResult: new JsonObject { ["type"] = "create", ["filePath"] = "new.cs", ["content"] = "a\nb\nc\n", ["structuredPatch"] = new JsonArray() });

        var tool = Assert.Single(Parse(log).Tools);

        Assert.Equal(3, tool.LinesAdded);
    }

    [Fact]
    public void Prompts_TellTypedFromCommandsAndAutomated_AndSkipInjectedText()
    {
        var log = new ClaudeLog("s1")
            .Prompt(T0, "Fix   the\nbug please", permissionMode: "plan")
            .Command(T0.AddMinutes(1), "/compact", "keep the plan")
            .Prompt(T0.AddMinutes(2), "<task-notification>agent done</task-notification>", automated: true)
            .Raw("{\"type\":\"user\",\"isMeta\":true,\"timestamp\":\"2026-10-04T10:03:00Z\",\"sessionId\":\"s1\",\"uuid\":\"meta\",\"message\":{\"content\":\"Caveat: injected\"}}")
            .Raw("{\"type\":\"user\",\"timestamp\":\"2026-10-04T10:04:00Z\",\"sessionId\":\"s1\",\"uuid\":\"out\",\"message\":{\"content\":\"<local-command-stdout>ok</local-command-stdout>\"}}")
            .Interrupt(T0.AddMinutes(5));

        var parsed = Parse(log);

        Assert.Collection(parsed.Prompts,
            p =>
            {
                Assert.Equal(PromptKind.Typed, p.Kind);
                Assert.Equal("Fix the bug please", p.Preview);
                Assert.Equal("plan", p.Mode);
            },
            p =>
            {
                Assert.Equal(PromptKind.Command, p.Kind);
                Assert.Equal("/compact", p.Command);
                Assert.Equal("/compact keep the plan", p.Preview);
            },
            p => Assert.Equal(PromptKind.Automated, p.Kind));
        Assert.Equal(ErrorKind.Interrupt, Assert.Single(parsed.Errors).Kind);
    }

    [Fact]
    public void SubagentTranscripts_MarkTheirCallsAndTools_AndDontCountAsPrompts()
    {
        var log = new ClaudeLog("parent")
            .Raw("{\"type\":\"user\",\"isSidechain\":true,\"agentId\":\"a1\",\"timestamp\":\"2026-10-04T10:00:00Z\",\"sessionId\":\"parent\",\"uuid\":\"p\",\"message\":{\"content\":\"Find the retry code\"}}")
            .Reply(T0, "claude-haiku-4-5-20251001", "m1", agentId: "a1", agentType: "Explore", tools: [Tool.Read("r", "x.cs")])
            .ToolResult(T0.AddSeconds(1), "r", agentId: "a1");

        var parsed = Parse(log);

        Assert.Empty(parsed.Prompts);
        var call = Assert.Single(parsed.Calls);
        Assert.True(call.IsSubagent);
        Assert.Equal(("a1", "Explore"), (call.AgentId, call.AgentType));
        Assert.Equal("a1", Assert.Single(parsed.Tools).AgentId);
    }

    [Fact]
    public void SystemLines_GiveTurnsCompactionsErrorsAndLimitReadings()
    {
        var log = new ClaudeLog("s1")
            .TurnDuration(T0, 65_000, 12)
            .Compaction(T0.AddMinutes(1), 556_000, 16_000, manual: true)
            .ApiError(T0.AddMinutes(2), "ECONNRESET", attempt: 3)
            .UsageCommand(T0.AddMinutes(3), 61, 83, T0.AddHours(2), T0.AddDays(1))
            .Raw("{\"type\":\"system\",\"subtype\":\"stop_hook_summary\",\"timestamp\":\"2026-10-04T10:05:00Z\",\"sessionId\":\"s1\",\"uuid\":\"h\",\"hookErrors\":[\"hook.ps1 exited 1\"]}");

        var parsed = Parse(log);

        var turn = Assert.Single(parsed.Turns);
        Assert.Equal((65_000, 12), (turn.DurationMs, turn.MessageCount));
        var compaction = Assert.Single(parsed.Compactions);
        Assert.True(compaction.Manual);
        Assert.Equal((556_000, 16_000, 30_000L), (compaction.PreTokens, compaction.PostTokens, compaction.DurationMs!.Value));
        Assert.Contains(parsed.Errors, e => e is { Kind: ErrorKind.Api, Code: "ECONNRESET", RetryAttempt: 3 });
        Assert.Contains(parsed.Errors, e => e is { Kind: ErrorKind.Hook, Message: "hook.ps1 exited 1" });
        Assert.Collection(parsed.Limits,
            l => Assert.Equal(("Session", 61.0), (l.Window, l.PercentUsed)),
            l => Assert.Equal(("Weekly", 83.0), (l.Window, l.PercentUsed)));
    }

    [Fact]
    public void SessionInfo_CollectsContextTitlePullRequestsAndReportedCost()
    {
        var log = new ClaudeLog("s1", cwd: @"E:\src\shop\.worktrees\swift-maple", branch: "HEAD", version: "2.1.300")
            .Title("Generated title")
            .Prompt(T0, "go")
            .Title("My own title", custom: true)
            .PullRequest(T0, "https://github.com/acme/shop/pull/7")
            .PullRequest(T0, "https://github.com/acme/shop/pull/7")
            .CostState(1.25, 40, 3);

        var info = Assert.Single(Parse(log).Sessions);

        Assert.Equal("My own title", info.Title);
        Assert.Equal("shop", info.Project); // a worktree belongs to its repository
        Assert.Null(info.Branch); // a detached HEAD says nothing
        Assert.Equal("2.1.300", info.ClientVersion);
        Assert.Equal(["https://github.com/acme/shop/pull/7"], info.PullRequests!);
        Assert.Equal((1.25, 40, 3), (info.ReportedCostUsd!.Value, info.LinesAdded!.Value, info.LinesRemoved!.Value));
    }

    [Fact]
    public void FastModeAndWebUse_AreRecordedOnTheCall()
    {
        var line = "{\"type\":\"assistant\",\"timestamp\":\"2026-10-04T10:00:00Z\",\"sessionId\":\"s1\",\"requestId\":\"r\",\"thinkingDurationMs\":4200,\"attributionSkill\":\"pdf\",\"attributionMcpServer\":\"jira\"," +
                   "\"message\":{\"id\":\"m\",\"model\":\"claude-opus-5-5\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"speed\":\"fast\",\"server_tool_use\":{\"web_search_requests\":2,\"web_fetch_requests\":1}}}}";

        var call = Assert.Single(ClaudeTranscriptParser.ParseLines([line]).Calls);

        Assert.Equal("fast", call.Speed);
        Assert.Equal((2, 1), (call.WebSearches, call.WebFetches));
        Assert.Equal(4200, call.ThinkingMs);
        Assert.Equal(("pdf", "jira"), (call.Skill, call.McpServer));
    }

    [Fact]
    public void Parse_ReadsAFileThatIsStillBeingWritten()
    {
        var root = Directory.CreateTempSubdirectory("dojo-claude-").FullName;
        try
        {
            var path = new ClaudeLog("live").Prompt(T0, "hi").Reply(T0.AddSeconds(2), "claude-sonnet-5-5", "m").WriteTo(root);
            using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

            var parsed = ClaudeTranscriptParser.Parse(path);

            Assert.Single(parsed.Calls);
            Assert.Equal("live", parsed.Calls[0].SessionId);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
