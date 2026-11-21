using System;
using AstroHybrid.Linux.Platform;
using AstroHybrid.Linux.Probe;

namespace AstroHybrid.Linux;

internal static partial class Cli
{
    static partial void RegisterProbe()
    {
        Commands["hw"] = ("show the graphics cards, CPU and RAM as the launcher sees them", _ => Hardware());
        Commands["pads"] = ("show connected game controllers", _ => Pads());
    }

    private static int Hardware()
    {
        var probe = (LinuxHardwareProbe)Services.Hardware;
        var info = probe.Detect();
        if (info.Gpus.Count == 0) Console.WriteLine("no Vulkan GPU found (is the Vulkan driver installed? CachyOS/Arch: vulkan-radeon)");
        for (var i = 0; i < info.Gpus.Count; i++)
        {
            var g = info.Gpus[i];
            Console.WriteLine($"GPU {i}: {g.Name}");
            Console.WriteLine($"    {g.Vendor}, {g.KindLabel}{(g.IsPrimaryDisplay ? ", drives the screen" : "")}, {g.VramMb} MiB, driver {g.DriverVersion}");
            var amd = AmdFeatures.For(probe.Devices[i]);
            if (amd != null)
            {
                Console.WriteLine($"    {AmdFeatures.Label(amd.Generation)}; Anti-Lag: {(amd.AntiLag ? "yes" : "no")}; " +
                                  $"Resizable BAR: {(amd.ResizableBar ? "on" : "OFF")} ({amd.HostVisibleVramBytes / (1024 * 1024)} MiB CPU-visible VRAM)");
                if (!amd.ResizableBar)
                    Console.WriteLine("    Turn on Resizable BAR / Smart Access Memory (\"Above 4G decoding\" + \"Re-Size BAR\") in the BIOS: the renderer streams a lot through CPU-visible VRAM.");
            }
            if (g.RtxSeries > 0) Console.WriteLine($"    RTX {g.RtxSeries}0 series: DLSS {g.SupportsDlss}, frame generation up to {g.MaxFrameGenMultiplier}x");
        }
        Console.WriteLine($"automatic choice: {info.SelectedGpu?.Name ?? "none"}");
        Console.WriteLine($"CPU: {info.CpuName}, {info.CpuThreads} threads; RAM: {info.RamGb} GB");
        return 0;
    }

    private static int Pads()
    {
        var pads = LinuxControllerProbe.Detect();
        if (pads.Count == 0) Console.WriteLine("no game controller connected");
        foreach (var p in pads)
            Console.WriteLine($"{p.Type,-12} {p.Name}  ({(LinuxControllerProbe.IsBluetooth(p) ? "Bluetooth" : "USB")}, {p.InstanceId})");
        return 0;
    }
}
