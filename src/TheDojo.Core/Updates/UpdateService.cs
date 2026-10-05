using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace TheDojo.Core.Updates;

/// <summary>A newer release published on GitHub.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string Notes, Uri AssetUrl, long Size, Uri? ChecksumsUrl, string AssetName);

/// <summary>
/// Checks the latest GitHub release and replaces the running single-file exe with it. Windows lets a running exe be
/// renamed but not overwritten, so the old file moves to <c>.old</c> (deleted on the next start) and the new one takes its name.
/// </summary>
public sealed class UpdateService(HttpClient http, string repository, string assetName)
{
    public const string DefaultRepository = "RobinMechele/TheDojo";
    public const string ChecksumsAsset = "SHA256SUMS.txt";

    /// <summary>The release asset for this machine, e.g. <c>TheDojo-win-x64.exe</c>.</summary>
    public static string AssetNameFor(string product, System.Runtime.InteropServices.Architecture architecture) =>
        $"{product}-win-{architecture.ToString().ToLowerInvariant()}.exe";

    /// <summary>The running version, without build metadata (<c>1.2.3+abc</c> becomes 1.2.3).</summary>
    public static Version CurrentVersion(Assembly? assembly = null)
    {
        var text = (assembly ?? typeof(UpdateService).Assembly).GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return TryParseVersion(text, out var version) ? version : new Version(0, 0, 0);
    }

    /// <summary>Accepts <c>v1.2.3</c>, <c>1.2.3</c> and <c>1.2.3-beta+meta</c>; builds are compared on their first three parts.</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var core = text.Trim().TrimStart('v', 'V').Split('+', '-')[0];
        if (!Version.TryParse(core, out var parsed))
        {
            return false;
        }

        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return true;
    }

    /// <summary>The latest published release if it is newer than <paramref name="current"/> and has this machine's exe; otherwise null. Never throws on network or format problems.</summary>
    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
            request.Headers.UserAgent.ParseAdd(AppIdentity.FileName + "/" + current);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return Parse(doc.RootElement, current);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return null;
        }
    }

    private UpdateInfo? Parse(JsonElement release, Version current)
    {
        if (release.ValueKind != JsonValueKind.Object
            || release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True
            || release.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        var tag = release.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        if (!TryParseVersion(tag, out var version) || version <= current || !release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        Uri? exe = null;
        Uri? sums = null;
        long size = 0;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            var url = asset.TryGetProperty("browser_download_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            if (name is null || url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                continue;
            }

            if (string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase))
            {
                exe = uri;
                size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
            }
            else if (string.Equals(name, ChecksumsAsset, StringComparison.OrdinalIgnoreCase))
            {
                sums = uri;
            }
        }

        // Without checksums there is nothing to verify the download against, so such a release is not offered.
        if (exe is null || sums is null)
        {
            return null;
        }

        var notes = release.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? string.Empty : string.Empty;
        return new UpdateInfo(version, tag!, notes, exe, size, sums, assetName);
    }

    /// <summary>
    /// Downloads the release exe, checks it against the published SHA-256, and swaps it in for <paramref name="targetExe"/>.
    /// Leaves the current exe untouched when anything fails.
    /// </summary>
    public async Task InstallAsync(UpdateInfo update, string targetExe, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var incoming = targetExe + ".new";
        var backup = targetExe + ".old";
        try
        {
            var expected = await ExpectedHashAsync(update, cancellationToken).ConfigureAwait(false);
            await DownloadAsync(update, incoming, progress, cancellationToken).ConfigureAwait(false);

            var actual = await HashAsync(incoming, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The downloaded update doesn't match its published checksum, so it was discarded.");
            }

            File.Delete(backup);
            File.Move(targetExe, backup);
            try
            {
                File.Move(incoming, targetExe);
            }
            catch
            {
                File.Move(backup, targetExe, overwrite: true);
                throw;
            }
        }
        finally
        {
            TryDelete(incoming);
        }
    }

    /// <summary>Removes the previous exe left behind by an update. Call at startup.</summary>
    public static void CleanUp(string targetExe)
    {
        TryDelete(targetExe + ".old");
        TryDelete(targetExe + ".new");
    }

    private async Task<string> ExpectedHashAsync(UpdateInfo update, CancellationToken cancellationToken)
    {
        if (update.ChecksumsUrl is null)
        {
            throw new InvalidDataException("The release has no checksums.");
        }

        var text = await http.GetStringAsync(update.ChecksumsUrl, cancellationToken).ConfigureAwait(false);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // "<hash>  <file>" (sha256sum) or "<hash> *<file>" (binary mode)
            var parts = line.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*').Equals(update.AssetName, StringComparison.OrdinalIgnoreCase) && parts[0].Length == 64)
            {
                return parts[0];
            }
        }

        throw new InvalidDataException("The release checksums don't list " + update.AssetName + ".");
    }

    private async Task DownloadAsync(UpdateInfo update, string path, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(update.AssetUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? update.Size;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            done += read;
            if (total > 0)
            {
                progress?.Report(Math.Min(1, (double)done / total));
            }
        }

        progress?.Report(1);
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // still in use or locked; the next start tries again
        }
    }
}
