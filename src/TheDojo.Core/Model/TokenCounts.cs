namespace TheDojo.Core.Model;

/// <summary>
/// Tokens of one or more model calls. <see cref="Input"/> is the uncached part of the prompt; cache writes are
/// split by tier (Claude's 5-minute and 1-hour caches; Copilot only reports one, counted as 5-minute).
/// <see cref="Reasoning"/> is the thinking share of <see cref="Output"/>, not an extra amount.
/// </summary>
public readonly record struct TokenCounts(
    long Input = 0,
    long Output = 0,
    long CacheRead = 0,
    long CacheWrite5m = 0,
    long CacheWrite1h = 0,
    long Reasoning = 0)
{
    public long CacheWrite => CacheWrite5m + CacheWrite1h;

    /// <summary>Everything processed: the whole prompt plus the output.</summary>
    public long Total => Input + Output + CacheRead + CacheWrite;

    /// <summary>The size of the prompt the model saw, i.e. how full the context window was.</summary>
    public long Context => Input + CacheRead + CacheWrite;

    /// <summary>Share of the prompt served from cache (0-1), or 0 for an empty prompt.</summary>
    public double CacheHitRate => Context == 0 ? 0 : (double)CacheRead / Context;

    public static TokenCounts operator +(TokenCounts a, TokenCounts b) => new(
        a.Input + b.Input,
        a.Output + b.Output,
        a.CacheRead + b.CacheRead,
        a.CacheWrite5m + b.CacheWrite5m,
        a.CacheWrite1h + b.CacheWrite1h,
        a.Reasoning + b.Reasoning);
}
