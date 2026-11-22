using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AstroHybrid.Linux.Platform;

/// <summary>
/// Settings that only the Linux fork has. Kept in their own file
/// (Build/astrohybrid-linux-settings.json) so upstream's AppSettings stays untouched.
/// </summary>
public sealed class LinuxSettings
{
    /// <summary>Id of the chosen compatibility layer (CompatLayer.Id). Empty = the default (Proton-CachyOS).</summary>
    [JsonPropertyName("compatLayer")] public string CompatLayer { get; set; } = "";

    /// <summary>ASTROHYBRID_FSR_VERSION: "auto" | "4" | "3" | "2".</summary>
    [JsonPropertyName("fsrVersion")] public string FsrVersion { get; set; } = "auto";

    /// <summary>ASTROHYBRID_FSR_SHARPNESS, 0..1.</summary>
    [JsonPropertyName("fsrSharpness")] public double FsrSharpness { get; set; } = 0.0;

    /// <summary>Frame generation backend: "off" | "fsr" (AMD FSR FG, needs the fork's kyty_gpu.dll) | "dlssg".</summary>
    [JsonPropertyName("frameGen")] public string FrameGen { get; set; } = "off";

    /// <summary>Extra env vars the user adds per launch (KEY=VALUE), applied last.</summary>
    [JsonPropertyName("extraEnv")] public string ExtraEnv { get; set; } = "";

    /// <summary>Run the game with `gamemoderun` when available.</summary>
    [JsonPropertyName("gamemode")] public bool GameMode { get; set; } = true;

    /// <summary>Wrap the game in MangoHud.</summary>
    [JsonPropertyName("mangohud")] public bool MangoHud { get; set; } = false;

    [JsonPropertyName("legacyImported")] public bool LegacyImported { get; set; } = false;
    [JsonPropertyName("checkUpdates")] public bool CheckUpdates { get; set; } = true;
    [JsonPropertyName("skippedVersion")] public string SkippedVersion { get; set; } = "";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static LinuxSettings Load(string path = null)
    {
        path ??= AppPaths.LinuxSettingsFile;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<LinuxSettings>(File.ReadAllText(path)) ?? new LinuxSettings();
        }
        catch (Exception) { /* corrupt → defaults; the file is rewritten on the next save */ }
        return new LinuxSettings();
    }

    public void Save(string path = null)
    {
        path ??= AppPaths.LinuxSettingsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
