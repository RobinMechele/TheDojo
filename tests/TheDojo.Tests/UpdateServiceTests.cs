using System.Net;
using TheDojo.Core.Updates;
using TheDojo.Tests.Fixtures;

namespace TheDojo.Tests;

public sealed class UpdateServiceTests : IDisposable
{
    private static readonly byte[] NewExe = [1, 2, 3, 4, 5, 6, 7, 8];
    private readonly string _dir = Directory.CreateTempSubdirectory("dojo-update-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private static UpdateService Service(FakeGitHub github) => new(new HttpClient(github), "o/r", "TheDojo-win-x64.exe");

    private string InstalledExe()
    {
        var path = Path.Combine(_dir, "TheDojo.exe");
        File.WriteAllBytes(path, [9, 9, 9]);
        return path;
    }

    [Theory]
    [InlineData("v1.2.3", true, "1.2.3")]
    [InlineData("1.2.3", true, "1.2.3")]
    [InlineData("1.2.3+abcdef", true, "1.2.3")]
    [InlineData("v2.0.0-beta.1", true, "2.0.0")]
    [InlineData("1.4", true, "1.4.0")]
    [InlineData("latest", false, "0.0.0")]
    [InlineData("", false, "0.0.0")]
    [InlineData(null, false, "0.0.0")]
    public void TryParseVersion_ReadsTagsAndInformationalVersions(string? text, bool ok, string expected)
    {
        Assert.Equal(ok, UpdateService.TryParseVersion(text, out var version));
        Assert.Equal(Version.Parse(expected), version);
    }

    [Fact]
    public void AssetName_FollowsTheMachineArchitecture() =>
        Assert.Equal("TheDojo-win-arm64.exe", UpdateService.AssetNameFor("TheDojo", System.Runtime.InteropServices.Architecture.Arm64));

    [Fact]
    public async Task Check_OffersANewerRelease()
    {
        var update = await Service(new FakeGitHub("v1.3.0", NewExe)).CheckAsync(new Version(1, 2, 0));

        Assert.NotNull(update);
        Assert.Equal(new Version(1, 3, 0), update.Version);
        Assert.Equal("Notes for v1.3.0", update.Notes);
        Assert.Equal(NewExe.Length, update.Size);
    }

    [Theory]
    [InlineData("v1.2.0")]
    [InlineData("v1.1.9")]
    public async Task Check_IgnoresTheSameOrAnOlderRelease(string tag) =>
        Assert.Null(await Service(new FakeGitHub(tag, NewExe)).CheckAsync(new Version(1, 2, 0)));

    [Fact]
    public async Task Check_IgnoresPrereleases() =>
        Assert.Null(await Service(new FakeGitHub("v9.0.0", NewExe) { Prerelease = true }).CheckAsync(new Version(1, 0, 0)));

    [Fact]
    public async Task Check_IgnoresReleasesWithoutChecksums() =>
        Assert.Null(await Service(new FakeGitHub("v9.0.0", NewExe) { OmitChecksums = true }).CheckAsync(new Version(1, 0, 0)));

    [Fact]
    public async Task Check_IgnoresReleasesWithoutThisMachinesExe() =>
        Assert.Null(await Service(new FakeGitHub("v9.0.0", NewExe, assetName: "TheDojo-win-arm64.exe")).CheckAsync(new Version(1, 0, 0)));

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Check_QuietlyReturnsNothingWhenGitHubSaysNo(HttpStatusCode status) =>
        Assert.Null(await Service(new FakeGitHub("v9.0.0", NewExe) { ApiStatus = status }).CheckAsync(new Version(1, 0, 0)));

    [Fact]
    public async Task Check_SurvivesAnUnreadableResponse()
    {
        var service = new UpdateService(new HttpClient(new Garbage()), "o/r", "TheDojo-win-x64.exe");
        Assert.Null(await service.CheckAsync(new Version(1, 0, 0)));
    }

    [Fact]
    public async Task Check_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(new FakeGitHub("v9.0.0", NewExe)).CheckAsync(new Version(1, 0, 0), cts.Token));
    }

    [Fact]
    public async Task Install_ReplacesTheExeAndKeepsTheOldOneUntilNextStart()
    {
        var exe = InstalledExe();
        var service = Service(new FakeGitHub("v1.3.0", NewExe));
        var update = (await service.CheckAsync(new Version(1, 0, 0)))!;
        var progress = new List<double>();

        await service.InstallAsync(update, exe, new Progress<double>(progress.Add));
        await Task.Delay(50);

        Assert.Equal(NewExe, File.ReadAllBytes(exe));
        Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(exe + ".old"));
        Assert.False(File.Exists(exe + ".new"));
        Assert.Contains(1.0, progress);

        UpdateService.CleanUp(exe);
        Assert.False(File.Exists(exe + ".old"));
    }

    [Fact]
    public async Task Install_RefusesADownloadThatDoesntMatchItsChecksum()
    {
        var exe = InstalledExe();
        var service = Service(new FakeGitHub("v1.3.0", NewExe) { CorruptChecksum = true });
        var update = (await service.CheckAsync(new Version(1, 0, 0)))!;

        await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallAsync(update, exe));

        Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(exe));
        Assert.False(File.Exists(exe + ".new"));
        Assert.False(File.Exists(exe + ".old"));
    }

    [Fact]
    public async Task Install_LeavesTheExeAloneWhenTheDownloadCantBeSaved()
    {
        var exe = InstalledExe();
        var service = Service(new FakeGitHub("v1.3.0", NewExe));
        var update = (await service.CheckAsync(new Version(1, 0, 0)))!;

        // A folder squatting on the .new path makes saving the download fail.
        Directory.CreateDirectory(exe + ".new");
        await Assert.ThrowsAnyAsync<Exception>(() => service.InstallAsync(update, exe));

        Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(exe));
    }

    [Fact]
    public void CleanUp_IgnoresMissingFiles() => UpdateService.CleanUp(Path.Combine(_dir, "nothing.exe"));

    private sealed class Garbage : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>not json</html>") });
    }
}
