using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AstroHybrid.Linux.Platform;

namespace AstroHybrid.Linux.Compat;

/// <summary>
/// Starts Windows programs through one chosen layer, each layer with its own prefix.
/// Proton runs through umu-run with an absolute PROTONPATH: umu then uses exactly that build and
/// downloads nothing (a keyword such as "GE-Proton" would make umu fetch and switch to the newest
/// GE). The Steam Linux Runtime container (pressure-vessel) comes with it, as in Steam. Plain Wine
/// runs directly with WINEPREFIX set.
/// </summary>
public sealed class CompatRunner : IRunner
{
    private readonly string _prefixesDir;
    private readonly string _logsDir;
    private readonly Func<string, string> _which;
    private readonly Func<bool> _hasNvidiaGpu;

    public CompatRunner() : this(AppPaths.PrefixesDir, AppPaths.LogsDir, Which, HasNvidiaGpu) { }

    internal CompatRunner(string prefixesDir, string logsDir, Func<string, string> which, Func<bool> hasNvidiaGpu)
    {
        _prefixesDir = prefixesDir;
        _logsDir = logsDir;
        _which = which;
        _hasNvidiaGpu = hasNvidiaGpu;
    }

    public string PrefixFor(CompatLayer layer) => Path.Combine(_prefixesDir, layer.Id);

    public async Task EnsurePrefixAsync(CompatLayer layer, IProgress<string> progress, CancellationToken ct)
    {
        var prefix = PrefixFor(layer);
        Directory.CreateDirectory(prefix);
        // The stamp names the exact build that last set the prefix up; while it matches, wineboot is skipped
        // (through umu it costs several seconds per start). A changed build (a Proton update in place) runs it again.
        var stampFile = Path.Combine(prefix, ".astrohybrid-layer");
        var stamp = $"{layer.Path}\n{layer.Version}\n";
        if (!File.Exists(stampFile) || File.ReadAllText(stampFile) != stamp)
        {
            var fresh = !File.Exists(Path.Combine(prefix, "system.reg")) && !File.Exists(Path.Combine(prefix, "pfx", "system.reg"));
            progress?.Report(fresh ? $"Creating the {layer.DisplayName} prefix (first run takes a minute)..." : $"Updating the {layer.DisplayName} prefix...");
            // wineboot -u creates or upgrades the prefix with this layer's own Wine; for Proton the
            // proton script also lays out its prefix structure (version file, default DLLs) first.
            var code = await RunToolAsync(layer, BuiltinExe(layer, "wineboot.exe"), new[] { "-u" }, ct);
            if (code != 0)
                throw new InvalidOperationException($"{layer.DisplayName} could not set up its prefix (wineboot exit code {code}); see {ToolLog}.");
            File.WriteAllText(stampFile, stamp);
        }
        var fixedKeys = await DualSenseFix.ApplyAsync(this, layer, ct);
        if (fixedKeys > 0) progress?.Report($"DualSense Bluetooth fix applied ({fixedKeys}).");
        progress?.Report($"{layer.DisplayName} prefix ready.");
    }

    public Task<RunHandle> StartAsync(RunRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var session = Guid.NewGuid().ToString("N");
        var prefix = PrefixFor(request.Layer);
        Directory.CreateDirectory(prefix);
        Directory.CreateDirectory(_logsDir);
        var wrapped = request.RedirectInWine ? WrapInCmd(request) : null;
        var (argv, env) = BuildCommand(wrapped ?? request, session, prefix);
        // Wrapped, the program's output goes to its logs from inside Wine; what umu/Proton/Wine print
        // on the Linux side goes to Logs/game-launch.log.
        var process = wrapped != null
            ? Spawn(argv, env, request.WorkingDirectory, LaunchLog, LaunchLog)
            : Spawn(argv, env, request.WorkingDirectory, request.StdoutLog, request.StderrLog);
        return Task.FromResult(new RunHandle
        {
            SessionId = session,
            Layer = request.Layer,
            Process = process,
            Prefix = prefix,
            Exited = WaitAsync(process),
            ExeName = Path.GetFileName(request.ExePath),
        });
    }

