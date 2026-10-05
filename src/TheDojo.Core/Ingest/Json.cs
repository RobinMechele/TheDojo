using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TheDojo.Core.Ingest;

/// <summary>Tolerant readers for agent logs, whose schemas grow a field or two with every CLI release.</summary>
internal static class Json
{
    public static JsonElement? Prop(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value
            : null;

    public static JsonElement? Obj(JsonElement parent, string name) =>
        Prop(parent, name) is { ValueKind: JsonValueKind.Object } value ? value : null;

    public static JsonElement? Arr(JsonElement parent, string name) =>
        Prop(parent, name) is { ValueKind: JsonValueKind.Array } value ? value : null;

    public static string? Str(JsonElement parent, string name) =>
        Prop(parent, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    public static double? Num(JsonElement parent, string name) =>
        Prop(parent, name) is { ValueKind: JsonValueKind.Number } value ? value.GetDouble() : null;

    /// <summary>A non-negative count; 0 when missing.</summary>
    public static long Count(JsonElement parent, string name) =>
        Prop(parent, name) is { ValueKind: JsonValueKind.Number } value
            ? Math.Max(0, value.TryGetInt64(out var n) ? n : (long)value.GetDouble())
            : 0;

    public static long? OptionalCount(JsonElement parent, string name) =>
        Prop(parent, name) is { ValueKind: JsonValueKind.Number } value
            ? Math.Max(0, value.TryGetInt64(out var n) ? n : (long)value.GetDouble())
            : null;

    public static bool Bool(JsonElement parent, string name) =>
        Prop(parent, name) is { ValueKind: JsonValueKind.True };

    /// <summary>An ISO-8601 string or an epoch-milliseconds number.</summary>
    public static DateTimeOffset? Time(JsonElement parent, string name)
    {
        switch (Prop(parent, name))
        {
            case { ValueKind: JsonValueKind.String } s when s.TryGetDateTimeOffset(out var at):
                return at;
            case { ValueKind: JsonValueKind.String } s when DateTimeOffset.TryParse(s.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed):
                return parsed;
            case { ValueKind: JsonValueKind.Number } n when n.TryGetInt64(out var ms) && ms > 0:
                return DateTimeOffset.FromUnixTimeMilliseconds(ms);
            default:
                return null;
        }
    }

    /// <summary>The text of a string, or of the text blocks of a content array.</summary>
    public static string Text(JsonElement? content)
    {
        if (content is not { } c)
        {
            return string.Empty;
        }

        if (c.ValueKind == JsonValueKind.String)
        {
            return c.GetString() ?? string.Empty;
        }

        if (c.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        foreach (var block in c.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.String)
            {
                text.Append(block.GetString());
            }
            else if (Str(block, "type") == "text" && Str(block, "text") is { } t)
            {
                if (text.Length > 0)
                {
                    text.Append('\n');
                }

                text.Append(t);
            }
        }

        return text.ToString();
    }

    /// <summary>The first <paramref name="max"/> characters on one line.</summary>
    public static string Preview(string text, int max = 200)
    {
        var flat = new StringBuilder(Math.Min(text.Length, max + 1));
        var space = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                space = flat.Length > 0;
                continue;
            }

            if (space)
            {
                flat.Append(' ');
                space = false;
            }

            flat.Append(ch);
            if (flat.Length >= max)
            {
                return flat.ToString(0, max - 1) + "…";
            }
        }

        return flat.ToString();
    }

    public static IEnumerable<string> ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    public static JsonDocument? TryParse(string line)
    {
        if (line.Length == 0 || line[0] != '{')
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 256 });
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
