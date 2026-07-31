using Microsoft.Win32;
using SoundStage.Core;
using SoundStage.Platform.Windows;

namespace SoundStage.Diagnostics;

internal static class WindowsCollector
{
    public static WindowsInfo ReadWindowsInfo()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");

        var productName = key?.GetValue("ProductName") as string ?? "Unknown";
        var displayVersion = key?.GetValue("DisplayVersion") as string ?? "Unknown";
        var buildText = key?.GetValue("CurrentBuild") as string ?? "0";
        var ubr = key?.GetValue("UBR") is int value ? value : 0;

        _ = int.TryParse(buildText, out var build);
        uint codeIntegrityOptions;
        try
        {
            codeIntegrityOptions = WindowsCodeIntegrity.Options;
        }
        catch
        {
            codeIntegrityOptions = 0;
        }

        bool? secureBootEnabled = null;
        using (var secureBoot = Registry.LocalMachine.OpenSubKey(
                   @"SYSTEM\CurrentControlSet\Control\SecureBoot\State"))
        {
            if (secureBoot?.GetValue("UEFISecureBootEnabled") is int secureBootValue)
            {
                secureBootEnabled = secureBootValue != 0;
            }
        }

        return new WindowsInfo(
            productName,
            displayVersion,
            build,
            ubr,
            Environment.Is64BitOperatingSystem ? "x64" : "x86",
            codeIntegrityOptions,
            (codeIntegrityOptions & 0x2) != 0,
            secureBootEnabled);
    }

    public static AudioRouteRecommendation BuildRecommendation(
        WindowsInfo windows,
        IReadOnlyList<AudioEndpointInfo> endpoints)
    {
        static bool Contains(AudioEndpointInfo endpoint, string value) =>
            endpoint.Name.Contains(value, StringComparison.OrdinalIgnoreCase);

        var hasStereo = endpoints.Any(endpoint =>
            endpoint.Flow == "Render" &&
            Contains(endpoint, "AirPods") &&
            (Contains(endpoint, "Stereo") || Contains(endpoint, "Стерео")));
        var hasHandsFreeRender = endpoints.Any(endpoint =>
            endpoint.Flow == "Render" &&
            Contains(endpoint, "AirPods") &&
            (Contains(endpoint, "Hands-Free") || Contains(endpoint, "Головной телефон")));
        var hasHandsFreeCapture = endpoints.Any(endpoint =>
            endpoint.Flow == "Capture" &&
            Contains(endpoint, "AirPods") &&
            (Contains(endpoint, "Hands-Free") || Contains(endpoint, "Головной телефон")));
        var hasExternalMicrophone = endpoints.Any(endpoint =>
            endpoint.Flow == "Capture" &&
            !Contains(endpoint, "AirPods"));

        var environment = new AudioEnvironment(
            windows.CurrentBuild,
            hasStereo,
            hasHandsFreeRender,
            hasHandsFreeCapture,
            hasExternalMicrophone,
            BluetoothLeAudioConfirmed: false);
        var preferences = new UserAudioPreferences(
            SpatialAudioMode.Off,
            PreferredMicrophone.Automatic,
            PreferStereo: true,
            AllowHandsFreeFallback: true);

        return AudioProfileAdvisor.Recommend(environment, preferences);
    }
}
