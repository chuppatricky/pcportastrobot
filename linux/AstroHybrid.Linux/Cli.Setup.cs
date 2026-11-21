using System;
using AstroHybrid.Linux.Platform;
using AstroHybrid.Linux.Setup;

namespace AstroHybrid.Linux;

internal static partial class Cli
{
    static partial void RegisterSetup()
    {
        Commands["setup"] = ("<dump-dir>  set the game up from your PS5 dump (the dump is only read)", a => Setup(a));
        Commands["repair"] = ("set the game up again from the saved dump", _ => Repair());
        Commands["check"] = ("report problems with the installed game folder", _ => Check());
        Commands["import-legacy"] = ("[--dry-run]  copy settings and saves from the older Wine-based AppImage", a => Import(a));
    }

    private static readonly IProgress<(int Percent, string Message)> SetupProgress =
        new Progress<(int Percent, string Message)>(p => Console.WriteLine($"{p.Percent,3}% {p.Message}"));

    private static int Setup(string[] a)
    {
        if (a.Length != 1) throw new ArgumentException("usage: setup <dump-dir>");
        var f = LinuxSetup.RunAsync(a[0], SetupProgress).GetAwaiter().GetResult();
        Console.WriteLine($"set up {f.TitleId} {f.Version} in {AppPaths.RunHybrid} (Intel conversion: {f.IntelConversion}, real-time speed: {f.RealTimeSpeed})");
        return 0;
    }

    private static int Repair()
    {
        LinuxSetup.RepairAsync(SetupProgress).GetAwaiter().GetResult();
        return Check();
    }

    private static int Check()
    {
        var problem = LinuxSetup.DescribeProblem();
        Console.WriteLine(problem ?? "game folder OK");
        return problem == null ? 0 : 1;
    }

    private static int Import(string[] a)
    {
        var dry = a.Length == 1 && a[0] == "--dry-run";
        if (!LegacyImport.Available()) { Console.WriteLine($"nothing to import ({AppPaths.LegacyInstall} has no settings)"); return 0; }
        var r = LegacyImport.Run(dry);
        foreach (var i in r.Items)
            Console.WriteLine($"{(i.Skipped ? "skip" : dry ? "would copy" : "copied")}  {i.What,-15} {i.Source} -> {i.Target}{(i.Skipped ? $" ({i.Reason})" : "")}");
        if (r.DumpPath != null) Console.WriteLine($"game dump: {r.DumpPath}  (set the game up from it: astrohybrid --cli setup \"{r.DumpPath}\")");
        return 0;
    }
}
