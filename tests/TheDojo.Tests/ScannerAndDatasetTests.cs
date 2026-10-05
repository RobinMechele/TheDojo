using TheDojo.Core.Ingest;
using TheDojo.Core.Model;
using TheDojo.Tests.Fixtures;

namespace TheDojo.Tests;

public sealed class ScannerAndDatasetTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 4, 10, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Directory.CreateTempSubdirectory("dojo-scan-").FullName;

    private string Projects => Path.Combine(_dir, "projects");

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

    private LogScanner Scanner(Func<string, SessionLog>? parse = null, bool cache = true) =>
        new(Projects, "*.jsonl", parse ?? ClaudeTranscriptParser.Parse, cache ? Path.Combine(_dir, "cache.json.gz") : null);

    [Fact]
    public async Task Scanner_OnlyReparsesFilesThatChanged_AndKeepsTheCacheOnDisk()
    {
        var path = new ClaudeLog("a").Reply(T0, "claude-sonnet-5-5", "m1").WriteTo(Projects);
        var parses = 0;
        LogScanner Make() => Scanner(p => { Interlocked.Increment(ref parses); return ClaudeTranscriptParser.Parse(p); });

        var first = Make();
        await first.ScanAsync(DateTimeOffset.MinValue, CancellationToken.None);
        await first.ScanAsync(DateTimeOffset.MinValue, CancellationToken.None);
        Assert.Equal(1, parses);

        // A new process (a new scanner) reuses the on-disk cache.
        var logs = await Make().ScanAsync(DateTimeOffset.MinValue, CancellationToken.None);
        Assert.Equal(1, parses);
        Assert.Single(logs.Single().Calls);

        File.AppendAllLines(path, new ClaudeLog("a").Reply(T0.AddMinutes(1), "claude-sonnet-5-5", "m2").Lines);
        var after = await Make().ScanAsync(DateTimeOffset.MinValue, CancellationToken.None);
        Assert.Equal(2, parses);
        Assert.Equal(2, after.Single().Calls.Length);
    }

    [Fact]
    public async Task Scanner_SkipsFilesNotWrittenSinceTheWindowStarted()
    {
        var path = new ClaudeLog("old").Reply(T0.AddDays(-200), "claude-sonnet-5-5", "m1").WriteTo(Projects);
        File.SetLastWriteTimeUtc(path, T0.AddDays(-200).UtcDateTime);

        Assert.Empty(await Scanner().ScanAsync(T0.AddDays(-90), CancellationToken.None));
    }

    [Fact]
    public async Task Scanner_WithoutTheFolder_ReturnsNothing()
    {
        Assert.Empty(await new LogScanner(Path.Combine(_dir, "missing"), "*.jsonl", ClaudeTranscriptParser.Parse).ScanAsync(DateTimeOffset.MinValue, CancellationToken.None));
    }

    [Fact]
    public async Task Scanner_ReadsSubagentTranscripts_AfterTheirSession()
    {
        new ClaudeLog("parent").Prompt(T0, "go").Reply(T0.AddSeconds(1), "claude-opus-5-5", "m1").WriteTo(Projects);
        new ClaudeLog("parent").Reply(T0.AddSeconds(2), "claude-haiku-4-5", "m2", agentId: "a1", agentType: "Explore").WriteTo(Projects, subagentOf: "parent");

        var data = UsageDataset.Merge(await Scanner(cache: false).ScanAsync(DateTimeOffset.MinValue, CancellationToken.None));

        Assert.Equal(2, data.Calls.Count);
        Assert.Single(data.Sessions);
        Assert.Single(data.Calls, c => c.IsSubagent);
    }

    [Fact]
    public async Task Dataset_DropsHistoryCopiedIntoResumedSessions_FirstCopyWins()
    {
        new ClaudeLog("a").Prompt(T0, "start").Reply(T0.AddSeconds(1), "claude-sonnet-5-5", "m1").WriteTo(Projects);
        // A resumed session's file starts with a copy of the original conversation (same uuids and message ids).
        var copy = new ClaudeLog("a").Prompt(T0, "start").Reply(T0.AddSeconds(1), "claude-sonnet-5-5", "m1").Lines.ToList();
        var resumed = new ClaudeLog("b").Prompt(T0.AddHours(1), "more").Reply(T0.AddHours(1).AddSeconds(1), "claude-sonnet-5-5", "m2");
        Directory.CreateDirectory(Path.Combine(Projects, "x"));
        File.WriteAllLines(Path.Combine(Projects, "x", "b.jsonl"), copy.Concat(resumed.Lines));

        var data = UsageDataset.Merge(await Scanner(cache: false).ScanAsync(DateTimeOffset.MinValue, CancellationToken.None));

        Assert.Equal(2, data.Calls.Count);
        Assert.Equal(2, data.Prompts.Count);
        Assert.Equal(["a", "b"], data.Calls.Select(c => c.SessionId));
    }

    [Fact]
    public void Dataset_MergesPartialSessionInfo_AndCreatesSessionsForOrphanEvents()
    {
        var a = SessionLog.Empty with { Sessions = [new SessionInfo(Provider.Claude, "s1", Title: "Title")] };
        var b = SessionLog.Empty with
        {
            Sessions = [new SessionInfo(Provider.Claude, "s1", Cwd: @"E:\src\shop", PullRequests: ["pr1"])],
            Calls = [new ApiCall(Provider.Copilot, T0, "s2", "c1", "gpt", new TokenCounts(1, 1))],
        };

        var data = UsageDataset.Merge([a, b]);

        Assert.Equal(("Title", "shop", "pr1"), (data.Sessions["s1"].Title, data.Sessions["s1"].Project, data.Sessions["s1"].PullRequests!.Single()));
        Assert.Equal(Provider.Copilot, data.Sessions["s2"].Provider);
    }

    [Theory]
    [InlineData(null, @"E:\Software development\Agent-Smith", "Agent-Smith")]
    [InlineData(null, @"E:\src\shop\.worktrees\swift-maple", "shop")]
    [InlineData(null, @"E:\src\shop\.claude\worktrees\eager-eagle", "shop")]
    [InlineData(null, "/home/neo/zion/", "zion")]
    [InlineData("acme/payments-api", @"C:\anything", "payments-api")]
    [InlineData(null, null, "(unknown)")]
    public void Project_IsTheRepositoryOrTheWorkingFolder(string? repository, string? cwd, string expected) =>
        Assert.Equal(expected, SessionInfo.ProjectName(repository, cwd));
}
