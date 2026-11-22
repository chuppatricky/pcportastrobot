using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AstroHybrid.Linux.Platform;

namespace AstroHybrid.Linux.Compat;

/// <summary>
/// Issue #59, as Run-AstroHybrid-Linux.sh fixes it: Wine registers a Bluetooth DualSense's devnode
/// only as WINEBUS\WINE_COMP_HID, so SDL (inside kyty_gpu.dll) treats it as USB and sends USB-format
/// reports — no rumble, haptics or light bar over Bluetooth. Adding the BTHENUM compatible IDs Windows
/// reports makes SDL see it as wireless. Only DualSense (054C:0CE6) and DualSense Edge (054C:0DF2).
///
/// The script asks `reg query` on every start; through Proton each reg call starts the runtime
/// container (seconds). Here the prefix's system.reg is read as text to find keys that still need the
/// fix, and `reg add` runs through the same layer only for those, so a normal start costs nothing.
/// The entries only exist once a pad has been connected under that prefix, so this runs before every
/// launch. ASTROHYBRID_DS_BT_FIX=0 skips it, as in the script.
/// </summary>
public static class DualSenseFix
{
    private const string Uuid = "{00001124-0000-1000-8000-00805f9b34fb}";
    private static readonly Regex Section = new(@"^\[(System\\\\(?:CurrentControlSet|ControlSet\d{3})\\\\Enum\\\\BTHENUM\\\\[^\]]+)\]", RegexOptions.IgnoreCase);
    private static readonly Regex Pad = new(@"VID_054C&PID_(0CE6|0DF2)\\", RegexOptions.IgnoreCase);

    public static async Task<int> ApplyAsync(CompatRunner runner, CompatLayer layer, CancellationToken ct)
    {
        if (Environment.GetEnvironmentVariable("ASTROHYBRID_DS_BT_FIX") == "0") return 0;
        var applied = 0;
        foreach (var (key, pid) in KeysNeedingFix(SystemReg(runner.PrefixFor(layer))))
        {
            var value = $@"BTHENUM\{Uuid}_VID&0002054c_PID&{pid}\0BTHENUM\{Uuid}\0WINEBUS\WINE_COMP_HID";
            var code = await runner.RunToolAsync(layer, CompatRunner.BuiltinExe(layer, "reg.exe"),
                new[] { "add", key, "/v", "CompatibleIDs", "/t", "REG_MULTI_SZ", "/d", value, "/f" }, ct);
            if (code == 0) applied++;
        }
        return applied;
    }

    /// <summary>Proton keeps the Wine prefix in &lt;prefix&gt;/pfx, plain Wine in &lt;prefix&gt; itself.</summary>
    internal static string SystemReg(string prefix)
    {
        var proton = Path.Combine(prefix, "pfx", "system.reg");
        return File.Exists(proton) ? proton : Path.Combine(prefix, "system.reg");
    }

    /// <summary>Registry keys (as reg.exe names them) of DualSense devnodes whose CompatibleIDs lack BTHENUM,
    /// with the lower-case PID. Wine stores CurrentControlSet as a link to ControlSet001, so system.reg holds
    /// the keys under ControlSet001; reg.exe accepts that name as well.</summary>
    internal static List<(string Key, string Pid)> KeysNeedingFix(string systemRegPath)
    {
        var result = new List<(string, string)>();
        if (!File.Exists(systemRegPath)) return result;
        string current = null;
        foreach (var line in File.ReadLines(systemRegPath))
        {
            if (line.StartsWith('['))
            {
                var m = Section.Match(line);
                current = m.Success && Pad.IsMatch(m.Groups[1].Value.Replace(@"\\", @"\") + @"\") ? m.Groups[1].Value : null;
                continue;
            }
            if (current == null || !line.StartsWith("\"CompatibleIDs\"", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase)) { current = null; continue; }
            var key = @"HKLM\" + current.Replace(@"\\", @"\");
            var pid = Regex.Match(key, @"PID_([0-9A-Fa-f]{4})").Groups[1].Value.ToLowerInvariant();
            result.Add((key, pid));
            current = null;
        }
        return result;
    }
}
