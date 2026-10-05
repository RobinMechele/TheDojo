namespace TheDojo.Core.Pricing;

/// <summary>USD per token.</summary>
public sealed record ModelRates(double Input, double Output, double CacheRead, double CacheWrite5m, double CacheWrite1h)
{
    /// <summary>Anthropic's cache multiples of the input rate: 1.25x for a 5-minute write, 2x for a 1-hour write, 0.1x for a read.</summary>
    public static ModelRates FromInput(double inputPerToken, double outputPerToken, double? cacheReadPerToken = null, double? cacheWrite5mPerToken = null) =>
        new(inputPerToken, outputPerToken, cacheReadPerToken ?? inputPerToken * 0.1, cacheWrite5mPerToken ?? inputPerToken * 1.25, inputPerToken * 2.0);

    public static ModelRates PerMillion(double input, double output, double cacheRead) =>
        new(input / 1e6, output / 1e6, cacheRead / 1e6, input * 1.25 / 1e6, input * 2.0 / 1e6);

    public ModelRates Times(double factor) => new(Input * factor, Output * factor, CacheRead * factor, CacheWrite5m * factor, CacheWrite1h * factor);
}
