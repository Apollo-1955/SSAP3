using System.IO;
using System.Text;

namespace SSAP3.App;

internal static class Ssap3AppLog
{
    private static readonly object Sync = new();
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SSAP3");

    public static string FilePath { get; } =
        Path.Combine(DirectoryPath, "SSAP3.log");

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(DirectoryPath);
                File.AppendAllText(
                    FilePath,
                    $"{DateTimeOffset.Now:O}  {message}{Environment.NewLine}",
                    new UTF8Encoding(false));
            }
        }
        catch
        {
            // Diagnostics must never make the control UI fail.
        }
    }
}
