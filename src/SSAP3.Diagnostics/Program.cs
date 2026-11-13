using System.Text;
using SSAP3.Platform.Windows;

namespace SSAP3.Diagnostics;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Any(argument => argument.Equals("--repair-control", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine("SSAP3: refreshing the paired AirPods AAP/UARP services (Windows Bluetooth API)...");
            var repair = WindowsAirPodsServiceManager.EnsureControlProfilesEnabled(forceRebind: true);
            Console.WriteLine(repair.Message);
            Console.WriteLine($"connected={repair.Connected}; changed={repair.Changed}");
            foreach (var service in repair.Services)
            {
                Console.WriteLine(
                    $"- {service.ServiceGuid:D}: previouslyEnabled={service.WasEnabled}; enabled={service.Enabled}; error={service.ErrorCode}");
            }

            return repair.AnyProfileEnabled ? 0 : 3;
        }

        var includeIdentifiers = args.Any(argument => argument.Equals("--include-identifiers", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine("SSAP3: collecting read-only, local diagnostics" +
            (includeIdentifiers ? " (identifiers INCLUDED - do not post publicly)..." : " (identifiers redacted)..."));

        var outputDirectory = ResolveOutputDirectory(args);
        var result = await Ssap3DiagnosticCollector.CollectAsync(outputDirectory, includeIdentifiers: includeIdentifiers);
        Console.WriteLine($"Diagnostic bundle: {result.ZipPath}");
        foreach (var error in result.Errors)
        {
            Console.WriteLine($"Warning: {error}");
        }

        return result.Complete ? 0 : 2;
    }

    private static string? ResolveOutputDirectory(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (args[index].Equals("--output", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(args[index + 1]);
            }
        }

        return null;
    }
}
