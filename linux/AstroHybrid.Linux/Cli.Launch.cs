using System;
using System.Linq;
using System.Threading;
using AstroHybrid.App.Services;
using AstroHybrid.Linux.Launch;
using AstroHybrid.Linux.Platform;

namespace AstroHybrid.Linux;

internal static partial class Cli
{
    static partial void RegisterLaunch()
    {
        Commands["env"] = ("[--layer <id>]  print the game's environment for the next start", a => Env(a));
        Commands["play"] = ("[--layer <id>] [--dry-run]  start the game through the chosen layer", a => Play(a));
        Commands["stop"] = ("end a running game", _ => { Console.WriteLine($"ended {GameLauncher.KillGame()} process(es)"); return 0; });
    }

    private static (string Layer, bool DryRun) LaunchArgs(string[] a)
    {
        string layer = null;
        var dry = false;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] == "--layer" && i + 1 < a.Length) layer = a[++i];
            else if (a[i] == "--dry-run") dry = true;
            else throw new ArgumentException($"unknown argument {a[i]}");
        }
        return (layer, dry);
    }

    private static RunRequest PrepareFromSettings(string layerId)
    {
        var settings = new SettingsManager(AppPaths.DataDir).Load();
        var linux = LinuxSettings.Load();
        var layer = layerId == null ? null : RequireLayer(layerId);
        return new GameLauncher().Prepare(settings, Services.Hardware?.Detect(), linux, layer);
    }

    private static int Env(string[] a)
    {
        var request = PrepareFromSettings(LaunchArgs(a).Layer);
        foreach (var (k, v) in request.Environment.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            Console.WriteLine($"{k}={v}");
        return 0;
    }

    private static int Play(string[] a)
    {
        var (layerId, dry) = LaunchArgs(a);
        if (dry)
        {
            var r = PrepareFromSettings(layerId);
            Console.WriteLine($"layer:    {r.Layer.Id} ({r.Layer.DisplayName}, {r.Layer.Path})");
            Console.WriteLine($"exe:      {r.ExePath} {string.Join(' ', r.Args)}");
            Console.WriteLine($"cwd:      {r.WorkingDirectory}");
            Console.WriteLine($"nice:     {r.Nice}   wrappers: {string.Join(' ', r.Wrappers)}");
            Console.WriteLine($"extra:    {string.Join(':', r.ExtraPaths)}");
            Console.WriteLine($"logs:     {r.StdoutLog} {r.StderrLog}");
            Console.WriteLine($"env:      {r.Environment.Count} variables (see: astrohybrid --cli env)");
            return 0;
        }
        var linux = LinuxSettings.Load();
        if (layerId != null) linux.CompatLayer = RequireLayer(layerId).Id;
        var settings = new SettingsManager(AppPaths.DataDir).Load();
        var run = new GameLauncher().LaunchAsync(settings, Services.Hardware?.Detect(), linux, new Progress<string>(Console.WriteLine), CancellationToken.None)
            .GetAwaiter().GetResult();
        ShowVerification(run.Verification.GetAwaiter().GetResult());
        var code = run.Handle.Exited.GetAwaiter().GetResult();
        Console.WriteLine($"game exited with {code}");
        return code;
    }
}
