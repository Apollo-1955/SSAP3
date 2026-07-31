using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SoundStage.Platform.Windows;

public static class WindowsAirPodsServiceManager
{
    private const uint BluetoothServiceEnable = 1;
    private const uint BluetoothServiceDisable = 0;
    private const uint ErrorSuccess = 0;
    private const uint ErrorMoreData = 234;
    private const uint ErrorServiceDoesNotExist = 1060;
    private const uint EInvalidArg = 0x80070057;

    public static readonly Guid UarpServiceGuid =
        new("4715650B-5E9D-4AC2-B898-A4FC0AA5DF78");

    public static readonly Guid AapServiceGuid =
        new("74EC2172-0BAD-4D01-8F77-997B2BE0722A");

    public static AirPodsControlRepairResult EnsureControlProfilesEnabled(
        bool forceRebind = false)
    {
        var radioParameters = new BluetoothFindRadioParameters
        {
            Size = Marshal.SizeOf<BluetoothFindRadioParameters>(),
        };
        var radioSearch = BluetoothFindFirstRadio(ref radioParameters, out var radio);
        if (radioSearch == 0)
        {
            var error = (uint)Marshal.GetLastWin32Error();
            return new AirPodsControlRepairResult(
                false,
                false,
                string.Empty,
                false,
                false,
                error == 0
                    ? "No Windows Bluetooth radio was found."
                    : $"Bluetooth radio enumeration failed with error {error}.",
                []);
        }

        var candidates = new List<AirPodsControlRepairResult>();
        try
        {
            do
            {
                try
                {
                    candidates.AddRange(RepairOnRadio(radio, forceRebind));
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

        if (candidates.Count == 0)
        {
            return new AirPodsControlRepairResult(
                false,
                false,
                string.Empty,
                false,
                false,
                "No paired AirPods were found in the classic Windows Bluetooth stack.",
                []);
        }

        return candidates
            .OrderByDescending(candidate => candidate.Connected)
            .ThenByDescending(candidate => candidate.AnyProfileEnabled)
            .ThenByDescending(candidate => candidate.Changed)
            .First();
    }

    private static IEnumerable<AirPodsControlRepairResult> RepairOnRadio(
        nint radio,
        bool forceRebind)
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
        var device = NewDeviceInfo();
        var search = BluetoothFindFirstDevice(ref parameters, ref device);
        if (search == 0)
        {
            yield break;
        }

        try
        {
            do
            {
                if (device.Name.Contains("AirPods", StringComparison.OrdinalIgnoreCase))
                {
                    yield return RepairDevice(radio, ref device, forceRebind);
                }

                device = NewDeviceInfo();
            }
            while (BluetoothFindNextDevice(search, ref device));
        }
        finally
        {
            _ = BluetoothFindDeviceClose(search);
        }
    }

    private static AirPodsControlRepairResult RepairDevice(
        nint radio,
        ref BluetoothDeviceInfoNative device,
        bool forceRebind)
    {
        var installed = EnumerateInstalledServices(radio, ref device);
        var statuses = new List<AirPodsControlServiceStatus>();
        var changed = false;

        foreach (var service in new[] { AapServiceGuid, UarpServiceGuid })
        {
            var wasEnabled = installed.Contains(service);
            if (wasEnabled && !forceRebind)
            {
                statuses.Add(new AirPodsControlServiceStatus(
                    service,
                    true,
                    true,
                    ErrorSuccess));
                continue;
            }

            var serviceCopy = service;
            var disableError = ErrorSuccess;
            if (wasEnabled)
            {
                disableError = BluetoothSetServiceState(
                    radio,
                    ref device,
                    ref serviceCopy,
                    BluetoothServiceDisable);
                if (disableError is ErrorSuccess or EInvalidArg)
                {
                    // PnP removal is asynchronous on some vendor Bluetooth
                    // filters. A short gap prevents the new PDO from racing it.
                    Thread.Sleep(150);
                }
            }

            serviceCopy = service;
            var enableError = BluetoothSetServiceState(
                radio,
                ref device,
                ref serviceCopy,
                BluetoothServiceEnable);
            var enabled = enableError is ErrorSuccess or EInvalidArg;
            changed |= disableError == ErrorSuccess || enableError == ErrorSuccess;
            statuses.Add(new AirPodsControlServiceStatus(
                service,
                wasEnabled,
                enabled,
                enabled ? ErrorSuccess : enableError));
        }

        var anyEnabled = statuses.Any(status => status.Enabled);
        var address = FormatAddress(device.Address);
        var message = anyEnabled
            ? changed
                ? forceRebind
                    ? "AirPods control profiles were refreshed. Waiting for the SoundStage driver."
                    : "AirPods control profile was re-enabled. Waiting for the SoundStage driver."
                : "AirPods control profile is enabled."
            : BuildFailureMessage(statuses);

        return new AirPodsControlRepairResult(
            true,
            device.Connected,
            address,
            changed,
            anyEnabled,
            message,
            statuses);
    }

    private static HashSet<Guid> EnumerateInstalledServices(
        nint radio,
        ref BluetoothDeviceInfoNative device)
    {
        uint count = 0;
        var error = BluetoothEnumerateInstalledServices(
            radio,
            ref device,
            ref count,
            null);
        if (error != ErrorSuccess && error != ErrorMoreData)
        {
            return [];
        }

        if (count == 0)
        {
            return [];
        }

        var services = new Guid[count];
        error = BluetoothEnumerateInstalledServices(
            radio,
            ref device,
            ref count,
            services);
        return error == ErrorSuccess
            ? services.Take((int)count).ToHashSet()
            : [];
    }

    private static string BuildFailureMessage(
        IReadOnlyCollection<AirPodsControlServiceStatus> statuses)
    {
        if (statuses.All(status => status.ErrorCode == ErrorServiceDoesNotExist))
        {
            return "Windows did not expose the Apple AAP/UARP services. Open the case and reconnect the AirPods.";
        }

        var errors = string.Join(
            ", ",
            statuses.Select(status =>
                $"{GetServiceName(status.ServiceGuid)}={status.ErrorCode}"));
        return $"Windows could not enable the AirPods control profiles ({errors}).";
    }

    private static string GetServiceName(Guid service) =>
        service == AapServiceGuid ? "AAP" : "UARP";

    private static BluetoothDeviceInfoNative NewDeviceInfo() =>
        new() { Size = Marshal.SizeOf<BluetoothDeviceInfoNative>() };

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
    private struct BluetoothDeviceInfoNative
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
        ref BluetoothDeviceInfoNative device);

    [DllImport("BluetoothApis.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindNextDevice(
        nint search,
        ref BluetoothDeviceInfoNative device);

    [DllImport("BluetoothApis.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindDeviceClose(nint search);

    [DllImport("BluetoothApis.dll", SetLastError = true)]
    private static extern uint BluetoothEnumerateInstalledServices(
        nint radio,
        ref BluetoothDeviceInfoNative device,
        ref uint serviceCount,
        [Out] Guid[]? services);

    [DllImport("BluetoothApis.dll", SetLastError = true)]
    private static extern uint BluetoothSetServiceState(
        nint radio,
        ref BluetoothDeviceInfoNative device,
        ref Guid serviceGuid,
        uint serviceFlags);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}

public sealed record AirPodsControlServiceStatus(
    Guid ServiceGuid,
    bool WasEnabled,
    bool Enabled,
    uint ErrorCode);

public sealed record AirPodsControlRepairResult(
    bool AirPodsFound,
    bool Connected,
    string Address,
    bool Changed,
    bool AnyProfileEnabled,
    string Message,
    IReadOnlyList<AirPodsControlServiceStatus> Services);

public static class WindowsCodeIntegrity
{
    private const int SystemCodeIntegrityInformation = 103;
    private const uint TestSigningEnabled = 0x2;

    public static uint Options
    {
        get
        {
            var information = new SystemCodeIntegrityInformationNative
            {
                Length = (uint)Marshal.SizeOf<SystemCodeIntegrityInformationNative>(),
            };
            var status = NtQuerySystemInformation(
                SystemCodeIntegrityInformation,
                ref information,
                (int)information.Length,
                0);
            if (status < 0)
            {
                throw new Win32Exception(
                    status,
                    $"NtQuerySystemInformation failed with NTSTATUS 0x{status:X8}.");
            }

            return information.CodeIntegrityOptions;
        }
    }

    public static bool IsTestSigningActive => (Options & TestSigningEnabled) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemCodeIntegrityInformationNative
    {
        public uint Length;
        public uint CodeIntegrityOptions;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int informationClass,
        ref SystemCodeIntegrityInformationNative information,
        int informationLength,
        nint returnLength);
}
