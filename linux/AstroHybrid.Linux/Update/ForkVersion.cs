using System;
using System.Text.RegularExpressions;

namespace AstroHybrid.Linux.Update;

/// <summary>
/// Release version of the Linux fork: "&lt;upstream x.y.z&gt;[-linux.&lt;n&gt;]", optionally with a leading 'v'
/// (tags) and a "+build" suffix (the SDK appends the commit to InformationalVersion).
/// The "-linux.N" part is a fork revision on top of upstream x.y.z, so it sorts AFTER plain x.y.z
/// and before the next upstream version (1.2.10 &lt; 1.2.10-linux.1 &lt; 1.2.10-linux.3 &lt; 1.2.11).
/// Any other suffix (-rc1, -beta.2) is a pre-release and sorts before plain x.y.z.
/// Upstream's UpdateInfo.CompareVersions treats "linux" as a pre-release label, which gets the
/// first of those orderings backwards.
/// </summary>
public sealed class ForkVersion : IComparable<ForkVersion>
{
    private static readonly Regex Pattern = new(
        @"^v?(?<core>\d+(?:\.\d+){1,3})(?:-(?:linux\.(?<rev>\d+)|(?<pre>[0-9A-Za-z.-]+)))?(?:\+[0-9A-Za-z.-]*)?$",
        RegexOptions.CultureInvariant);

    public int[] Core { get; }
    /// <summary>The N of "-linux.N"; 0 when absent.</summary>
    public int LinuxRevision { get; }
    /// <summary>Pre-release label ("rc1"), or null for a release.</summary>
    public string PreRelease { get; }

    private ForkVersion(int[] core, int rev, string pre)
    {
        Core = core;
        LinuxRevision = rev;
        PreRelease = pre;
    }

    public static ForkVersion Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"not a version: '{text}'");

    public static bool TryParse(string text, out ForkVersion version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = Pattern.Match(text.Trim());
        if (!m.Success) return false;
        var parts = m.Groups["core"].Value.Split('.');
        var core = new int[4];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], out core[i])) return false;
        int rev = 0;
        if (m.Groups["rev"].Success && !int.TryParse(m.Groups["rev"].Value, out rev)) return false;
        version = new ForkVersion(core, rev, m.Groups["pre"].Success ? m.Groups["pre"].Value : null);
        return true;
    }

    public int CompareTo(ForkVersion other)
    {
        if (other is null) return 1;
        for (int i = 0; i < Core.Length; i++)
            if (Core[i] != other.Core[i]) return Core[i].CompareTo(other.Core[i]);
        // A pre-release of x.y.z comes before x.y.z and every x.y.z-linux.N.
        bool pre = PreRelease != null, otherPre = other.PreRelease != null;
        if (pre != otherPre) return pre ? -1 : 1;
        if (pre) return string.CompareOrdinal(PreRelease, other.PreRelease);
        return LinuxRevision.CompareTo(other.LinuxRevision);
    }

    public static int Compare(string a, string b) => Parse(a).CompareTo(Parse(b));
}
