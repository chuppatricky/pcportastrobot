using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using AstroHybrid.Linux.Platform;

namespace AstroHybrid.Linux.Setup;

/// <summary>
/// Brings the player's settings and saves over from the older AppImage (the Windows launcher under
/// Wine, ~/.local/share/AstroHybrid-AppImage/app). Copies only — the old install keeps working and is
/// never changed. The game itself is set up again natively afterwards (its app0 there is made of
/// Wine-only links), from the dump the old settings name.
/// </summary>
public static class LegacyImport
{
    /// <summary>Saves (_sd, _sd_mem), trophies and the shader/pipeline cache, all relative to Build/RunHybrid
    /// (the game writes them to its working directory).</summary>
    internal static readonly string[] SaveDirs = { "_sd", "_sd_mem", "_trophy", "_PipelineCache" };

    public sealed record Item(string What, string Source, string Target, bool Skipped, string Reason);

    public sealed record Result(List<Item> Items, string DumpPath);

    public static bool Available(string legacy = null) =>
        File.Exists(Path.Combine(legacy ?? AppPaths.LegacyInstall, "Build", "astrohybrid-settings.json"));

    /// <param name="dryRun">List what would be copied without copying.</param>
    public static Result Run(bool dryRun, string legacy = null, string dataDir = null)
    {
        legacy ??= AppPaths.LegacyInstall;
        dataDir ??= AppPaths.DataDir;
        var items = new List<Item>();
        string dump = null;

        var srcSettings = Path.Combine(legacy, "Build", "astrohybrid-settings.json");
        var dstSettings = Path.Combine(dataDir, "Build", "astrohybrid-settings.json");
        if (File.Exists(srcSettings))
        {
            var json = JsonNode.Parse(File.ReadAllText(srcSettings))!.AsObject();
            // The old launcher saw the dump through Wine (Z:\home\...); the native one needs the Linux path.
            if (json["gameDumpPath"]?.GetValue<string>() is { Length: > 0 } p)
                json["gameDumpPath"] = dump = WinPath.ToLinux(p);
            if (File.Exists(dstSettings))
                items.Add(new Item("settings", srcSettings, dstSettings, true, "already present here"));
            else
            {
                if (!dryRun)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dstSettings)!);
                    File.WriteAllText(dstSettings, json.ToJsonString(new() { WriteIndented = true }));
                }
                items.Add(new Item("settings", srcSettings, dstSettings, false, ""));
            }
        }

        foreach (var name in SaveDirs)
        {
            var src = Path.Combine(legacy, "Build", "RunHybrid", name);
            var dst = Path.Combine(dataDir, "Build", "RunHybrid", name);
            if (!Directory.Exists(src)) continue;
            // Never merge into or replace saves that already exist here: they may be newer.
            if (Directory.Exists(dst) && Directory.EnumerateFileSystemEntries(dst).Any())
            {
                items.Add(new Item(name, src, dst, true, "already present here"));
                continue;
            }
            if (!dryRun) CopyTree(src, dst);
            items.Add(new Item(name, src, dst, false, ""));
        }

        var mods = Path.Combine(legacy, "mods");
        if (Directory.Exists(mods) && !Directory.Exists(Path.Combine(dataDir, "mods")))
        {
            if (!dryRun) CopyTree(mods, Path.Combine(dataDir, "mods"));
            items.Add(new Item("mods", mods, Path.Combine(dataDir, "mods"), false, ""));
        }

        if (!dryRun)
        {
            var linux = LinuxSettings.Load(Path.Combine(dataDir, "Build", "astrohybrid-linux-settings.json"));
            linux.LegacyImported = true;
            linux.Save(Path.Combine(dataDir, "Build", "astrohybrid-linux-settings.json"));
        }
        return new Result(items, dump);
    }

    /// <summary>cp -a --reflink=auto: on btrfs/XFS the copy shares blocks with the source (instant, no
    /// extra space), elsewhere it is a normal copy. Either way the source is only read.</summary>
    private static void CopyTree(string src, string dst)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        if (Directory.Exists(dst)) Directory.Delete(dst);   // empty (checked above); cp would nest into it
        var psi = new ProcessStartInfo("cp") { UseShellExecute = false, RedirectStandardError = true };
        foreach (var a in new[] { "-a", "--reflink=auto", "--", src, dst }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new IOException($"copying {src} failed: {err.Trim()}");
    }
}
