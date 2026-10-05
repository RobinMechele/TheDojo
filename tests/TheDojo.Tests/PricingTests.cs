using Microsoft.Extensions.Time.Testing;
using TheDojo.Core.Model;
using TheDojo.Core.Pricing;

namespace TheDojo.Tests;

public sealed class PricingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 4, 10, 0, 0, TimeSpan.Zero);

    private static ApiCall Call(string model, TokenCounts tokens, Provider provider = Provider.Claude, double? reported = null, string? speed = null) =>
        new(provider, T0, "s", Guid.NewGuid().ToString(), model, tokens, reported, Speed: speed);

    [Theory]
    [InlineData("claude-opus-5-5", 4.00, 20.00, 0.20)]
    [InlineData("claude-opus-5", 5.00, 25.00, 0.50)]
    [InlineData("claude-sonnet-5-5", 2.00, 10.00, 0.20)]
    [InlineData("claude-fable-5-1", 10.00, 50.00, 0.25)]
    [InlineData("claude-haiku-4-5-20251001", 1.00, 5.00, 0.10)]
    [InlineData("claude-opus-5-5[1m]", 4.00, 20.00, 0.20)]
    [InlineData("claude-sonnet-4.6", 3.00, 15.00, 0.30)]
    [InlineData("anthropic/claude-opus-4-8", 5.00, 25.00, 0.50)]
    public void BuiltInRates_MatchTheMostSpecificModel(string model, double input, double output, double read)
    {
        var rates = ClaudePricing.TryGetRates(model)!;

        Assert.Equal(input / 1e6, rates.Input, 12);
        Assert.Equal(output / 1e6, rates.Output, 12);
        Assert.Equal(read / 1e6, rates.CacheRead, 12);
        Assert.Equal(input * 1.25 / 1e6, rates.CacheWrite5m, 12);
        Assert.Equal(input * 2 / 1e6, rates.CacheWrite1h, 12);
    }

    [Fact]
    public void Cost_PricesEachTokenKind_IncludingBothCacheTiers()
    {
        var costs = new CostCalculator(new PriceBook());
        // Opus 5.5: $4 in, $20 out, $0.20 read, $5 5-minute write, $8 1-hour write per million.
        var call = Call("claude-opus-5-5", new TokenCounts(1_000_000, 100_000, 2_000_000, 100_000, 50_000));

        var cost = costs.Cost(call);

        Assert.True(cost.Priced);
        Assert.Equal(4.0 + 2.0 + 0.4 + 0.5 + 0.4, cost.Usd, 9);
        Assert.Equal(2_000_000 * (4.0 - 0.2) / 1e6, cost.CacheSavingsUsd, 9);
    }

    [Fact]
    public void FastMode_CostsTwiceTheStandardRate()
    {
        var costs = new CostCalculator(new PriceBook());
        var tokens = new TokenCounts(1_000_000, 1_000_000);

        Assert.Equal(costs.Cost(Call("claude-opus-5-5", tokens)).Usd * 2, costs.Cost(Call("claude-opus-5-5", tokens, speed: "fast")).Usd, 9);
    }

    [Fact]
    public void ReportedCost_Wins_AndCopilotTokensWithoutOneCostNothing()
    {
        var costs = new CostCalculator(new PriceBook());

        Assert.Equal(0.42, costs.Cost(Call("claude-opus-5-5", new TokenCounts(1_000_000), Provider.Copilot, reported: 0.42)).Usd, 9);
        Assert.Equal(0, costs.Cost(Call("claude-sonnet-4.6", new TokenCounts(1_000_000), Provider.Copilot)).Usd);
    }

    [Fact]
    public void UnknownAndSyntheticModels_AreUnpriced_NotGuessed()
    {
        var costs = new CostCalculator(new PriceBook());

        Assert.False(costs.Cost(Call("some-new-model", new TokenCounts(100))).Priced);
        Assert.Null(new PriceBook().Lookup("<synthetic>"));
        Assert.Null(new PriceBook().Lookup("sonnet"));
    }

    [Fact]
    public void PriceBook_ReadsTheLiteLlmTable_ForModelsTheBuiltInTableDoesntKnow()
    {
        var table = PriceBook.Parse(
            "{\"gpt-6-luna\":{\"input_cost_per_token\":2e-6,\"output_cost_per_token\":8e-6,\"cache_read_input_token_cost\":5e-7}," +
            "\"text-embedding\":{\"mode\":\"embedding\"}}");
        var book = new PriceBook(table);

        Assert.Equal(1, book.Count);
        var rates = book.Lookup("openai/gpt-6-luna")!;
        Assert.Equal(8e-6, rates.Output, 12);
        Assert.Equal(5e-7, rates.CacheRead, 12);
        Assert.Equal(2e-6 * 1.25, rates.CacheWrite5m, 12); // missing: Anthropic's standard multiple
    }

    [Fact]
    public async Task PriceBook_UsesItsDayOldCache_AndFallsBackToItWhenOffline()
    {
        var dir = Directory.CreateTempSubdirectory("dojo-prices-").FullName;
        try
        {
            var cache = Path.Combine(dir, "rates.json");
            var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
            var handler = new StubHandler("{\"gpt-x\":{\"input_cost_per_token\":1e-6,\"output_cost_per_token\":2e-6}}");
            var http = new HttpClient(handler);

            var fresh = await PriceBook.LoadAsync(http, cache, time, CancellationToken.None);
            Assert.Equal(1, fresh.Count);
            Assert.True(File.Exists(cache));

            await PriceBook.LoadAsync(http, cache, time, CancellationToken.None);
            Assert.Equal(1, handler.Calls); // within a day: the cache, no request

            time.Advance(TimeSpan.FromDays(2));
            handler.Fail = true;
            var offline = await PriceBook.LoadAsync(http, cache, time, CancellationToken.None);
            Assert.Equal(1, offline.Count);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("claude-opus-5-5", true)]
    [InlineData("claude-fable-5-1", true)]
    [InlineData("claude-sonnet-5-5", false)]
    [InlineData("gpt-6-luna", false)]
    public void Premium_IsOpusAndFable(string model, bool premium) => Assert.Equal(premium, ClaudePricing.IsPremium(model));

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public bool Fail { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Fail)
            {
                throw new HttpRequestException("offline");
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
