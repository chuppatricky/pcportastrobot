using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AstroHybrid.Linux.Platform;

namespace AstroHybrid.Linux.Compat;

/// <summary>
/// Finds every Proton and Wine build on the machine: Steam's compatibilitytools.d (user, Flatpak,
/// Snap and system-wide), Proton installed as Steam apps in every library, the runner folders of
/// umu, Bottles, Heroic and Lutris, plus plain Wine on PATH and in /opt. One entry per real
/// directory (realpath), so ~/.steam/root and ~/.local/share/Steam do not list everything twice.
/// </summary>
public sealed class LayerCatalog : ILayerCatalog
{
    private readonly string _home;
    private readonly string _xdgData;
    private readonly IReadOnlyList<string> _systemToolDirs;
    private readonly IReadOnlyList<string> _wineCandidates;
    private readonly Func<string, string> _wineVersion;

    public LayerCatalog() : this(AppPaths.Home, null, null, null, null) { }

    /// <summary>Test seam: every root can be pointed into a fake tree.</summary>
    internal LayerCatalog(string home, string xdgData, IReadOnlyList<string> systemToolDirs,
        IReadOnlyList<string> wineCandidates, Func<string, string> wineVersion)
    {
        _home = home;
        var xdgEnv = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        _xdgData = xdgData ?? (string.IsNullOrEmpty(xdgEnv) ? Path.Combine(home, ".local", "share") : xdgEnv);
        _systemToolDirs = systemToolDirs ?? new[]
        {
            "/usr/share/steam/compatibilitytools.d", "/usr/local/share/steam/compatibilitytools.d",
            "/opt/steam/compatibilitytools.d",
        };
        _wineCandidates = wineCandidates ?? DefaultWineCandidates();
        _wineVersion = wineVersion ?? ReadWineVersion;
    }

    public IReadOnlyList<CompatLayer> Discover()
    {
        var found = new List<CompatLayer>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddProtonDir(string dir, string source)
        {
            var real = RealPath(dir);
            if (real == null || !seen.Add(real)) return;
            var layer = ReadProton(dir, source);
            if (layer != null) found.Add(layer); else seen.Remove(real);
        }

        foreach (var root in SteamRoots())
        {
            foreach (var d in Subdirs(Path.Combine(root, "compatibilitytools.d")))
                AddProtonDir(d, "steam-user");
            foreach (var lib in LibraryDirs(root))
                foreach (var d in Subdirs(Path.Combine(lib, "steamapps", "common")).Where(d => Path.GetFileName(d).StartsWith("Proton", StringComparison.Ordinal)))
                    AddProtonDir(d, "steam-library");
        }
        foreach (var sys in _systemToolDirs)
            foreach (var d in Subdirs(sys))
                AddProtonDir(d, "steam-system");
        foreach (var (dir, source) in new[]
        {
            (Path.Combine(_xdgData, "umu", "compatibilitytools"), "umu"),
            (Path.Combine(_xdgData, "bottles", "runners"), "bottles"),
            (Path.Combine(_home, ".var", "app", "com.usebottles.bottles", "data", "bottles", "runners"), "bottles"),
            (Path.Combine(_home, ".config", "heroic", "tools", "proton"), "heroic"),
            (Path.Combine(_xdgData, "lutris", "runners", "proton"), "lutris"),
        })
            foreach (var d in Subdirs(dir))
                AddProtonDir(d, source);

        foreach (var wine in _wineCandidates)
        {
            if (!File.Exists(wine)) continue;
            var real = RealPath(wine);
            if (real == null || !seen.Add(real)) continue;
            var layer = ReadWine(wine);
            if (layer != null) found.Add(layer);
        }

        return UniqueIds(found);
    }

    public CompatLayer Find(string id) =>
        string.IsNullOrEmpty(id) ? null : Discover().FirstOrDefault(l => l.Id == id);

    public CompatLayer Default() => PickDefault(Discover());

