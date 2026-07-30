using System.IO;
using System.Text.Json;

namespace SoundStage.App;

public enum SoundStageSpatialMode
{
    Off,
    Fixed,
    Tracked,
}

public sealed class SoundStageUserSettings
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    public string NoiseMode { get; set; } = "NoiseCancellation";

    public string SpatialMode { get; set; } = "Off";

    public int? LastCaseBattery { get; set; }

    public bool LastCaseCharging { get; set; }

    public DateTimeOffset? LastCaseBatteryObservedAt { get; set; }

    public static SoundStageUserSettings Load()
    {
        try
        {
            var path = GetPath();
            if (File.Exists(path))
            {
                return Deserialize(path);
            }

            var legacyPath = GetLegacyPath();
            if (!File.Exists(legacyPath))
            {
                return new SoundStageUserSettings();
            }

            var migrated = Deserialize(legacyPath);
            migrated.Save();
            return migrated;
        }
        catch
        {
            return new SoundStageUserSettings();
        }
    }

    public void Save()
    {
        try
        {
            var path = GetPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(this, SerializerOptions));
        }
        catch
        {
            // Settings persistence must never interrupt audio controls.
        }
    }

    private static string GetPath() =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "SoundStage",
            "settings.json");

    private static string GetLegacyPath() =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "Pod" + "Link",
            "settings.json");

    private static SoundStageUserSettings Deserialize(string path) =>
        JsonSerializer.Deserialize<SoundStageUserSettings>(
            File.ReadAllText(path),
            SerializerOptions) ?? new SoundStageUserSettings();
}
