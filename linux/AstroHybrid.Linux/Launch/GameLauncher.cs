using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AstroHybrid.App.Models;
using AstroHybrid.App.Services;
using AstroHybrid.Linux.Platform;

namespace AstroHybrid.Linux.Launch;

/// <summary>A started game: the run itself and the check of which layer it really runs on.</summary>
public sealed record GameRun(RunHandle Handle, Task<LayerVerification> Verification);

/// <summary>
/// Starts the game the way upstream's LaunchGameAsync + Run-AstroHybrid.bat do, but through the layer
/// chosen on the Compatibility page: same environment (GameEnvironment), same exe choice, cwd
/// Build/RunHybrid, output in Logs/hybrid-run.{out,err}.log.
/// </summary>
public sealed class GameLauncher
{
    private readonly string _root;

    public GameLauncher() : this(AppPaths.DataDir) { }
    internal GameLauncher(string root) => _root = root;

    private string RunHybrid => Path.Combine(_root, "Build", "RunHybrid");
    private string Logs => Path.Combine(_root, "Logs");

    /// <summary>The layer a start would use: the saved choice when it still exists, else the default.</summary>
    public static CompatLayer ChosenLayer(LinuxSettings linux) =>
        Services.Catalog.Find(linux.CompatLayer) ?? Services.Catalog.Default();

    /// <summary>Everything a start would do, without starting: for `--cli play --dry-run` and the UI's "show command".</summary>
    public RunRequest Prepare(AppSettings settings, HardwareInfo hardware, LinuxSettings linux, CompatLayer layer = null)
    {
        if (!File.Exists(Path.Combine(RunHybrid, "AstroBot.exe")))
            throw new FileNotFoundException("The game is not set up yet (Build/RunHybrid/AstroBot.exe is missing): run the setup first.");
        layer ??= ChosenLayer(linux) ?? throw new InvalidOperationException("No Wine or Proton found. Install Proton-CachyOS or GE-Proton (or wine) first.");

        var launcherEnv = new LauncherService(_root).BuildEnvironmentVariables(settings, hardware);
        var env = GameEnvironment.Build(launcherEnv, Environment.GetEnvironmentVariable, _root, settings, linux);
        if (linux.MangoHud) env["MANGOHUD"] = "1";   // MangoHud's Vulkan layer; also reaches into Proton's container

        var args = (env.GetValueOrDefault("ASTROHYBRID_EXTRA_ARGS") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new RunRequest
        {
            Layer = layer,
            ExePath = Path.Combine(RunHybrid, env["ASTROHYBRID_EXE"]),
            Args = args,
            WorkingDirectory = RunHybrid,
            Environment = env,
            StdoutLog = Path.Combine(Logs, "hybrid-run.out.log"),
            StderrLog = Path.Combine(Logs, "hybrid-run.err.log"),
            RedirectInWine = true,
            Wrappers = linux.GameMode ? new[] { "gamemoderun" } : Array.Empty<string>(),
            Nice = PriorityToNice(env.GetValueOrDefault("ASTROHYBRID_PRIORITY")),
            ExtraPaths = PathsOutsideHome(),
        };
    }

    public async Task<GameRun> LaunchAsync(AppSettings settings, HardwareInfo hardware, LinuxSettings linux,
        IProgress<string> progress, CancellationToken ct)
    {
        if (IsGameRunning()) throw new InvalidOperationException("The game is already running.");
        var request = Prepare(settings, hardware, linux);
        Directory.CreateDirectory(Logs);

        // As upstream LaunchGameAsync: remember the start in the settings file.
        var settingsManager = new SettingsManager(_root);
        settings.LastLaunched = DateTime.Now;
        settingsManager.Save(settings);

        await Services.Runner.EnsurePrefixAsync(request.Layer, progress, ct);
        progress?.Report($"Starting Astro Bot through {request.Layer.DisplayName}...");
        var handle = await Services.Runner.StartAsync(request, ct);
        var verification = Services.Verifier.VerifyAsync(handle, TimeSpan.FromSeconds(120), ct);
        return new GameRun(handle, verification);
    }

    /// <summary>The bat's `start /abovenormal` raises the whole game process over other programs; on Linux
    /// the counterpart is the process's nice value. Raising it needs permission (RLIMIT_NICE, as CachyOS
    /// grants); without it nice says so and runs the game at normal priority.</summary>
    internal static int PriorityToNice(string priority) => priority?.ToLowerInvariant() switch
    {
        "high" => -10,
        "abovenormal" => -5,
        _ => 0,
    };

    /// <summary>The game reads its data through app0's symlinks into the dump; Proton's container only
    /// shares $HOME (and a few system paths) by default, so a dump elsewhere (another drive) must be added.</summary>
    internal List<string> PathsOutsideHome()
    {
        var home = AppPaths.Home.TrimEnd('/') + "/";
        var result = new List<string>();
        var app0 = Path.Combine(RunHybrid, "app0");
        if (!Directory.Exists(app0)) return result;
        foreach (var entry in Directory.EnumerateFileSystemEntries(app0))
        {
            var target = Posix.RealPath(entry);
            if (target == null || target.StartsWith(home, StringComparison.Ordinal) || target.StartsWith(Path.GetFullPath(_root), StringComparison.Ordinal)) continue;
            var dir = Path.GetDirectoryName(target);   // the dump folder that holds data/, sce_sys/, ...
            if (dir != null && !result.Contains(dir)) result.Add(dir);
        }
        return result;
    }

    /// <summary>Any AstroBot*.exe Wine process started by this launcher (it carries our session marker).
    /// /proc/&lt;pid&gt;/comm is cut to 15 characters ("AstroBot-NonRT-"), so the command line is read instead.</summary>
    public static List<int> GamePids()
    {
        var marker = Encoding.UTF8.GetBytes("ASTROHYBRID_COMPAT_SESSION=");
        var pids = new List<int>();
        string[] dirs;
        try { dirs = Directory.GetDirectories("/proc"); } catch (Exception) { return pids; }
        foreach (var dir in dirs)
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            try
            {
                var cmdline = File.ReadAllText(Path.Combine(dir, "cmdline")).Replace('\0', ' ');
                if (!cmdline.Contains("AstroBot", StringComparison.Ordinal) || !cmdline.Contains(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (File.ReadAllBytes(Path.Combine(dir, "environ")).AsSpan().IndexOf(marker) < 0) continue;
                pids.Add(pid);
            }
            catch (Exception) { /* gone or not ours */ }
        }
        return pids;
    }

    public static bool IsGameRunning() => GamePids().Count > 0;

    /// <summary>Ends the game's Wine processes. Wine's own services in that prefix exit on their own once
    /// the game is gone.</summary>
    public static int KillGame()
    {
        var pids = GamePids();
        foreach (var pid in pids) Posix.Signal(pid, Posix.SIGKILL);
        return pids.Count;
    }
}
