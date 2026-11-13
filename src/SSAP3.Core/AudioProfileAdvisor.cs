namespace SSAP3.Core;

public enum AudioRouteMode
{
    StereoOnly,
    StereoWithExternalMicrophone,
    HandsFreeCompatibility,
    Unsupported,
}

public sealed record AudioEnvironment(
    int WindowsBuild,
    bool HasAirPodsStereoRender,
    bool HasAirPodsHandsFreeRender,
    bool HasAirPodsHandsFreeCapture,
    bool HasExternalMicrophone,
    bool BluetoothLeAudioConfirmed);

public sealed record AudioRouteRecommendation(
    AudioRouteMode Mode,
    string Output,
    string Input,
    string Explanation,
    bool PreservesStereo);

public static class AudioProfileAdvisor
{
    public static AudioRouteRecommendation Recommend(
        AudioEnvironment environment,
        UserAudioPreferences preferences)
    {
        if (!environment.HasAirPodsStereoRender)
        {
            return new AudioRouteRecommendation(
                AudioRouteMode.Unsupported,
                "Unavailable",
                "Unavailable",
                "Windows has not created the AirPods Stereo (A2DP) endpoint.",
                false);
        }

        if (preferences.Microphone is PreferredMicrophone.External &&
            environment.HasExternalMicrophone)
        {
            return ExternalMicrophone();
        }

        if (preferences.Microphone is PreferredMicrophone.Automatic &&
            preferences.PreferStereo &&
            environment.HasExternalMicrophone)
        {
            return ExternalMicrophone();
        }

        if (environment.BluetoothLeAudioConfirmed && environment.WindowsBuild >= 26100)
        {
            return new AudioRouteRecommendation(
                AudioRouteMode.StereoOnly,
                "AirPods LE Audio",
                "AirPods LE Audio",
                "LE Audio is confirmed, so Windows 11 can negotiate a bidirectional stream without the classic HFP route.",
                true);
        }

        if (environment.HasAirPodsHandsFreeRender &&
            environment.HasAirPodsHandsFreeCapture &&
            preferences.AllowHandsFreeFallback)
        {
            return new AudioRouteRecommendation(
                AudioRouteMode.HandsFreeCompatibility,
                "AirPods Hands-Free AG Audio",
                "AirPods Hands-Free AG Audio",
                "Classic Bluetooth must switch to HFP when the AirPods microphone is active. Voice works, but playback bandwidth and microphone quality are limited.",
                false);
        }

        return new AudioRouteRecommendation(
            AudioRouteMode.StereoOnly,
            "AirPods Stereo",
            "Disabled",
            "Stereo is preserved by not opening the AirPods HFP microphone.",
            true);
    }

    private static AudioRouteRecommendation ExternalMicrophone() =>
        new(
            AudioRouteMode.StereoWithExternalMicrophone,
            "AirPods Stereo",
            "External microphone",
            "The AirPods stay on A2DP while a separate microphone handles Discord or calls.",
            true);
}
