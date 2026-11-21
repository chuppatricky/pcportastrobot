using System.IO;

namespace AstroHybrid.Linux.Platform;

/// <summary>Linux path ⇄ Wine path. Every Wine/Proton prefix maps Z: to "/".</summary>
public static class WinPath
{
    /// <summary>/home/u/x → Z:\home\u\x</summary>
    public static string ToWindows(string linuxPath) =>
        "Z:" + Path.GetFullPath(linuxPath).Replace('/', '\\');

    /// <summary>Z:\home\u\x → /home/u/x (other drive letters are returned unchanged).</summary>
    public static string ToLinux(string windowsPath) =>
        windowsPath.Length >= 2 && (windowsPath[0] == 'Z' || windowsPath[0] == 'z') && windowsPath[1] == ':'
            ? windowsPath[2..].Replace('\\', '/')
            : windowsPath;
}
