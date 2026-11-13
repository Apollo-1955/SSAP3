using System.IO;
using System.Text.Json;

namespace SSAP3.App;

public enum Ssap3SpatialMode
{
    Off,
    Fixed,
    Tracked,
}

public sealed class Ssap3UserSettings
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

    public static Ssap3UserSettings Load()
    {
        try
        {
            var path = GetPath();
            return File.Exists(path)
                ? Deserialize(path)
                : new Ssap3UserSettings();
        }
        catch
        {
            return new Ssap3UserSettings();
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
            "SSAP3",
            "settings.json");

    private static Ssap3UserSettings Deserialize(string path) =>
        JsonSerializer.Deserialize<Ssap3UserSettings>(
            File.ReadAllText(path),
            SerializerOptions) ?? new Ssap3UserSettings();
}
