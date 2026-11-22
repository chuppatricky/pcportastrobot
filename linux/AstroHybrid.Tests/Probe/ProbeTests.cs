using AstroHybrid.App.Models;
using AstroHybrid.Linux.Platform;
using AstroHybrid.Linux.Probe;
using Xunit;

namespace AstroHybrid.Tests.Probe;

public class ProbeTests
{
    [Theory]
    [InlineData("AMD Radeon RX 7900 GRE (RADV NAVI31)", RdnaGeneration.Rdna3)]
    [InlineData("AMD Radeon RX 9070 XT (RADV GFX1201)", RdnaGeneration.Rdna4)]
    [InlineData("AMD Radeon RX 6800 XT (RADV NAVI21)", RdnaGeneration.Rdna2)]
    [InlineData("AMD Radeon RX 5700 XT (RADV NAVI10)", RdnaGeneration.Rdna1)]
    [InlineData("AMD Radeon 780M (RADV PHOENIX)", RdnaGeneration.Rdna3)]
    [InlineData("AMD Radeon 890M (RADV STRIX1)", RdnaGeneration.Rdna35)]
    [InlineData("AMD Custom GPU 0405 (RADV VANGOGH)", RdnaGeneration.Rdna2)]
    [InlineData("AMD Radeon RX 7800 XT", RdnaGeneration.Rdna3)]
    [InlineData("AMD Radeon 680M", RdnaGeneration.Rdna2)]
    [InlineData("AMD Radeon RX 580 Series (RADV POLARIS10)", RdnaGeneration.None)]
    public void ClassifiesRdna(string name, RdnaGeneration expected) => Assert.Equal(expected, AmdFeatures.Classify(name));

    private static VulkanDevice Amd(ulong hostVisibleMb, params string[] ext) => new()
    {
        Name = "AMD Radeon RX 7900 GRE (RADV NAVI31)", VendorId = 0x1002, DeviceType = 2,
        VramBytes = 16UL << 30, HostVisibleVramBytes = hostVisibleMb << 20, Extensions = ext,
    };

    [Fact]
    public void ResizableBarAndAntiLagFromVulkan()
    {
        var off = AmdFeatures.For(Amd(256));
        Assert.False(off.ResizableBar);
        Assert.False(off.AntiLag);
        var on = AmdFeatures.For(Amd(16384, "VK_AMD_anti_lag"));
        Assert.True(on.ResizableBar);
        Assert.True(on.AntiLag);
        Assert.Null(AmdFeatures.For(Amd(256) with { VendorId = 0x10DE }));
    }

    [Fact]
    public void BestSettingsForAmdTurnOnFsrAndAntiLag()
    {
        var s = new AppSettings { Upscale = 0, FrameGenMode = 2 };
        var l = new LinuxSettings { FsrVersion = "3", FrameGen = "fsr" };
        AmdFeatures.ApplyBestSettings(s, l, AmdFeatures.For(Amd(256, "VK_AMD_anti_lag")));
        Assert.Equal(2, s.Upscale);
        Assert.Equal(0, s.FrameGenMode);
        Assert.True(s.LowLatency);
        Assert.Equal("auto", l.FsrVersion);
        Assert.Equal("off", l.FrameGen);
    }

    [Fact]
    public void NvidiaFeatureFlagsMatchUpstream()
    {
        var g = LinuxHardwareProbe.ToAdapter(new VulkanDevice { Name = "NVIDIA GeForce RTX 4070", VendorId = 0x10DE, DeviceType = 2 }, false);
        Assert.Equal(40, g.RtxSeries);
        Assert.True(g.SupportsDlss);
        Assert.True(g.SupportsFrameGeneration);
        Assert.Equal(2, g.MaxFrameGenMultiplier);
        var a = LinuxHardwareProbe.ToAdapter(Amd(256), true);
        Assert.Equal("AMD", a.Vendor);
        Assert.True(a.IsDiscrete && a.IsPrimaryDisplay);
        Assert.Equal(0, a.RtxSeries);
    }

    [Fact]
    public void ProcParsers()
    {
        Assert.Equal("AMD Ryzen 7 5800X 8-Core Processor",
            LinuxHardwareProbe.CpuName("processor\t: 0\nvendor_id\t: AuthenticAMD\nmodel name\t: AMD Ryzen 7 5800X 8-Core Processor\n"));
        Assert.Equal(31, LinuxHardwareProbe.RamGb("MemTotal:       32775432 kB\nMemFree: 1 kB\n"));
    }

    [Fact]
    public void ControllersFromProcInputDevices()
    {
        const string text =
            "I: Bus=0005 Vendor=054c Product=0ce6 Version=8100\nN: Name=\"DualSense Wireless Controller\"\nH: Handlers=event20 js0\n\n" +
            "I: Bus=0005 Vendor=054c Product=0ce6 Version=8100\nN: Name=\"DualSense Wireless Controller Motion Sensors\"\nH: Handlers=event21\n\n" +
            "I: Bus=0003 Vendor=045e Product=0b12 Version=0509\nN: Name=\"Microsoft Xbox Controller\"\nH: Handlers=event22 js1\n\n" +
            "I: Bus=0003 Vendor=258a Product=020c Version=0111\nN: Name=\"RK R98Pro Keyboard\"\nH: Handlers=sysrq kbd event3\n";
        var pads = LinuxControllerProbe.Parse(text);
        Assert.Equal(2, pads.Count);
        Assert.Equal("DualSense", pads[0].Type);
        Assert.True(LinuxControllerProbe.IsBluetooth(pads[0]));
        Assert.True(pads[0].HasTriggerEffects);
        Assert.Equal("Xbox", pads[1].Type);
        Assert.False(LinuxControllerProbe.IsBluetooth(pads[1]));
    }
}
