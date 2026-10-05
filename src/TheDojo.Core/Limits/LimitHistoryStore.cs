using System.Text.Json;
using TheDojo.Core.Model;

namespace TheDojo.Core.Limits;

/// <summary>
/// Every limit reading The Dojo has taken, one JSON line each, so the Limits page can show how fast a window
/// filled up over time (the agents' own logs only hold the readings of <c>/usage</c> commands).
/// </summary>
public sealed class LimitHistoryStore(string path)
{
    private readonly object _lock = new();

    public string Path => path;

    public static LimitHistoryStore CreateDefault() => new(System.IO.Path.Combine(AppIdentity.LocalFolder, "limit-history.jsonl"));

    public void Append(IEnumerable<LimitReading> readings)
    {
        var lines = readings.Select(r => JsonSerializer.Serialize(r)).ToList();
        if (lines.Count == 0)
        {
            return;
        }

        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                File.AppendAllLines(path, lines);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public IReadOnlyList<LimitReading> Read()
    {
        lock (_lock)
        {
            if (!File.Exists(path))
            {
                return [];
            }

            var readings = new List<LimitReading>();
            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    try
                    {
                        if (JsonSerializer.Deserialize<LimitReading>(line) is { } reading)
                        {
                            readings.Add(reading);
                        }
                    }
                    catch (JsonException)
                    {
                        // a line cut short by a crash
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            return readings;
        }
    }
}
