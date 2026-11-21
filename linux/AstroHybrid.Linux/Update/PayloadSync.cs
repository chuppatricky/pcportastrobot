using System;
using System.IO;
using System.Linq;
using AstroHybrid.Linux.Platform;

namespace AstroHybrid.Linux.Update;

/// <summary>
/// Copies the Windows runtime shipped in the AppImage (payload/: Build/RunHybrid DLLs, libs/*.prx, fonts,
/// presets, licenses) into the writable data dir, once per launcher version. The game runs from the data dir,
/// so this is how an updated AppImage brings its new runtime along. What setup built and what the player owns
/// is never overwritten, even if a future payload happens to contain a file of the same name.
/// </summary>
public static class PayloadSync
{
    public sealed record Result(bool Synced, int FilesCopied, int FilesProtected, string Message);

    /// <summary>Directory names that are never written into, at any depth: the dump links made by setup
    /// (app0 and the Wine-junction leftovers data/sce_sys/fakelib), saves, trophies, caches, logs,
    /// Wine prefixes, mods and the old WPF updater's folder.</summary>
    private static readonly string[] ProtectedDirs =
    {
        "app0", "data", "sce_sys", "fakelib", "_sd", "_sd_mem", "_trophy", "_PipelineCache",
        "Logs", "prefixes", "mods", "launcher",
    };

    /// <summary>Call once at startup (GUI and CLI), before anything reads Build/RunHybrid.</summary>
    public static Result EnsureCurrent() =>
        EnsureCurrent(AppPaths.PayloadDir, AppPaths.DataDir, AppPaths.PayloadStamp, AppImageUpdater.AssemblyVersion);

    public static Result EnsureCurrent(string payloadDir, string dataDir, string stampFile, string version)
    {
        if (!Directory.Exists(payloadDir))
            return new Result(false, 0, 0, $"no payload at {payloadDir} (not running from the AppImage?)");
        var stamp = File.Exists(stampFile) ? File.ReadAllText(stampFile).Trim() : "";
        if (stamp == version)
            return new Result(false, 0, 0, $"payload {version} already in place");

        int copied = 0, skipped = 0;
        foreach (var src in Directory.EnumerateFiles(payloadDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(payloadDir, src);
            if (IsProtected(rel)) { skipped++; continue; }
            var dst = Path.Combine(dataDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            // Copy beside the target and rename over it: replaces a symlink instead of writing through it,
            // and a file in use keeps its old inode instead of being truncated under the reader.
            var tmp = dst + ".payload-tmp";
            File.Copy(src, tmp, overwrite: true);
            // The squashfs payload is read-only; the data dir copy must stay writable for the next sync.
            File.SetUnixFileMode(tmp, File.GetUnixFileMode(tmp) | UnixFileMode.UserWrite);
            File.Move(tmp, dst, overwrite: true);
            copied++;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(stampFile)!);
        File.WriteAllText(stampFile + ".tmp", version + "\n");
        File.Move(stampFile + ".tmp", stampFile, overwrite: true);
        return new Result(true, copied, skipped,
            $"payload {version} synced into {dataDir}: {copied} files copied, {skipped} protected files left alone" +
            (stamp.Length > 0 ? $" (was {stamp})" : ""));
    }

    /// <summary>True for paths (relative to the data dir) the sync must never write.</summary>
    public static bool IsProtected(string relativePath)
    {
        var parts = relativePath.Split('/', '\\');
        var dirs = parts.Take(parts.Length - 1);
        if (dirs.Any(d => ProtectedDirs.Contains(d, StringComparer.OrdinalIgnoreCase))) return true;
        var name = parts[^1].ToLowerInvariant();
        return (name.StartsWith("astrobot") && name.EndsWith(".exe"))       // relinked game exes
            || name.EndsWith(".needed.json") || name.EndsWith(".registry.json") // relinker outputs
            || name.StartsWith("astrohybrid-settings")                        // upstream settings (+.bak/.tmp)
            || name.StartsWith("astrohybrid-linux-settings")
            || name.StartsWith("astrohybrid-game.json")                       // setup state
            || name == "app0-aliases.txt"
            || name == ".payload-version";
    }
}
