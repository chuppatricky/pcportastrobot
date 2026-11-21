using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AstroHybrid.Linux.Compat;
using AstroHybrid.Linux.Platform;
using Xunit;

namespace AstroHybrid.Tests.Compat;

public sealed class CompatTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ah-compat-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Proton(string parent, string name, string version, string display = null, bool ntdll = true)
    {
        var dir = Path.Combine(parent, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "proton"), "#!/usr/bin/env python3\n");
        File.WriteAllText(Path.Combine(dir, "version"), $"1793687165 {version}\n");
        if (display != null)
            File.WriteAllText(Path.Combine(dir, "compatibilitytool.vdf"),
                $"\"compatibilitytools\"\n{{\n  \"compat_tools\"\n  {{\n    \"{name}\"\n    {{\n      \"display_name\" \"{display}\"\n    }}\n  }}\n}}\n");
        if (ntdll)
        {
            var unix = Path.Combine(dir, "files", "lib", "wine", "x86_64-unix");
            Directory.CreateDirectory(unix);
            File.WriteAllText(Path.Combine(unix, "ntdll.so"), name);
        }
        return dir;
    }

    private LayerCatalog Catalog(params string[] wines) => new(
        home: Path.Combine(_root, "home"),
        xdgData: Path.Combine(_root, "home", ".local", "share"),
        systemToolDirs: new[] { Path.Combine(_root, "usr-share-steam") },
        wineCandidates: wines,
        wineVersion: w => File.ReadAllText(w).Trim());

    private string SteamCompat => Path.Combine(_root, "home", ".local", "share", "Steam", "compatibilitytools.d");

    [Fact]
    public void ReadsProtonFoldersWithDisplayNamesVersionsAndNtdll()
    {
        Proton(SteamCompat, "GE-Proton11-12-x86_64", "GE-Proton11-12", "GE-Proton11-12-x86_64");
        var layers = Catalog().Discover();
        var ge = Assert.Single(layers);
        Assert.Equal("ge-proton11-12", ge.Id);
        Assert.Equal("GE-Proton11-12", ge.Version);
        Assert.Equal("GE-Proton11-12-x86_64", ge.DisplayName);
        Assert.Equal(CompatKind.Proton, ge.Kind);
        Assert.Equal("steam-user", ge.Source);
        Assert.EndsWith("files/lib/wine/x86_64-unix/ntdll.so", ge.NtdllPath);
        Assert.Empty(ge.Warnings);
    }

    [Fact]
    public void SymlinkedSteamRootsAreListedOnce()
    {
        Proton(SteamCompat, "GE-Proton11-12-x86_64", "GE-Proton11-12");
        var dotSteam = Path.Combine(_root, "home", ".steam");
        Directory.CreateDirectory(dotSteam);
        Directory.CreateSymbolicLink(Path.Combine(dotSteam, "root"), Path.Combine(_root, "home", ".local", "share", "Steam"));
        Directory.CreateSymbolicLink(Path.Combine(dotSteam, "steam"), Path.Combine(_root, "home", ".local", "share", "Steam"));
        Assert.Single(Catalog().Discover());
    }

    [Fact]
    public void ACopyUnderAnotherNameIsItsOwnLayer()
    {
        // "Proton-GE Latest" is a real second copy of GE11-12 (own inode), not a symlink.
        Proton(SteamCompat, "GE-Proton11-12-x86_64", "GE-Proton11-12");
        Proton(SteamCompat, "Proton-GE Latest", "GE-Proton11-12");
        var ids = Catalog().Discover().Select(l => l.Id).ToList();
        Assert.Equal(new[] { "ge-proton11-12", "proton-ge-latest" }, ids);
    }

    [Fact]
    public void SameFolderNameInTwoPlacesGetsUniqueIds()
    {
        Proton(SteamCompat, "protosoda-11.0-3", "ProtoSoda-11.0-3");
        Proton(Path.Combine(_root, "home", ".local", "share", "bottles", "runners"), "protosoda-11.0-3", "ProtoSoda-11.0-3");
        var ids = Catalog().Discover().Select(l => l.Id).ToList();
        Assert.Equal(new[] { "protosoda-11.0-3", "protosoda-11.0-3-bottles" }, ids);
    }

    [Fact]
    public void FoldersWithoutAProtonScriptAreIgnored()
    {
        Directory.CreateDirectory(Path.Combine(SteamCompat, "not-a-tool"));
        Assert.Empty(Catalog().Discover());
    }

    [Fact]
    public void LibraryFoldersVdfIsFollowedForSteamProtons()
    {
        var lib = Path.Combine(_root, "games");
        Proton(Path.Combine(lib, "steamapps", "common"), "Proton 11.0", "proton-11.0-2e-x86_64");
        var apps = Path.Combine(_root, "home", ".local", "share", "Steam", "steamapps");
        Directory.CreateDirectory(apps);
        File.WriteAllText(Path.Combine(apps, "libraryfolders.vdf"),
            $"\"libraryfolders\"\n{{\n  \"1\"\n  {{\n    \"path\"    \"{lib}\"\n  }}\n}}\n");
        var l = Assert.Single(Catalog().Discover());
        Assert.Equal("proton-11.0", l.Id);
        Assert.Equal("steam-library", l.Source);
    }

    [Fact]
    public void OldWineIsListedWithAWarningAndSystemWineIsNamedSystem()
    {
        var sysBin = Path.Combine(_root, "usr", "bin");
        var optBin = Path.Combine(_root, "opt", "wine-cachyos", "bin");
        foreach (var (bin, ver) in new[] { (sysBin, "wine-11.22"), (optBin, "wine-10.0-20260425 (CachyOS)") })
        {
            Directory.CreateDirectory(bin);
            File.WriteAllText(Path.Combine(bin, "wine"), ver);
            Directory.CreateDirectory(Path.Combine(bin, "..", "lib", "wine", "x86_64-unix"));
            File.WriteAllText(Path.Combine(bin, "..", "lib", "wine", "x86_64-unix", "ntdll.so"), ver);
        }
        var layers = Catalog(Path.Combine(sysBin, "wine"), Path.Combine(optBin, "wine")).Discover();
        Assert.Equal(new[] { "wine-system", "wine-cachyos" }, layers.Select(l => l.Id));
        Assert.Empty(layers[0].Warnings);
        Assert.Contains(layers[1].Warnings, w => w.Contains("older than 11"));
    }

    [Fact]
    public void DefaultPrefersCachyOsThenNewestGeThenOtherProtonThenWine()
    {
        CompatLayer L(string id, string version, string source = "steam-user", CompatKind kind = CompatKind.Proton) =>
            new() { Id = id, DisplayName = id, Kind = kind, Path = "/x", Version = version, Source = source };
        var wine = L("wine-system", "wine-11.22", "system", CompatKind.Wine);
        var valve = L("proton-11.0", "proton-11.0-2e-x86_64", "steam-library");
        var ge10 = L("ge-proton11-10", "GE-Proton11-10");
        var ge12 = L("ge-proton11-12", "GE-Proton11-12");
        var cachyBottles = L("proton-cachyos-bottles", "cachyos-11.0-20261005-slr", "bottles");
        var cachySteam = L("proton-cachyos-latest", "cachyos-11.0-20261015-slr");

        Assert.Equal(cachySteam, LayerCatalog.PickDefault(new[] { wine, valve, ge10, ge12, cachyBottles, cachySteam }));
        Assert.Equal(ge12, LayerCatalog.PickDefault(new[] { wine, valve, ge10, ge12 }));
        Assert.Equal(valve, LayerCatalog.PickDefault(new[] { wine, valve }));
        Assert.Equal(wine, LayerCatalog.PickDefault(new[] { wine }));
        Assert.Null(LayerCatalog.PickDefault(Array.Empty<CompatLayer>()));
    }

    [Theory]
    [InlineData("GE-Proton11-12-x86_64", "ge-proton11-12")]
    [InlineData("Proton-CachyOS Latest", "proton-cachyos-latest")]
    [InlineData("Proton - Experimental", "proton-experimental")]
    [InlineData("Proton 11.0", "proton-11.0")]
    public void Slugs(string name, string slug) => Assert.Equal(slug, LayerCatalog.Slug(name));

    private CompatRunner Runner(bool nvidia = false) => new(
        Path.Combine(_root, "prefixes"), Path.Combine(_root, "Logs"),
        name => name switch { "umu-run" => "/usr/bin/umu-run", "gamemoderun" => "/usr/bin/gamemoderun", "nice" => "/usr/bin/nice", _ => null },
        () => nvidia);

    private static RunRequest Request(CompatLayer layer) => new()
    {
        Layer = layer,
        ExePath = "/data/Build/RunHybrid/AstroBot.exe",
        Args = new[] { "-skipIntro" },
        WorkingDirectory = "/data/Build/RunHybrid",
        Environment = new Dictionary<string, string> { ["KYTY_GPU_WIDTH"] = "2560" },
        Wrappers = new[] { "gamemoderun", "not-installed" },
        ExtraPaths = new[] { "/mnt/games" },
    };

    [Fact]
    public void ProtonRunsThroughUmuWithAnAbsolutePinnedProtonPath()
    {
        var layer = new CompatLayer { Id = "proton-cachyos-latest", DisplayName = "x", Kind = CompatKind.Proton, Path = "/steam/compat/Proton-CachyOS Latest" };
        var runner = Runner();
        var (argv, env) = runner.BuildCommand(Request(layer), "abc", runner.PrefixFor(layer));

        Assert.Equal(new[] { "/usr/bin/gamemoderun", "/usr/bin/umu-run", "/data/Build/RunHybrid/AstroBot.exe", "-skipIntro" }, argv);
        Assert.Equal("/steam/compat/Proton-CachyOS Latest", env["PROTONPATH"]);
        Assert.Equal(Path.Combine(_root, "prefixes", "proton-cachyos-latest"), env["WINEPREFIX"]);
        Assert.Equal("umu-default", env["GAMEID"]);
        Assert.Equal("abc", env["ASTROHYBRID_COMPAT_SESSION"]);
        Assert.False(env.ContainsKey("PROTON_LOG"));   // off for the game: +seh logs every write fault
        Assert.Equal("-all", env["WINEDEBUG"]);
        Assert.Equal("/mnt/games", env["STEAM_COMPAT_LIBRARY_PATHS"]);
        Assert.Equal("/mnt/games", env["PRESSURE_VESSEL_FILESYSTEMS_RW"]);
        Assert.Equal("2560", env["KYTY_GPU_WIDTH"]);
        Assert.False(env.ContainsKey("PROTON_ENABLE_NVAPI"));
    }

    [Fact]
    public void NvapiOnlyWithAnNvidiaCard()
    {
        var layer = new CompatLayer { Id = "ge", DisplayName = "x", Kind = CompatKind.Proton, Path = "/p" };
        var (_, env) = Runner(nvidia: true).BuildCommand(Request(layer), "s", "/pfx");
        Assert.Equal("1", env["PROTON_ENABLE_NVAPI"]);
    }

    [Fact]
    public void TheProtonLogOnlyWhenAskedFor()
    {
        var layer = new CompatLayer { Id = "p", DisplayName = "x", Kind = CompatKind.Proton, Path = "/p" };
        var (_, env) = Runner().BuildCommand(Request(layer) with { ProtonLog = true }, "s", "/pfx");
        Assert.Equal("1", env["PROTON_LOG"]);
        Assert.Equal(Path.Combine(_root, "Logs"), env["PROTON_LOG_DIR"]);
        Assert.False(env.ContainsKey("WINEDEBUG"));   // Proton sets its own for the log
        var (_, tool) = Runner().BuildCommand(Request(layer) with { ProtonLog = true }, "s", "/pfx", protonLogOverride: false);
        Assert.False(tool.ContainsKey("PROTON_LOG"));
    }

    [Fact]
    public void TheGameIsStartedByCmdWhichRedirectsItsOutputInsideWine()
    {
        var layer = new CompatLayer { Id = "wine-system", DisplayName = "x", Kind = CompatKind.Wine, Path = "/usr/bin/wine" };
        var r = Request(layer) with { Args = new[] { "-skipIntro", "a b", "50%" }, StdoutLog = "/d/Logs/hybrid-run.out.log", StderrLog = "/d/Logs/hybrid-run.err.log" };
        Assert.Equal("@echo off\r\n"
            + "\"Z:\\data\\Build\\RunHybrid\\AstroBot.exe\" -skipIntro \"a b\" 50%% "
            + "1>\"Z:\\d\\Logs\\hybrid-run.out.log\" 2>\"Z:\\d\\Logs\\hybrid-run.err.log\"\r\n"
            + "exit /b %ERRORLEVEL%\r\n", CompatRunner.LaunchScript(r));

        var wrapped = Runner().WrapInCmd(r);
        Assert.Equal("cmd.exe", wrapped.ExePath);   // plain Wine finds its own cmd.exe by name
        Assert.Equal(new[] { "/c", WinPath.ToWindows(Path.Combine(_root, "Logs", "astrohybrid-launch.cmd")) }, wrapped.Args);
        Assert.Equal(CompatRunner.LaunchScript(r), File.ReadAllText(Path.Combine(_root, "Logs", "astrohybrid-launch.cmd")));

        Assert.Null(CompatRunner.LaunchScript(r with { ExePath = "/home/jos\u00e9/AstroBot.exe" }));   // cmd reads batch files in the OEM code page
        var proton = new CompatLayer { Id = "p", DisplayName = "x", Kind = CompatKind.Proton, Path = "/p", NtdllPath = "/nowhere/lib/wine/x86_64-unix/ntdll.so" };
        Assert.Null(Runner().WrapInCmd(r with { Layer = proton }));   // no cmd.exe on disk: start it unwrapped
    }

    [Fact]
    public void PlainWineRunsDirectlyAndNiceWrapsEverything()
    {
        var layer = new CompatLayer { Id = "wine-system", DisplayName = "x", Kind = CompatKind.Wine, Path = "/usr/bin/wine" };
        var (argv, env) = Runner().BuildCommand(Request(layer) with { Nice = -5, Wrappers = Array.Empty<string>() }, "s", "/pfx");
        Assert.Equal(new[] { "/usr/bin/nice", "-n", "-5", "/usr/bin/wine", "/data/Build/RunHybrid/AstroBot.exe", "-skipIntro" }, argv);
        Assert.Equal("/pfx", env["WINEPREFIX"]);
        Assert.False(env.ContainsKey("PROTONPATH"));
    }

    [Fact]
    public void DualSenseKeysWithoutBthenumAreFoundAndFixedOnesSkipped()
    {
        var reg = Path.Combine(_root, "system.reg");
        File.WriteAllText(reg, string.Join("\n",
            @"WINE REGISTRY Version 2",
            @"[System\\ControlSet001\\Enum\\BTHENUM\\VID_054C&PID_0CE6\\0&aa:bb&0&0&0] 1795235230",
            @"""Class""=""HIDClass""",
            @"""CompatibleIDs""=str(7):""WINEBUS\\WINE_COMP_HID\0""",
            "",
            @"[System\\ControlSet001\\Enum\\BTHENUM\\VID_054C&PID_0CE6\\0&aa:bb&0&0&0\\Device Parameters] 1794892188",
            "",
            @"[System\\ControlSet001\\Enum\\BTHENUM\\VID_054C&PID_0DF2\\0&cc:dd&0&0&0] 1795235230",
            @"""CompatibleIDs""=str(7):""BTHENUM\\{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&0df2\0WINEBUS\\WINE_COMP_HID\0""",
            "",
            // A DualShock 4 is not part of the fix (upstream #59 covers DualSense only).
            @"[System\\ControlSet001\\Enum\\BTHENUM\\VID_054C&PID_09CC\\0&ee:ff&0&0&0] 1795235230",
            @"""CompatibleIDs""=str(7):""WINEBUS\\WINE_COMP_HID\0""",
            ""));
        var keys = DualSenseFix.KeysNeedingFix(reg);
        var (key, pid) = Assert.Single(keys);
        Assert.Equal(@"HKLM\System\ControlSet001\Enum\BTHENUM\VID_054C&PID_0CE6\0&aa:bb&0&0&0", key);
        Assert.Equal("0ce6", pid);
    }

    [Fact]
    public void ProtonPrefixesKeepTheRegistryInPfx()
    {
        var prefix = Path.Combine(_root, "pfx-test");
        Directory.CreateDirectory(Path.Combine(prefix, "pfx"));
        File.WriteAllText(Path.Combine(prefix, "pfx", "system.reg"), "");
        Assert.Equal(Path.Combine(prefix, "pfx", "system.reg"), DualSenseFix.SystemReg(prefix));
        Assert.Equal(Path.Combine(_root, "plain", "system.reg"), DualSenseFix.SystemReg(Path.Combine(_root, "plain")));
    }

    [Fact]
    public void FileIdMatchesAcrossSymlinksAndDiffersBetweenCopies()
    {
        var a = Path.Combine(_root, "a.so");
        File.WriteAllText(a, "x");
        File.Copy(a, Path.Combine(_root, "b.so"));
        File.CreateSymbolicLink(Path.Combine(_root, "link.so"), a);
        Assert.Equal(Posix.FileId(a), Posix.FileId(Path.Combine(_root, "link.so")));
        Assert.NotEqual(Posix.FileId(a), Posix.FileId(Path.Combine(_root, "b.so")));
        Assert.Null(Posix.FileId(Path.Combine(_root, "missing")));
    }
}
