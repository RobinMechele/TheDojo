using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using TheDojo.Core.Limits;
using TheDojo.Core.Model;

namespace TheDojo.Tests;

public sealed class LimitsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Directory.CreateTempSubdirectory("dojo-limits-").FullName;

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

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static readonly Func<CancellationToken, Task<string?>> NoCli = _ => Task.FromResult<string?>(null);
    private static readonly Func<CancellationToken, Task<string?>> NoGh = _ => Task.FromResult<string?>(null);

    private void WriteClaudeCredentials(string subscription = "max") =>
        File.WriteAllText(Path.Combine(_dir, ".credentials.json"), "{\"claudeAiOauth\":{\"accessToken\":\"tok-123\",\"subscriptionType\":\"" + subscription + "\"}}");

    [Fact]
    public async Task Claude_ReadsSessionAndWeeklyWindows_AndSendsTheOAuthTokenOnlyToAnthropic()
    {
        WriteClaudeCredentials();
        var handler = new StubHandler(HttpStatusCode.OK,
            "{\"five_hour\":{\"utilization\":61.0,\"resets_at\":\"2026-10-04T14:00:00Z\"},\"seven_day\":{\"utilization\":83.5,\"resets_at\":\"2026-10-10T09:00:00Z\"},\"seven_day_opus\":null}");

        var limits = await new ClaudePlanLimitsSource(new HttpClient(handler), _dir).FetchAsync(CancellationToken.None);

        Assert.Null(limits.Error);
        Assert.Equal("Max", limits.Plan);
        Assert.Equal(["Session", "Weekly"], limits.Windows.Select(w => w.Label));
        Assert.Equal(39, limits.Windows[0].PercentLeft, 1);
        Assert.Equal(TimeSpan.FromHours(5), limits.Windows[0].Length);
        Assert.Equal("api.anthropic.com", handler.Request!.RequestUri!.Host);
        Assert.Equal(("Bearer", "tok-123"), (handler.Request.Headers.Authorization!.Scheme, handler.Request.Headers.Authorization.Parameter));
        Assert.Contains("oauth-2025-04-20", handler.Request.Headers.GetValues("anthropic-beta"));
    }

    [Fact]
    public async Task Claude_WithoutCredentials_AsksToSignIn_WithoutCallingTheNetwork()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");

        var limits = await new ClaudePlanLimitsSource(new HttpClient(handler), _dir).FetchAsync(CancellationToken.None);

        Assert.NotNull(limits.Error);
        Assert.True(limits.Local);
        Assert.Null(handler.Request);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "expired")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate limiting")]
    [InlineData(HttpStatusCode.InternalServerError, "didn't return")]
    public async Task Claude_Failures_AreReportedNotThrown(HttpStatusCode status, string message)
    {
        WriteClaudeCredentials();

        var limits = await new ClaudePlanLimitsSource(new HttpClient(new StubHandler(status, "{}")), _dir).FetchAsync(CancellationToken.None);

        Assert.Contains(message, limits.Error);
    }

    [Fact]
    public async Task Copilot_PrefersTheCliQuota()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        Func<CancellationToken, Task<string?>> cli = _ => Task.FromResult<string?>(
            "{\"quotaSnapshots\":{\"premium_interactions\":{\"entitlementRequests\":300,\"remainingPercentage\":62,\"tokenBasedBilling\":false},\"chat\":{\"isUnlimitedEntitlement\":true}}}");

        var limits = await new CopilotPlanLimitsSource(new HttpClient(handler), NoGh, _ => null, cli, new FakeTimeProvider(Now)).FetchAsync(CancellationToken.None);

        var window = Assert.Single(limits.Windows);
        Assert.Equal(("Premium requests", 38.0), (window.Label, window.PercentUsed));
        Assert.Equal("186 of 300 requests left", window.Detail);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), window.ResetsAt);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task Copilot_FallsBackToTheEditorEndpoint_WithAnEnvironmentToken()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            "{\"copilot_plan\":\"individual_pro\",\"quota_reset_date\":\"2026-11-01\",\"quota_snapshots\":{\"premium_interactions\":{\"entitlement\":1500,\"remaining\":1125,\"percent_remaining\":75.0,\"token_based_billing\":true}}}");

        var limits = await new CopilotPlanLimitsSource(new HttpClient(handler), NoGh, n => n == "GH_TOKEN" ? "gho_x" : null, NoCli).FetchAsync(CancellationToken.None);

        Assert.Equal("Pro+", limits.Plan);
        var window = Assert.Single(limits.Windows);
        Assert.Equal(("AI credits", 25.0), (window.Label, window.PercentUsed));
        Assert.Equal("api.github.com", handler.Request!.RequestUri!.Host);
        Assert.Equal("token gho_x", handler.Request.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task Copilot_WithoutCliOrToken_AsksToSignIn()
    {
        var limits = await new CopilotPlanLimitsSource(new HttpClient(new StubHandler(HttpStatusCode.OK, "{}")), NoGh, _ => null, NoCli).FetchAsync(CancellationToken.None);

        Assert.True(limits.Local);
        Assert.Contains("Sign in", limits.Error);
    }

    // ---- polling and history ----

    private sealed class ScriptedSource : IPlanLimitsSource
    {
        public Queue<ProviderLimits> Results { get; } = new();

        public int Calls { get; private set; }

        public Provider Provider => Provider.Claude;

        public Task<ProviderLimits> FetchAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Results.Dequeue());
        }
    }

    private static ProviderLimits Good(double used) => new(Provider.Claude, "Max", [new LimitWindow("Session", used, null)]);

    [Fact]
    public async Task Service_ReusesAReadingWithinTheInterval_AndAManualRefreshWaitsAMinute()
    {
        var time = new FakeTimeProvider(Now);
        var source = new ScriptedSource();
        source.Results.Enqueue(Good(10));
        source.Results.Enqueue(Good(20));
        var service = new PlanLimitsService([source], time);

        await service.FetchAllAsync();
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(10, (await service.FetchAllAsync()).Single().Windows.Single().PercentUsed);
        Assert.Equal(1, source.Calls);

        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(20, (await service.FetchAllAsync(manual: true)).Single().Windows.Single().PercentUsed);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task Service_KeepsTheLastGoodReading_WhenARecheckFails()
    {
        var time = new FakeTimeProvider(Now);
        var source = new ScriptedSource();
        source.Results.Enqueue(Good(61));
        source.Results.Enqueue(ProviderLimits.Failed(Provider.Claude, "Anthropic is rate limiting limit checks."));
        var service = new PlanLimitsService([source], time);

        await service.FetchAllAsync();
        time.Advance(TimeSpan.FromMinutes(6));
        var result = (await service.FetchAllAsync()).Single();

        Assert.Null(result.Error);
        Assert.Equal(61, result.Windows.Single().PercentUsed);
        Assert.Contains("6 min ago", result.Note);
    }

    [Fact]
    public async Task Service_RecordsGoodReadingsInTheHistory()
    {
        var store = new LimitHistoryStore(Path.Combine(_dir, "history.jsonl"));
        var source = new ScriptedSource();
        source.Results.Enqueue(Good(30));
        source.Results.Enqueue(ProviderLimits.Failed(Provider.Claude, "offline"));
        var time = new FakeTimeProvider(Now);
        var service = new PlanLimitsService([source], time, store);

        await service.FetchAllAsync();
        time.Advance(TimeSpan.FromMinutes(10));
        await service.FetchAllAsync();

        var reading = Assert.Single(store.Read());
        Assert.Equal((Provider.Claude, "Session", 30.0, Now), (reading.Provider, reading.Window, reading.PercentUsed, reading.Timestamp));
    }

    [Fact]
    public void HistoryStore_SkipsALineCutShortByACrash()
    {
        var path = Path.Combine(_dir, "history.jsonl");
        var store = new LimitHistoryStore(path);
        store.Append([new LimitReading(Provider.Copilot, Now, "Premium requests", 12)]);
        File.AppendAllText(path, "{\"Provider\":0,\"Timest");

        Assert.Single(store.Read());
        Assert.Empty(new LimitHistoryStore(Path.Combine(_dir, "none.jsonl")).Read());
    }

    [Fact]
    public void Window_PaceAndProjectedExhaustion()
    {
        // Half the week gone, 75% used: on pace to run out two days before the reset.
        var window = new LimitWindow("Weekly", 75, Now.AddDays(3.5), TimeSpan.FromDays(7));

        Assert.Equal(LimitPace.Ahead, window.PaceAt(Now));
        Assert.Equal(Now.AddDays(3.5 * 25 / 75).Ticks, window.ProjectedExhaustion(Now)!.Value.Ticks, TimeSpan.FromSeconds(1).Ticks);
        Assert.Equal(LimitPace.OnTrack, (window with { PercentUsed = 40 }).PaceAt(Now));
        Assert.Null((window with { PercentUsed = 40 }).ProjectedExhaustion(Now));
        Assert.Equal(LimitPace.Unknown, new LimitWindow("x", 50, null).PaceAt(Now));
    }

    [Fact]
    public void CommandResolver_FindsCommandsOnThePath()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_dir, "bin")).FullName;
        var name = OperatingSystem.IsWindows() ? "neo.cmd" : "neo";
        File.WriteAllText(Path.Combine(bin, name), "echo");

        Assert.Equal(Path.Combine(bin, name), CommandResolver.Resolve("neo", path: bin, pathExt: ".EXE;.CMD"));
        Assert.Null(CommandResolver.Resolve("morpheus", path: bin));
    }
}
