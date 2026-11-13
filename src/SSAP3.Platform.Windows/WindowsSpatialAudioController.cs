using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace SSAP3.Platform.Windows;

public sealed class WindowsSpatialAudioController
{
    public const string WindowsSonicGuid = "{B53D940C-B846-4831-9F76-D102B9B725A0}";

    private const string RenderEndpointsRegistryPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
    private const string EndpointDeviceInstanceProperty =
        "{b3f8fa53-0004-438e-9003-51a46e139bfc},2";
    private const string EndpointNameProperty =
        "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";

    private readonly string _toolPath;

    public WindowsSpatialAudioController()
    {
        _toolPath = ResolveToolPath();
    }

    public bool IsAvailable => File.Exists(_toolPath);

    public async Task<SpatialAudioStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var endpoint = FindAirPodsStereoEndpoint();
        if (endpoint is null)
        {
            return new SpatialAudioStatus(
                Available: IsAvailable,
                Enabled: false,
                EndpointId: null,
                EndpointName: null,
                Message: "The AirPods A2DP Stereo endpoint was not found.");
        }

        if (!IsAvailable)
        {
            return new SpatialAudioStatus(
                Available: false,
                Enabled: false,
                EndpointId: endpoint.EndpointId,
                EndpointName: endpoint.Name,
                Message: "The Windows Sonic control module is not installed.");
        }

        var reportPath = Path.Combine(
            Path.GetTempPath(),
            $"ssap3-spatial-{Environment.ProcessId}-{Guid.NewGuid():N}.json");
        try
        {
            var exitCode = await RunAsync(
                ["/sjson", reportPath],
                cancellationToken);
            if (exitCode != 0 || !File.Exists(reportPath))
            {
                return new SpatialAudioStatus(
                    Available: true,
                    Enabled: false,
                    EndpointId: endpoint.EndpointId,
                    EndpointName: endpoint.Name,
                    Message: "Windows Sonic status could not be read.");
            }

            using var reader = new StreamReader(
                reportPath,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            var json = await reader.ReadToEndAsync(cancellationToken);
            using var document = JsonDocument.Parse(json);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (!TryGetString(item, "Type", out var type) ||
                    !type.Equals("Device", StringComparison.OrdinalIgnoreCase) ||
                    !TryGetString(item, "Direction", out var direction) ||
                    !direction.Equals("Render", StringComparison.OrdinalIgnoreCase) ||
                    !TryGetString(item, "Item ID", out var itemId) ||
                    !itemId.Equals(endpoint.EndpointId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _ = TryGetString(item, "Spatial Guid", out var spatialGuid);
                var enabled = spatialGuid.Equals(
                    WindowsSonicGuid,
                    StringComparison.OrdinalIgnoreCase);
                return new SpatialAudioStatus(
                    Available: true,
                    Enabled: enabled,
                    EndpointId: endpoint.EndpointId,
                    EndpointName: endpoint.Name,
                    Message: enabled
                        ? "Windows Sonic is enabled for AirPods Stereo."
                        : "Windows spatial processing is off.");
            }
        }
        catch (Exception exception)
        {
            return new SpatialAudioStatus(
                Available: true,
                Enabled: false,
                EndpointId: endpoint.EndpointId,
                EndpointName: endpoint.Name,
                Message: exception.Message);
        }
        finally
        {
            try
            {
                File.Delete(reportPath);
            }
            catch
            {
                // A stale diagnostic file is harmless.
            }
        }

        return new SpatialAudioStatus(
            Available: true,
            Enabled: false,
            EndpointId: endpoint.EndpointId,
            EndpointName: endpoint.Name,
            Message: "AirPods Stereo was found, but Spatial Audio status is unknown.");
    }

    public async Task<SpatialAudioStatus> SetWindowsSonicAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var endpoint = FindAirPodsStereoEndpoint();
        if (endpoint is null)
        {
            return new SpatialAudioStatus(
                Available: IsAvailable,
                Enabled: false,
                EndpointId: null,
                EndpointName: null,
                Message: "Connect AirPods in Stereo mode and try again.");
        }

        if (!IsAvailable)
        {
            return new SpatialAudioStatus(
                Available: false,
                Enabled: false,
                EndpointId: endpoint.EndpointId,
                EndpointName: endpoint.Name,
                Message: "SoundVolumeView is missing from the SSAP3 installation.");
        }

        var spatialFormat = enabled ? WindowsSonicGuid : string.Empty;
        var exitCode = await RunAsync(
            ["/SetSpatial", endpoint.EndpointId, spatialFormat],
            cancellationToken);
        if (exitCode != 0)
        {
            return new SpatialAudioStatus(
                Available: true,
                Enabled: !enabled,
                EndpointId: endpoint.EndpointId,
                EndpointName: endpoint.Name,
                Message: $"Windows returned code {exitCode} while changing Spatial Audio.");
        }

        await Task.Delay(250, cancellationToken);
        return await GetStatusAsync(cancellationToken);
    }

    private AirPodsAudioEndpoint? FindAirPodsStereoEndpoint()
    {
        using var renderEndpoints = Registry.LocalMachine.OpenSubKey(
            RenderEndpointsRegistryPath);
        if (renderEndpoints is null)
        {
            return null;
        }

        foreach (var endpointKeyName in renderEndpoints.GetSubKeyNames())
        {
            using var endpointKey = renderEndpoints.OpenSubKey(endpointKeyName);
            if (endpointKey is null ||
                Convert.ToInt32(endpointKey.GetValue("DeviceState", 0)) != 1)
            {
                continue;
            }

            using var properties = endpointKey.OpenSubKey("Properties");
            var deviceInstance = properties?.GetValue(
                EndpointDeviceInstanceProperty) as string;
            if (string.IsNullOrWhiteSpace(deviceInstance) ||
                !deviceInstance.Contains(
                    "{0000110B-0000-1000-8000-00805F9B34FB}",
                    StringComparison.OrdinalIgnoreCase) ||
                !deviceInstance.Contains(
                    "VID&0001004C",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = properties?.GetValue(EndpointNameProperty) as string ??
                "Apple Bluetooth Stereo";
            return new AirPodsAudioEndpoint(
                $"{{0.0.0.00000000}}.{endpointKeyName}",
                name);
        }

        return null;
    }

    private async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        // S-10/S-11: only the copy installed next to SSAP3.exe is used, and only
        // when its SHA-256 matches the pinned, reviewed NirSoft binary.
        if (!IsTrustedTool(_toolPath))
        {
            throw new InvalidOperationException("SoundVolumeView failed its integrity check and was not started.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _toolPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_toolPath),
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("SoundVolumeView could not be started.");
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }

    /// <summary>SHA-256 of SoundVolumeView 2.53 x64 as reviewed for SSAP3 (THIRD_PARTY.md).</summary>
    public const string PinnedSoundVolumeViewSha256 = "B5AF5BD60F7A29AF8CB4D8A566382B90F0FE07CAC97228D218CB913F3382D647";

    public static bool IsTrustedTool(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
            return string.Equals(hash, PinnedSoundVolumeViewSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
    private static bool TryGetString(
        JsonElement element,
        string name,
        out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static string ResolveToolPath() =>
        Path.Combine(AppContext.BaseDirectory, "tools", "SoundVolumeView", "SoundVolumeView.exe");
    private sealed record AirPodsAudioEndpoint(string EndpointId, string Name);
}

public sealed record SpatialAudioStatus(
    bool Available,
    bool Enabled,
    string? EndpointId,
    string? EndpointName,
    string Message);
