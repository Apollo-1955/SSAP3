using SSAP3.Platform.Windows;

namespace SSAP3.Diagnostics;

internal static class WindowsDriverPresence
{
    public static bool IsSsap3DriverPresent() => Ssap3DriverAacpClient.IsDriverPresent;
}
