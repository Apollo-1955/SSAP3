using System.Text;
using SoundStage.Platform.Windows;

namespace SoundStage.Diagnostics;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Any(argument => argument.Equals(
                "--repair-control",
                StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine(
                "SoundStage: refreshing the paired AirPods AAP/UARP control profiles...");
            var repair = WindowsAirPodsServiceManager.EnsureControlProfilesEnabled(
                forceRebind: true);
            Console.WriteLine(repair.Message);
            Console.WriteLine(
                $"AirPods={repair.Address}; connected={repair.Connected}; changed={repair.Changed}");
            foreach (var service in repair.Services)
            {
                Console.WriteLine(
                    $"- {service.ServiceGuid:D}: previouslyEnabled={service.WasEnabled}; " +
                    $"enabled={service.Enabled}; error={service.ErrorCode}");
            }

            return repair.AnyProfileEnabled ? 0 : 3;
        }

        Console.WriteLine(
            "SoundStage: collecting read-only Windows and AirPods diagnostics...");

        var outputDirectory = ResolveOutputDirectory(args);
        var result = await SoundStageDiagnosticCollector.CollectAsync(outputDirectory);
        Console.WriteLine($"Diagnostic bundle: {result.ZipPath}");
        if (result.Errors.Count > 0)
        {
            foreach (var error in result.Errors)
            {
                Console.WriteLine($"Warning: {error}");
            }
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
