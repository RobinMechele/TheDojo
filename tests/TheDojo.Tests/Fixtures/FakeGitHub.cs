using System.Net.Http;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TheDojo.Tests.Fixtures;

/// <summary>A GitHub release (API, exe and checksums) served from memory, for the updater tests.</summary>
public sealed class FakeGitHub(string tag, byte[] exe, string assetName = "TheDojo-win-x64.exe") : HttpMessageHandler
{
    public bool Prerelease { get; init; }

    public bool OmitChecksums { get; init; }

    public bool CorruptChecksum { get; init; }

    public HttpStatusCode ApiStatus { get; init; } = HttpStatusCode.OK;

    public List<string> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        Requests.Add(url);
        if (url.Contains("/releases/latest"))
        {
            return Task.FromResult(ApiStatus == HttpStatusCode.OK
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleaseJson(), Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(ApiStatus));
        }

        if (url.EndsWith(assetName))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(exe) });
        }

        if (url.EndsWith("SHA256SUMS.txt"))
        {
            var hash = CorruptChecksum ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(exe)).ToLowerInvariant();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{hash}  {assetName}\n{new string('a', 64)}  dojo-win-x64.exe\n") });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private string ReleaseJson()
    {
        var assets = new List<object>
        {
            new { name = assetName, browser_download_url = "https://github.com/test/releases/download/" + tag + "/" + assetName, size = exe.Length },
        };
        if (!OmitChecksums)
        {
            assets.Add(new { name = "SHA256SUMS.txt", browser_download_url = "https://github.com/test/releases/download/" + tag + "/SHA256SUMS.txt", size = 100 });
        }

        return JsonSerializer.Serialize(new { tag_name = tag, prerelease = Prerelease, draft = false, body = "Notes for " + tag, assets });
    }
}