    /// <summary>Runs a helper program (wineboot, reg) through the layer and waits for it. Output goes to Logs/compat-tool.log.</summary>
    internal async Task<int> RunToolAsync(CompatLayer layer, string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        Directory.CreateDirectory(_logsDir);
        var request = new RunRequest
        {
            Layer = layer,
            ExePath = exe,
            Args = args,
            WorkingDirectory = _prefixesDir,
            StdoutLog = ToolLog,
            StderrLog = ToolLog,
            Environment = new Dictionary<string, string> { ["WINEDEBUG"] = "-all" },
        };
        var (argv, env) = BuildCommand(request, "tool", PrefixFor(layer), protonLogOverride: false);
        using var process = Spawn(argv, env, _prefixesDir, ToolLog, ToolLog, append: true);
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { TryKill(process); throw; }
        return process.ExitCode;
    }

    private string ToolLog => Path.Combine(_logsDir, "compat-tool.log");
    private string LaunchLog => Path.Combine(_logsDir, "game-launch.log");

    /// <summary>The same program started by the layer's cmd.exe running Logs/astrohybrid-launch.cmd, which
    /// redirects its output to the request's logs inside Wine. Null (no wrapping) when the layer has no
    /// cmd.exe on disk or a path cannot be written in a batch file.</summary>
    internal RunRequest WrapInCmd(RunRequest r)
    {
        var cmd = BuiltinExe(r.Layer, "cmd.exe");
        if (r.Layer.Kind == CompatKind.Proton && !File.Exists(cmd)) return null;
        var script = LaunchScript(r);
        if (script == null) return null;
        Directory.CreateDirectory(_logsDir);
        var path = Path.Combine(_logsDir, "astrohybrid-launch.cmd");
        File.WriteAllText(path, script);
        return r with { ExePath = cmd, Args = new[] { "/c", WinPath.ToWindows(path) }, RedirectInWine = false };
    }

    /// <summary>The batch file of <see cref="WrapInCmd"/>: run the program, output to its logs, pass its exit code on.
    /// Null when a path or argument holds characters cmd would read differently (non-ASCII, quotes, newlines).</summary>
    internal static string LaunchScript(RunRequest r)
    {
        static string Quote(string s) => "\"" + s.Replace("%", "%%") + "\"";
        var raw = new[] { r.ExePath, r.StdoutLog ?? "", r.StderrLog ?? "" }.Concat(r.Args);
        if (raw.Any(s => s.Any(c => c > 126 || c < 32 || c == '"'))) return null;
        var parts = new List<string> { Quote(WinPath.ToWindows(r.ExePath)) };
        parts.AddRange(r.Args.Select(a => a.Any(c => c == ' ' || c == '&' || c == '|' || c == '<' || c == '>' || c == '^') ? Quote(a) : a.Replace("%", "%%")));
        if (r.StdoutLog != null) parts.Add("1>" + Quote(WinPath.ToWindows(r.StdoutLog)));
        if (r.StderrLog != null) parts.Add("2>" + Quote(WinPath.ToWindows(r.StderrLog)));
        return "@echo off\r\n" + string.Join(' ', parts) + "\r\nexit /b %ERRORLEVEL%\r\n";
    }

