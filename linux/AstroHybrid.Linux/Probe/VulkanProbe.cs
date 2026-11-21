using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace AstroHybrid.Linux.Probe;

/// <summary>One Vulkan physical device as the game will see it (Kyty picks its card by these names).</summary>
public sealed record VulkanDevice
{
    public string Name { get; init; } = "";
    public uint VendorId { get; init; }
    public uint DeviceId { get; init; }
    /// <summary>VkPhysicalDeviceType: 1 integrated, 2 discrete, 3 virtual, 4 CPU.</summary>
    public int DeviceType { get; init; }
    public uint ApiVersion { get; init; }
    public uint DriverVersion { get; init; }
    /// <summary>Largest DEVICE_LOCAL heap, bytes.</summary>
    public ulong VramBytes { get; init; }
    /// <summary>Largest heap that is both DEVICE_LOCAL and HOST_VISIBLE (the CPU-mappable part of VRAM), bytes.</summary>
    public ulong HostVisibleVramBytes { get; init; }
    public IReadOnlyList<string> Extensions { get; init; } = Array.Empty<string>();

    public bool IsDiscrete => DeviceType == 2;
    public bool IsIntegrated => DeviceType == 1;
    public bool Has(string extension) => ((IList<string>)Extensions).Contains(extension);
    /// <summary>Resizable BAR / Smart Access Memory: without it only a 256 MiB window of VRAM is CPU-visible.</summary>
    public bool ResizableBar => HostVisibleVramBytes > 256UL * 1024 * 1024 + 16UL * 1024 * 1024;
}

/// <summary>
/// Lists Vulkan devices through libvulkan directly (no vulkaninfo needed). The structs are read from raw
/// buffers at their fixed x86_64 offsets, which the Vulkan headers guarantee.
/// </summary>
public static class VulkanProbe
{
    private const string Lib = "libvulkan.so.1";

    [DllImport(Lib)] private static extern int vkCreateInstance(IntPtr createInfo, IntPtr allocator, out IntPtr instance);
    [DllImport(Lib)] private static extern void vkDestroyInstance(IntPtr instance, IntPtr allocator);
    [DllImport(Lib)] private static extern int vkEnumeratePhysicalDevices(IntPtr instance, ref uint count, IntPtr[] devices);
    [DllImport(Lib)] private static extern void vkGetPhysicalDeviceProperties(IntPtr device, byte[] properties);
    [DllImport(Lib)] private static extern void vkGetPhysicalDeviceMemoryProperties(IntPtr device, byte[] properties);
    [DllImport(Lib)] private static extern int vkEnumerateDeviceExtensionProperties(IntPtr device, IntPtr layer, ref uint count, byte[] properties);

    // VkPhysicalDeviceProperties: apiVersion 0, driverVersion 4, vendorID 8, deviceID 12, deviceType 16, deviceName[256] 20.
    // VkPhysicalDeviceMemoryProperties: typeCount 0, types[32]{flags,heap} 4, heapCount 260, heaps[16]{u64 size, u32 flags} 264.
    // VkExtensionProperties: extensionName[256], specVersion → 260 bytes.
    private const int MemoryPropertyDeviceLocal = 1, MemoryPropertyHostVisible = 2;

    /// <summary>All devices except software renderers (llvmpipe), or an empty list when Vulkan is unavailable.</summary>
    public static List<VulkanDevice> Devices()
    {
        var result = new List<VulkanDevice>();
        IntPtr instance;
        var info = Marshal.AllocHGlobal(64);
        try
        {
            for (var i = 0; i < 64; i++) Marshal.WriteByte(info, i, 0);
            Marshal.WriteInt32(info, 0, 1);   // VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO; no app info, layers or extensions
            if (vkCreateInstance(info, IntPtr.Zero, out instance) != 0) return result;
        }
        catch (DllNotFoundException) { return result; }
        catch (EntryPointNotFoundException) { return result; }
        finally { Marshal.FreeHGlobal(info); }

        try
        {
            uint count = 0;
            vkEnumeratePhysicalDevices(instance, ref count, null);
            var handles = new IntPtr[count];
            vkEnumeratePhysicalDevices(instance, ref count, handles);
            foreach (var h in handles)
            {
                var d = Read(h);
                if (d.DeviceType != 4) result.Add(d);
            }
        }
        finally { vkDestroyInstance(instance, IntPtr.Zero); }
        return result;
    }

    private static VulkanDevice Read(IntPtr device)
    {
        var props = new byte[1024];
        vkGetPhysicalDeviceProperties(device, props);
        var mem = new byte[528];
        vkGetPhysicalDeviceMemoryProperties(device, mem);

        var heapCount = BitConverter.ToUInt32(mem, 260);
        var heapSize = new ulong[heapCount];
        var heapDeviceLocal = new bool[heapCount];
        for (var i = 0; i < heapCount; i++)
        {
            heapSize[i] = BitConverter.ToUInt64(mem, 264 + 16 * i);
            heapDeviceLocal[i] = (BitConverter.ToUInt32(mem, 264 + 16 * i + 8) & 1) != 0;   // VK_MEMORY_HEAP_DEVICE_LOCAL_BIT
        }
        ulong vram = 0, hostVisible = 0;
        for (var i = 0; i < heapCount; i++)
            if (heapDeviceLocal[i]) vram = Math.Max(vram, heapSize[i]);
        var typeCount = BitConverter.ToUInt32(mem, 0);
        for (var t = 0; t < typeCount; t++)
        {
            var flags = BitConverter.ToUInt32(mem, 4 + 8 * t);
            var heap = BitConverter.ToUInt32(mem, 4 + 8 * t + 4);
            if ((flags & (MemoryPropertyDeviceLocal | MemoryPropertyHostVisible)) == (MemoryPropertyDeviceLocal | MemoryPropertyHostVisible) && heap < heapCount)
                hostVisible = Math.Max(hostVisible, heapSize[heap]);
        }

        uint extCount = 0;
        vkEnumerateDeviceExtensionProperties(device, IntPtr.Zero, ref extCount, null);
        var ext = new byte[260 * extCount];
        vkEnumerateDeviceExtensionProperties(device, IntPtr.Zero, ref extCount, ext);
        var names = new List<string>((int)extCount);
        for (var i = 0; i < extCount; i++) names.Add(CString(ext, 260 * i, 256));

        return new VulkanDevice
        {
            ApiVersion = BitConverter.ToUInt32(props, 0),
            DriverVersion = BitConverter.ToUInt32(props, 4),
            VendorId = BitConverter.ToUInt32(props, 8),
            DeviceId = BitConverter.ToUInt32(props, 12),
            DeviceType = BitConverter.ToInt32(props, 16),
            Name = CString(props, 20, 256),
            VramBytes = vram,
            HostVisibleVramBytes = hostVisible,
            Extensions = names,
        };
    }

    private static string CString(byte[] buffer, int offset, int max)
    {
        var end = Array.IndexOf(buffer, (byte)0, offset, max);
        return Encoding.UTF8.GetString(buffer, offset, (end < 0 ? offset + max : end) - offset);
    }
}
