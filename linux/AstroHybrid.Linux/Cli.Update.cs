using System;
using System.Linq;
using System.Threading;
using AstroHybrid.Linux.Platform;
using AstroHybrid.Linux.Update;

namespace AstroHybrid.Linux;

internal static partial class Cli
{
    static partial void RegisterUpdate()
    {
        Commands["update-check"] = ("[--skip]  show the newest release; --skip hides it from future notifications", UpdateCheck);
        Commands["update-install"] = ("[--yes] [--relaunch]  replace this AppImage with the newest release", UpdateInstall);
        Commands["payload-sync"] = ("copy the bundled Windows runtime into the data dir if its version changed", PayloadSyncCmd);
    }

    private static int UpdateCheck(string[] args)
    {
        var updater = Services.Updater;
        Console.WriteLine($"current: {updater.CurrentVersion}");
        var offer = updater.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (offer == null) { Console.WriteLine("up to date"); return 0; }

        var settings = LinuxSettings.Load();
        if (args.Contains("--skip"))
        {
            settings.SkippedVersion = offer.Version;
            settings.Save();
        }
        Console.WriteLine($"available: {offer.Version} ({offer.Size} bytes, {offer.Digest})");
        Console.WriteLine($"asset: {offer.AssetUrl}");
        if (offer.HtmlUrl.Length > 0) Console.WriteLine($"page: {offer.HtmlUrl}");
        if (AppImageUpdater.IsSkipped(offer, settings))
            Console.WriteLine("skipped: yes (no notification; `update-install` still installs it)");
        return 0;
    }

    private static int UpdateInstall(string[] args)
    {
        var updater = Services.Updater;
        var offer = updater.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (offer == null) { Console.WriteLine($"up to date ({updater.CurrentVersion})"); return 0; }
        Console.WriteLine($"update: {updater.CurrentVersion} -> {offer.Version} ({offer.Size} bytes)");
        if (!args.Contains("--yes"))
        {
            Console.Write("Install it? [y/N] ");
            var answer = Console.ReadLine();
            if (answer == null || !answer.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("cancelled");
                return 1;
            }
        }

        var (ok, message) = updater.InstallAsync(offer, new TenthsProgress(), CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine(message);
        if (!ok) return 1;
        // Proves the new file runs: it replaces this process and reports its own version.
        if (args.Contains("--relaunch")) updater.Relaunch(new[] { "--cli", "update-check" });
        return 0;
    }

    /// <summary>Prints every 10% synchronously (Progress&lt;T&gt; would post to the thread pool and reorder lines).</summary>
    private sealed class TenthsProgress : IProgress<double>
    {
        private int _last = -1;
        public void Report(double value)
        {
            int tenth = (int)(value * 10);
            if (tenth == _last) return;
            _last = tenth;
            Console.Error.WriteLine($"download {tenth * 10}%");
        }
    }

    private static int PayloadSyncCmd(string[] args)
    {
        var result = PayloadSync.EnsureCurrent();
        Console.WriteLine(result.Message);
        return 0;
    }
}
