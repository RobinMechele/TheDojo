using System.IO.Compression;
using System.Text.Json;
using TheDojo.Core.Model;

namespace TheDojo.Core.Ingest;

/// <summary>
/// Reads every log an agent has written under one folder. A cold scan of months of history is hundreds of
/// megabytes, so parsed results are kept per file, keyed by size and last-write time, in memory and on disk:
/// later scans only re-read files that changed (in practice, the sessions still being written to).
/// </summary>
public sealed class LogScanner
{
    /// <summary>Bump when a parser starts reading something new, so cached results are re-parsed.</summary>
    public const int CacheVersion = 4;

    private readonly string _root;
    private readonly string _fileFilter;
    private readonly Func<string, SessionLog> _parse;
    private readonly string? _cachePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, CachedFile>? _cache;

    public LogScanner(string root, string fileFilter, Func<string, SessionLog> parse, string? cachePath = null)
    {
        _root = root;
        _fileFilter = fileFilter;
        _parse = parse;
        _cachePath = cachePath;
    }

    public string Root => _root;

    private sealed record CachedFile(long Length, long LastWriteTicks, SessionLog Log);

    private sealed record CacheDocument(int Version, Dictionary<string, CachedFile> Files);

    /// <summary>Logs of every file written to at or after <paramref name="since"/>, in path order (so "first copy wins" is stable).</summary>
    public async Task<IReadOnlyList<SessionLog>> ScanAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_root))
        {
            return [];
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Scan(since, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private List<SessionLog> Scan(DateTimeOffset since, CancellationToken cancellationToken)
    {
        var cache = _cache ??= LoadCache();
        var files = new List<(string Path, long Length, long Ticks)>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(_root, _fileFilter, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.LastWriteTimeUtc >= since.UtcDateTime)
                    {
                        files.Add((path, info.Length, info.LastWriteTimeUtc.Ticks));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        // Main transcripts before their subagents' and older files first, so the original copy of copied history wins.
        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

        var stale = files.Where(f => !cache.TryGetValue(f.Path, out var c) || c.Length != f.Length || c.LastWriteTicks != f.Ticks).ToList();
        var parsed = new Dictionary<string, CachedFile>();
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8), CancellationToken = cancellationToken };
        Parallel.ForEach(stale, options, f =>
        {
            SessionLog log;
            try
            {
                log = _parse(f.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return; // being rewritten; picked up by the next scan
            }

            lock (parsed)
            {
                parsed[f.Path] = new CachedFile(f.Length, f.Ticks, log);
            }
        });

        foreach (var (path, file) in parsed)
        {
            cache[path] = file;
        }

        if (parsed.Count > 0)
        {
            SaveCache(cache);
        }

        return files.Where(f => cache.ContainsKey(f.Path)).Select(f => cache[f.Path].Log).ToList();
    }

    private Dictionary<string, CachedFile> LoadCache()
    {
        if (_cachePath is null || !File.Exists(_cachePath))
        {
            return new Dictionary<string, CachedFile>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            using var stream = new GZipStream(File.OpenRead(_cachePath), CompressionMode.Decompress);
            var doc = JsonSerializer.Deserialize<CacheDocument>(stream);
            if (doc is { Version: CacheVersion, Files: not null })
            {
                return new Dictionary<string, CachedFile>(doc.Files, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
        }

        return new Dictionary<string, CachedFile>(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveCache(Dictionary<string, CachedFile> cache)
    {
        if (_cachePath is null)
        {
            return;
        }

        // Files deleted since the last scan don't need to be kept.
        foreach (var gone in cache.Keys.Where(p => !File.Exists(p)).ToList())
        {
            cache.Remove(gone);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var temp = _cachePath + ".tmp";
            using (var stream = new GZipStream(File.Create(temp), CompressionLevel.Fastest))
            {
                JsonSerializer.Serialize(stream, new CacheDocument(CacheVersion, cache));
            }

            File.Move(temp, _cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
