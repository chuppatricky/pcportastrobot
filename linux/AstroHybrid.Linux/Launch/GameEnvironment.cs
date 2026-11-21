using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AstroHybrid.App.Models;
using AstroHybrid.Linux.Platform;

namespace AstroHybrid.Linux.Launch;

/// <summary>
/// The game's complete environment for one start: what Run-AstroHybrid.bat produces on Windows,
/// built without cmd.exe so it does not depend on the chosen layer's cmd.
///
/// Order matters and follows the bat exactly: the launcher's variables (upstream
/// LauncherService.BuildEnvironmentVariables) come first, then the bat's fixed and "if not defined"
/// defaults, then u59-preset.cmd's defaults, then the bat's later lines. "Defined" includes the
/// environment AstroHybrid itself was started with, as Wine passes that through to cmd too — so
/// `KYTY_GPU_WIDTH=2560 astrohybrid` still wins over the defaults, as with the bat.
/// The Linux fork's own settings (FSR version, sharpness, frame generation, extra variables) go last.
/// </summary>
public static class GameEnvironment
{
    public const string WineDllOverrides = "astrohybrid_host=n,b;kyty_gpu=n,b";

    /// <param name="launcherEnv">Upstream BuildEnvironmentVariables(settings, hardware).</param>
    /// <param name="hostEnv">The launcher's own environment (Environment.GetEnvironmentVariable).</param>
    /// <param name="root">The data dir (upstream's root: Build/RunHybrid, mods, Logs).</param>
    public static Dictionary<string, string> Build(IReadOnlyDictionary<string, string> launcherEnv, Func<string, string> hostEnv,
        string root, AppSettings settings, LinuxSettings linux)
    {
        var env = new Dictionary<string, string>(launcherEnv, StringComparer.Ordinal);
        var runHybrid = Path.Combine(root, "Build", "RunHybrid");

        // Upstream builds these with Path.Combine on the root; the game (a Windows program) needs Wine paths.
        foreach (var key in new[] { "ASTROHYBRID_DLSS_PATH", "ASTROHYBRID_FSR_PATH" })
            if (env.TryGetValue(key, out var p) && p.StartsWith('/')) env[key] = WinPath.ToWindows(p);

        string Value(string k) => env.TryGetValue(k, out var v) ? v : hostEnv(k);
        bool Defined(string k) => !string.IsNullOrEmpty(Value(k));
        void Default(string k, string v) { if (!Defined(k)) env[k] = v; }

        env["APS5_GPU_ENGINE"] = "kyty";
        Default("ASTROHYBRID_COMMIT_EPOCH_FASTPATH", "read");
        Default("KYTY_BUFFER_RANGE_FIXES", "1");
        Default("ASTROHYBRID_OPT_1", "1");
        Default("ASTROHYBRID_OPT_2", "1");
        Default("KYTY_SUBMIT_MIN_INTERVAL_US", "500");
        Default("KYTY_GFX_SLICE_DRAWS", "512");
        if (!Defined("KYTY_HANG_WATCHDOG") && Value("ASTROHYBRID_HANG_WATCHDOG") != "1") env["KYTY_HANG_WATCHDOG"] = "0";
        if (!Defined("ASTROHYBRID_CACHE_TITLE_ID") && Value("ASTROHYBRID_SHADER_CACHE") != "0") env["ASTROHYBRID_CACHE_TITLE_ID"] = "PPSA21567";

        foreach (var (k, v) in ReadPreset(Path.Combine(runHybrid, "u59-preset.cmd")))
            Default(k, v);

        var aliases = Path.Combine(runHybrid, "app0-aliases.txt");
        if (!Defined("APS5_PATH_ALIASES") && File.Exists(aliases))
        {
            // `set /p` takes the first line only.
            var line = File.ReadLines(aliases).FirstOrDefault() ?? "";
            if (line.Length > 0) env["APS5_PATH_ALIASES"] = AliasesToWine(line);
        }
        if (!Defined("APS5_PATH_OVERLAYS") && Directory.Exists(Path.Combine(root, "mods")))
            env["APS5_PATH_OVERLAYS"] = "/app0=" + WinPath.ToWindows(Path.Combine(root, "mods"));

        if (!Defined("ASTROHYBRID_EXE"))
            env["ASTROHYBRID_EXE"] = new[] { "AstroBot-NonRT-SoftAssert.exe", "AstroBot-NonRT.exe" }
                .FirstOrDefault(e => File.Exists(Path.Combine(runHybrid, e))) ?? "AstroBot.exe";
        env["KYTY_GPU_HOST_MODULE"] = "astrohybrid_host.dll";
        Default("APS5_PAD_TRIGGER_STATE", "1");
        Default("APS5_SAVEDATA_TRANSFER_FALLBACK", "1");
        Default("ASTROHYBRID_PAD_MOTION", "1");
        Default("KYTY_CP_PRIORITY", "2");
        if (Value("ASTROHYBRID_DRAW_PREP_TUNING") != "0")
        {
            Default("KYTY_DRAW_PREP_PRIORITY", "2");
            Default("KYTY_DRAW_PREP_SPIN_US", "10");
            Default("KYTY_DRAW_PREP_COLD_SPIN_US", "5");
        }
        Default("ASTROHYBRID_PRIORITY", "abovenormal");
        Default("KYTY_GPU_WIDTH", "1920");
        Default("KYTY_GPU_HEIGHT", "1080");
        // `set "ASTROHYBRID_EXTRA_ARGS="` clears it in cmd; here an empty value is simply left out.
        if (Value("ASTROHYBRID_SKIP_INTRO") == "1") env["ASTROHYBRID_EXTRA_ARGS"] = "-skipIntro";
        else env.Remove("ASTROHYBRID_EXTRA_ARGS");

        AddForkSettings(env, settings, linux);

        var overrides = Value("WINEDLLOVERRIDES");
        env["WINEDLLOVERRIDES"] = string.IsNullOrEmpty(overrides) ? WineDllOverrides
            : overrides.Contains("kyty_gpu", StringComparison.Ordinal) ? overrides : WineDllOverrides + ";" + overrides;

        foreach (var (k, v) in ParseExtraEnv(linux.ExtraEnv))
            env[k] = v;
        return env;
    }

