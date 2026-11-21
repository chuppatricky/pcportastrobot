using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AstroHybrid.Linux.Platform;

namespace AstroHybrid.Linux.Compat;

/// <summary>
/// Proves which layer a started program really runs on. Version strings cannot tell (GE-Proton11-10
/// and 11-12 both report "wine-11.0 (Staging)") and paths differ inside Proton's container
/// (/run/host/...), so the check is by file identity: the x86_64-unix/ntdll.so the Wine process has
/// mapped, stat'ed through /proc/&lt;pid&gt;/root, must be the very same inode (st_dev, st_ino) as the
/// chosen layer's ntdll.so. The process is found by the session id the runner put in its environment.
/// </summary>
public sealed class LayerVerifier : ILayerVerifier
{
    private readonly ILayerCatalog _catalog;
    private readonly string _logsDir;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public LayerVerifier(ILayerCatalog catalog) : this(catalog, AppPaths.LogsDir) { }

    internal LayerVerifier(ILayerCatalog catalog, string logsDir)
    {
        _catalog = catalog;
        _logsDir = logsDir;
    }

    private string ResultFile => Path.Combine(_logsDir, "compat-verify.json");

    public async Task<LayerVerification> VerifyAsync(RunHandle handle, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var started = DateTime.Now;
        WineProcess found = null;
        while (found == null)
        {
            found = FindSessionProcess(handle.SessionId, handle.ExeName);
            if (found != null) break;
            if (handle.Process.HasExited)
                return Save(Fail(handle, $"The program exited (code {handle.Process.ExitCode}) before Wine started; see the logs."));
            if (DateTime.UtcNow > deadline)
                return Save(Fail(handle, $"No Wine process of this run appeared within {timeout.TotalSeconds:0} s."));
            await Task.Delay(250, ct);
        }

        var expected = Posix.FileId(handle.Layer.NtdllPath);
        var observed = Posix.FileId($"/proc/{found.Pid}/root{found.NtdllPath}");
        var verified = expected != null && observed != null && expected == observed;
        var observedLayer = verified ? handle.Layer
            : observed == null ? null
            : _catalog.Discover().FirstOrDefault(l => !string.IsNullOrEmpty(l.NtdllPath) && Posix.FileId(l.NtdllPath) == observed);

        var result = new LayerVerification
        {
            Verified = verified,
            ExpectedLayerId = handle.Layer.Id,
            ObservedLayerId = observedLayer?.Id ?? "",
            ObservedNtdll = found.NtdllPath,
            GamePid = found.Pid,
            NtsyncOpen = await NtsyncOpenedAsync(found.Pid, ct),
            ProtonLogHeader = ProtonLogHeader(started),
            Message = verified
                ? $"Running through {handle.Layer.DisplayName} (verified: pid {found.Pid} uses its ntdll.so)."
                : observedLayer != null
                    ? $"MISMATCH: expected {handle.Layer.DisplayName}, but pid {found.Pid} runs on {observedLayer.DisplayName}."
                    : $"MISMATCH: expected {handle.Layer.DisplayName}, but pid {found.Pid} uses {found.NtdllPath}, which belongs to no known layer.",
        };
        return Save(result);
    }

    public LayerVerification LastResult()
    {
        try { return File.Exists(ResultFile) ? JsonSerializer.Deserialize<LayerVerification>(File.ReadAllText(ResultFile)) : null; }
        catch (Exception) { return null; }
    }

    private LayerVerification Save(LayerVerification v)
    {
        try
        {
            Directory.CreateDirectory(_logsDir);
            File.WriteAllText(ResultFile, JsonSerializer.Serialize(v, Json));
        }
        catch (IOException) { /* the result is still returned; only the record is lost */ }
        return v;
    }

    private static LayerVerification Fail(RunHandle h, string message) => new()
    {
        Verified = false,
        ExpectedLayerId = h.Layer.Id,
        Message = message,
    };

    internal sealed record WineProcess(int Pid, string NtdllPath, string CommandLine);

    /// <summary>A process carrying the session id that has Wine's unix ntdll.so mapped (the preloader and
    /// wineserver do not). When several do (services.exe, winedevice.exe, ...), the one running the
    /// program itself is preferred; they all share one Wine build anyway.</summary>
    internal static WineProcess FindSessionProcess(string session, string exeName)
    {
        var marker = Encoding.UTF8.GetBytes("ASTROHYBRID_COMPAT_SESSION=" + session + "\0");
        var candidates = new List<WineProcess>();
        foreach (var dir in SafeDirs("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            byte[] environ;
            try { environ = File.ReadAllBytes(Path.Combine(dir, "environ")); }
            catch (Exception) { continue; }
            if (environ.AsSpan().IndexOf(marker) < 0) continue;
            var ntdll = MappedNtdll(pid);
            if (ntdll == null) continue;
            candidates.Add(new WineProcess(pid, ntdll, CommandLine(pid)));
        }
        if (candidates.Count == 0) return null;
        return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(exeName) && c.CommandLine.Contains(exeName, StringComparison.OrdinalIgnoreCase))
            ?? candidates[0];
    }

    internal static string MappedNtdll(int pid)
    {
        try
        {
            foreach (var line in File.ReadLines($"/proc/{pid}/maps"))
            {
                var idx = line.IndexOf('/');
                if (idx > 0 && line.EndsWith("/x86_64-unix/ntdll.so", StringComparison.Ordinal))
                    return line[idx..];
            }
        }
        catch (Exception) { }
        return null;
    }

    private static string CommandLine(int pid)
    {
        try { return File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ').Trim(); }
        catch (Exception) { return ""; }
    }

    /// <summary>Wine opens /dev/ntsync a moment after ntdll.so is mapped, so look for up to 3 s.</summary>
    private static async Task<bool> NtsyncOpenedAsync(int pid, CancellationToken ct)
    {
        for (var i = 0; i < 12; i++)
        {
            if (HasNtsync(pid)) return true;
            await Task.Delay(250, ct);
        }
        return false;
    }

    private static bool HasNtsync(int pid)
    {
        foreach (var fd in SafeFiles($"/proc/{pid}/fd"))
        {
            try
            {
                if (new FileInfo(fd).LinkTarget == "/dev/ntsync") return true;
            }
            catch (Exception) { }
        }
        return false;
    }

    /// <summary>"Proton: 1793687165 GE-Proton11-12" from the Proton log this run wrote (PROTON_LOG_DIR = Logs).</summary>
    private string ProtonLogHeader(DateTime since)
    {
        var log = SafeFiles(_logsDir)
            .Where(f => Path.GetFileName(f).StartsWith("steam-", StringComparison.Ordinal) && f.EndsWith(".log", StringComparison.Ordinal))
            .Select(f => new FileInfo(f))
            .Where(f => f.LastWriteTime >= since.AddSeconds(-5))
            .OrderByDescending(f => f.LastWriteTime)
            .FirstOrDefault();
        if (log == null) return "";
        try
        {
            return File.ReadLines(log.FullName).Take(30).FirstOrDefault(l => l.StartsWith("Proton:", StringComparison.Ordinal))?.Trim() ?? "";
        }
        catch (IOException) { return ""; }
    }

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); } catch (Exception) { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir) : Array.Empty<string>(); } catch (Exception) { return Array.Empty<string>(); }
    }
}
