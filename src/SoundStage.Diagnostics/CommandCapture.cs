using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SoundStage.Diagnostics;

internal static class CommandCapture
{
    public static async Task<CommandResult> RunAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken)
    {
        // pnputil/sc switch to the current Windows ANSI code page when stdout is
        // redirected. Using the console OEM page (866 on Russian Windows) turns
        // CP1251 output into box-drawing mojibake.
        var redirectedOutputEncoding =
            Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = redirectedOutputEncoding,
                StandardErrorEncoding = redirectedOutputEncoding,
            },
        };

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return new CommandResult(
            fileName,
            arguments,
            process.ExitCode,
            await outputTask,
            await errorTask);
    }
}
