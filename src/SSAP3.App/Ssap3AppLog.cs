using System.IO;
using System.Text;
using SSAP3.Core;

namespace SSAP3.App;

/// <summary>
/// Local application log in %LocalAppData%\SSAP3\SSAP3.log. Bluetooth
/// addresses are redacted before writing, and the file rotates at 1 MiB, so
/// it cannot grow without bound. Nothing is ever uploaded.
/// </summary>
internal static class Ssap3AppLog
{
    private const long MaximumBytes = 1024 * 1024;
    private static readonly object Sync = new();
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SSAP3");

    public static string FilePath { get; } = Path.Combine(DirectoryPath, "SSAP3.log");

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(DirectoryPath);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaximumBytes)
                {
                    File.Move(FilePath, FilePath + ".1", overwrite: true);
                }

                File.AppendAllText(
                    FilePath,
                    $"{DateTimeOffset.Now:O}  {DiagnosticRedactor.Redact(message)}{Environment.NewLine}",
                    new UTF8Encoding(false));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Logging must never break the controls.
        }
    }
}
