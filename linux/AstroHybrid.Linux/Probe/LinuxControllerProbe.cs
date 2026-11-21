using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using AstroHybrid.App.Models;

namespace AstroHybrid.Linux.Probe;

/// <summary>
/// Game controllers from /proc/bus/input/devices (the ones with a joystick handler), classified the way
/// upstream's ControllerDetector does by USB vendor/product id, so the Controller page works unchanged.
/// </summary>
public static class LinuxControllerProbe
{
    public static List<ControllerInfo> Detect()
    {
        try { return Parse(File.ReadAllText("/proc/bus/input/devices")); }
        catch (IOException) { return new List<ControllerInfo>(); }
    }

    internal static List<ControllerInfo> Parse(string devices)
    {
        var result = new List<ControllerInfo>();
        foreach (var block in devices.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string name = "", handlers = "", bus = "", vendor = "", product = "";
            foreach (var line in block.Split('\n'))
            {
                if (line.StartsWith("N: Name=")) name = line[8..].Trim('"');
                else if (line.StartsWith("H: Handlers=")) handlers = line[12..];
                else if (line.StartsWith("I: "))
                    foreach (var part in line[3..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var kv = part.Split('=');
                        if (kv.Length != 2) continue;
                        if (kv[0] == "Bus") bus = kv[1];
                        else if (kv[0] == "Vendor") vendor = kv[1];
                        else if (kv[0] == "Product") product = kv[1];
                    }
            }
            // Only the gamepad itself has a js handler; a DualSense's touchpad and motion devices do not.
            if (!System.Text.RegularExpressions.Regex.IsMatch(handlers, @"\bjs\d+\b")) continue;
            uint.TryParse(vendor, NumberStyles.HexNumber, null, out var vid);
            uint.TryParse(product, NumberStyles.HexNumber, null, out var pid);
            var c = new ControllerInfo
            {
                Name = name,
                InstanceId = $"{(bus == "0005" ? "BT" : "USB")}\\VID_{vid:X4}&PID_{pid:X4}",
                IsConnected = true,
            };
            if (vid == 0x054C && (pid == 0x0CE6 || pid == 0x0DF2)) { c.Type = "DualSense"; c.HasHaptics = c.HasTriggerEffects = c.HasMotionSensors = true; }
            else if (vid == 0x054C) { c.Type = "DualShock 4"; c.HasHaptics = c.HasMotionSensors = true; }
            else if (vid == 0x057E) { c.Type = "Switch Pro"; c.HasHaptics = c.HasMotionSensors = true; }
            else if (vid == 0x045E) { c.Type = "Xbox"; c.HasHaptics = true; }
            else c.Type = "Generic";
            result.Add(c);
        }
        return result;
    }

    public static bool IsBluetooth(ControllerInfo c) => c.InstanceId.StartsWith("BT\\", StringComparison.Ordinal);
}
