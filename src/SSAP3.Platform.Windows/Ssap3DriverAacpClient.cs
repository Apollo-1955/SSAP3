using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SSAP3.Platform.Windows;

public sealed class Ssap3DriverAacpClient : IDisposable
{
    private const uint CmGetDeviceInterfaceListPresent = 0;
    private const uint CrSuccess = 0;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint AacpCapability = 1 << 2;
    private const int MaxPacketLength = 512;
    private const uint IoctlGetBridgeStatus = 0x00222000;
    private const uint IoctlOpenAacpChannel = 0x00222004;
    private const uint IoctlCloseAacpChannel = 0x00222008;
    private const uint IoctlSendAacpPacket = 0x0022200C;
    private const uint IoctlReadAacpPacket = 0x00222010;

    private static readonly Guid DriverInterfaceGuid =
        new("3E98D13A-B2A5-4C3D-AE1E-8DB4F3D8C1F0");

    private readonly SafeFileHandle _device;
    private ulong _sessionId;
    private bool _disposed;

    private Ssap3DriverAacpClient(
        SafeFileHandle device,
        string devicePath,
        ulong sessionId,
        uint maximumPacketSize)
    {
        _device = device;
        DevicePath = devicePath;
        _sessionId = sessionId;
        MaximumPacketSize = maximumPacketSize;
    }

    public string DevicePath { get; }

    public uint MaximumPacketSize { get; }

    public static Ssap3DriverConnectionResult TryConnect(string bluetoothAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bluetoothAddress);
        var errors = new List<string>();

        foreach (var path in EnumerateDevicePaths())
        {
            var handle = CreateFileW(
                path,
                GenericRead | GenericWrite,
                FileShareRead | FileShareWrite,
                0,
                OpenExisting,
                0,
                0);
            if (handle.IsInvalid)
            {
                errors.Add($"{path}: CreateFile error {Marshal.GetLastWin32Error()}");
                handle.Dispose();
                continue;
            }

            try
            {
                var statusBuffer = IoControl(handle, IoctlGetBridgeStatus, null, 32);
                var protocolVersion = BinaryPrimitives.ReadUInt32LittleEndian(statusBuffer.AsSpan(4));
                var capabilities = BinaryPrimitives.ReadUInt32LittleEndian(statusBuffer.AsSpan(8));
                var maximumPacketSize =
                    BinaryPrimitives.ReadUInt32LittleEndian(statusBuffer.AsSpan(12));
                var lastError = BinaryPrimitives.ReadUInt32LittleEndian(statusBuffer.AsSpan(16));
                var initializationFlags =
                    BinaryPrimitives.ReadUInt32LittleEndian(statusBuffer.AsSpan(20));
                var initializationResult =
                    BinaryPrimitives.ReadUInt64LittleEndian(statusBuffer.AsSpan(24));
                var initializationStage = (uint)(initializationResult >> 32);
                var initializationStatus = (uint)initializationResult;
                if (protocolVersion != 1 || (capabilities & AacpCapability) == 0)
                {
                    errors.Add(
                        $"{path}: incompatible driver protocol/capabilities " +
                        $"({protocolVersion}, 0x{capabilities:X8}); " +
                        $"lastError={lastError}; initFlags=0x{initializationFlags:X8}; " +
                        $"initStage={initializationStage}; initStatus=0x{initializationStatus:X8}");
                    handle.Dispose();
                    continue;
                }

                var request = new byte[24];
                BinaryPrimitives.WriteUInt32LittleEndian(request, (uint)request.Length);
                var addressBytes = Encoding.ASCII.GetBytes(bluetoothAddress);
                if (addressBytes.Length > 17)
                {
                    throw new FormatException("The Bluetooth address is too long.");
                }

                addressBytes.CopyTo(request, 4);
                var response = IoControl(handle, IoctlOpenAacpChannel, request, 24);
                var sessionId = BinaryPrimitives.ReadUInt64LittleEndian(response.AsSpan(16));
                if (sessionId == 0)
                {
                    throw new IOException("The SSAP3 driver returned an empty AACP session id.");
                }

                return new Ssap3DriverConnectionResult(
                    Connected: true,
                    Message: "SSAP3 AACP profile driver connected.",
                    Client: new Ssap3DriverAacpClient(
                        handle,
                        path,
                        sessionId,
                        maximumPacketSize));
            }
            catch (Exception exception)
            {
                errors.Add($"{path}: {exception.Message}");
                handle.Dispose();
            }
        }

