using System.Text.RegularExpressions;

namespace TheDojo.Core.Pricing;

/// <summary>
/// Anthropic's published first-party prices (USD per million tokens), for models the live price list doesn't know
/// yet or when it can't be fetched. Cache writes are a multiple of the input rate (1.25x for 5 minutes, 2x for
/// 1 hour); cache reads have their own per-model rate. Fast mode on Opus costs twice the standard rate.
/// </summary>
public static partial class ClaudePricing
{
    public const double FastModeMultiplier = 2.0;

    // Most specific first: the first prefix match wins ("opus-5-5" must come before "opus-5").
    private static readonly (string Prefix, double Input, double Output, double CacheRead)[] Table =
    [
        ("claude-fable-5-1", 10.00, 50.00, 0.25),
        ("claude-mythos-5-1", 10.00, 50.00, 0.25),
        ("claude-fable-5", 10.00, 50.00, 0.25),
        ("claude-mythos-5", 10.00, 50.00, 0.25),
        ("claude-opus-5-5", 4.00, 20.00, 0.20),
        ("claude-opus-5", 5.00, 25.00, 0.50),
        ("claude-opus-4-8", 5.00, 25.00, 0.50),
        ("claude-opus-4-7", 5.00, 25.00, 0.50),
        ("claude-opus-4-6", 5.00, 25.00, 0.50),
        ("claude-opus-4-5", 5.00, 25.00, 0.50),
        ("claude-opus-4-1", 15.00, 75.00, 1.50),
        ("claude-opus-4", 15.00, 75.00, 1.50),
        ("claude-3-opus", 15.00, 75.00, 1.50),
        ("claude-sonnet-5-5", 2.00, 10.00, 0.20),
        ("claude-sonnet-5", 2.00, 10.00, 0.20),
        ("claude-sonnet-4-6", 3.00, 15.00, 0.30),
        ("claude-sonnet-4-5", 3.00, 15.00, 0.30),
        ("claude-sonnet-4", 3.00, 15.00, 0.30),
        ("claude-3-7-sonnet", 3.00, 15.00, 0.30),
        ("claude-3-5-sonnet", 3.00, 15.00, 0.30),
        ("claude-haiku-4-5", 1.00, 5.00, 0.10),
        ("claude-3-5-haiku", 0.80, 4.00, 0.08),
        ("claude-3-haiku", 0.25, 1.25, 0.03),
    ];

    [GeneratedRegex(@"-\d{8}$")]
    private static partial Regex DateSuffix();

    /// <summary>
    /// A model id in Anthropic's form: lower case, without a snapshot date (<c>-20251001</c>), a bracketed variant
    /// (<c>[1m]</c>) or a provider prefix, and with Copilot's dotted versions (<c>claude-sonnet-4.6</c>) dashed.
    /// </summary>
    public static string Normalize(string model)
    {
        var key = model.Trim().ToLowerInvariant();
        var bracket = key.IndexOf('[');
        if (bracket >= 0)
        {
            key = key[..bracket];
        }

        var slash = key.LastIndexOf('/');
        if (slash >= 0)
        {
            key = key[(slash + 1)..];
        }

        if (key.StartsWith("claude-", StringComparison.Ordinal))
        {
            key = key.Replace('.', '-');
        }

        return DateSuffix().Replace(key, string.Empty);
    }

    public static ModelRates? TryGetRates(string model)
    {
        var normalized = Normalize(model);
        foreach (var (prefix, input, output, read) in Table)
        {
            if (normalized.StartsWith(prefix, StringComparison.Ordinal))
            {
                return ModelRates.PerMillion(input, output, read);
            }
        }

        return null;
    }

    public static bool IsKnownModel(string model) => TryGetRates(model) is not null;

    /// <summary>Opus and Fable/Mythos: the premium tiers a cheaper model can often stand in for.</summary>
    public static bool IsPremium(string model)
    {
        var n = Normalize(model);
        return n.Contains("opus", StringComparison.Ordinal) || n.Contains("fable", StringComparison.Ordinal) || n.Contains("mythos", StringComparison.Ordinal);
    }

    /// <summary>The model family for grouping: Fable, Opus, Sonnet, Haiku, or the id itself for other vendors.</summary>
    public static string Family(string model)
    {
        var n = Normalize(model);
        foreach (var family in new[] { "fable", "mythos", "opus", "sonnet", "haiku" })
        {
            if (n.Contains(family, StringComparison.Ordinal))
            {
                return char.ToUpperInvariant(family[0]) + family[1..];
            }
        }

        return model;
    }
}
