using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SoundStage.Diagnostics;

internal static class BluetoothNative
{
    private const int ErrorMoreData = 234;

    public static (IReadOnlyList<BluetoothRadioInfo> Radios, IReadOnlyList<BluetoothDeviceInfo> Devices)
        Enumerate()
    {
        var radios = new List<BluetoothRadioInfo>();
        var devices = new Dictionary<string, BluetoothDeviceInfo>(StringComparer.OrdinalIgnoreCase);

        var findParams = new BluetoothFindRadioParams
        {
            Size = Marshal.SizeOf<BluetoothFindRadioParams>(),
        };

        var findHandle = BluetoothFindFirstRadio(ref findParams, out var radioHandle);
        if (findHandle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 0 || error == 259)
            {
                return (radios, devices.Values.ToArray());
            }

            throw new Win32Exception(error, "BluetoothFindFirstRadio failed.");
        }

        try
        {
            do
            {
                try
                {
                    radios.Add(ReadRadio(radioHandle));
                    foreach (var device in ReadDevices(radioHandle))
                    {
                        devices[device.Address] = device;
                    }
                }
                finally
                {
                    _ = CloseHandle(radioHandle);
                }
            }
            while (BluetoothFindNextRadio(findHandle, out radioHandle));
        }
        finally
        {
            _ = BluetoothFindRadioClose(findHandle);
        }

        return (radios, devices.Values.OrderBy(device => device.Name).ToArray());
    }

    private static BluetoothRadioInfo ReadRadio(IntPtr radioHandle)
    {
        var info = new BluetoothRadioNative
        {
            Size = Marshal.SizeOf<BluetoothRadioNative>(),
        };

        var error = BluetoothGetRadioInfo(radioHandle, ref info);
        if (error != 0)
        {
            throw new Win32Exception(error, "BluetoothGetRadioInfo failed.");
        }

        return new BluetoothRadioInfo(
            info.Name,
            FormatAddress(info.Address),
            info.ClassOfDevice,
            info.Manufacturer,
            info.LmpSubversion);
    }

    private static IEnumerable<BluetoothDeviceInfo> ReadDevices(IntPtr radioHandle)
    {
        var searchParams = new BluetoothDeviceSearchParams
        {
            Size = Marshal.SizeOf<BluetoothDeviceSearchParams>(),
            ReturnAuthenticated = true,
            ReturnRemembered = true,
            ReturnUnknown = true,
            ReturnConnected = true,
            IssueInquiry = false,
            TimeoutMultiplier = 2,
            RadioHandle = radioHandle,
        };
        var deviceInfo = NewDeviceInfo();
        var findHandle = BluetoothFindFirstDevice(ref searchParams, ref deviceInfo);
        if (findHandle == IntPtr.Zero)
        {
            yield break;
        }

        try
        {
            do
            {
                var services = EnumerateInstalledServices(radioHandle, ref deviceInfo);
                yield return new BluetoothDeviceInfo(
                    deviceInfo.Name,
                    FormatAddress(deviceInfo.Address),
                    deviceInfo.Connected,
                    deviceInfo.Remembered,
                    deviceInfo.Authenticated,
                    deviceInfo.LastSeen.ToDateTime(),
                    deviceInfo.LastUsed.ToDateTime(),
                    services);

                deviceInfo = NewDeviceInfo();
            }
            while (BluetoothFindNextDevice(findHandle, ref deviceInfo));
        }
        finally
        {
            _ = BluetoothFindDeviceClose(findHandle);
        }
    }

    private static IReadOnlyList<string> EnumerateInstalledServices(
        IntPtr radioHandle,
        ref BluetoothDeviceNative deviceInfo)
    {
        uint serviceCount = 0;
        var firstError = BluetoothEnumerateInstalledServices(
            radioHandle,
            ref deviceInfo,
            ref serviceCount,
            null);
        if (firstError != 0 && firstError != ErrorMoreData)
        {
            return [];
        }

        if (serviceCount == 0)
        {
            return [];
        }

        var services = new Guid[serviceCount];
        var error = BluetoothEnumerateInstalledServices(
            radioHandle,
            ref deviceInfo,
            ref serviceCount,
            services);
        if (error != 0)
        {
            return [];
        }

        return services
            .Take((int)serviceCount)
            .Select(service => service.ToString("D"))
            .OrderBy(service => service)
            .ToArray();
    }

    private static BluetoothDeviceNative NewDeviceInfo() =>
        new()
        {
            Size = Marshal.SizeOf<BluetoothDeviceNative>(),
        };

    private static string FormatAddress(ulong address)
    {
        var hex = address.ToString("X12");
        return string.Join(':', Enumerable.Range(0, 6).Select(index => hex.Substring(index * 2, 2)));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BluetoothFindRadioParams
    {
        public int Size;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BluetoothRadioNative
    {
        public int Size;
        public ulong Address;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
        public string Name;

        public uint ClassOfDevice;
        public ushort LmpSubversion;
        public ushort Manufacturer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BluetoothDeviceSearchParams
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
        public IntPtr RadioHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BluetoothDeviceNative
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

        public SystemTime LastSeen;
        public SystemTime LastUsed;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
        public string Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct SystemTime
    {
        private readonly ushort year;
        private readonly ushort month;
        private readonly ushort dayOfWeek;
        private readonly ushort day;
        private readonly ushort hour;
        private readonly ushort minute;
        private readonly ushort second;
        private readonly ushort milliseconds;

        public DateTime? ToDateTime()
        {
            if (year == 0 || month == 0 || day == 0)
            {
                return null;
            }

            try
            {
                return new DateTime(
                    year,
                    month,
                    day,
                    hour,
                    minute,
                    second,
                    milliseconds,
                    DateTimeKind.Local);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
    }

    [DllImport("BluetoothApis.dll", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstRadio(
        ref BluetoothFindRadioParams findParams,
        out IntPtr radioHandle);

    [DllImport("BluetoothApis.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindNextRadio(
        IntPtr findHandle,
        out IntPtr radioHandle);

    [DllImport("BluetoothApis.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindRadioClose(IntPtr findHandle);

    [DllImport("BluetoothApis.dll")]
    private static extern int BluetoothGetRadioInfo(
        IntPtr radioHandle,
        ref BluetoothRadioNative radioInfo);

    [DllImport("BluetoothApis.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstDevice(
        ref BluetoothDeviceSearchParams searchParams,
        ref BluetoothDeviceNative deviceInfo);

    [DllImport("BluetoothApis.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindNextDevice(
        IntPtr findHandle,
        ref BluetoothDeviceNative deviceInfo);

    [DllImport("BluetoothApis.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindDeviceClose(IntPtr findHandle);

    [DllImport("BluetoothApis.dll", SetLastError = true)]
    private static extern int BluetoothEnumerateInstalledServices(
        IntPtr radioHandle,
        ref BluetoothDeviceNative deviceInfo,
        ref uint serviceCount,
        [Out] Guid[]? services);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
