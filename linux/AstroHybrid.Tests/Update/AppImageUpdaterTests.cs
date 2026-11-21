using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AstroHybrid.Linux.Platform;
using AstroHybrid.Linux.Update;
using Xunit;

namespace AstroHybrid.Tests.Update;

public class AppImageUpdaterTests
{
    private const string Api = "http://mock/releases";
    private static readonly byte[] NewImage = Encoding.ASCII.GetBytes("new appimage payload " + new string('x', 200_000));
    private static string Sha(byte[] b) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(b));

    /// <summary>Serves the releases JSON and asset bytes from memory.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        public readonly Dictionary<string, byte[]> Files = new();
        public int Downloads;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (!Files.TryGetValue(url, out var body)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (url != Api) Downloads++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    private static object Release(string tag, string digest, bool prerelease = false, bool draft = false, string assetName = null)
    {
        var ver = tag.TrimStart('v');
        var asset = new Dictionary<string, object>
        {
            ["name"] = assetName ?? AppImageUpdater.AssetName(ver),
            ["browser_download_url"] = $"http://mock/dl/{ver}",
            ["size"] = NewImage.Length,
        };
        if (digest != null) asset["digest"] = digest;
        return new Dictionary<string, object>
        {
            ["tag_name"] = tag, ["html_url"] = $"http://mock/page/{tag}", ["body"] = "notes",
            ["draft"] = draft, ["prerelease"] = prerelease, ["assets"] = new[] { asset },
        };
    }

    private static (AppImageUpdater, FakeGitHub) Make(string current, string appImage, params object[] releases)
    {
        var gh = new FakeGitHub();
        gh.Files[Api] = JsonSerializer.SerializeToUtf8Bytes(releases);
        foreach (Dictionary<string, object> r in releases)
            gh.Files[$"http://mock/dl/{((string)r["tag_name"]).TrimStart('v')}"] = NewImage;
        return (new AppImageUpdater(new HttpClient(gh), Api, current, () => appImage), gh);
    }

    [Fact]
    public async Task PicksNewestStableReleaseWithDigest()
    {
        var (u, _) = Make("1.2.10-linux.1", null,
            Release("v1.2.10-linux.2", Sha(NewImage)),
            Release("v1.2.10-linux.5", Sha(NewImage), prerelease: true),
            Release("v1.2.10-linux.6", Sha(NewImage), draft: true),
            Release("v1.2.10-linux.4", null),                                       // no digest: never offered
            Release("v1.2.10-linux.7", Sha(NewImage), assetName: "AstroBotPC-AstroHybrid-1.2.10.zip"),
            Release("v1.2.10-linux.3", Sha(NewImage)),
            Release("v1.2.9", Sha(NewImage)));
        var offer = await u.CheckAsync(CancellationToken.None);
        Assert.Equal("1.2.10-linux.3", offer.Version);
        Assert.Equal("v1.2.10-linux.3", offer.Tag);
        Assert.Equal(Sha(NewImage), offer.Digest);
        Assert.Equal("http://mock/page/v1.2.10-linux.3", offer.HtmlUrl);
    }

    [Fact]
    public async Task UpstreamStyleVersionBeatsForkRevision()
    {
        var (u, _) = Make("1.2.10-linux.3", null, Release("v1.2.11", Sha(NewImage)));
        Assert.Equal("1.2.11", (await u.CheckAsync(CancellationToken.None)).Version);
    }

    [Fact]
    public async Task NullWhenUpToDateOrOnlyUndigestedNewer()
    {
        var (u, _) = Make("1.2.10-linux.2", null, Release("v1.2.10-linux.2", Sha(NewImage)), Release("v1.2.10-linux.3", null));
        Assert.Null(await u.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task InstallReplacesFileAtomicallyAndMakesItExecutable()
    {
        using var dir = new TestDir("install");
        var image = dir.Write("AstroHybrid.AppImage", "old");
        var (u, _) = Make("1.2.10-linux.1", image, Release("v1.2.10-linux.2", Sha(NewImage)));
        var offer = await u.CheckAsync(CancellationToken.None);
        var reports = new List<double>();
        var (ok, msg) = await u.InstallAsync(offer, new SyncProgress(reports.Add), CancellationToken.None);
        Assert.True(ok, msg);
        Assert.Equal(NewImage, File.ReadAllBytes(image));
        Assert.Equal((UnixFileMode)0b111_101_101, File.GetUnixFileMode(image));
        Assert.False(File.Exists(Path.Combine(dir.Path, AppImageUpdater.TempName)));
        Assert.Equal(1.0, reports[^1]);
    }

    [Fact]
    public async Task WrongDigestIsRejectedAndFileUntouched()
    {
        using var dir = new TestDir("baddigest");
        var image = dir.Write("AstroHybrid.AppImage", "old");
        var (u, _) = Make("1.2.10-linux.1", image, Release("v1.2.10-linux.2", Sha(Encoding.ASCII.GetBytes("something else"))));
        var (ok, msg) = await u.InstallAsync(await u.CheckAsync(CancellationToken.None), null, CancellationToken.None);
        Assert.False(ok);
        Assert.Contains("verification", msg);
        Assert.Equal("old", File.ReadAllText(image));
        Assert.False(File.Exists(Path.Combine(dir.Path, AppImageUpdater.TempName)));
    }

    [Fact]
    public async Task ReadOnlyDirectoryFallsBackWithoutDownloading()
    {
        using var dir = new TestDir("readonly");
        var sub = Path.Combine(dir.Path, "ro");
        Directory.CreateDirectory(sub);
        var image = Path.Combine(sub, "AstroHybrid.AppImage");
        File.WriteAllText(image, "old");
        File.SetUnixFileMode(sub, (UnixFileMode)0b101_101_101);
        var (u, gh) = Make("1.2.10-linux.1", image, Release("v1.2.10-linux.2", Sha(NewImage)));
        var (ok, msg) = await u.InstallAsync(await u.CheckAsync(CancellationToken.None), null, CancellationToken.None);
        Assert.False(ok);
        Assert.Contains("not writable", msg);
        Assert.Contains("http://mock/page/v1.2.10-linux.2", msg);
        Assert.Equal(0, gh.Downloads);
        Assert.Equal("old", File.ReadAllText(image));
    }

    [Fact]
    public async Task NotAnAppImageFallsBack()
    {
        var (u, gh) = Make("1.2.10-linux.1", null, Release("v1.2.10-linux.2", Sha(NewImage)));
        var (ok, msg) = await u.InstallAsync(await u.CheckAsync(CancellationToken.None), null, CancellationToken.None);
        Assert.False(ok);
        Assert.Contains("Not running from an AppImage", msg);
        Assert.Equal(0, gh.Downloads);
    }

    [Fact]
    public async Task SkippedVersionIsOfferedAndStillInstallable()
    {
        using var dir = new TestDir("skip");
        var image = dir.Write("AstroHybrid.AppImage", "old");
        var (u, _) = Make("1.2.10-linux.1", image, Release("v1.2.10-linux.2", Sha(NewImage)));
        var settings = new LinuxSettings { SkippedVersion = "1.2.10-linux.2" };
        var offer = await u.CheckAsync(CancellationToken.None);
        Assert.True(AppImageUpdater.IsSkipped(offer, settings));
        Assert.False(AppImageUpdater.IsSkipped(offer, new LinuxSettings { SkippedVersion = "1.2.10-linux.1" }));
        var (ok, msg) = await u.InstallAsync(offer, null, CancellationToken.None);
        Assert.True(ok, msg);
        Assert.Equal(NewImage, File.ReadAllBytes(image));
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
