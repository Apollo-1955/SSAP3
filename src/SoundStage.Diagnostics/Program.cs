using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoundStage.Diagnostics;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("SoundStage: collecting read-only Windows and AirPods diagnostics...");

        var outputDirectory = ResolveOutputDirectory(args);
        Directory.CreateDirectory(outputDirectory);
        var errors = new List<string>();

        var windows = WindowsCollector.ReadWindowsInfo();
        var bluetooth = TryCollect(
            BluetoothNative.Enumerate,
            (
                Radios: (IReadOnlyList<BluetoothRadioInfo>)[],
                Devices: (IReadOnlyList<BluetoothDeviceInfo>)[]
            ),
            "Bluetooth API",
            errors);
        var (radios, devices) = bluetooth;
        var endpoints = TryCollect(
            AudioEndpointEnumerator.Enumerate,
            (IReadOnlyList<AudioEndpointInfo>)[],
            "Core Audio",
            errors);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var commandTasks = new[]
        {
            CommandCapture.RunAsync(
                "pnputil.exe",
                "/enum-devices /class Bluetooth /connected",
                cancellation.Token),
            CommandCapture.RunAsync(
                "pnputil.exe",
                "/enum-devices /class AudioEndpoint /connected",
                cancellation.Token),
            CommandCapture.RunAsync(
                "pnputil.exe",
                "/enum-drivers",
                cancellation.Token),
            CommandCapture.RunAsync(
                "sc.exe",
                "query bthserv",
                cancellation.Token),
            CommandCapture.RunAsync(
                "sc.exe",
                "query Audiosrv",
                cancellation.Token),
        };

        CommandResult[] commands;
        try
        {
            commands = await Task.WhenAll(commandTasks);
        }
        catch (Exception exception)
        {
            errors.Add($"External commands: {exception.Message}");
            commands = [];
        }

        var recommendation = WindowsCollector.BuildRecommendation(windows, endpoints);
        var findings = BuildFindings(windows, devices, endpoints, commands);
        var report = new DiagnosticReport(
            DateTimeOffset.Now,
            windows,
            radios,
            devices,
            endpoints,
            recommendation,
            commands,
            findings,
            errors);

        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var jsonPath = Path.Combine(outputDirectory, $"SoundStage-diagnostic-{timestamp}.json");
        var textPath = Path.Combine(outputDirectory, $"SoundStage-diagnostic-{timestamp}.txt");
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        await File.WriteAllTextAsync(
            jsonPath,
            JsonSerializer.Serialize(report, jsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await File.WriteAllTextAsync(
            textPath,
            ReportFormatter.ToText(report),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Console.WriteLine($"Text report: {textPath}");
        Console.WriteLine($"JSON report: {jsonPath}");
        Console.WriteLine($"Recommended Discord output: {recommendation.Output}");
        Console.WriteLine($"Recommended Discord input: {recommendation.Input}");
        return errors.Count == 0 ? 0 : 2;
    }

    private static T TryCollect<T>(
        Func<T> collector,
        T fallback,
        string source,
        ICollection<string> errors)
    {
        try
        {
            return collector();
        }
        catch (Exception exception)
        {
            errors.Add($"{source}: {exception.GetType().Name}: {exception.Message}");
            return fallback;
        }
    }

    private static string ResolveOutputDirectory(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (args[index].Equals("--output", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(args[index + 1]);
            }
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "SoundStage",
            "Reports");
    }

    private static IReadOnlyList<string> BuildFindings(
        WindowsInfo windows,
        IReadOnlyList<BluetoothDeviceInfo> devices,
        IReadOnlyList<AudioEndpointInfo> endpoints,
        IReadOnlyList<CommandResult> commands)
    {
        var findings = new List<string>();
        var combinedOutput = string.Join(
            Environment.NewLine,
            commands.Select(command => command.StandardOutput));

        if (windows.CurrentBuild < 22000)
        {
            findings.Add("Windows 10 detected: classic A2DP/HFP is expected; Windows LE Audio is unavailable.");
        }

        if (combinedOutput.Contains("VID_2357&PID_0604", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add("TP-Link Bluetooth 5.4 adapter USB VID_2357/PID_0604 detected.");
        }

        if (combinedOutput.Contains("PID&201b", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add("AirPods 4 with ANC Bluetooth product id 201B detected.");
        }

        var airPods = devices.FirstOrDefault(device =>
            device.Name.Contains("AirPods", StringComparison.OrdinalIgnoreCase));
        if (airPods is not null)
        {
            findings.Add(
                $"AirPods are {(airPods.Connected ? "connected" : "remembered but not connected")} " +
                $"at {airPods.Address}.");
        }

        var hasStereo = endpoints.Any(endpoint =>
            endpoint.Flow == "Render" &&
            endpoint.Name.Contains("AirPods", StringComparison.OrdinalIgnoreCase) &&
            endpoint.Name.Contains("Stereo", StringComparison.OrdinalIgnoreCase));
        var hasHandsFreeCapture = endpoints.Any(endpoint =>
            endpoint.Flow == "Capture" &&
            endpoint.Name.Contains("AirPods", StringComparison.OrdinalIgnoreCase) &&
            endpoint.Name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase));
        if (hasStereo && hasHandsFreeCapture)
        {
            findings.Add(
                "Windows exposes separate AirPods A2DP Stereo and HFP microphone endpoints. " +
                "Opening the HFP microphone causes the classic Bluetooth quality downgrade.");
        }

        var hasExternalMic = endpoints.Any(endpoint =>
            endpoint.Flow == "Capture" &&
            !endpoint.Name.Contains("AirPods", StringComparison.OrdinalIgnoreCase));
        if (hasExternalMic)
        {
            findings.Add(
                "A non-AirPods microphone is available; using it in Discord preserves the AirPods Stereo route.");
        }

        return findings;
    }
}
