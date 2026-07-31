using System.IO;
using System.Text;

namespace SoundStage.App;

internal static class SoundStageAppLog
{
    private static readonly object Sync = new();
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SoundStage");

    public static string FilePath { get; } =
        Path.Combine(DirectoryPath, "SoundStage.log");

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
