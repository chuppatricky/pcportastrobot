using System;
using System.IO;

namespace AstroHybrid.Linux.Platform;

/// <summary>
/// Where everything lives. The data dir plays the role of upstream's "root directory"
/// (it holds Build/RunHybrid, Build/astrohybrid-settings.json, Logs, mods), so upstream
/// services that take a rootDirectory are given <see cref="DataDir"/>.
/// </summary>
public static class AppPaths
{
    /// <summary>$ASTROHYBRID_HOME, else $XDG_DATA_HOME/AstroHybrid, else ~/.local/share/AstroHybrid.</summary>
    public static string DataDir { get; } = ResolveDataDir();

    public static string RunHybrid => Path.Combine(DataDir, "Build", "RunHybrid");
    public static string LogsDir => Path.Combine(DataDir, "Logs");
    public static string PrefixesDir => Path.Combine(DataDir, "prefixes");
    public static string ToolsDir => Path.Combine(DataDir, "tools");
    public static string LinuxSettingsFile => Path.Combine(DataDir, "Build", "astrohybrid-linux-settings.json");
    public static string PayloadStamp => Path.Combine(DataDir, ".payload-version");

    /// <summary>The read-only payload shipped in the AppImage ($APPDIR/usr/share/astrohybrid/payload),
    /// overridable with $ASTROHYBRID_PAYLOAD for development.</summary>
    public static string PayloadDir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("ASTROHYBRID_PAYLOAD");
            if (!string.IsNullOrEmpty(env)) return env;
            var appdir = Environment.GetEnvironmentVariable("APPDIR");
            if (!string.IsNullOrEmpty(appdir)) return Path.Combine(appdir, "usr", "share", "astrohybrid", "payload");
            return Path.Combine(AppContext.BaseDirectory, "payload");
        }
    }

    /// <summary>The running AppImage file ($APPIMAGE), or null when not run from an AppImage.</summary>
    public static string AppImageFile => NullIfEmpty(Environment.GetEnvironmentVariable("APPIMAGE"));

    /// <summary>Install made by the old WPF-under-Wine AppImage. Import only; never modified.</summary>
    public static string LegacyInstall => Path.Combine(Home, ".local", "share", "AstroHybrid-AppImage", "app");
    public static string LegacyPrefix => Path.Combine(Home, ".local", "share", "AstroHybrid-AppImage", "prefix");

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string ResolveDataDir()
    {
        var env = Environment.GetEnvironmentVariable("ASTROHYBRID_HOME");
        if (!string.IsNullOrEmpty(env)) return Path.GetFullPath(env);
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var baseDir = string.IsNullOrEmpty(xdg) ? Path.Combine(Home, ".local", "share") : xdg;
        return Path.Combine(baseDir, "AstroHybrid");
    }

    private static string NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
}
