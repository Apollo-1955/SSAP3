using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace SSAP3.Platform.Windows;

/// <summary>
/// Experimental user-mode AACP transport. Windows 10/11 may reject direct
/// L2CAP access; callers can use <see cref="TryConnect"/> as a non-writing
/// capability probe before falling back to the narrow BTHENUM profile driver.
/// </summary>
public sealed class WindowsL2CapAacpClient : IDisposable
{
    private const int AfBth = 32;
    private const int SockSeqPacket = 5;
    private const int BthProtoL2Cap = 0x0100;
    private const uint AacpPsm = 0x1001;
    private const int SocketError = -1;
    private const int SolSocket = 0xFFFF;
    private const int SoSendTimeout = 0x1005;
    private const int SoReceiveTimeout = 0x1006;

    private nuint _socket = nuint.MaxValue;
    private bool _winsockStarted;

    public bool Connected => _socket != nuint.MaxValue;

    public static AacpTransportProbeResult TryConnect(
        string bluetoothAddress,
        int timeoutMilliseconds = 2500)
    {
        var client = new WindowsL2CapAacpClient();
        try
        {
            client.Connect(bluetoothAddress, timeoutMilliseconds);
            return new AacpTransportProbeResult(
                Supported: true,
                ErrorCode: 0,
                Message: "Windows accepted a direct user-mode AACP L2CAP session.",
                Client: client);
        }
        catch (Win32Exception exception)
        {
            client.Dispose();
            return new AacpTransportProbeResult(
                Supported: false,
                ErrorCode: exception.NativeErrorCode,
                Message: exception.Message,
                Client: null);
        }
        catch (Exception exception)
        {
            client.Dispose();
            return new AacpTransportProbeResult(
                Supported: false,
                ErrorCode: 0,
                Message: exception.Message,
                Client: null);
        }
    }

    public void Connect(string bluetoothAddress, int timeoutMilliseconds = 2500)
    {
        ObjectDisposedException.ThrowIf(_winsockStarted && _socket == nuint.MaxValue, this);
        if (Connected)
        {
            throw new InvalidOperationException("The AACP transport is already connected.");
        }

        var address = ParseBluetoothAddress(bluetoothAddress);
        try
        {
            using var capabilitySocket = new Socket(
                (AddressFamily)AfBth,
                (SocketType)SockSeqPacket,
                (ProtocolType)BthProtoL2Cap);
        }
        catch (SocketException exception)
        {
            throw new Win32Exception(
                exception.NativeErrorCode,
                "Windows does not expose user-mode Bluetooth L2CAP sockets on this system.");
        }

        var wsaData = Marshal.AllocHGlobal(512);
        try
        {
            var startupResult = WSAStartup(0x0202, wsaData);
            if (startupResult != 0)
            {
                throw new Win32Exception(startupResult, "WSAStartup failed.");
            }

            _winsockStarted = true;
        }
        finally
        {
            Marshal.FreeHGlobal(wsaData);
        }

        _socket = socket(AfBth, SockSeqPacket, BthProtoL2Cap);
        if (_socket == nuint.MaxValue)
        {
            ThrowLastSocketError("Windows could not create an AF_BTH L2CAP socket.");
        }

        var timeout = timeoutMilliseconds;
        _ = setsockopt(_socket, SolSocket, SoReceiveTimeout, ref timeout, sizeof(int));
        _ = setsockopt(_socket, SolSocket, SoSendTimeout, ref timeout, sizeof(int));

        var remote = new SockAddrBth
        {
            AddressFamily = AfBth,
            BluetoothAddress = address,
            ServiceClassId = Guid.Empty,
            Port = AacpPsm,
        };

        if (connect(_socket, ref remote, Marshal.SizeOf<SockAddrBth>()) == SocketError)
        {
            ThrowLastSocketError(
                $"Windows could not open AirPods AACP L2CAP PSM 0x{AacpPsm:X4}.");
        }
    }

    public void Send(ReadOnlySpan<byte> packet)
    {
        if (!Connected)
        {
            throw new InvalidOperationException("The AACP transport is not connected.");
        }

        if (packet.IsEmpty)
        {
            throw new ArgumentException("An AACP packet cannot be empty.", nameof(packet));
        }

        var buffer = packet.ToArray();
        var sent = send(_socket, buffer, buffer.Length, 0);
        if (sent == SocketError)
        {
            ThrowLastSocketError("Sending the AACP packet failed.");
        }

        if (sent != buffer.Length)
        {
            throw new IOException($"Only {sent} of {buffer.Length} AACP bytes were sent.");
        }
    }

    public byte[] Receive(int maximumLength = 512)
    {
        if (!Connected)
        {
            throw new InvalidOperationException("The AACP transport is not connected.");
        }

        var buffer = new byte[maximumLength];
        var received = recv(_socket, buffer, buffer.Length, 0);
        if (received == SocketError)
        {
            ThrowLastSocketError("Receiving an AACP packet failed.");
        }

        return buffer.AsSpan(0, received).ToArray();
    }

    public void Dispose()
    {
        if (_socket != nuint.MaxValue)
        {
            _ = closesocket(_socket);
            _socket = nuint.MaxValue;
        }

        if (_winsockStarted)
        {
            _ = WSACleanup();
            _winsockStarted = false;
        }
    }

    private static ulong ParseBluetoothAddress(string bluetoothAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bluetoothAddress);
        var compact = bluetoothAddress.Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        if (compact.Length != 12 ||
            !ulong.TryParse(
                compact,
                System.Globalization.NumberStyles.HexNumber,
                provider: null,
                out var value))
        {
            throw new FormatException(
                "Bluetooth address must contain six hexadecimal octets.");
        }

        return value;
    }

    private static void ThrowLastSocketError(string message)
    {
        var error = WSAGetLastError();
        throw new Win32Exception(error, $"{message} Winsock error {error}.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SockAddrBth
    {
        public int AddressFamily;
        public ulong BluetoothAddress;
        public Guid ServiceClassId;
        public uint Port;
    }

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int WSAStartup(ushort requestedVersion, nint wsaData);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int WSACleanup();

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int WSAGetLastError();

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern nuint socket(int addressFamily, int socketType, int protocol);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int connect(
        nuint socketHandle,
        ref SockAddrBth socketAddress,
        int socketAddressLength);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int setsockopt(
        nuint socketHandle,
        int level,
        int optionName,
        ref int optionValue,
        int optionLength);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int send(
        nuint socketHandle,
        byte[] buffer,
        int length,
        int flags);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int recv(
        nuint socketHandle,
        byte[] buffer,
        int length,
        int flags);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int closesocket(nuint socketHandle);
}

public sealed record AacpTransportProbeResult(
    bool Supported,
    int ErrorCode,
    string Message,
    WindowsL2CapAacpClient? Client) : IDisposable
{
    public void Dispose() => Client?.Dispose();
}
