using TheDojo.Core.Model;

namespace TheDojo.Core.Pricing;

public readonly record struct CallCost(double Usd, bool Priced, double CacheSavingsUsd);

/// <summary>Prices model calls. A cost the agent reported itself (Copilot's AI credits) always wins over an estimate.</summary>
public sealed class CostCalculator(PriceBook prices)
{
    public PriceBook Prices => prices;

    public CallCost Cost(ApiCall call)
    {
        var rates = RatesFor(call);
        var savings = rates is null ? 0 : call.Tokens.CacheRead * Math.Max(0, rates.Input - rates.CacheRead);
        if (call.ReportedCostUsd is { } reported)
        {
            return new CallCost(reported, true, savings);
        }

        // Copilot bills in AI credits; a Copilot record without a reported cost is tokens whose cost is counted elsewhere.
        if (call.Provider == Provider.Copilot)
        {
            return new CallCost(0, true, savings);
        }

        return rates is null ? new CallCost(0, false, 0) : new CallCost(Price(call.Tokens, rates), true, savings);
    }

    /// <summary>What the call would have cost on another model at standard speed: the "what if" behind model-choice tips.</summary>
    public double? CostOn(ApiCall call, string model) =>
        prices.Lookup(model) is { } rates ? Price(call.Tokens, rates) : null;

    public ModelRates? RatesFor(ApiCall call)
    {
        var rates = prices.Lookup(call.Model);
        return rates is not null && call.Speed == "fast" ? rates.Times(ClaudePricing.FastModeMultiplier) : rates;
    }

    public static double Price(TokenCounts t, ModelRates r) =>
        t.Input * r.Input + t.Output * r.Output + t.CacheRead * r.CacheRead + t.CacheWrite5m * r.CacheWrite5m + t.CacheWrite1h * r.CacheWrite1h;
}
