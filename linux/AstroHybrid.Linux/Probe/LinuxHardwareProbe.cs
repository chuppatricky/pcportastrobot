using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AstroHybrid.App.Models;
using AstroHybrid.Linux.Platform;

namespace AstroHybrid.Linux.Probe;

/// <summary>
/// Linux counterpart of upstream's HardwareDetector. GPUs come from Vulkan, so their names are the ones
/// the game's renderer sees (ANYPS5_GPU matches on them); CPU and RAM from /proc. The screen size is
/// left at upstream's default for the UI to fill in from its own screen information.
/// </summary>
public sealed class LinuxHardwareProbe : IHardwareProbe
{
    /// <summary>The Vulkan devices of the last Detect(), in the same order as HardwareInfo.Gpus.</summary>
    public IReadOnlyList<VulkanDevice> Devices { get; private set; } = Array.Empty<VulkanDevice>();

    public HardwareInfo Detect()
    {
        var info = new HardwareInfo();
        Devices = VulkanProbe.Devices();
        var bootVga = BootVgaIds();
        foreach (var d in Devices)
            info.Gpus.Add(ToAdapter(d, bootVga.Contains((d.VendorId, d.DeviceId))));
        info.CpuName = CpuName(SafeRead("/proc/cpuinfo")) ?? info.CpuName;
        info.RamGb = RamGb(SafeRead("/proc/meminfo"));
        info.CpuThreads = Environment.ProcessorCount;
        return info;
    }

    public AmdFeatures AmdFor(GpuAdapter gpu)
    {
        var i = gpu == null ? -1 : Devices.ToList().FindIndex(d => d.Name == gpu.Name);
        return i < 0 ? null : AmdFeatures.For(Devices[i]);
    }

    internal static GpuAdapter ToAdapter(VulkanDevice d, bool primary)
    {
        var gpu = new GpuAdapter
        {
            Name = d.Name,
            Vendor = d.VendorId switch { 0x1002 => "AMD", 0x10DE => "NVIDIA", 0x8086 => "Intel", _ => $"0x{d.VendorId:x4}" },
            DriverVersion = DriverVersion(d),
            PnpDeviceId = $"PCI\\VEN_{d.VendorId:X4}&DEV_{d.DeviceId:X4}",
            IsDiscrete = d.IsDiscrete,
            IsIntegrated = d.IsIntegrated,
            IsPrimaryDisplay = primary,
            VramMb = (int)(d.VramBytes / (1024 * 1024)),
        };
        // Same rule as upstream's ApplyFeatureSupport for NVIDIA cards.
        var rtx = Regex.Match(d.Name, @"RTX\s*(\d{1,2})\d{2}", RegexOptions.IgnoreCase);
        if (d.VendorId == 0x10DE && rtx.Success && int.TryParse(rtx.Groups[1].Value, out var series))
        {
            gpu.RtxSeries = series;
            gpu.SupportsDlss = gpu.SupportsReflex = series >= 20;
            gpu.SupportsFrameGeneration = series >= 40;
            gpu.MaxFrameGenMultiplier = series >= 50 ? 4 : series >= 40 ? 2 : 1;
        }
        return gpu;
    }

    /// <summary>Mesa packs the version as Vulkan does (major.minor.patch); NVIDIA uses its own 10.8.8.6 split.</summary>
    private static string DriverVersion(VulkanDevice d) => d.VendorId == 0x10DE
        ? $"{d.DriverVersion >> 22}.{(d.DriverVersion >> 14) & 0xff}"
        : $"{d.DriverVersion >> 22}.{(d.DriverVersion >> 12) & 0x3ff}.{d.DriverVersion & 0xfff}";

    /// <summary>PCI ids of the card the firmware booted on (drives the screen).</summary>
    private static HashSet<(uint, uint)> BootVgaIds()
    {
        var ids = new HashSet<(uint, uint)>();
        try
        {
            foreach (var card in Directory.GetDirectories("/sys/class/drm", "card*").Where(c => !Path.GetFileName(c).Contains('-')))
            {
                var dev = Path.Combine(card, "device");
                if (SafeRead(Path.Combine(dev, "boot_vga")).Trim() != "1") continue;
                if (TryHex(SafeRead(Path.Combine(dev, "vendor")), out var v) && TryHex(SafeRead(Path.Combine(dev, "device")), out var p))
                    ids.Add((v, p));
            }
        }
        catch (IOException) { }
        return ids;
    }

    private static bool TryHex(string s, out uint value) =>
        uint.TryParse(s.Trim().Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out value);

    internal static string CpuName(string cpuinfo)
    {
        var line = cpuinfo.Split('\n').FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal));
        return line == null ? null : line[(line.IndexOf(':') + 1)..].Trim();
    }

    internal static int RamGb(string meminfo)
    {
        var m = Regex.Match(meminfo, @"^MemTotal:\s+(\d+)\s+kB", RegexOptions.Multiline);
        return m.Success ? (int)Math.Round(long.Parse(m.Groups[1].Value) / 1024.0 / 1024.0) : 0;
    }

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); } catch (Exception) { return ""; }
    }
}