    /// <summary>Proton-CachyOS first (the fork's chosen default), then the newest GE-Proton, then any other
    /// Proton (newest first), then system Wine. Within a group, Steam's own folders beat other launchers'.</summary>
    internal static CompatLayer PickDefault(IReadOnlyList<CompatLayer> layers) =>
        layers.Where(l => l.Warnings.Count == 0 || l.Kind == CompatKind.Proton)
            .OrderBy(l => l.Kind == CompatKind.Proton ? 0 : 1)
            .ThenBy(l => l.IsCachyOs ? 0 : IsGe(l) ? 1 : 2)
            .ThenBy(l => SourceRank(l.Source))
            .ThenByDescending(l => VersionKey(l.Version), VersionKeyComparer.Instance)
            .FirstOrDefault()
        ?? layers.FirstOrDefault();

    private static bool IsGe(CompatLayer l) => l.Version.StartsWith("GE-Proton", StringComparison.OrdinalIgnoreCase);

    private static int SourceRank(string source) => source switch
    {
        "steam-user" => 0, "steam-system" => 1, "steam-library" => 2, "umu" => 3, _ => 4,
    };

    private CompatLayer ReadProton(string dir, string source)
    {
        if (!File.Exists(Path.Combine(dir, "proton"))) return null;
        var name = Path.GetFileName(dir.TrimEnd('/'));
        var version = "";
        var versionFile = Path.Combine(dir, "version");
        if (File.Exists(versionFile))
        {
            // "<build stamp> <name>", e.g. "1793687165 GE-Proton11-12"
            var parts = SafeRead(versionFile).Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            version = parts.Length > 1 ? parts[1] : parts.FirstOrDefault() ?? "";
        }
        var display = VdfValue(SafeRead(Path.Combine(dir, "compatibilitytool.vdf")), "display_name");
        var ntdll = FirstExisting(
            Path.Combine(dir, "files", "lib", "wine", "x86_64-unix", "ntdll.so"),
            Path.Combine(dir, "files", "lib64", "wine", "x86_64-unix", "ntdll.so"),
            Path.Combine(dir, "dist", "lib", "wine", "x86_64-unix", "ntdll.so"),
            Path.Combine(dir, "dist", "lib64", "wine", "x86_64-unix", "ntdll.so"));
        var warnings = new List<string>();
        if (ntdll == null) warnings.Add("No 64-bit Wine (x86_64-unix/ntdll.so) in this Proton: it cannot run the game.");
        return new CompatLayer
        {
            Id = Slug(name),
            DisplayName = string.IsNullOrWhiteSpace(display) ? name : display,
            Kind = CompatKind.Proton,
            Path = dir,
            Version = version,
            Source = source,
            NtdllPath = ntdll ?? "",
            Warnings = warnings,
        };
    }

    private CompatLayer ReadWine(string wine)
    {
        var bin = Path.GetDirectoryName(wine)!;
        var ntdll = FirstExisting(
            Path.Combine(bin, "..", "lib", "wine", "x86_64-unix", "ntdll.so"),
            Path.Combine(bin, "..", "lib64", "wine", "x86_64-unix", "ntdll.so"));
        var version = _wineVersion(wine);
        var warnings = new List<string>();
        if (ntdll == null) warnings.Add("No 64-bit Wine (x86_64-unix/ntdll.so) found next to this wine.");
        var m = Regex.Match(version, @"wine-(\d+)");
        if (m.Success && int.Parse(m.Groups[1].Value) < 11)
            warnings.Add("Wine older than 11 is not supported by AstroHybrid (the game needs Wine 11 or newer).");
        // /usr/bin/wine → "wine-system"; /opt/wine-cachyos/bin/wine → "wine-cachyos"
        var owner = Path.GetFileName(Path.GetFullPath(Path.Combine(bin, "..")));
        var id = owner is "usr" or "local" ? "wine-system" : Slug(owner.StartsWith("wine", StringComparison.OrdinalIgnoreCase) ? owner : "wine-" + owner);
        return new CompatLayer
        {
            Id = id,
            DisplayName = string.IsNullOrEmpty(version) ? $"Wine ({wine})" : $"Wine {version.Replace("wine-", "")}" + (id == "wine-system" ? " (system)" : ""),
            Kind = CompatKind.Wine,
            Path = wine,
            Version = version,
            Source = "system",
            NtdllPath = ntdll == null ? "" : Path.GetFullPath(ntdll),
            Warnings = warnings,
        };
    }

