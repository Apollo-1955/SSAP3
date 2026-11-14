using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SSAP3.Core;

namespace SSAP3.Platform.Windows;

public enum DriverConnectOutcome
{
    Connected,

    /// <summary>No SSAP3 driver interface exists (driver not installed, or not loaded because it is unsigned).</summary>
    NotInstalled,

    /// <summary>A driver interface exists but speaks a different protocol version.</summary>
    Incompatible,

    /// <summary>Interfaces exist but none belongs to the requested AirPods.</summary>
    NoMatchingDevice,

    /// <summary>The matching interface refused or failed to open a session.</summary>
    Failed,
}

/// <summary>
/// User-mode client for the SSAP3 AAP transport driver (protocol v3,
/// driver/shared/ssap3_driver_protocol.h). One instance owns exactly one
/// handle and one session; dispose it to close both.
/// </summary>
public sealed class Ssap3DriverAacpClient : IDisposable
{
    public const uint ProtocolVersion = 3;

    /// <summary>Largest packet SSAP3 sends (the driver enforces the same cap).</summary>
    public const int MaxOutboundPacketLength = 512;

    /// <summary>The L2CAP receive MTU iOS uses; AirPods hide sensor services that do not fit.</summary>
    public const int MaxInboundPacketLength = 2582;

    private const uint CmGetDeviceInterfaceListPresent = 0;
    private const uint CrSuccess = 0;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint AacpCapability = 1 << 2;
    private const int StatusSize = 40;
    private const int OpenRequestSize = 24;
    private const int OpenResponseSize = 24;
    private const int SendRequestSize = 24 + MaxOutboundPacketLength;
    private const int ReadRequestSize = 24;

    // 16 + 2582 = 2598, padded by the C compiler to a multiple of 8 (the driver
    // header checks sizeof == 2600 at compile time).
    private const int ReadResponseSize = (16 + MaxInboundPacketLength + 7) & ~7;
    private const uint IoctlGetBridgeStatus = 0x00222000;
    private const uint IoctlOpenAacpChannel = 0x00222004;
    private const uint IoctlCloseAacpChannel = 0x00222008;
    private const uint IoctlSendAacpPacket = 0x0022200C;
    private const uint IoctlReadAacpPacket = 0x00222010;

    /// <summary>{1C3A8128-3668-4428-95A4-B1CAD51B7249}: must match the INF and driver.c.</summary>
    public static readonly Guid DriverInterfaceGuid = new("1C3A8128-3668-4428-95A4-B1CAD51B7249");

    private readonly SafeFileHandle _device;
    private readonly object _ioSync = new();
    private ulong _sessionId;
    private bool _disposed;

    private Ssap3DriverAacpClient(SafeFileHandle device, string devicePath, ulong sessionId, uint maximumPacketSize)
    {
        _device = device;
        DevicePath = devicePath;
        _sessionId = sessionId;
        MaximumPacketSize = maximumPacketSize;
    }

    public string DevicePath { get; }

    public uint MaximumPacketSize { get; }

    /// <summary>True when at least one SSAP3 driver interface is present.</summary>
    public static bool IsDriverPresent => EnumerateDevicePaths().Count > 0;

