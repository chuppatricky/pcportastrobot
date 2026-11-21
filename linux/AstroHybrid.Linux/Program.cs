using System;
using Avalonia;

namespace AstroHybrid.Linux;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Platform.Services.Init();
        if (args.Length > 0 && args[0] == "--cli")
            return Cli.Run(args[1..]);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
