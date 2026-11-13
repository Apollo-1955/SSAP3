using System.Text;

namespace SSAP3.Diagnostics;

internal static class ReportFormatter
{
    public static string ToText(DiagnosticReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("SSAP3 diagnostic report");
        builder.AppendLine($"Collected: {report.CollectedAt:O}");
        builder.AppendLine();
        builder.AppendLine(
            $"Windows: {report.Windows.ProductName} {report.Windows.DisplayVersion}, " +
            $"build {report.Windows.CurrentBuild}.{report.Windows.UpdateBuildRevision}, " +
            report.Windows.Architecture);
        builder.AppendLine(
            $"Code Integrity: 0x{report.Windows.CodeIntegrityOptions:X8}; " +
            $"Test Mode active={report.Windows.TestSigningActive}; " +
            $"Secure Boot={report.Windows.SecureBootEnabled?.ToString() ?? "unknown"}");

        builder.AppendLine();
        builder.AppendLine("Bluetooth radios:");
        foreach (var radio in report.BluetoothRadios)
        {
            builder.AppendLine(
                $"- {radio.Name}; {radio.Address}; manufacturer={radio.Manufacturer}; " +
                $"LMP={radio.LmpSubversion}; class=0x{radio.ClassOfDevice:X}");
        }

        builder.AppendLine();
        builder.AppendLine("Bluetooth devices:");
        foreach (var device in report.BluetoothDevices)
        {
            builder.AppendLine(
                $"- {device.Name}; {device.Address}; connected={device.Connected}; " +
                $"remembered={device.Remembered}; authenticated={device.Authenticated}");
            foreach (var service in device.InstalledServiceGuids)
            {
                builder.AppendLine($"    service: {service}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Audio endpoints:");
        foreach (var endpoint in report.AudioEndpoints)
        {
            var roles = endpoint.DefaultRoles.Count == 0
                ? string.Empty
                : $"; default={string.Join(',', endpoint.DefaultRoles)}";
            builder.AppendLine(
                $"- [{endpoint.Flow}] {endpoint.Name}; state=0x{endpoint.State:X}{roles}");
            builder.AppendLine($"    {endpoint.Id}");
        }

        builder.AppendLine();
        builder.AppendLine("Recommended route:");
        builder.AppendLine($"- Mode: {report.Recommendation.Mode}");
        builder.AppendLine($"- Output: {report.Recommendation.Output}");
        builder.AppendLine($"- Input: {report.Recommendation.Input}");
        builder.AppendLine($"- Stereo preserved: {report.Recommendation.PreservesStereo}");
        builder.AppendLine($"- {report.Recommendation.Explanation}");

        builder.AppendLine();
        builder.AppendLine("Findings:");
        foreach (var finding in report.Findings)
        {
            builder.AppendLine($"- {finding}");
        }

        if (report.Errors.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Collection errors:");
            foreach (var error in report.Errors)
            {
                builder.AppendLine($"- {error}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Raw command output:");
        foreach (var command in report.Commands)
        {
            builder.AppendLine();
            builder.AppendLine($"> {command.FileName} {command.Arguments}");
            builder.AppendLine($"Exit code: {command.ExitCode}");
            builder.AppendLine(command.StandardOutput.TrimEnd());
            if (!string.IsNullOrWhiteSpace(command.StandardError))
            {
                builder.AppendLine("STDERR:");
                builder.AppendLine(command.StandardError.TrimEnd());
            }
        }

        return builder.ToString();
    }
}