    public static Ssap3DriverConnectionResult TryConnect(string bluetoothAddress)
    {
        if (!BluetoothCryptography.TryParseMacAddress(bluetoothAddress, out var octets))
        {
            return new(DriverConnectOutcome.Failed, "The Bluetooth address is not valid.", null);
        }

        var requested = octets.Aggregate(0UL, (value, octet) => (value << 8) | octet);
        var paths = EnumerateDevicePaths();
        if (paths.Count == 0)
        {
            return new(DriverConnectOutcome.NotInstalled, "The SSAP3 AAP driver is not installed or not loaded.", null);
        }

        var sawIncompatible = false;
        var errors = new List<string>();
        foreach (var path in paths)
        {
            var handle = CreateFileW(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
            if (handle.IsInvalid)
            {
                errors.Add($"CreateFile error {Marshal.GetLastWin32Error()}");
                handle.Dispose();
                continue;
            }

            try
            {
                var status = IoControl(handle, IoctlGetBridgeStatus, null, StatusSize);
                var protocolVersion = BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(4));
                var capabilities = BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(8));
                var maximumPacketSize = BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(12));
                var remoteAddress = BinaryPrimitives.ReadUInt64LittleEndian(status.AsSpan(32));
                if (protocolVersion != ProtocolVersion)
                {
                    sawIncompatible = true;
                    handle.Dispose();
                    continue;
                }

                if (remoteAddress != requested)
                {
                    handle.Dispose();
                    continue;
                }

                if ((capabilities & AacpCapability) == 0)
                {
                    errors.Add("The driver is loaded but the AAP service is not usable yet.");
                    handle.Dispose();
                    continue;
                }

                var request = new byte[OpenRequestSize];
                BinaryPrimitives.WriteUInt32LittleEndian(request, OpenRequestSize);
                Encoding.ASCII.GetBytes(FormatAddress(requested)).CopyTo(request, 4);
                var response = IoControl(handle, IoctlOpenAacpChannel, request, OpenResponseSize);
                var sessionId = BinaryPrimitives.ReadUInt64LittleEndian(response.AsSpan(16));
                if (sessionId == 0)
                {
                    throw new IOException("The driver returned an empty session id.");
                }

                return new(
                    DriverConnectOutcome.Connected,
                    "AACP session open.",
                    new Ssap3DriverAacpClient(handle, path, sessionId, Math.Min(maximumPacketSize, (uint)MaxInboundPacketLength)));
            }
            catch (Exception exception) when (exception is Win32Exception or IOException)
            {
                errors.Add(exception.Message);
                handle.Dispose();
            }
        }

        if (errors.Count > 0)
        {
            return new(DriverConnectOutcome.Failed, string.Join(" ", errors.Distinct()), null);
        }

        return sawIncompatible
            ? new(DriverConnectOutcome.Incompatible, "The installed SSAP3 driver uses a different protocol version.", null)
            : new(DriverConnectOutcome.NoMatchingDevice, "No SSAP3 driver instance belongs to these AirPods.", null);
    }

    public void Send(ReadOnlySpan<byte> packet)
    {
        if (packet.IsEmpty || packet.Length > MaxOutboundPacketLength)
        {
            throw new ArgumentOutOfRangeException(nameof(packet), $"AACP packets must be 1-{MaxOutboundPacketLength} bytes.");
        }

        var request = new byte[SendRequestSize];
        BinaryPrimitives.WriteUInt32LittleEndian(request, SendRequestSize);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), (uint)packet.Length);
        packet.CopyTo(request.AsSpan(24));
        lock (_ioSync)
        {
            ThrowIfClosed();
            BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(16), _sessionId);
            _ = IoControl(_device, IoctlSendAacpPacket, request, 0);
        }
    }

    /// <summary>Returns the next queued packet, or an empty packet when none is waiting.</summary>
    public ReceivedPacket Receive()
    {
        var request = new byte[ReadRequestSize];
        BinaryPrimitives.WriteUInt32LittleEndian(request, ReadRequestSize);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), MaxInboundPacketLength);
        byte[] response;
        lock (_ioSync)
        {
            ThrowIfClosed();
            BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(16), _sessionId);
            response = IoControl(_device, IoctlReadAacpPacket, request, ReadResponseSize);
        }

        var packetLength = BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(4));
        if (packetLength > MaxInboundPacketLength)
        {
            throw new IOException("The driver returned an oversized AACP packet.");
        }

        var receiveTime = BinaryPrimitives.ReadUInt64LittleEndian(response.AsSpan(8));
        return new ReceivedPacket(response.AsSpan(16, (int)packetLength).ToArray(), receiveTime);
    }

    public void Dispose()
    {
        lock (_ioSync)
        {
            if (_disposed)
            {
                return;
            }

            if (_sessionId != 0 && !_device.IsInvalid && !_device.IsClosed)
            {
                try
                {
                    var request = new byte[16];
                    BinaryPrimitives.WriteUInt32LittleEndian(request, 16);
                    BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(8), _sessionId);
                    _ = IoControl(_device, IoctlCloseAacpChannel, request, 0);
                }
                catch (Exception exception) when (exception is Win32Exception or IOException)
                {
                    // Device removal and disconnects are normal at shutdown; the
                    // driver also closes every session of this handle on cleanup.
                }

                _sessionId = 0;
            }

            _device.Dispose();
            _disposed = true;
        }
    }

    private void ThrowIfClosed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sessionId == 0)
        {
            throw new InvalidOperationException("The AACP session is closed.");
        }
    }

    private static string FormatAddress(ulong address) =>
        string.Join(':', Enumerable.Range(0, 6).Select(index =>
            ((byte)(address >> (8 * (5 - index)))).ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));

    private static List<string> EnumerateDevicePaths()
    {
        var guid = DriverInterfaceGuid;
        if (CM_Get_Device_Interface_List_SizeW(out var requiredCharacters, ref guid, null, CmGetDeviceInterfaceListPresent) != CrSuccess ||
            requiredCharacters <= 1 || requiredCharacters > 1 << 20)
        {
            return [];
        }

        var buffer = new char[requiredCharacters];
        if (CM_Get_Device_Interface_ListW(ref guid, null, buffer, requiredCharacters, CmGetDeviceInterfaceListPresent) != CrSuccess)
        {
            return [];
        }

        return [.. new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    private static byte[] IoControl(SafeFileHandle device, uint controlCode, byte[]? input, int outputLength)
    {
        var output = outputLength == 0 ? null : new byte[outputLength];
        if (!DeviceIoControl(device, controlCode, input, input?.Length ?? 0, output, output?.Length ?? 0, out var bytesReturned, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"SSAP3 driver request 0x{controlCode:X8} failed.");
        }

        if (output is not null && (bytesReturned < 0 || bytesReturned > output.Length))
        {
            throw new IOException("The SSAP3 driver returned an invalid response length.");
        }

        return output ?? [];
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_List_SizeW(out uint bufferLength, ref Guid interfaceClassGuid, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_ListW(ref Guid interfaceClassGuid, string? deviceId, [Out] char[] buffer, uint bufferLength, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, byte[]? inputBuffer, int inputBufferSize, [Out] byte[]? outputBuffer, int outputBufferSize, out int bytesReturned, nint overlapped);
}

/// <param name="Data">Packet bytes (empty when the queue was empty).</param>
/// <param name="ReceiveInterruptTime">Kernel receive time (KeQueryInterruptTime, 100 ns units).</param>
public readonly record struct ReceivedPacket(byte[] Data, ulong ReceiveInterruptTime)
{
    public bool IsEmpty => Data.Length == 0;
}

public sealed record Ssap3DriverConnectionResult(
    DriverConnectOutcome Outcome,
    string Message,
    Ssap3DriverAacpClient? Client) : IDisposable
{
    public bool Connected => Outcome == DriverConnectOutcome.Connected && Client is not null;

    public void Dispose() => Client?.Dispose();
}
