using AstroHybrid.App.Services;
using AstroHybrid.Linux.Setup;

namespace AstroHybrid.Linux.Platform;

public static partial class Services
{
    static partial void InitSetup() => GameSetup.LinuxRelinker = LinuxSetup.RelinkerPath();
}
