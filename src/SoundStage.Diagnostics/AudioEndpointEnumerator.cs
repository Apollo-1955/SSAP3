using System.Runtime.InteropServices;

namespace SoundStage.Diagnostics;

internal static class AudioEndpointEnumerator
{
    private const uint DeviceStateAll = 0x0000000F;
    private static readonly PropertyKey FriendlyNameKey =
        new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    public static IReadOnlyList<AudioEndpointInfo> Enumerate()
    {
        var type = Type.GetTypeFromCLSID(
            new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"),
            throwOnError: true)!;
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(type)!;

        try
        {
            var defaults = ReadDefaults(enumerator);
            var endpoints = new List<AudioEndpointInfo>();
            ReadFlow(enumerator, DataFlow.Render, defaults, endpoints);
            ReadFlow(enumerator, DataFlow.Capture, defaults, endpoints);
            return endpoints.OrderBy(endpoint => endpoint.Flow).ThenBy(endpoint => endpoint.Name).ToArray();
        }
        finally
        {
            _ = Marshal.FinalReleaseComObject(enumerator);
        }
    }

    private static Dictionary<string, List<string>> ReadDefaults(IMMDeviceEnumerator enumerator)
    {
        var defaults = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
        {
            foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
            {
                if (enumerator.GetDefaultAudioEndpoint(flow, role, out var device) != 0 || device is null)
                {
                    continue;
                }

                try
                {
                    _ = device.GetId(out var id);
                    if (!defaults.TryGetValue(id, out var roles))
                    {
                        roles = [];
                        defaults[id] = roles;
                    }

                    roles.Add(role.ToString());
                }
                finally
                {
                    _ = Marshal.FinalReleaseComObject(device);
                }
            }
        }

        return defaults;
    }

    private static void ReadFlow(
        IMMDeviceEnumerator enumerator,
        DataFlow flow,
        IReadOnlyDictionary<string, List<string>> defaults,
        ICollection<AudioEndpointInfo> endpoints)
    {
        Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(flow, DeviceStateAll, out var collection));
        try
        {
            Marshal.ThrowExceptionForHR(collection.GetCount(out var count));
            for (uint index = 0; index < count; index++)
            {
                Marshal.ThrowExceptionForHR(collection.Item(index, out var device));
                try
                {
                    Marshal.ThrowExceptionForHR(device.GetId(out var id));
                    Marshal.ThrowExceptionForHR(device.GetState(out var state));
                    var name = ReadFriendlyName(device);
                    endpoints.Add(new AudioEndpointInfo(
                        id,
                        name,
                        flow.ToString(),
                        state,
                        defaults.GetValueOrDefault(id) ?? []));
                }
                finally
                {
                    _ = Marshal.FinalReleaseComObject(device);
                }
            }
        }
        finally
        {
            _ = Marshal.FinalReleaseComObject(collection);
        }
    }

    private static string ReadFriendlyName(IMMDevice device)
    {
        Marshal.ThrowExceptionForHR(device.OpenPropertyStore(StorageAccessMode.Read, out var store));
        try
        {
            var key = FriendlyNameKey;
            Marshal.ThrowExceptionForHR(store.GetValue(ref key, out var value));
            try
            {
                return value.GetString() ?? "Unnamed endpoint";
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            _ = Marshal.FinalReleaseComObject(store);
        }
    }

    private enum DataFlow
    {
        Render,
        Capture,
        All,
    }

    private enum Role
    {
        Console,
        Multimedia,
        Communications,
    }

    private enum StorageAccessMode
    {
        Read,
        Write,
        ReadWrite,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        private readonly ushort valueType;

        [FieldOffset(8)]
        private readonly IntPtr pointerValue;

        public string? GetString() =>
            valueType == 31 ? Marshal.PtrToStringUni(pointerValue) : null;
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(
            DataFlow flow,
            uint stateMask,
            [MarshalAs(UnmanagedType.Interface)] out IMMDeviceCollection devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(
            DataFlow flow,
            Role role,
            [MarshalAs(UnmanagedType.Interface)] out IMMDevice? device);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Item(uint index, [MarshalAs(UnmanagedType.Interface)] out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid interfaceId, uint context, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);

        [PreserveSig]
        int OpenPropertyStore(StorageAccessMode accessMode, out IPropertyStore properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out uint state);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant variant);
}
