using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SSAP3.Core.Spatial;

namespace SSAP3.App;

/// <summary>
/// Per-user settings in %LocalAppData%\SSAP3\settings.json. The file is
/// treated as untrusted input: it is size-limited, every value is validated,
/// and a corrupt file falls back to defaults without being executed or
/// interpreted beyond these fields.
/// </summary>
public sealed class Ssap3UserSettings
{
    private const long MaximumFileBytes = 64 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public SpatialEngineMode SpatialMode { get; set; } = SpatialEngineMode.Off;

    public SoundstageSettings Soundstage { get; set; } = SoundstageSettings.Default;

    public bool HeadTrackingEnabled { get; set; } = true;

    public bool InvertYaw { get; set; }

    public bool InvertPitch { get; set; }

    public bool DriftCorrection { get; set; } = true;

    public bool ConversationAwarenessEnabled { get; set; }

    public int ConversationSilenceSeconds { get; set; } = 6;

    public int? LastCaseBattery { get; set; }

    public bool LastCaseCharging { get; set; }

    public DateTimeOffset? LastCaseBatteryObservedAt { get; set; }

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SSAP3",
        "settings.json");

    public static Ssap3UserSettings Load()
    {
        try
        {
            var file = new FileInfo(FilePath);
            if (!file.Exists || file.Length > MaximumFileBytes)
            {
                return new Ssap3UserSettings();
            }

            var settings = JsonSerializer.Deserialize<Ssap3UserSettings>(File.ReadAllText(FilePath), SerializerOptions);
            return (settings ?? new Ssap3UserSettings()).Validated();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new Ssap3UserSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Validated(), SerializerOptions));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Ssap3AppLog.Write($"Settings could not be saved: {exception.Message}");
        }
    }

    private Ssap3UserSettings Validated()
    {
        SpatialMode = Enum.IsDefined(SpatialMode) ? SpatialMode : SpatialEngineMode.Off;
        Soundstage = (Soundstage ?? SoundstageSettings.Default).Sanitized();
        ConversationSilenceSeconds = Math.Clamp(ConversationSilenceSeconds, 2, 60);
        LastCaseBattery = LastCaseBattery is { } level ? Math.Clamp(level, 0, 100) : null;
        return this;
    }
}