    /// <summary>Two folders can share a name (a Proton in compatibilitytools.d and in a Bottles runner):
    /// the later one gets its source appended, so ids stay unique and stable between scans.</summary>
    private static IReadOnlyList<CompatLayer> UniqueIds(List<CompatLayer> layers)
    {
        var used = new HashSet<string>();
        var result = new List<CompatLayer>(layers.Count);
        foreach (var l in layers)
        {
            var id = l.Id;
            if (!used.Add(id))
            {
                id = $"{l.Id}-{l.Source}";
                for (var n = 2; !used.Add(id); n++) id = $"{l.Id}-{l.Source}-{n}";
            }
            result.Add(id == l.Id ? l : l with { Id = id });
        }
        return result;
    }

    private IEnumerable<string> SteamRoots()
    {
        var candidates = new[]
        {
            Path.Combine(_xdgData, "Steam"),
            Path.Combine(_home, ".steam", "root"),
            Path.Combine(_home, ".steam", "steam"),
            Path.Combine(_home, ".steam", "debian-installation"),
            Path.Combine(_home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam"),
            Path.Combine(_home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"),
            Path.Combine(_home, "snap", "steam", "common", ".local", "share", "Steam"),
        };
        var seen = new HashSet<string>();
        foreach (var c in candidates)
        {
            if (!Directory.Exists(c)) continue;
            var real = RealPath(c);
            if (real != null && seen.Add(real)) yield return real;
        }
    }

    private static IEnumerable<string> LibraryDirs(string steamRoot)
    {
        yield return steamRoot;
        var vdf = SafeRead(Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"));
        foreach (Match m in Regex.Matches(vdf, "\"path\"\\s+\"([^\"]+)\""))
        {
            var p = m.Groups[1].Value.Replace(@"\\", @"\");
            if (RealPath(p) != RealPath(steamRoot)) yield return p;
        }
    }

    internal static string VdfValue(string text, string key)
    {
        var m = Regex.Match(text ?? "", "\"" + Regex.Escape(key) + "\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    internal static string Slug(string name)
    {
        var s = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9.]+", "-").Trim('-');
        s = Regex.Replace(s, "-x86-64$", "");   // "GE-Proton11-12-x86_64" → "ge-proton11-12"
        return s.Length == 0 ? "layer" : s;
    }

    /// <summary>Numbers in a version string, for "newest first": GE-Proton11-12 → [11, 12].</summary>
    internal static int[] VersionKey(string version) =>
        Regex.Matches(version ?? "", @"\d+").Select(m => int.TryParse(m.Value, out var n) ? n : int.MaxValue).ToArray();

    private sealed class VersionKeyComparer : IComparer<int[]>
    {
        public static readonly VersionKeyComparer Instance = new();
        public int Compare(int[] a, int[] b)
        {
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
                if (a[i] != b[i]) return a[i].CompareTo(b[i]);
            return a.Length.CompareTo(b.Length);
        }
    }

    private static IReadOnlyList<string> DefaultWineCandidates()
    {
        var list = new List<string>();
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var w = Path.Combine(dir, "wine");
            if (File.Exists(w)) { list.Add(w); break; }
        }
        try { list.AddRange(Directory.GetDirectories("/opt", "wine*").Select(d => Path.Combine(d, "bin", "wine"))); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        return list;
    }

    private static string ReadWineVersion(string wine)
    {
        try
        {
            var psi = new ProcessStartInfo(wine, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.Environment.Remove("DISPLAY");
            psi.Environment.Remove("WAYLAND_DISPLAY");
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd().Trim();
            return p.WaitForExit(10000) ? output : "";
        }
        catch (Exception) { return ""; }
    }

    private static IEnumerable<string> Subdirs(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.Ordinal) : Enumerable.Empty<string>(); }
        catch (IOException) { return Enumerable.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Enumerable.Empty<string>(); }
    }

    private static string FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);

    private static string SafeRead(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    /// <summary>libc realpath(3): resolves every symlinked component. Null when the path does not exist.</summary>
    internal static string RealPath(string path) => Posix.RealPath(path);
}
