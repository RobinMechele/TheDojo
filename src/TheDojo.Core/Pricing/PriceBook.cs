using System.Text.Json;

namespace TheDojo.Core.Pricing;

/// <summary>
/// Per-model token prices. The live list is LiteLLM's public price table (the one <c>ccusage</c> prices against),
/// cached for a day; Anthropic's built-in rates (<see cref="ClaudePricing"/>) cover a model the list doesn't know
/// yet, or being offline.
/// </summary>
public sealed class PriceBook
{
    public const string RatesUrl = "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json";
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    // Locally generated or ambiguous names that are never billed or can't be priced honestly.
    private static readonly HashSet<string> Unpriceable = ["<synthetic>", "synthetic", "opus", "sonnet", "haiku", "fable", "auto", "copilot"];

    private readonly Dictionary<string, ModelRates> _table;

    public PriceBook(Dictionary<string, ModelRates>? table = null)
    {
        _table = table ?? [];
    }

    public int Count => _table.Count;

    /// <summary>Built-in rates first for Anthropic models (they know the 1-hour cache tier); the live list for everything else.</summary>
    public ModelRates? Lookup(string model)
    {
        var raw = model.Trim().ToLowerInvariant();
        if (raw.Length == 0 || Unpriceable.Contains(raw))
        {
            return null;
        }

        if (ClaudePricing.TryGetRates(raw) is { } builtIn)
        {
            return builtIn;
        }

        var key = ClaudePricing.Normalize(raw);
        foreach (var candidate in new[] { raw, key, "anthropic/" + key, "openai/" + key, "github_copilot/" + key })
        {
            if (_table.TryGetValue(candidate, out var rates))
            {
                return rates;
            }
        }

        return null;
    }

    public static async Task<PriceBook> LoadAsync(HttpClient http, string? cachePath, TimeProvider time, CancellationToken cancellationToken)
    {
        var cached = TryReadCache(cachePath, out var writtenAt);
        if (cached is not null && time.GetUtcNow() - writtenAt < Ttl)
        {
            return new PriceBook(cached);
        }

        try
        {
            var json = await http.GetStringAsync(RatesUrl, cancellationToken).ConfigureAwait(false);
            var table = Parse(json);
            if (table.Count > 0)
            {
                WriteCache(cachePath, table);
                return new PriceBook(table);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
        }

        return new PriceBook(cached);
    }

    internal static Dictionary<string, ModelRates> Parse(string json)
    {
        var table = new Dictionary<string, ModelRates>();
        using var doc = JsonDocument.Parse(json);
        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object
                || Number(entry.Value, "input_cost_per_token") is not { } input
                || Number(entry.Value, "output_cost_per_token") is not { } output)
            {
                continue;
            }

            table[entry.Name.Trim().ToLowerInvariant()] = ModelRates.FromInput(input, output,
                Number(entry.Value, "cache_read_input_token_cost"), Number(entry.Value, "cache_creation_input_token_cost"));
        }

        return table;
    }

    private static double? Number(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static Dictionary<string, ModelRates>? TryReadCache(string? path, out DateTimeOffset writtenAt)
    {
        writtenAt = DateTimeOffset.MinValue;
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            writtenAt = File.GetLastWriteTimeUtc(path);
            return JsonSerializer.Deserialize<Dictionary<string, ModelRates>>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteCache(string? path, Dictionary<string, ModelRates> table)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(table));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