        return new Ssap3DriverConnectionResult(
            Connected: false,
            Message: errors.Count == 0
                ? "SSAP3 AACP profile driver is not installed."
                : string.Join(Environment.NewLine, errors),
            Client: null);
    }

    public void Send(ReadOnlySpan<byte> packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sessionId == 0)
        {
            throw new InvalidOperationException("The driver AACP session is closed.");
        }

        if (packet.IsEmpty || packet.Length > MaxPacketLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(packet),
                $"AACP packet length must be between 1 and {MaxPacketLength} bytes.");
        }

        var request = new byte[536];
        BinaryPrimitives.WriteUInt32LittleEndian(request, (uint)request.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), (uint)packet.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(16), _sessionId);
        packet.CopyTo(request.AsSpan(24));
        _ = IoControl(_device, IoctlSendAacpPacket, request, 0);
    }

    public byte[] Receive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sessionId == 0)
        {
            throw new InvalidOperationException("The driver AACP session is closed.");
        }

        var request = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(request, (uint)request.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), MaxPacketLength);
        BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(16), _sessionId);
        var response = IoControl(_device, IoctlReadAacpPacket, request, 528);
        var packetLength = BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(4));
        if (packetLength > MaxPacketLength)
        {
            throw new IOException("The SSAP3 driver returned an oversized AACP packet.");
        }

        return response.AsSpan(16, (int)packetLength).ToArray();
    }

    public void Dispose()
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
                BinaryPrimitives.WriteUInt32LittleEndian(request, (uint)request.Length);
                BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(8), _sessionId);
                _ = IoControl(_device, IoctlCloseAacpChannel, request, 0);
            }
            catch
            {
                // Device removal and Bluetooth disconnect are normal during shutdown.
            }

            _sessionId = 0;
        }

        _device.Dispose();
        _disposed = true;
    }

    private static IReadOnlyList<string> EnumerateDevicePaths()
    {
        var guid = DriverInterfaceGuid;
        var result = CM_Get_Device_Interface_List_SizeW(
            out var requiredCharacters,
            ref guid,
            null,
            CmGetDeviceInterfaceListPresent);
        if (result != CrSuccess || requiredCharacters <= 1)
        {
            return [];
        }

        var buffer = new char[requiredCharacters];
        result = CM_Get_Device_Interface_ListW(
            ref guid,
            null,
            buffer,
            requiredCharacters,
            CmGetDeviceInterfaceListPresent);
        if (result != CrSuccess)
        {
            return [];
        }

        return new string(buffer)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static byte[] IoControl(
        SafeFileHandle device,
        uint controlCode,
        byte[]? input,
        int outputLength)
    {
        var output = outputLength == 0 ? null : new byte[outputLength];
        if (!DeviceIoControl(
                device,
                controlCode,
                input,
                input?.Length ?? 0,
                output,
                output?.Length ?? 0,
                out var bytesReturned,
                0))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"SSAP3 driver IOCTL 0x{controlCode:X8} failed.");
        }

        if (output is not null && bytesReturned > output.Length)
        {
            throw new IOException("The SSAP3 driver returned an oversized response.");
        }

        return output ?? [];
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_List_SizeW(
        out uint bufferLength,
        ref Guid interfaceClassGuid,
        string? deviceId,
        uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_ListW(
        ref Guid interfaceClassGuid,
        string? deviceId,
        [Out] char[] buffer,
        uint bufferLength,
        uint flags);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        byte[]? inputBuffer,
        int inputBufferSize,
        [Out] byte[]? outputBuffer,
        int outputBufferSize,
        out int bytesReturned,
        nint overlapped);
}

public sealed record Ssap3DriverConnectionResult(
    bool Connected,
    string Message,
    Ssap3DriverAacpClient? Client) : IDisposable
{
    public void Dispose() => Client?.Dispose();
}
