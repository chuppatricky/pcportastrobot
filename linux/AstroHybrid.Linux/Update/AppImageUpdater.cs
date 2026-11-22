using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AstroHybrid.Linux.Platform;

namespace AstroHybrid.Linux.Update;

/// <summary>
/// Self-update of the AppImage from the fork's GitHub releases: pick the newest stable
/// AstroHybrid-Linux-&lt;ver&gt;-x86_64.AppImage that carries an API sha256 digest, download it next to
/// $APPIMAGE, verify, chmod 755 and rename() it over $APPIMAGE. The rename is atomic and the running
/// FUSE mount keeps the old inode, so the current process is unaffected until <see cref="Relaunch"/>.
/// </summary>
public sealed class AppImageUpdater : IUpdater
{
    public const string DefaultApi = "https://api.github.com/repos/chuppatricky/pcportastrobot/releases";
    /// <summary>Env var that replaces <see cref="DefaultApi"/> (tests serve a mock releases JSON).</summary>
    public const string ApiEnvVar = "ASTROHYBRID_UPDATE_API";
    /// <summary>The temporary download, a dot file in $APPIMAGE's directory so rename() stays on one filesystem.</summary>
    public const string TempName = ".AstroHybrid.new";

    private static readonly Regex DigestPattern = new("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    private readonly HttpClient _http;
    private readonly string _apiUrl;
    private readonly Func<string> _appImage;

    public string CurrentVersion { get; }

    public AppImageUpdater() : this(null, null, null, null) { }

    /// <param name="http">Client to use; a new one by default.</param>
    /// <param name="apiUrl">Releases endpoint; $ASTROHYBRID_UPDATE_API, else <see cref="DefaultApi"/>.</param>
    /// <param name="currentVersion">Version to compare against; the assembly's InformationalVersion by default.</param>
    /// <param name="appImage">Returns the file to replace; $APPIMAGE by default.</param>
    public AppImageUpdater(HttpClient http, string apiUrl, string currentVersion, Func<string> appImage)
    {
        CurrentVersion = currentVersion ?? AssemblyVersion;
        _apiUrl = apiUrl ?? (Environment.GetEnvironmentVariable(ApiEnvVar) is { Length: > 0 } env ? env : DefaultApi);
        _appImage = appImage ?? (() => AppPaths.AppImageFile);
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    }

    /// <summary>InformationalVersion of the launcher (e.g. "1.2.10-linux.1") without the SDK's "+commit" suffix.</summary>
    public static string AssemblyVersion
    {
        get
        {
            var v = typeof(AppImageUpdater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            int plus = v.IndexOf('+');
            return plus >= 0 ? v[..plus] : v;
        }
    }

    public static string AssetName(string version) => $"AstroHybrid-Linux-{version}-x86_64.AppImage";

    /// <summary>True when the user chose "skip this version" for exactly this offer. Only the banner/notification
    /// honours it; an explicit install of a skipped version still goes through (upstream's updater refused to).</summary>
    public static bool IsSkipped(UpdateOffer offer, LinuxSettings settings) =>
        offer != null && !string.IsNullOrEmpty(settings.SkippedVersion) && settings.SkippedVersion == offer.Version;

    public async Task<UpdateOffer> CheckAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _apiUrl);
        req.Headers.UserAgent.ParseAdd($"AstroHybrid-Linux/{CurrentVersion}");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var releases = JsonSerializer.Deserialize<List<Release>>(json) ?? new List<Release>();
        return SelectOffer(releases, CurrentVersion);
    }

