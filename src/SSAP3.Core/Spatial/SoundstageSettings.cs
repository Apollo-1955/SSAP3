namespace SSAP3.Core.Spatial;

/// <summary>
/// The engine's direct-path renderer (shared state v2, offset 20). Values
/// must match <c>ssap3::Renderer</c> in audio/SSAP3Spatializer/ssap3_shared_state.h.
/// </summary>
public enum SpatialRenderer
{
    /// <summary>Measured dummy-head HRTFs (MIT KEMAR) with the stage timbre correction.</summary>
    Measured = 0,

    /// <summary>The earlier parametric model (time and level differences, head-shadow filter).</summary>
    Classic = 1,
}

/// <summary>
/// User-configurable virtual soundstage. Ranges match the validation in the
/// SSAP3 Spatial Engine (audio/SSAP3Spatializer/ssap3_shared_state.h); values
/// outside them would make the engine reject the whole state record.
/// </summary>
public sealed record SoundstageSettings
{
    public const float MinSpeakerAzimuth = 10f;
    public const float MaxSpeakerAzimuth = 90f;
    public const float MinSpeakerDistance = 0.3f;
    public const float MaxSpeakerDistance = 5f;
    public const float MinSpeakerElevation = -30f;
    public const float MaxSpeakerElevation = 30f;

    public static SoundstageSettings Default { get; } = new();

    /// <summary>Half-angle of the virtual speaker pair, in degrees (30 = standard stereo triangle).</summary>
    public float SpeakerAzimuthDegrees { get; init; } = 30f;

    public float SpeakerDistanceMeters { get; init; } = 1.0f;

    public float SpeakerElevationDegrees { get; init; }

    /// <summary>Virtual room reflections, 0 (anechoic) to 1.</summary>
    public float RoomMix { get; init; } = 1.0f;

    /// <summary>Spatial rendering strength, 0 (pass-through) to 1.</summary>
    public float Intensity { get; init; } = 1.0f;

    /// <summary>Direct-path renderer; settings saved before it existed load as <see cref="SpatialRenderer.Measured"/>.</summary>
    public SpatialRenderer Renderer { get; init; } = SpatialRenderer.Measured;

    /// <summary>Returns a copy with every value clamped into its valid range; NaN becomes the default.</summary>
    public SoundstageSettings Sanitized() => new()
    {
        SpeakerAzimuthDegrees = Clamp(SpeakerAzimuthDegrees, MinSpeakerAzimuth, MaxSpeakerAzimuth, 30f),
        SpeakerDistanceMeters = Clamp(SpeakerDistanceMeters, MinSpeakerDistance, MaxSpeakerDistance, 1f),
        SpeakerElevationDegrees = Clamp(SpeakerElevationDegrees, MinSpeakerElevation, MaxSpeakerElevation, 0f),
        RoomMix = Clamp(RoomMix, 0f, 1f, 1f),
        Intensity = Clamp(Intensity, 0f, 1f, 1f),
        Renderer = Enum.IsDefined(Renderer) ? Renderer : SpatialRenderer.Measured,
    };

    private static float Clamp(float value, float minimum, float maximum, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}
