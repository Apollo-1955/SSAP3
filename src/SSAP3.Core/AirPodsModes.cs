namespace SSAP3.Core;

public enum NoiseControlMode
{
    Off,
    NoiseCancellation,
    Transparency,
    Adaptive,
}

public enum SpatialAudioMode
{
    Off,
    Fixed,
    HeadTracked,
}

public enum PreferredMicrophone
{
    AirPods,
    External,
    Automatic,
}

public sealed record UserAudioPreferences(
    SpatialAudioMode SpatialAudio,
    PreferredMicrophone Microphone,
    bool PreferStereo,
    bool AllowHandsFreeFallback);