    /// <summary>The newest stable release that is newer than <paramref name="current"/> and has a
    /// correctly named asset with a sha256 digest; null when there is none.</summary>
    internal static UpdateOffer SelectOffer(IEnumerable<Release> releases, string current)
    {
        ForkVersion.TryParse(current, out var cur);
        UpdateOffer best = null;
        ForkVersion bestVersion = cur;
        foreach (var r in releases)
        {
            if (r.Draft || r.Prerelease || !ForkVersion.TryParse(r.TagName, out var v)) continue;
            if (bestVersion != null && v.CompareTo(bestVersion) <= 0) continue;
            var version = r.TagName.StartsWith('v') ? r.TagName[1..] : r.TagName;
            var asset = r.Assets?.Find(a => a.Name == AssetName(version));
            // No digest, no offer: the digest is the only integrity check we have.
            if (asset == null || asset.Digest == null || !DigestPattern.IsMatch(asset.Digest)) continue;
            best = new UpdateOffer
            {
                Version = version,
                Tag = r.TagName,
                AssetUrl = asset.BrowserDownloadUrl,
                Size = asset.Size,
                Digest = asset.Digest,
                Notes = r.Body ?? "",
                HtmlUrl = r.HtmlUrl ?? "",
            };
            bestVersion = v;
        }
        return best;
    }

    public async Task<(bool Ok, string Message)> InstallAsync(UpdateOffer offer, IProgress<double> progress, CancellationToken ct)
    {
        var target = _appImage();
        if (string.IsNullOrEmpty(target) || !File.Exists(target))
            return (false, $"Not running from an AppImage, so it cannot update itself. Download {offer.Version} from {offer.HtmlUrl}");
        if (offer.Digest == null || !DigestPattern.IsMatch(offer.Digest))
            return (false, $"Release {offer.Version} has no sha256 digest; refusing to install it.");

        var tmp = Path.Combine(Path.GetDirectoryName(target)!, TempName);
        FileStream file;
        try
        {
            file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        }
        catch (Exception e) when (e is UnauthorizedAccessException || e is IOException)
        {
            return (false, $"{Path.GetDirectoryName(target)} is not writable ({e.Message}). Download {offer.Version} from {offer.HtmlUrl}");
        }

        try
        {
            string actual;
            using (file)
            {
                using var resp = await _http.GetAsync(offer.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? offer.Size;
                using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buf = new byte[1 << 16];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buf, 0, n);
                    await file.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    done += n;
                    if (total > 0) progress?.Report(Math.Min(1.0, (double)done / total));
                }
                // fsync before the rename, so a crash can never leave a truncated AppImage in place.
                file.Flush(flushToDisk: true);
                actual = "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            if (actual != offer.Digest)
            {
                File.Delete(tmp);
                return (false, $"Download of {offer.Version} failed verification (expected {offer.Digest}, got {actual}); nothing was changed.");
            }

            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                      UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                      UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            // Same directory, so this is a single rename(2) over the old file.
            File.Move(tmp, target, overwrite: true);
            progress?.Report(1.0);
            return (true, $"Installed {offer.Version} to {target}. Restart AstroHybrid to use it.");
        }
        catch (Exception e)
        {
            try { File.Delete(tmp); } catch (IOException) { }
            if (e is OperationCanceledException) throw;
            return (false, $"Update to {offer.Version} failed: {e.Message}");
        }
    }

    public void Relaunch(string[] args)
    {
        var target = _appImage();
        if (string.IsNullOrEmpty(target))
            throw new InvalidOperationException("not running from an AppImage");
        Console.Out.Flush();
        Console.Error.Flush();
        // execv wants a NULL-terminated char*[]; the marshaller has no UTF-8 string-array mode, so build it by hand.
        var argv = new IntPtr[args.Length + 2];
        argv[0] = Marshal.StringToCoTaskMemUTF8(target);
        for (var i = 0; i < args.Length; i++) argv[i + 1] = Marshal.StringToCoTaskMemUTF8(args[i]);
        execv(target, argv);
        var error = Marshal.GetLastPInvokeErrorMessage();
        foreach (var p in argv) if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
        throw new InvalidOperationException($"execv {target} failed: {error}");
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int execv([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr[] argv);

    internal sealed class Release
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; }
        [JsonPropertyName("html_url")] public string HtmlUrl { get; set; }
        [JsonPropertyName("body")] public string Body { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<Asset> Assets { get; set; }
    }

    internal sealed class Asset
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("digest")] public string Digest { get; set; }
    }
}
