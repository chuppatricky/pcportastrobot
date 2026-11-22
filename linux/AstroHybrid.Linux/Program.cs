using System;
using Avalonia;

namespace AstroHybrid.Linux;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Platform.Services.Init();
        // An updated AppImage brings a new Windows runtime: copy it into the data dir before anything uses it.
        Update.PayloadSync.EnsureCurrent();
        if (args.Length > 0 && args[0] == "--cli")
            return Cli.Run(args[1..]);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
