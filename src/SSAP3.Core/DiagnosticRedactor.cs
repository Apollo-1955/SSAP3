using System.Text.RegularExpressions;

namespace SSAP3.Core;

/// <summary>
/// Removes personal identifiers from logs and diagnostic reports
/// (SECURITY_AUDIT.md, S-13). Bluetooth addresses keep only their last two
/// octets, so entries can still be correlated within one report. User
/// profile paths and caller-supplied device names are replaced.
/// </summary>
public static partial class DiagnosticRedactor
{
    public static string Redact(string? text, IEnumerable<string>? sensitiveNames = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = SeparatedAddress().Replace(text, match =>
        {
            var value = match.Value;
            var separator = value[2];
            return $"XX{separator}XX{separator}XX{separator}XX{separator}{value[^5..]}";
        });
        result = CompactAddress().Replace(result, match => "XXXXXXXX" + match.Value[^4..]);
        result = UserProfilePath().Replace(result, @"$1<user>");

        if (sensitiveNames is not null)
        {
            foreach (var name in sensitiveNames.Where(name => !string.IsNullOrWhiteSpace(name) && name.Length >= 3).Distinct())
            {
                result = result.Replace(name, "<device name>", StringComparison.OrdinalIgnoreCase);
            }
        }

        return result;
    }

    [GeneratedRegex(@"\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatedAddress();

    // 12 hex digits embedded in PnP instance IDs (e.g. "...&0&A1B2C3D4E5F6_C00000000").
    [GeneratedRegex(@"(?<=[&_\\#])[0-9A-Fa-f]{12}(?=[_\\#&]|\b)", RegexOptions.CultureInvariant)]
    private static partial Regex CompactAddress();

    [GeneratedRegex(@"([A-Za-z]:\\Users\\)[^\\\r\n""]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UserProfilePath();
}
