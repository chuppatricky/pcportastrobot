using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AstroHybrid.App.Models;

namespace AstroHybrid.Linux.Platform;

// Shared contracts between the Linux launcher's parts. Implementations live in
// Compat/, Launch/, Setup/, Update/ and Probe/. Members may be ADDED; existing
// members must not be renamed or have their meaning changed.

public enum CompatKind { Proton, Wine }

/// <summary>One Wine/Proton build the game can run through.</summary>
public sealed record CompatLayer
{
    /// <summary>Stable slug, used as the settings value and the prefix folder name, e.g. "proton-cachyos-slr", "ge-proton11-12", "wine-system".</summary>
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required CompatKind Kind { get; init; }
    /// <summary>Proton: the tool directory (contains `proton`, `files/`). Wine: the `wine` binary.</summary>
    public required string Path { get; init; }
    /// <summary>The `version` file / `wine --version` text.</summary>
    public string Version { get; init; } = "";
    /// <summary>Where it was found: "steam-user", "steam-system", "steam-library", "flatpak", "lutris", "bottles", "heroic", "system".</summary>
    public string Source { get; init; } = "";
    /// <summary>The layer's x86_64 unix ntdll.so: the file whose inode proves which layer a game process really runs on.</summary>
    public string NtdllPath { get; init; } = "";
    /// <summary>Human-readable cautions (e.g. "Wine older than 11 is not supported").</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public bool IsCachyOs => Id.Contains("cachyos", StringComparison.OrdinalIgnoreCase);
}

public interface ILayerCatalog
{
    /// <summary>Scan all known locations, de-duplicated by realpath. Cheap enough to call on page open.</summary>
    IReadOnlyList<CompatLayer> Discover();
    CompatLayer Find(string id);
    /// <summary>Proton-CachyOS if installed, else newest GE-Proton, else newest other Proton, else system Wine.</summary>
    CompatLayer Default();
}

/// <summary>Everything needed to start one Windows process through a layer.</summary>
public sealed record RunRequest
{
    public required CompatLayer Layer { get; init; }
    /// <summary>Linux path of the .exe.</summary>
    public required string ExePath { get; init; }
    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();
    /// <summary>Linux path; the game's cwd (Build/RunHybrid for the game).</summary>
    public required string WorkingDirectory { get; init; }
    /// <summary>Game env vars (already Wine-path-converted where needed). The runner adds its own on top.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    public string StdoutLog { get; init; }
    public string StderrLog { get; init; }
    /// <summary>Optional wrappers prepended to the command (e.g. "gamemoderun", "mangohud").</summary>
    public IReadOnlyList<string> Wrappers { get; init; } = Array.Empty<string>();
    /// <summary>nice(1) adjustment for the runner process; 0 = none, negative = higher priority (needs permission; ignored on failure).</summary>
    public int Nice { get; init; }
}

public sealed class RunHandle
{
    /// <summary>Random id placed in the child's environment as ASTROHYBRID_COMPAT_SESSION, so the game process can be found and verified.</summary>
    public required string SessionId { get; init; }
    public required CompatLayer Layer { get; init; }
    public required Process Process { get; init; }
    /// <summary>The prefix used (WINEPREFIX).</summary>
    public required string Prefix { get; init; }
    /// <summary>Completes with the exit code once the runner process (and therefore the game) exits.</summary>
    public required Task<int> Exited { get; init; }
}

public interface IRunner
{
    /// <summary>prefixes/&lt;layer.Id&gt;</summary>
    string PrefixFor(CompatLayer layer);
    /// <summary>Create/upgrade the layer's prefix (wineboot through that same layer) and apply per-prefix fixes (DualSense BTHENUM).</summary>
    Task EnsurePrefixAsync(CompatLayer layer, IProgress<string> progress, CancellationToken ct);
    Task<RunHandle> StartAsync(RunRequest request, CancellationToken ct);
}

public sealed record LayerVerification
{
    public required bool Verified { get; init; }
    public required string ExpectedLayerId { get; init; }
    /// <summary>The layer the process was observed on (Id), or "" if it matched none of the known layers.</summary>
    public string ObservedLayerId { get; init; } = "";
    /// <summary>Path of ntdll.so as mapped by the game process (may be /run/host/... inside pressure-vessel).</summary>
    public string ObservedNtdll { get; init; } = "";
    public int GamePid { get; init; }
    public bool NtsyncOpen { get; init; }
    /// <summary>The "Proton: …" header line from the Proton log, if any.</summary>
    public string ProtonLogHeader { get; init; } = "";
    public string Message { get; init; } = "";
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.Now;
}

public interface ILayerVerifier
{
    /// <summary>Waits (up to timeout) for a process carrying the handle's session id that has ntdll.so mapped,
    /// then compares (st_dev, st_ino) of that file with the expected layer's NtdllPath.
    /// Also writes Logs/compat-verify.json.</summary>
    Task<LayerVerification> VerifyAsync(RunHandle handle, TimeSpan timeout, CancellationToken ct);
    /// <summary>The last verification written to Logs/compat-verify.json, or null.</summary>
    LayerVerification LastResult();
}

public interface IHardwareProbe
{
    /// <summary>Linux equivalent of upstream HardwareDetector: GPUs (Vulkan names; vendor; VRAM from sysfs;
    /// feature flags incl. AMD FSR), CPU, RAM, primary display resolution.</summary>
    HardwareInfo Detect();
}

public sealed record UpdateOffer
{
    public required string Version { get; init; }
    public required string Tag { get; init; }
    public required string AssetUrl { get; init; }
    public required long Size { get; init; }
    /// <summary>"sha256:&lt;hex&gt;" from the GitHub API asset digest; required — an asset without a digest is not offered.</summary>
    public required string Digest { get; init; }
    public string Notes { get; init; } = "";
    public string HtmlUrl { get; init; } = "";
}

public interface IUpdater
{
    string CurrentVersion { get; }
    /// <summary>Null when up to date.</summary>
    Task<UpdateOffer> CheckAsync(CancellationToken ct);
    /// <summary>Download, verify the digest, atomically replace $APPIMAGE. Returns false (with a reason) when
    /// it cannot self-update (not run from an AppImage, dir not writable); the UI then opens HtmlUrl.</summary>
    Task<(bool Ok, string Message)> InstallAsync(UpdateOffer offer, IProgress<double> progress, CancellationToken ct);
    /// <summary>execv the new AppImage (never returns on success).</summary>
    void Relaunch(string[] args);
}
