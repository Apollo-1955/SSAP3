using System.Runtime.InteropServices;

namespace SoundStage.Platform.Windows;

public static class WindowsRememberedAirPodsFinder
{
    public static RememberedBluetoothDevice? FindBest()
    {
        var candidates = new List<RememberedBluetoothDevice>();
        var radioParameters = new BluetoothFindRadioParameters
        {
            Size = Marshal.SizeOf<BluetoothFindRadioParameters>(),
        };

        var radioSearch = BluetoothFindFirstRadio(ref radioParameters, out var radio);
        if (radioSearch == 0)
        {
            return null;
        }

        try
        {
            do
            {
                try
                {
                    candidates.AddRange(FindOnRadio(radio));
                }
                finally
                {
                    _ = CloseHandle(radio);
                }
            }
            while (BluetoothFindNextRadio(radioSearch, out radio));
        }
        finally
        {
            _ = BluetoothFindRadioClose(radioSearch);
        }

        return candidates
            .OrderByDescending(device => device.Connected)
            .ThenByDescending(device => device.Authenticated)
            .FirstOrDefault();
    }

    private static IEnumerable<RememberedBluetoothDevice> FindOnRadio(nint radio)
    {
        var parameters = new BluetoothDeviceSearchParameters
        {
            Size = Marshal.SizeOf<BluetoothDeviceSearchParameters>(),
            ReturnAuthenticated = true,
            ReturnRemembered = true,
            ReturnUnknown = false,
            ReturnConnected = true,
            IssueInquiry = false,
            TimeoutMultiplier = 2,
            RadioHandle = radio,
        };
        var native = NewDeviceInfo();
        var search = BluetoothFindFirstDevice(ref parameters, ref native);
        if (search == 0)
        {
            yield break;
        }

        try
        {
            do
            {
                if (native.Name.Contains("AirPods", StringComparison.OrdinalIgnoreCase))
                {
                    yield return new RememberedBluetoothDevice(
                        native.Name,
                        FormatAddress(native.Address),
                        native.Connected,
                        native.Authenticated);
                }

                native = NewDeviceInfo();
            }
            while (BluetoothFindNextDevice(search, ref native));
        }
        finally
        {
            _ = BluetoothFindDeviceClose(search);
        }
    }

    private static BluetoothDeviceInfo NewDeviceInfo() =>
        new() { Size = Marshal.SizeOf<BluetoothDeviceInfo>() };

    private static string FormatAddress(ulong address)
    {
        var value = address.ToString("X12");
        return string.Join(
            ':',
            Enumerable.Range(0, 6).Select(index => value.Substring(index * 2, 2)));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BluetoothFindRadioParameters
    {
        public int Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BluetoothDeviceSearchParameters
    {
        public int Size;

        [MarshalAs(UnmanagedType.Bool)]
        public bool ReturnAuthenticated;

        [MarshalAs(UnmanagedType.Bool)]
        public bool ReturnRemembered;

        [MarshalAs(UnmanagedType.Bool)]
        public bool ReturnUnknown;

        [MarshalAs(UnmanagedType.Bool)]
        public bool ReturnConnected;

        [MarshalAs(UnmanagedType.Bool)]
        public bool IssueInquiry;

        public byte TimeoutMultiplier;
        public nint RadioHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BluetoothDeviceInfo
    {
        public int Size;
        public ulong Address;
        public uint ClassOfDevice;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Connected;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Remembered;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Authenticated;

        private SystemTime lastSeen;
        private SystemTime lastUsed;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
        public string Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        private ushort year;
        private ushort month;
        private ushort dayOfWeek;
        private ushort day;
        private ushort hour;
        private ushort minute;
        private ushort second;
        private ushort milliseconds;
    }

    [DllImport("BluetoothApis.dll", SetLastError = true)]
    private static extern nint BluetoothFindFirstRadio(
        ref BluetoothFindRadioParameters parameters,
        out nint radio);

    [DllImport("BluetoothApis.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindNextRadio(nint search, out nint radio);

    [DllImport("BluetoothApis.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindRadioClose(nint search);

    [DllImport("BluetoothApis.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint BluetoothFindFirstDevice(
        ref BluetoothDeviceSearchParameters parameters,
        ref BluetoothDeviceInfo device);

    [DllImport("BluetoothApis.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindNextDevice(
        nint search,
        ref BluetoothDeviceInfo device);

    [DllImport("BluetoothApis.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindDeviceClose(nint search);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}

public sealed record RememberedBluetoothDevice(
    string Name,
    string Address,
    bool Connected,
    bool Authenticated);
