using System;
using System.Collections.Generic;

namespace AstroHybrid.Linux;

/// <summary>`astrohybrid --cli &lt;command&gt; [args]`. Each area adds its commands in its own
/// partial file (Cli.Compat.cs, Cli.Launch.cs, ...) by adding to <see cref="Commands"/> in a
/// static partial method.</summary>
internal static partial class Cli
{
    private static readonly Dictionary<string, (string Help, Func<string[], int> Run)> Commands = new();

    static partial void RegisterCompat();
    static partial void RegisterLaunch();
    static partial void RegisterSetup();
    static partial void RegisterUpdate();
    static partial void RegisterProbe();

    public static int Run(string[] args)
    {
        RegisterCompat(); RegisterLaunch(); RegisterSetup(); RegisterUpdate(); RegisterProbe();
        if (args.Length == 0 || !Commands.TryGetValue(args[0], out var cmd))
        {
            Console.Error.WriteLine("usage: astrohybrid --cli <command> [args]");
            foreach (var (name, c) in Commands) Console.Error.WriteLine($"  {name,-14} {c.Help}");
            return args.Length == 0 ? 0 : 2;
        }
        try { return cmd.Run(args[1..]); }
        catch (Exception e) { Console.Error.WriteLine($"error: {e.Message}"); return 1; }
    }
}
