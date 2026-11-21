using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AstroHybrid.App.Models;
using AstroHybrid.App.Services;
using AstroHybrid.Linux.Launch;
using AstroHybrid.Linux.Platform;
using Xunit;

namespace AstroHybrid.Tests.Launch;

/// <summary>
/// GameEnvironment must give the game exactly what Run-AstroHybrid.bat gives it on Windows.
/// The golden files are env dumps of the real bat run by Wine's cmd, with its final `start` line
/// replaced by `set`. Regenerate them with AH_GOLDEN=1 (needs `wine` on PATH); normally they are
/// read from TestData so the test needs no Wine.
/// </summary>
public sealed class GameEnvironmentTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ah-env-").FullName;
    private string RunHybrid => Path.Combine(_root, "Build", "RunHybrid");

    public GameEnvironmentTests()
    {
        Directory.CreateDirectory(RunHybrid);
        Directory.CreateDirectory(Path.Combine(_root, "Logs"));
        Directory.CreateDirectory(Path.Combine(_root, "mods"));
        // Every variant exists, so the launcher picks without patching anything.
        foreach (var exe in new[] { "AstroBot.exe", "AstroBot-NonRT.exe", "AstroBot-NonRT-SoftAssert.exe" })
            File.WriteAllBytes(Path.Combine(RunHybrid, exe), new byte[] { 0x4d, 0x5a });
        File.Copy(TestData("u59-preset.cmd"), Path.Combine(RunHybrid, "u59-preset.cmd"));
        File.WriteAllText(Path.Combine(RunHybrid, "app0-aliases.txt"), "/app0/data=/mnt/usb/PPSA21567-app/data");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string TestData(string name) => Path.Combine(SourceDir(), "TestData", name);

    private static string SourceDir([System.Runtime.CompilerServices.CallerFilePath] string file = "") => Path.GetDirectoryName(file)!;

    public static IEnumerable<object[]> Cases() => new[]
    {
        new object[] { "defaults" },
        new object[] { "fsr" },
        new object[] { "dlss-fg" },
        new object[] { "advanced-off" },
    };

    private static AppSettings Settings(string name)
    {
        var s = new AppSettings();
        switch (name)
        {
            case "fsr": s.Upscale = 2; s.SkipIntro = true; s.Fps = 3; break;
            case "dlss-fg": s.Upscale = 1; s.FrameGenMode = 2; s.LowLatency = true; break;
            case "advanced-off": s.HangWatchdog = true; s.ShaderCache = false; s.DrawPrepTuning = false; s.Priority = "normal"; s.VSync = true; break;
        }
        return s;
    }

    private Dictionary<string, string> LauncherEnv(AppSettings s) => new LauncherService(_root).BuildEnvironmentVariables(s, null);

    private static bool GameKey(string k) =>
        k.StartsWith("KYTY_") || k.StartsWith("ASTROHYBRID_") || k.StartsWith("APS5_") || k.StartsWith("ANYPS5_");

    // Differences that are on purpose: the fork's own settings, and the variables the Linux runner
    // adds for Wine itself.
    private static readonly HashSet<string> ForkOnly = new() { "ASTROHYBRID_FSR_VERSION", "ASTROHYBRID_FSR_SHARPNESS", "ASTROHYBRID_FG" };

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesTheBatRunByWineCmd(string name)
    {
        var settings = Settings(name);
        var golden = TestData($"golden-{name}.env");
        if (Environment.GetEnvironmentVariable("AH_GOLDEN") == "1")
            File.WriteAllText(golden, RunBatUnderWine(settings));
        Assert.True(File.Exists(golden), $"missing {golden}; run the tests once with AH_GOLDEN=1");

        var expected = Normalize(File.ReadAllLines(golden).Select(Split));
        // The one intended difference: the bat passes app0-aliases.txt on as written (Windows setup writes
        // Windows paths); Linux setup writes Linux paths, which GameEnvironment turns into Wine paths.
        if (expected.TryGetValue("APS5_PATH_ALIASES", out var aliases))
            expected["APS5_PATH_ALIASES"] = GameEnvironment.AliasesToWine(aliases);
        var actual = Normalize(GameEnvironment.Build(LauncherEnv(settings), _ => null, _root, settings, new LinuxSettings())
            .Select(kv => (kv.Key, kv.Value)));

        foreach (var (k, v) in expected)
            Assert.True(actual.TryGetValue(k, out var got) && string.Equals(got, v, StringComparison.OrdinalIgnoreCase),
                $"{name}: {k} expected '{v}', got '{(actual.TryGetValue(k, out var g) ? g : "<unset>")}'");
        var extra = actual.Keys.Except(expected.Keys).Where(k => !ForkOnly.Contains(k)).ToList();
        Assert.True(extra.Count == 0, $"{name}: set only by GameEnvironment: {string.Join(", ", extra)}");
    }

    /// <summary>Game variables only, with this test's temporary root replaced by a placeholder.</summary>
    private Dictionary<string, string> Normalize(IEnumerable<(string Key, string Value)> pairs)
    {
        var winRoot = WinPath.ToWindows(_root);
        var goldenRoot = @"Z:\AH_ROOT";
        return pairs.Where(p => p.Key != null && GameKey(p.Key))
            .ToDictionary(p => p.Key, p => p.Value.Replace(winRoot, goldenRoot, StringComparison.OrdinalIgnoreCase).TrimEnd('\\'));
    }

    private static (string, string) Split(string line)
    {
        var eq = line.IndexOf('=');
        return eq <= 0 ? (null, null) : (line[..eq], line[(eq + 1)..]);
    }

    /// <summary>The real bat under `wine cmd`, launcher env passed as the Windows launcher would
    /// (Windows paths), its `start` line swapped for `set`; returns the game variables, root normalized.</summary>
    private string RunBatUnderWine(AppSettings settings)
    {
        var bat = File.ReadAllText(Path.Combine(SourceDir(), "..", "..", "..", "Run-AstroHybrid.bat"));
        var start = bat.Split('\n').Single(l => l.StartsWith("start \"\" /b /wait"));
        File.WriteAllText(Path.Combine(_root, "Run-AstroHybrid.bat"), bat.Replace(start, "set > \"%ROOT%Logs\\envdump.txt\"\r"));

        var psi = new ProcessStartInfo("wine") { UseShellExecute = false, WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("cmd");
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(WinPath.ToWindows(Path.Combine(_root, "Run-AstroHybrid.bat")));
        psi.Environment.Remove("DISPLAY");
        psi.Environment.Remove("WAYLAND_DISPLAY");
        psi.Environment["WINEDEBUG"] = "-all";
        psi.Environment["WINEPREFIX"] = Path.Combine(_root, "prefix");   // wine needs a parent dir owned by the user
        foreach (var (k, v) in LauncherEnv(settings))
            psi.Environment[k] = k is "ASTROHYBRID_DLSS_PATH" or "ASTROHYBRID_FSR_PATH" ? WinPath.ToWindows(v) : v;
        using (var p = Process.Start(psi)!)
        {
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
        }
        var dump = File.ReadAllLines(Path.Combine(_root, "Logs", "envdump.txt"));
        var winRoot = WinPath.ToWindows(_root);
        return string.Join("\n", dump.Select(Split).Where(p => p.Item1 != null && GameKey(p.Item1)).OrderBy(p => p.Item1, StringComparer.Ordinal)
            .Select(p => $"{p.Item1}={p.Item2.Replace(winRoot, @"Z:\AH_ROOT", StringComparison.OrdinalIgnoreCase)}")) + "\n";
    }

    [Fact]
    public void ForkSettingsOnlyApplyWithFsrOn()
    {
        var linux = new LinuxSettings { FsrVersion = "4", FsrSharpness = 0.35, FrameGen = "fsr" };
        var off = GameEnvironment.Build(new Dictionary<string, string>(), _ => null, _root, new AppSettings { Upscale = 0 }, linux);
        Assert.False(off.ContainsKey("ASTROHYBRID_FSR_VERSION"));
        var on = GameEnvironment.Build(new Dictionary<string, string>(), _ => null, _root, new AppSettings { Upscale = 2 }, linux);
        Assert.Equal("4", on["ASTROHYBRID_FSR_VERSION"]);
        Assert.Equal("0.35", on["ASTROHYBRID_FSR_SHARPNESS"]);
        Assert.Equal("fsr", on["ASTROHYBRID_FG"]);
    }

    [Fact]
    public void UnknownFsrVersionFallsBackToAuto()
    {
        var env = GameEnvironment.Build(new Dictionary<string, string>(), _ => null, _root, new AppSettings { Upscale = 2 }, new LinuxSettings { FsrVersion = "9" });
        Assert.Equal("auto", env["ASTROHYBRID_FSR_VERSION"]);
    }

    [Fact]
    public void VariablesTheUserStartedWithWinOverDefaultsAsWithTheBat()
    {
        var host = new Dictionary<string, string> { ["KYTY_GPU_WIDTH"] = "2560", ["KYTY_CP_RECORDER"] = "0" };
        var env = GameEnvironment.Build(new Dictionary<string, string>(), k => host.GetValueOrDefault(k), _root, new AppSettings(), new LinuxSettings());
        Assert.False(env.ContainsKey("KYTY_GPU_WIDTH"));   // inherited from the host, not overwritten
        Assert.False(env.ContainsKey("KYTY_CP_RECORDER"));
        Assert.Equal("1080", env["KYTY_GPU_HEIGHT"]);
    }

    [Fact]
    public void WineDllOverridesAreMergedAndExtraEnvWins()
    {
        var host = new Dictionary<string, string> { ["WINEDLLOVERRIDES"] = "dxgi=n" };
        var linux = new LinuxSettings { ExtraEnv = "# comment\nKYTY_GPU_WIDTH=3440\n\nbad line\nRADV_PERFTEST=transfer_queue\n" };
        var env = GameEnvironment.Build(new Dictionary<string, string>(), k => host.GetValueOrDefault(k), _root, new AppSettings(), linux);
        Assert.Equal("astrohybrid_host=n,b;kyty_gpu=n,b;dxgi=n", env["WINEDLLOVERRIDES"]);
        Assert.Equal("3440", env["KYTY_GPU_WIDTH"]);
        Assert.Equal("transfer_queue", env["RADV_PERFTEST"]);
    }

    [Fact]
    public void AliasTargetsBecomeWinePaths() =>
        Assert.Equal(@"/app0/data=Z:\mnt\usb\x\data;/app0/sce_sys=E:\dump\sce_sys",
            GameEnvironment.AliasesToWine(@"/app0/data=/mnt/usb/x/data;/app0/sce_sys=E:\dump\sce_sys"));
}
