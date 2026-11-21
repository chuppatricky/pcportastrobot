using AstroHybrid.Linux.Compat;

namespace AstroHybrid.Linux.Platform;

public static partial class Services
{
    static partial void InitCompat()
    {
        Catalog = new LayerCatalog();
        Runner = new CompatRunner();
        Verifier = new LayerVerifier(Catalog);
    }
}
