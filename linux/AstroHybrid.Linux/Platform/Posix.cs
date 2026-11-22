using System;
using System.Runtime.InteropServices;

namespace AstroHybrid.Linux.Platform;

/// <summary>The few libc calls .NET has no managed equivalent for.</summary>
public static partial class Posix
{
    [LibraryImport("libc", EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial IntPtr realpath(string path, IntPtr resolved);

    [LibraryImport("libc", EntryPoint = "free")]
    private static partial void free(IntPtr p);

    // glibc ≥ 2.33 exports stat() itself (older ones only __xstat); x86_64 struct stat starts with
    // st_dev (u64) and st_ino (u64), which is all this needs.
    [LibraryImport("libc", EntryPoint = "stat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int stat(string path, Span<byte> buf);

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int kill(int pid, int sig);

    public const int SIGTERM = 15, SIGKILL = 9;

    /// <summary>realpath(3), or null when the path does not exist.</summary>
    public static string RealPath(string path)
    {
        var p = realpath(path, IntPtr.Zero);
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(p); }
        finally { free(p); }
    }

    /// <summary>(st_dev, st_ino) of a file, following symlinks; null if it cannot be stat'ed.
    /// Identifies a file across mount namespaces (pressure-vessel's /run/host/... is the same inode).</summary>
    public static (ulong Dev, ulong Ino)? FileId(string path)
    {
        Span<byte> buf = stackalloc byte[256];
        if (stat(path, buf) != 0) return null;
        return (BitConverter.ToUInt64(buf[..8]), BitConverter.ToUInt64(buf.Slice(8, 8)));
    }

    public static bool Signal(int pid, int sig) => kill(pid, sig) == 0;
}
