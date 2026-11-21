namespace AstroHybrid.Linux.Platform;

/// <summary>The app's service instances. Each area fills in its own in a partial method
/// implemented in its folder (Compat/Services.Compat.cs, ...). Call <see cref="Init"/> once at startup
/// (Program does, for both the GUI and the CLI).</summary>
public static partial class Services
{
    public static ILayerCatalog Catalog { get; set; }
    public static IRunner Runner { get; set; }
    public static ILayerVerifier Verifier { get; set; }
    public static IHardwareProbe Hardware { get; set; }
    public static IUpdater Updater { get; set; }

    private static bool _initialized;

    static partial void InitCompat();
    static partial void InitLaunch();
    static partial void InitSetup();
    static partial void InitUpdate();
    static partial void InitProbe();

    public static void Init()
    {
        if (_initialized) return;
        _initialized = true;
        InitCompat(); InitLaunch(); InitSetup(); InitUpdate(); InitProbe();
    }
}