    /// <summary>A program that ships with the layer's Wine, by Linux path (umu-run needs a real file).</summary>
    internal static string BuiltinExe(CompatLayer layer, string name)
    {
        if (layer.Kind == CompatKind.Wine || string.IsNullOrEmpty(layer.NtdllPath)) return name;
        // .../lib/wine/x86_64-unix/ntdll.so → .../lib/wine/x86_64-windows/<name>
        var windowsDir = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(layer.NtdllPath))!, "x86_64-windows");
        var path = Path.Combine(windowsDir, name);
        return File.Exists(path) ? path : name;
    }

    /// <summary>The command line and environment for one run. Pure, so tests can check it.</summary>
    internal (List<string> Argv, Dictionary<string, string> Env) BuildCommand(RunRequest r, string session, string prefix, bool? protonLogOverride = null)
    {
        var protonLog = protonLogOverride ?? r.ProtonLog;
        var env = new Dictionary<string, string>(r.Environment);
        // Wine's debug channels cost time per call they log (an exception each, with +seh); off unless asked
        // for, by the Proton log or by a WINEDEBUG the launcher itself was started with (diagnostics).
        if (!protonLog && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WINEDEBUG"))) env.TryAdd("WINEDEBUG", "-all");
        env["WINEPREFIX"] = prefix;
        env["ASTROHYBRID_COMPAT_SESSION"] = session;
        env["ASTROHYBRID_COMPAT_LAYER"] = r.Layer.Id;
        if (_hasNvidiaGpu()) env.TryAdd("PROTON_ENABLE_NVAPI", "1");

        var argv = new List<string>();
        foreach (var w in r.Wrappers)
        {
            var path = _which(w);
            if (path != null) argv.Add(path);
        }

        if (r.Layer.Kind == CompatKind.Proton)
        {
            var umu = _which("umu-run") ?? throw new InvalidOperationException("umu-run is not installed (CachyOS/Arch: pacman -S umu-launcher). It is needed to run Proton outside Steam.");
            env["PROTONPATH"] = r.Layer.Path;
            env["GAMEID"] = "umu-default";   // no protonfixes: AstroHybrid's needs are set explicitly here
            env["STEAM_COMPAT_INSTALL_PATH"] = r.WorkingDirectory;
            if (protonLog)
            {
                env["PROTON_LOG"] = "1";
                env["PROTON_LOG_DIR"] = _logsDir;
            }
            if (r.ExtraPaths.Count > 0)
            {
                // umu rewrites STEAM_COMPAT_MOUNTS itself but appends STEAM_COMPAT_LIBRARY_PATHS;
                // pressure-vessel also reads its own variable directly.
                var extra = string.Join(':', r.ExtraPaths.Distinct());
                env["STEAM_COMPAT_LIBRARY_PATHS"] = Append(env.GetValueOrDefault("STEAM_COMPAT_LIBRARY_PATHS"), extra);
                env["PRESSURE_VESSEL_FILESYSTEMS_RW"] = Append(env.GetValueOrDefault("PRESSURE_VESSEL_FILESYSTEMS_RW"), extra);
            }
            argv.Add(umu);
        }
        else
        {
            argv.Add(r.Layer.Path);
        }
        argv.Add(r.ExePath);
        argv.AddRange(r.Args);

        if (r.Nice != 0)
            argv.InsertRange(0, new[] { _which("nice") ?? "nice", "-n", r.Nice.ToString() });
        return (argv, env);
    }

    private static string Append(string existing, string add) => string.IsNullOrEmpty(existing) ? add : existing + ":" + add;

    /// <summary>Starts argv through /bin/sh so stdout/stderr go straight to the log files: the game
    /// keeps running (and logging) if the launcher closes.</summary>
    private static Process Spawn(List<string> argv, Dictionary<string, string> env, string cwd, string stdout, string stderr, bool append = false)
    {
        var redirect = append ? ">>" : ">";
        var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, WorkingDirectory = cwd };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add($"exec \"$@\" {redirect}\"$AH_STDOUT\" 2{redirect}\"$AH_STDERR\"");
        psi.ArgumentList.Add("astrohybrid-run");
        foreach (var a in argv) psi.ArgumentList.Add(a);

        // Nothing of the AppImage's own mount may leak into Wine (e.g. a library path into $APPDIR).
        var appdir = Environment.GetEnvironmentVariable("APPDIR");
        if (!string.IsNullOrEmpty(appdir))
            foreach (var key in psi.Environment.Keys.ToList())
                if (psi.Environment[key]?.Contains(appdir, StringComparison.Ordinal) == true) psi.Environment.Remove(key);
        foreach (var (k, v) in env) psi.Environment[k] = v;
        psi.Environment["AH_STDOUT"] = stdout ?? "/dev/null";
        psi.Environment["AH_STDERR"] = stderr ?? "/dev/null";
        return Process.Start(psi) ?? throw new InvalidOperationException("could not start /bin/sh");
    }

    private static async Task<int> WaitAsync(Process p)
    {
        await p.WaitForExitAsync();
        return p.ExitCode;
    }

    private static void TryKill(Process p)
    {
        try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }

    internal static string Which(string name)
    {
        if (name.Contains('/')) return File.Exists(name) ? name : null;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static bool HasNvidiaGpu()
    {
        try
        {
            return Directory.GetDirectories("/sys/class/drm", "card*")
                .Select(d => Path.Combine(d, "device", "vendor"))
                .Where(File.Exists)
                .Any(f => File.ReadAllText(f).Trim().Equals("0x10de", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception) { return false; }
    }
}
