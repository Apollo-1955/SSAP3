using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SSAP3.Platform.Windows;

namespace SSAP3.Diagnostics;

public static class Ssap3DiagnosticCollector
{
    public static async Task<DiagnosticBundleResult> CollectAsync(
        string? outputDirectory = null,
        CancellationToken cancellationToken = default)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        outputDirectory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "SSAP3",
            "Reports");
        outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var workingDirectory = Path.Combine(
            outputDirectory,
            $"SSAP3-diagnostics-{timestamp}");
        Directory.CreateDirectory(workingDirectory);
        var errors = new List<string>();

        try
        {
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

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var commandTasks = new List<Task<CommandResult>>
            {
                CommandCapture.RunAsync(
                    "bcdedit.exe",
                    "/enum {current}",
                    timeout.Token),
                CommandCapture.RunAsync(
                    "pnputil.exe",
                    "/enum-devices /class Bluetooth /connected",
                    timeout.Token),
                CommandCapture.RunAsync(
                    "powershell.exe",
                    "-NoProfile -NonInteractive -Command \"" +
                    "$devices = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue; " +
                    "$devices | Where-Object { $_.Class -eq 'Bluetooth' -or " +
                    "$_.Problem -ne 'CM_PROB_NONE' } | Select-Object Status,Class," +
                    "FriendlyName,InstanceId,Problem,ConfigManagerErrorCode | Format-List\"",
                    timeout.Token),
                CommandCapture.RunAsync(
                    "pnputil.exe",
                    "/enum-drivers",
                    timeout.Token),
                CommandCapture.RunAsync(
                    "sc.exe",
                    "query bthserv",
                    timeout.Token),
                CommandCapture.RunAsync(
                    "sc.exe",
                    "query Audiosrv",
                    timeout.Token),
            };
            if (windows.CurrentBuild >= 22000)
            {
                commandTasks.Add(CommandCapture.RunAsync(
                    "pnputil.exe",
                    "/enum-devices /class Bluetooth /drivers /deviceids /stack /interfaces",
                    timeout.Token));
                commandTasks.Add(CommandCapture.RunAsync(
                    "pnputil.exe",
                    "/enum-devices /problem /deviceids",
                    timeout.Token));
            }

            CommandResult[] commands;
            try
            {
                commands = await Task.WhenAll(commandTasks);
                foreach (var command in commands.Where(command =>
                             command.ExitCode != 0))
                {
                    errors.Add(
                        $"{command.FileName} {command.Arguments} exited with code " +
                        $"{command.ExitCode}.");
                }
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

            var jsonPath = Path.Combine(workingDirectory, "diagnostic.json");
            var textPath = Path.Combine(workingDirectory, "diagnostic.txt");
            var standaloneTextPath = Path.Combine(
                outputDirectory,
                $"SSAP3-diagnostic-{timestamp}.txt");
            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() },
            };
            await File.WriteAllTextAsync(
                jsonPath,
                JsonSerializer.Serialize(report, jsonOptions),
                new UTF8Encoding(false),
                cancellationToken);
            await File.WriteAllTextAsync(
                textPath,
                ReportFormatter.ToText(report),
                new UTF8Encoding(true),
                cancellationToken);
            File.Copy(textPath, standaloneTextPath, overwrite: true);

            CopyIfPresent(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "SSAP3",
                    "install.log"),
                Path.Combine(workingDirectory, "install.log"),
                errors);
            CopyIfPresent(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SSAP3",
                    "SSAP3.log"),
                Path.Combine(workingDirectory, "SSAP3.log"),
                errors);

            var zipPath = Path.Combine(
                outputDirectory,
                $"SSAP3-diagnostics-{timestamp}.zip");
            ZipFile.CreateFromDirectory(
                workingDirectory,
                zipPath,
                CompressionLevel.Optimal,
                includeBaseDirectory: false);
            return new DiagnosticBundleResult(
                zipPath,
                standaloneTextPath,
                errors.Count == 0,
                errors);
        }
        finally
        {
            try
            {
                if (Directory.Exists(workingDirectory))
                {
                    Directory.Delete(workingDirectory, recursive: true);
                }
            }
            catch
            {
                // The ZIP is complete; leaving a readable working folder is harmless.
            }
        }
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

        findings.Add(
            $"Kernel Code Integrity options are 0x{windows.CodeIntegrityOptions:X8}; " +
            $"Test Mode is {(windows.TestSigningActive ? "active" : "inactive")} in this boot.");

        var bcdOutput = commands.FirstOrDefault(command =>
            command.FileName.Equals("bcdedit.exe", StringComparison.OrdinalIgnoreCase))?
            .StandardOutput ?? string.Empty;
        var bcdConfigured = bcdOutput
            .Split('\r', '\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.TrimStart().StartsWith(
                "testsigning",
                StringComparison.OrdinalIgnoreCase))
            .Select(line => line.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries).LastOrDefault())
            .Any(value => value is not null && new[]
            {
                "yes", "on", "true", "да", "вкл", "вкл.", "включено",
            }.Contains(value, StringComparer.OrdinalIgnoreCase));
        if (windows.TestSigningActive && !bcdConfigured)
        {
            findings.Add(
                "Test Mode is active now but disabled in BCD for the next boot. " +
                "Run SSAP3 Setup repair before restarting Windows.");
        }

        if (combinedOutput.Contains(
                "CM_PROB_UNSIGNED_DRIVER",
                StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(
                "A present PnP device has problem code 52 (unsigned driver). " +
                "For SSAP3 this normally means Test Mode was not active at boot.");
        }
        else if (!windows.TestSigningActive && !bcdConfigured)
        {
            findings.Add(
                "Test Mode is disabled. The test-signed SSAP3 control driver cannot load; " +
                "run SSAP3 Setup and complete its restart step.");
        }
        else if (!windows.TestSigningActive && bcdConfigured)
        {
            findings.Add(
                "Test Mode is enabled in BCD but is not active in this boot. " +
                "Restart Windows, then run SSAP3 Setup again.");
        }

        if (windows.CurrentBuild < 22000)
        {
            findings.Add(
                "Windows 10 detected: classic A2DP/HFP is expected; Windows LE Audio is unavailable.");
        }

        if (windows.CurrentBuild >= 28000)
        {
            findings.Add(
                "Windows 11 26H1/Insider platform build detected; Bluetooth behavior may differ from retail builds.");
        }

        if (combinedOutput.Contains("VID_2357&PID_0604", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add("TP-Link Bluetooth 5.4 adapter USB VID_2357/PID_0604 detected.");
        }

        if (combinedOutput.Contains("VID_33FA&PID_0010", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add("BARROT Bluetooth 5.4 adapter USB VID_33FA/PID_0010 detected.");
        }

        if (combinedOutput.Contains("PID&201b", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add("AirPods 4 with ANC Bluetooth product id 201B detected.");
        }

        var enabledServices = devices.SelectMany(device =>
            device.InstalledServiceGuids);
        var hasAap = ContainsGuid(
            combinedOutput,
            enabledServices,
            WindowsAirPodsServiceManager.AapServiceGuid);
        var hasUarp = ContainsGuid(
            combinedOutput,
            enabledServices,
            WindowsAirPodsServiceManager.UarpServiceGuid);
        findings.Add(
            $"Apple profile PDOs: AAP={(hasAap ? "present" : "missing")}; " +
            $"UARP={(hasUarp ? "present" : "missing")}.");

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
            (endpoint.Name.Contains("Stereo", StringComparison.OrdinalIgnoreCase) ||
             endpoint.Name.Contains("Стерео", StringComparison.OrdinalIgnoreCase)));
        var hasHandsFreeCapture = endpoints.Any(endpoint =>
            endpoint.Flow == "Capture" &&
            endpoint.Name.Contains("AirPods", StringComparison.OrdinalIgnoreCase) &&
            (endpoint.Name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase) ||
             endpoint.Name.Contains("Головной телефон", StringComparison.OrdinalIgnoreCase)));
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

    private static bool ContainsGuid(
        string commandOutput,
        IEnumerable<string> enabledServices,
        Guid service)
    {
        var formatted = service.ToString("D");
        var compact = service.ToString("N");
        return commandOutput.Contains(formatted, StringComparison.OrdinalIgnoreCase) ||
               commandOutput.Contains(compact, StringComparison.OrdinalIgnoreCase) ||
               enabledServices.Any(value =>
                   value.Contains(formatted, StringComparison.OrdinalIgnoreCase) ||
                   value.Contains(compact, StringComparison.OrdinalIgnoreCase));
    }

    private static void CopyIfPresent(
        string source,
        string destination,
        ICollection<string> errors)
    {
        try
        {
            if (File.Exists(source))
            {
                File.Copy(source, destination, overwrite: true);
            }
        }
        catch (Exception exception)
        {
            errors.Add($"Could not include {Path.GetFileName(source)}: {exception.Message}");
        }
    }
}

public sealed record DiagnosticBundleResult(
    string ZipPath,
    string TextReportPath,
    bool Complete,
    IReadOnlyList<string> Errors);
