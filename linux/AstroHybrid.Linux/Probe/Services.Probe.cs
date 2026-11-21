using AstroHybrid.Linux.Probe;

namespace AstroHybrid.Linux.Platform;

public static partial class Services
{
    static partial void InitProbe() => Hardware = new LinuxHardwareProbe();
}
