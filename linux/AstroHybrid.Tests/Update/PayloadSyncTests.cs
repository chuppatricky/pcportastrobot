using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using AstroHybrid.Linux.Update;
using Xunit;

namespace AstroHybrid.Tests.Update;

public class PayloadSyncTests
{
    /// <summary>Every file that setup or the player owns, with the same names also present in the payload
    /// (a worst-case payload), must survive a sync byte for byte.</summary>
    private static readonly string[] Owned =
    {
        "Build/RunHybrid/app0/eboot.bin", "Build/RunHybrid/AstroBot.exe", "Build/RunHybrid/AstroBot-NonRT-Ultrawide.exe",
        "Build/RunHybrid/AstroBot.needed.json", "Build/RunHybrid/AstroBot.libc.prx.guest.prx.registry.json",
        "Build/RunHybrid/_sd/slot0/save.dat", "Build/RunHybrid/_sd_mem/m.bin", "Build/RunHybrid/_trophy/t.dat",
        "Build/RunHybrid/_PipelineCache/p.bin", "Build/RunHybrid/astrohybrid-game.json", "Build/RunHybrid/app0-aliases.txt",
        "Build/astrohybrid-settings.json", "Build/astrohybrid-settings.json.bak", "Build/astrohybrid-linux-settings.json",
        "Logs/hybrid-run.out.log", "prefixes/wine-system/system.reg", "mods/a.txt",
    };

    private static Dictionary<string, string> Hashes(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    [Fact]
    public void CopiesRuntimeAndNeverTouchesOwnedFiles()
    {
        using var t = new TestDir("payload");
        var payload = Path.Combine(t.Path, "payload");
        var data = Path.Combine(t.Path, "data");
        var stamp = Path.Combine(data, ".payload-version");

        foreach (var f in Owned) t.Write(Path.Combine("data", f), "user " + f);
        t.Write("data/Build/RunHybrid/kyty_gpu.dll", "old kyty");
        t.Write("data/Build/RunHybrid/libs/libc.prx", "old libc");
        t.Write("data/Build/RunHybrid/stale-from-old-version.dll", "stale");

        foreach (var f in Owned) t.Write(Path.Combine("payload", f), "PAYLOAD " + f);
        t.Write("payload/Build/RunHybrid/kyty_gpu.dll", "new kyty");
        t.Write("payload/Build/RunHybrid/libs/libc.prx", "new libc");
        t.Write("payload/Build/RunHybrid/anyps5-fonts/NotoSans.ttf", "font");
        t.Write("payload/licenses/amd_fsr.license.txt", "MIT");
        // The AppImage's squashfs is read-only; the copies must still be replaceable by the next sync.
        File.SetUnixFileMode(Path.Combine(payload, "Build/RunHybrid/kyty_gpu.dll"), UnixFileMode.UserRead);

        var before = Hashes(data);
        var r = PayloadSync.EnsureCurrent(payload, data, stamp, "1.2.10-linux.2");
        var after = Hashes(data);

        Assert.True(r.Synced);
        Assert.Equal(4, r.FilesCopied);
        Assert.Equal(Owned.Length, r.FilesProtected);
        var changed = after.Keys.Where(k => !before.TryGetValue(k, out var h) || h != after[k]).OrderBy(k => k).ToArray();
        Assert.Equal(new[]
        {
            ".payload-version",
            "Build/RunHybrid/anyps5-fonts/NotoSans.ttf",
            "Build/RunHybrid/kyty_gpu.dll",
            "Build/RunHybrid/libs/libc.prx",
            "licenses/amd_fsr.license.txt",
        }, changed);
        Assert.Equal("new kyty", File.ReadAllText(Path.Combine(data, "Build/RunHybrid/kyty_gpu.dll")));
        Assert.True(File.Exists(Path.Combine(data, "Build/RunHybrid/stale-from-old-version.dll")));
        Assert.Equal("1.2.10-linux.2", File.ReadAllText(stamp).Trim());
        Assert.Empty(Directory.EnumerateFiles(data, "*.payload-tmp", SearchOption.AllDirectories));

        // Same version again: nothing to do. A newer version overwrites the (now writable) copies.
        Assert.False(PayloadSync.EnsureCurrent(payload, data, stamp, "1.2.10-linux.2").Synced);
        File.WriteAllText(Path.Combine(payload, "Build/RunHybrid/libs/libc.prx"), "newer libc");
        Assert.True(PayloadSync.EnsureCurrent(payload, data, stamp, "1.2.10-linux.3").Synced);
        Assert.Equal("newer libc", File.ReadAllText(Path.Combine(data, "Build/RunHybrid/libs/libc.prx")));
    }

    [Fact]
    public void ReplacesASymlinkInsteadOfWritingThroughIt()
    {
        using var t = new TestDir("symlink");
        var outside = t.Write("outside.dll", "must stay");
        t.Write("payload/Build/RunHybrid/kyty_gpu.dll", "new");
        Directory.CreateDirectory(Path.Combine(t.Path, "data/Build/RunHybrid"));
        File.CreateSymbolicLink(Path.Combine(t.Path, "data/Build/RunHybrid/kyty_gpu.dll"), outside);
        PayloadSync.EnsureCurrent(Path.Combine(t.Path, "payload"), Path.Combine(t.Path, "data"),
                                  Path.Combine(t.Path, "data/.payload-version"), "1");
        Assert.Equal("must stay", File.ReadAllText(outside));
        Assert.Equal("new", File.ReadAllText(Path.Combine(t.Path, "data/Build/RunHybrid/kyty_gpu.dll")));
    }

    [Fact]
    public void MissingPayloadWritesNoStamp()
    {
        using var t = new TestDir("nopayload");
        var stamp = Path.Combine(t.Path, ".payload-version");
        Assert.False(PayloadSync.EnsureCurrent(Path.Combine(t.Path, "nope"), t.Path, stamp, "1").Synced);
        Assert.False(File.Exists(stamp));
    }
}