    /// <summary>The fork's AMD settings. FSR's version and sharpness only mean something with FSR on
    /// (Upscale 2); the renderer (Kyty patch 0050) reads ASTROHYBRID_FSR_VERSION=auto|4|3|2 and
    /// ASTROHYBRID_FSR_SHARPNESS=0..1.</summary>
    private static void AddForkSettings(Dictionary<string, string> env, AppSettings settings, LinuxSettings linux)
    {
        if (settings.Upscale != 2) return;
        var version = linux.FsrVersion is "4" or "3" or "2" ? linux.FsrVersion : "auto";
        env["ASTROHYBRID_FSR_VERSION"] = version;
        env["ASTROHYBRID_FSR_SHARPNESS"] = Math.Clamp(linux.FsrSharpness, 0, 1).ToString("0.##", CultureInfo.InvariantCulture);
        if (linux.FrameGen == "fsr") env["ASTROHYBRID_FG"] = "fsr";
    }

    /// <summary>`if not defined K set "K=V"` lines of u59-preset.cmd, in file order.</summary>
    internal static IEnumerable<(string Key, string Value)> ReadPreset(string cmdFile)
    {
        if (!File.Exists(cmdFile)) yield break;
        var line = new Regex(@"^\s*if\s+not\s+defined\s+(\S+)\s+set\s+""([^=""]+)=([^""]*)""\s*$", RegexOptions.IgnoreCase);
        foreach (var text in File.ReadLines(cmdFile))
        {
            var m = line.Match(text);
            // `set "K="` with nothing after = leaves K undefined in cmd, so an empty default sets nothing.
            if (m.Success && m.Groups[3].Value.Length > 0 && m.Groups[1].Value.Equals(m.Groups[2].Value, StringComparison.OrdinalIgnoreCase))
                yield return (m.Groups[2].Value, m.Groups[3].Value);
        }
    }

    /// <summary>"/app0/data=/mnt/usb/dump/data;..." → targets as Wine paths (Linux setup writes Linux paths).</summary>
    internal static string AliasesToWine(string line) =>
        string.Join(";", line.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(item =>
        {
            var eq = item.IndexOf('=');
            if (eq <= 0) return item;
            var target = item[(eq + 1)..].Trim();
            return item[..eq] + "=" + (target.StartsWith('/') ? WinPath.ToWindows(target) : target);
        }));

    /// <summary>One KEY=VALUE per line; blank lines and # comments are skipped.</summary>
    internal static IEnumerable<(string Key, string Value)> ParseExtraEnv(string text)
    {
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            if (Regex.IsMatch(key, "^[A-Za-z_][A-Za-z0-9_]*$")) yield return (key, line[(eq + 1)..].Trim());
        }
    }
}
