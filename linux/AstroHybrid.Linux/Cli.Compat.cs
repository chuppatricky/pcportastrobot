using System;
using System.IO;
using System.Linq;
using System.Threading;
using AstroHybrid.Linux.Compat;
using AstroHybrid.Linux.Platform;

namespace AstroHybrid.Linux;

internal static partial class Cli
{
    static partial void RegisterCompat()
    {
        Commands["layers"] = ("list the Wine/Proton builds the game can run through", _ => Layers());
        Commands["prefix"] = ("<layer-id>  create or update that layer's prefix", a => Prefix(a));
        Commands["run"] = ("[--expect <id>] <layer-id> <exe|builtin.exe> [args]  run a program through a layer and verify it", a => RunVerify(a));
        Commands["verify"] = ("show the last layer verification", _ => ShowVerification(Services.Verifier.LastResult()));
    }

    private static int Layers()
    {
        var layers = Services.Catalog.Discover();
        var def = LayerCatalog.PickDefault(layers);
        foreach (var l in layers)
        {
            Console.WriteLine($"{(l == def ? "*" : " ")} {l.Id,-44} {l.DisplayName,-46} {l.Version,-36} {l.Source}");
            foreach (var w in l.Warnings) Console.WriteLine($"    ! {w}");
        }
        Console.WriteLine("* = default");
        return 0;
    }

    private static CompatLayer RequireLayer(string id) =>
        Services.Catalog.Find(id) ?? throw new ArgumentException($"no layer '{id}' (see: astrohybrid --cli layers)");

    private static int Prefix(string[] a)
    {
        if (a.Length != 1) throw new ArgumentException("usage: prefix <layer-id>");
        var layer = RequireLayer(a[0]);
        Services.Runner.EnsurePrefixAsync(layer, new Progress<string>(Console.WriteLine), CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine(Services.Runner.PrefixFor(layer));
        return 0;
    }

    /// <summary>Runs a program through a layer, verifies the layer, then waits for it to exit.
    /// A bare name ending in .exe that is not a file is taken from the layer's own Wine (e.g. cmd.exe).</summary>
    /// <remarks>--expect &lt;id&gt; checks the run against a different layer than the one used, to show that
    /// the verifier reports a mismatch (and names the real layer).</remarks>
    private static int RunVerify(string[] a)
    {
        CompatLayer expect = null;
        if (a.Length >= 2 && a[0] == "--expect") { expect = RequireLayer(a[1]); a = a[2..]; }
        if (a.Length < 2) throw new ArgumentException("usage: run [--expect <layer-id>] <layer-id> <exe> [args...]");
        var layer = RequireLayer(a[0]);
        var exe = File.Exists(a[1]) ? Path.GetFullPath(a[1]) : CompatRunner.BuiltinExe(layer, a[1]);
        Directory.CreateDirectory(AppPaths.LogsDir);
        var handle = Services.Runner.StartAsync(new RunRequest
        {
            Layer = layer,
            ExePath = exe,
            Args = a.Skip(2).ToArray(),
            WorkingDirectory = Directory.GetCurrentDirectory(),
            StdoutLog = Path.Combine(AppPaths.LogsDir, "compat-run.out.log"),
            StderrLog = Path.Combine(AppPaths.LogsDir, "compat-run.err.log"),
        }, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine($"started pid {handle.Process.Id} through {layer.DisplayName}, prefix {handle.Prefix}");
        var checkAgainst = expect == null ? handle : new RunHandle
        {
            SessionId = handle.SessionId, Layer = expect, Process = handle.Process, Prefix = handle.Prefix,
            Exited = handle.Exited, ExeName = handle.ExeName,
        };
        var v = Services.Verifier.VerifyAsync(checkAgainst, TimeSpan.FromSeconds(120), CancellationToken.None).GetAwaiter().GetResult();
        ShowVerification(v);
        var code = handle.Exited.GetAwaiter().GetResult();
        Console.WriteLine($"exit code {code}");
        return v.Verified ? 0 : 3;
    }

    private static int ShowVerification(LayerVerification v)
    {
        if (v == null) { Console.WriteLine("no verification recorded yet"); return 1; }
        Console.WriteLine($"verified:  {v.Verified}");
        Console.WriteLine($"expected:  {v.ExpectedLayerId}");
        Console.WriteLine($"observed:  {v.ObservedLayerId}");
        Console.WriteLine($"pid:       {v.GamePid}");
        Console.WriteLine($"ntdll.so:  {v.ObservedNtdll}");
        Console.WriteLine($"ntsync:    {v.NtsyncOpen}");
        Console.WriteLine($"proton:    {v.ProtonLogHeader}");
        Console.WriteLine($"message:   {v.Message}");
        return v.Verified ? 0 : 3;
    }
}
