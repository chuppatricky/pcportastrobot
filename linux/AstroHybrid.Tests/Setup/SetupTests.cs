using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AstroHybrid.App.Services;
using AstroHybrid.Linux.Setup;
using Xunit;

namespace AstroHybrid.Tests.Setup;

public sealed class SetupTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ah-setup-test-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string rel, string text)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        return p;
    }

    [Fact]
    public void CpuVendorComesFromProcCpuinfo() =>
        Assert.Contains(GameSetup.CpuVendor(), new[] { "AuthenticAMD", "GenuineIntel" });

    [Fact]
    public void WineReparsePointsAreFoundByTheirXattr()
    {
        var app0 = Path.Combine(_root, "app0");
        Directory.CreateDirectory(Path.Combine(app0, "data?"));
        Directory.CreateDirectory(Path.Combine(app0, "plain"));
        var setfattr = new ProcessStartInfo("setfattr") { UseShellExecute = false, RedirectStandardError = true };
        foreach (var a in new[] { "-n", "user.WINEREPARSE", "-v", "0xa0000003", Path.Combine(app0, "data?") }) setfattr.ArgumentList.Add(a);
        using (var p = Process.Start(setfattr)!) { p.WaitForExit(); Assert.Equal(0, p.ExitCode); }
        var found = LinuxSetup.WineReparsePoints(app0);
        Assert.Equal(new[] { Path.Combine(app0, "data?") }, found);
    }

    [Fact]
    public void LegacyImportCopiesSettingsAndSavesAndNeverOverwrites()
    {
        var legacy = Path.Combine(_root, "legacy");
        var data = Path.Combine(_root, "data");
        Write("legacy/Build/astrohybrid-settings.json", "{\"fps\": 3, \"gameDumpPath\": \"Z:\\\\home\\\\u\\\\dump\"}");
        var save = Write("legacy/Build/RunHybrid/_sd_mem/u1/slot0.bin", "SAVE");
        Write("legacy/Build/RunHybrid/_trophy/t.txt", "T");
        var before = File.GetLastWriteTimeUtc(save);

        var r = LegacyImport.Run(false, legacy, data);
        Assert.Equal("/home/u/dump", r.DumpPath);
        Assert.Equal("SAVE", File.ReadAllText(Path.Combine(data, "Build", "RunHybrid", "_sd_mem", "u1", "slot0.bin")));
        Assert.Contains("\"gameDumpPath\": \"/home/u/dump\"", File.ReadAllText(Path.Combine(data, "Build", "astrohybrid-settings.json")));
        Assert.Contains("\"fps\": 3", File.ReadAllText(Path.Combine(data, "Build", "astrohybrid-settings.json")));
        Assert.Equal(before, File.GetLastWriteTimeUtc(save));
        Assert.Equal("SAVE", File.ReadAllText(save));

        // A newer save made here is kept.
        File.WriteAllText(Path.Combine(data, "Build", "RunHybrid", "_sd_mem", "u1", "slot0.bin"), "NEWER");
        var again = LegacyImport.Run(false, legacy, data);
        Assert.All(again.Items, i => Assert.True(i.Skipped));
        Assert.Equal("NEWER", File.ReadAllText(Path.Combine(data, "Build", "RunHybrid", "_sd_mem", "u1", "slot0.bin")));
    }

    [Fact]
    public void DryRunCopiesNothing()
    {
        Write("legacy/Build/astrohybrid-settings.json", "{}");
        Write("legacy/Build/RunHybrid/_sd_mem/u1/slot0.bin", "SAVE");
        var r = LegacyImport.Run(true, Path.Combine(_root, "legacy"), Path.Combine(_root, "data"));
        Assert.Equal(2, r.Items.Count(i => !i.Skipped));
        Assert.False(Directory.Exists(Path.Combine(_root, "data")));
    }
}
