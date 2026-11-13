using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SSAP3.Platform.Windows;

/// <summary>Reads the Bluetooth Device ID (vendor/product) Windows recorded for a paired device.</summary>
public static partial class WindowsBluetoothIdentity
{
    private const uint CmGetIdListFilterEnumerator = 0x00000001;
    private const uint CrSuccess = 0;

    /// <summary>Finds the Apple product ID (e.g. 0x2027 for AirPods Pro 3) of the paired device at <paramref name="address"/>.</summary>
    public static bool TryGetAppleProductId(string address, out int productId)
    {
        productId = 0;
        var compact = address.Replace(":", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        if (compact.Length != 12)
        {
            return false;
        }

        foreach (var instanceId in EnumerateBthEnumInstanceIds())
        {
            var match = AppleServiceInstance().Match(instanceId);
            if (match.Success &&
                string.Equals(match.Groups["address"].Value, compact, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(match.Groups["pid"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out productId))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> EnumerateBthEnumInstanceIds()
    {
        if (CM_Get_Device_ID_List_SizeW(out var length, "BTHENUM", CmGetIdListFilterEnumerator) != CrSuccess ||
            length <= 1 || length > 1 << 22)
        {
            return [];
        }

        var buffer = new char[length];
        return CM_Get_Device_ID_ListW("BTHENUM", buffer, length, CmGetIdListFilterEnumerator) == CrSuccess
            ? [.. new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries)]
            : [];
    }

    [GeneratedRegex(@"_VID&0001004C_PID&(?<pid>[0-9A-F]{4})\\[^\\]*?(?<address>[0-9A-F]{12})_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AppleServiceInstance();

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, uint length, uint flags);
}
