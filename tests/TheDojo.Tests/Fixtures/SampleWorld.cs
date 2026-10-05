namespace TheDojo.Tests.Fixtures;

/// <summary>
/// A month of believable agent use on disk, the same every run: several projects, Claude Code sessions on
/// different models (one of them a long session whose context keeps growing), subagents, failing and denied tools,
/// compactions, interruptions, <c>/usage</c> readings and a couple of Copilot CLI sessions.
/// </summary>
public sealed class SampleWorld : IDisposable
{
    /// <summary>A fixed "now": Monday 5 October 2026, 18:00 UTC.</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 5, 18, 0, 0, TimeSpan.Zero);

    private readonly string _root = Directory.CreateTempSubdirectory("dojo-world-").FullName;

    public SampleWorld()
    {
        Directory.CreateDirectory(ClaudeProjects);
        Directory.CreateDirectory(CopilotSessions);
        Build();
    }

    public string Root => _root;

    public string ClaudeHome => Path.Combine(_root, "claude");

    public string CopilotHome => Path.Combine(_root, "copilot");

    public string ClaudeProjects => Path.Combine(ClaudeHome, "projects");

    public string CopilotSessions => Path.Combine(CopilotHome, "session-state");

    public string Cache => Path.Combine(_root, "cache");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private void Build()
    {
        var random = new Random(42);
        string[] projects = [@"E:\src\shop", @"E:\src\billing-api", @"E:\src\design-system", @"E:\src\shop\.worktrees\swift-maple"];
        string[] titles = ["Fix checkout rounding", "Add invoice export", "Refactor button tokens", "Speed up product search", "Write tests for cart", "Investigate flaky login"];
        string[] models = ["claude-opus-5-5", "claude-sonnet-5-5", "claude-sonnet-5-5", "claude-opus-5-5", "claude-haiku-4-5-20251001"];

        var n = 0;
        for (var day = 27; day >= 0; day--)
        {
            var sessionsToday = day % 7 is 5 or 6 ? random.Next(0, 2) : random.Next(1, 4);
            for (var s = 0; s < sessionsToday; s++)
            {
                n++;
                var id = $"sess-{n:000}";
                var cwd = projects[random.Next(projects.Length)];
                var model = models[random.Next(models.Length)];
                var start = Now.Date.AddDays(-day).AddHours(8 + random.Next(0, 10)).AddMinutes(random.Next(60));
                var start2 = new DateTimeOffset(start, TimeSpan.Zero);
                if (start2 > Now.AddHours(-1))
                {
                    start2 = Now.AddHours(-2 - s);
                }

                var log = new ClaudeLog(id, cwd, branch: "feature/" + n);
                log.Title(titles[random.Next(titles.Length)]);
                var at = start2;
                var context = 18_000L;
                var prompts = random.Next(2, 7);
                for (var p = 0; p < prompts; p++)
                {
                    log.Prompt(at, p == 0 ? "Please " + titles[random.Next(titles.Length)].ToLowerInvariant() + " and run the tests when done" : "ok, continue");
                    var replies = random.Next(2, 8);
                    var turnStart = at;
                    for (var r = 0; r < replies; r++)
                    {
                        at = at.AddSeconds(random.Next(5, 40));
                        var toolId = $"{id}-t{p}-{r}";
                        var tool = (r % 3) switch
                        {
                            0 => Tool.Read(toolId, $@"{cwd}\src\file{random.Next(4)}.cs"),
                            1 => Tool.Bash(toolId, random.Next(3) == 0 ? "dotnet test" : "git status"),
                            _ => Tool.Edit(toolId, $@"{cwd}\src\file{random.Next(4)}.cs"),
                        };
                        var output = random.Next(200, 2500);
                        log.Reply(at, model, $"msg-{id}-{p}-{r}", input: 6, output: output, cacheRead: context, write1h: random.Next(400, 3000), thinking: output / 3,
                            tools: r < replies - 1 ? [tool] : null, text: r == replies - 1 ? "All done." : null);
                        if (r < replies - 1)
                        {
                            var failed = tool.Name == "Bash" && random.Next(5) == 0;
                            var denied = !failed && random.Next(25) == 0;
                            at = at.AddSeconds(random.Next(1, 20));
                            log.ToolResult(at, toolId, failed ? "Exit code 1\nerror CS1002: ; expected" : "ok", isError: failed || denied,
                                toolUseResult: tool.Name == "Edit" ? ClaudeLog.Patch("+added line", "+another", "-removed") : null,
                                denialKind: denied ? "user-rejected" : null);
                        }

                        context += random.Next(3_000, 25_000);
                    }

                    log.TurnDuration(at.AddSeconds(1), (long)(at - turnStart).TotalMilliseconds + 1000, replies * 2);
                    at = at.AddMinutes(random.Next(1, 12));
                }

                if (n % 6 == 0)
                {
                    // A subagent on the session's (premium) model.
                    var agent = new ClaudeLog(id, cwd);
                    for (var r = 0; r < 4; r++)
                    {
                        agent.Reply(at.AddSeconds(r * 10), "claude-opus-5-5", $"msg-{id}-agent-{r}", output: 900, cacheRead: 30_000, write5m: 8_000, agentId: "a" + n, agentType: "Explore");
                    }

                    agent.WriteTo(ClaudeProjects, subagentOf: id);
                    log.Reply(at, model, $"msg-{id}-spawn", tools: [Tool.Agent($"{id}-agent", "Explore")], text: null);
                    log.ToolResult(at.AddSeconds(45), $"{id}-agent", "found it");
                }

                if (n % 5 == 0)
                {
                    log.Interrupt(at);
                }

                if (n % 9 == 0)
                {
                    log.ApiError(at.AddSeconds(3));
                }

                if (n % 4 == 0)
                {
                    log.UsageCommand(at, 20 + n % 60, 30 + n, at.AddHours(3), Now.AddDays(2));
                }

                if (n % 7 == 0)
                {
                    log.PullRequest(at, $"https://github.com/acme/shop/pull/{n}");
                }

                log.WriteTo(ClaudeProjects);
            }
        }

        // The marathon: one long session whose context keeps growing and gets compacted twice.
        var marathon = new ClaudeLog("sess-marathon", @"E:\src\billing-api", branch: "feature/ledger");
        marathon.Title("Rewrite the ledger reconciliation");
        var t = Now.AddDays(-2).AddHours(-6);
        marathon.Prompt(t, "Rewrite the ledger reconciliation so it handles partial refunds, keep the public API stable");
        var ctx = 40_000L;
        for (var i = 0; i < 60; i++)
        {
            t = t.AddMinutes(2);
            if (i is 20 or 40)
            {
                marathon.Compaction(t, ctx, 30_000);
                ctx = 30_000;
            }

            marathon.Reply(t, "claude-opus-5-5", $"msg-marathon-{i}", input: 4, output: 1500, cacheRead: ctx, write1h: 6_000, thinking: 900,
                tools: [Tool.Read($"m-{i}", @"E:\src\billing-api\Ledger.cs")], text: null);
            marathon.ToolResult(t.AddSeconds(2), $"m-{i}");
            ctx += 22_000;
        }

        marathon.TurnDuration(t.AddSeconds(5), 2 * 60 * 60 * 1000, 120);
        marathon.WriteTo(ClaudeProjects);

        // Copilot CLI: one session with per-call logs, one older one with running totals only.
        var copilot = new CopilotLog("copilot-1");
        var c = Now.AddDays(-3);
        copilot.Start(c).Prompt(c.AddSeconds(5), "explain the retry policy in api/http.ts", messageId: "cm1")
            .Turn(c.AddSeconds(6), c.AddSeconds(40), "t1", "i-cm1")
            .ModelCall(c.AddSeconds(20), "call-1", "claude-sonnet-4.6", 2_000, 18_000, 1_000, 600, 2_500_000_000, premiumLeft: 64)
            .Tool(c.AddSeconds(21), c.AddSeconds(23), "tc1", "view", new() { ["path"] = @"E:\src\api\http.ts" })
            .Tool(c.AddSeconds(24), c.AddSeconds(30), "tc2", "powershell", new() { ["command"] = "npm test" }, success: false, errorCode: "failure")
            .ModelCall(c.AddSeconds(35), "call-2", "gpt-5.6-sol", 1_500, 20_000, 0, 900, 1_200_000_000, premiumLeft: 62)
            .Abort(c.AddSeconds(41));
        copilot.WriteTo(CopilotSessions, workspaceName: "Retry policy");

        var older = new CopilotLog("copilot-2");
        var o = Now.AddDays(-9);
        older.Start(o, model: "auto").Prompt(o.AddSeconds(3), "add a health endpoint", messageId: "om1")
            .Checkpoint(o.AddMinutes(5), 3_000_000_000, "gpt-6-luna", "x1", 12_000, 4_000)
            .Shutdown(o.AddMinutes(9), 5_000_000_000, "gpt-6-luna", 20_000, 30_000, 2_000, linesAdded: 40, linesRemoved: 4);
        older.WriteTo(CopilotSessions);
    }
}
