using AstroHybrid.Linux.Update;

namespace AstroHybrid.Linux.Platform;

public static partial class Services
{
    static partial void InitUpdate()
    {
        Updater = new AppImageUpdater();
    }
}
