using System;
using System.IO;

namespace AstroHybrid.Tests.Update;

/// <summary>A scratch directory, removed on Dispose. Lives under $ASTROHYBRID_TEST_ROOT when set,
/// so tests never write near a real install.</summary>
internal sealed class TestDir : IDisposable
{
    public string Path { get; }

    public TestDir(string name)
    {
        var root = Environment.GetEnvironmentVariable("ASTROHYBRID_TEST_ROOT");
        if (string.IsNullOrEmpty(root)) root = System.IO.Path.GetTempPath();
        Path = System.IO.Path.Combine(root, $"update-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Write(string rel, string content)
    {
        var p = System.IO.Path.Combine(Path, rel);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    public void Dispose()
    {
        // Read-only test dirs must be made writable again before they can be removed.
        foreach (var d in Directory.EnumerateDirectories(Path, "*", SearchOption.AllDirectories))
            File.SetUnixFileMode(d, (UnixFileMode)0b111_101_101);
        File.SetUnixFileMode(Path, (UnixFileMode)0b111_101_101);
        Directory.Delete(Path, recursive: true);
    }
}
