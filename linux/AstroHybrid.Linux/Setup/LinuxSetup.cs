using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AstroHybrid.App.Models;
using AstroHybrid.App.Services;
using AstroHybrid.Linux.Platform;

namespace AstroHybrid.Linux.Setup;

/// <summary>
/// Game setup on Linux: upstream's GameSetup does the work (unwrap, relink, byte patches, variants);
/// on Linux it uses the native relinker shipped in the AppImage, symbolic links for app0, link(2)
/// for hard links and /proc/cpuinfo for the CPU (see the Linux branches in GameSetup.cs).
/// </summary>
public static partial class LinuxSetup
{
    /// <summary>The native relinker: $ASTROHYBRID_RELINKER, else the AppImage's usr/bin/relinker, else &lt;data&gt;/tools/relinker.</summary>
    public static string RelinkerPath()
    {
        var env = Environment.GetEnvironmentVariable("ASTROHYBRID_RELINKER");
        if (!string.IsNullOrEmpty(env)) return env;
        var appdir = Environment.GetEnvironmentVariable("APPDIR");
        if (!string.IsNullOrEmpty(appdir) && File.Exists(Path.Combine(appdir, "usr", "bin", "relinker")))
            return Path.Combine(appdir, "usr", "bin", "relinker");
        var next = Path.Combine(AppContext.BaseDirectory, "relinker");
        return File.Exists(next) ? next : Path.Combine(AppPaths.ToolsDir, "relinker");
    }

    /// <summary>As upstream's `AstroHybrid.exe --setup &lt;dump&gt;`: set up the game, then record the dump in the settings.</summary>
    public static async Task<GameFeatures> RunAsync(string dump, IProgress<(int Percent, string Message)> progress)
    {
        dump = Path.GetFullPath(dump);
        if (!GameSetup.IsAstroBot(dump))
            throw new InvalidOperationException($"{dump} is not an Astro Bot dump (no sce_sys/param.json naming Astro Bot).");
        var features = await new GameSetup(AppPaths.DataDir).RunAsync(dump, progress);
        var manager = new SettingsManager(AppPaths.DataDir);
        var settings = manager.Load();
        settings.FirstRunComplete = true;
        settings.ApplyBestOnStart = true;
        settings.GameDumpPath = dump;
        settings.GameVersion = features.Version;
        settings.GameTitleId = features.TitleId;
        manager.Save(settings);
        return features;
    }

    /// <summary>What is wrong with the installed game folder, or null when it is usable: upstream's
    /// broken-link check plus Wine reparse points (folders a Wine `mklink /J` made, which only a Wine
    /// that understands them can follow — another Proton cannot).</summary>
    public static string DescribeProblem()
    {
        var reparse = WineReparsePoints(Path.Combine(AppPaths.RunHybrid, "app0"));
        if (reparse.Count > 0)
            return $"app0 holds Wine-only links ({string.Join(", ", reparse.Select(Path.GetFileName))}); repair the game to replace them with normal links.";
        return GameSetup.DescribeBrokenGameLink(AppPaths.RunHybrid);
    }

    /// <summary>Repairs by setting the game up again from the saved dump (upstream's Repair does the same);
    /// app0 is rebuilt with symbolic links, so Wine reparse points and dangling links go away.</summary>
    public static Task<GameFeatures> RepairAsync(IProgress<(int Percent, string Message)> progress)
    {
        var settings = new SettingsManager(AppPaths.DataDir).Load();
        if (string.IsNullOrEmpty(settings.GameDumpPath))
            throw new InvalidOperationException("No game dump is saved yet: run the setup with your dump folder.");
        return RunAsync(WinPath.ToLinux(settings.GameDumpPath), progress);
    }

    [LibraryImport("libc", EntryPoint = "getxattr", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial nint getxattr(string path, string name, nint value, nuint size);

    /// <summary>Directories carrying Wine's user.WINEREPARSE attribute: how Wine stores a junction
    /// (an empty directory, e.g. "data?", whose xattr names the target).</summary>
    internal static List<string> WineReparsePoints(string dir)
    {
        var found = new List<string>();
        if (!Directory.Exists(dir)) return found;
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            if (getxattr(entry, "user.WINEREPARSE", 0, 0) >= 0) found.Add(entry);
        return found;
    }
}
