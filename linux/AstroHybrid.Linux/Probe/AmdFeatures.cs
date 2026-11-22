using System;
using System.Text.RegularExpressions;
using AstroHybrid.App.Models;
using AstroHybrid.Linux.Platform;

namespace AstroHybrid.Linux.Probe;

public enum RdnaGeneration { None, Rdna1, Rdna2, Rdna3, Rdna35, Rdna4 }

/// <summary>What an AMD card offers the game on Linux.</summary>
public sealed record AmdFeatures
{
    public RdnaGeneration Generation { get; init; }
    /// <summary>VK_AMD_anti_lag on the device: the renderer can cut input latency (the fork's low-latency path on AMD).</summary>
    public bool AntiLag { get; init; }
    /// <summary>Resizable BAR / Smart Access Memory on: all of VRAM is CPU-visible, not a 256 MiB window.</summary>
    public bool ResizableBar { get; init; }
    public ulong HostVisibleVramBytes { get; init; }

    public static AmdFeatures For(VulkanDevice d) => d.VendorId != 0x1002 ? null : new AmdFeatures
    {
        Generation = Classify(d.Name),
        AntiLag = d.Has("VK_AMD_anti_lag"),
        ResizableBar = d.ResizableBar,
        HostVisibleVramBytes = d.HostVisibleVramBytes,
    };

    /// <summary>From RADV's chip name in the device name ("AMD Radeon RX 7900 GRE (RADV NAVI31)"), else the model number.</summary>
    public static RdnaGeneration Classify(string name)
    {
        var n = name.ToUpperInvariant();
        var radv = Regex.Match(n, @"\(RADV ([A-Z_]+?)(\d*)\)");
        if (radv.Success)
        {
            var chip = radv.Groups[1].Value + radv.Groups[2].Value;
            if (Regex.IsMatch(chip, @"^NAVI1\d")) return RdnaGeneration.Rdna1;
            if (Regex.IsMatch(chip, @"^NAVI2\d|^VANGOGH|^REMBRANDT|^RAPHAEL|^MENDOCINO")) return RdnaGeneration.Rdna2;
            if (Regex.IsMatch(chip, @"^NAVI3\d|^PHOENIX|^GFX1103")) return RdnaGeneration.Rdna3;
            if (Regex.IsMatch(chip, @"^STRIX|^KRACKAN|^GFX115\d")) return RdnaGeneration.Rdna35;
            if (Regex.IsMatch(chip, @"^NAVI4\d|^GFX120\d")) return RdnaGeneration.Rdna4;
        }
        var rx = Regex.Match(n, @"RX\s*(\d)\d{3}");
        if (rx.Success)
            return rx.Groups[1].Value switch
            {
                "5" => RdnaGeneration.Rdna1, "6" => RdnaGeneration.Rdna2, "7" => RdnaGeneration.Rdna3, "9" => RdnaGeneration.Rdna4,
                _ => RdnaGeneration.None,
            };
        var igpu = Regex.Match(n, @"RADEON\s*(\d{3})M");
        if (igpu.Success)
            return igpu.Groups[1].Value[0] switch { '6' => RdnaGeneration.Rdna2, '7' => RdnaGeneration.Rdna3, '8' => RdnaGeneration.Rdna35, _ => RdnaGeneration.None };
        return RdnaGeneration.None;
    }

    public static string Label(RdnaGeneration g) => g switch
    {
        RdnaGeneration.Rdna1 => "RDNA 1", RdnaGeneration.Rdna2 => "RDNA 2", RdnaGeneration.Rdna3 => "RDNA 3",
        RdnaGeneration.Rdna35 => "RDNA 3.5", RdnaGeneration.Rdna4 => "RDNA 4", _ => "not RDNA",
    };

    /// <summary>
    /// The AMD counterpart of upstream's PickBestSettings (which only knows RTX cards): FSR upscaling
    /// (FSR version left to the renderer's "auto"), low latency where Anti-Lag exists. Frame generation
    /// stays off: the renderer has no AMD frame generation yet.
    /// </summary>
    public static void ApplyBestSettings(AppSettings settings, LinuxSettings linux, AmdFeatures amd)
    {
        if (amd == null) return;
        settings.Upscale = 2;
        settings.FrameGenMode = 0;
        settings.LowLatency = amd.AntiLag;
        linux.FsrVersion = "auto";
        linux.FrameGen = "off";
    }
}
