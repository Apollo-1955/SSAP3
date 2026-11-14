using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace SSAP3.Core.Motion.Companion;

public enum CompanionSampleKind : byte
{
    /// <summary>Head orientation already in SSAP3's head frame (for example from Apple's headphone-motion API).</summary>
    HeadOrientation = 1,

    /// <summary>The AirPods' raw sensor-to-world attitude (Z up), decoded by SSAP3 like the AAP stream.</summary>
    AirPodsAttitude = 2,
}

[Flags]
public enum CompanionSampleFlags : byte
{
    None = 0,

    /// <summary>The orientation includes pitch and roll (head-orientation samples only).</summary>
    PitchRoll = 1,

    /// <summary>The sender generated the data. SSAP3 then labels tracking as simulated.</summary>
    Simulated = 2,
}

/// <param name="TimestampMicroseconds">The companion's own monotonic clock.</param>
public readonly record struct CompanionSample(
    CompanionSampleKind Kind,
    CompanionSampleFlags Flags,
    ulong TimestampMicroseconds,
    Quaternion Orientation,
    float RotationRateDegreesPerSecond);

/// <summary>
/// SSAP3 companion motion protocol, version 1 (docs/COMPANION_PROTOCOL.md).
/// It runs over TCP. SSAP3 is always the client, so SSAP3 never listens and
/// needs no inbound firewall rule.
///
/// <code>
/// Client hello  (40 B)  "S3MC" | version 1 | 3 reserved | client nonce (32)
/// Server hello  (72 B)  "S3MC" | version 1 | flags 0 | 2 reserved | server nonce (32) |
///                       HMAC-SHA256(K, "SSAP3 companion server proof v1" | cnonce | snonce)
/// Client proof  (32 B)  HMAC-SHA256(K, "SSAP3 companion client proof v1" | snonce | cnonce)
/// Session key           HKDF-SHA256(K, salt = cnonce | snonce, info = "SSAP3 companion motion key v1", 32 B)
/// Record        (62 B)  u16 length (60) | u64 counter | AES-256-GCM(payload 36 B) | tag (16)
///                       nonce = "S3M1" | counter, AAD = length | counter; the counter strictly increases
/// Payload       (36 B)  kind | flags | 2 reserved | u64 timestamp (µs) | quaternion x, y, z, w (f32) |
///                       rotation rate (deg/s, f32) | 4 reserved
/// </code>
/// K is the 32-byte pairing key, which is shared once as a pairing code.
/// Everything is little-endian.
/// </summary>
public static class CompanionProtocol
{
    public const byte Version = 1;
    public const int KeyLength = 32;
    public const int NonceLength = 32;
    public const int ProofLength = 32;
    public const int ClientHelloLength = 40;
    public const int ServerHelloLength = 72;
    public const int PayloadLength = 36;
    public const int RecordBodyLength = 8 + PayloadLength + 16;
    public const int RecordLength = 2 + RecordBodyLength;
    public const int DefaultPort = 47820;
    public const float MaximumRotationRate = 10_000f;

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private static ReadOnlySpan<byte> Magic => "S3MC"u8;

    private static ReadOnlySpan<byte> NoncePrefix => "S3M1"u8;

    private static ReadOnlySpan<byte> ServerProofLabel => "SSAP3 companion server proof v1"u8;

    private static ReadOnlySpan<byte> ClientProofLabel => "SSAP3 companion client proof v1"u8;

    private static ReadOnlySpan<byte> KeyInfo => "SSAP3 companion motion key v1"u8;

    public static byte[] CreatePairingKey() => RandomNumberGenerator.GetBytes(KeyLength);

    public static byte[] CreateNonce() => RandomNumberGenerator.GetBytes(NonceLength);

    public static byte[] CreateClientHello(ReadOnlySpan<byte> clientNonce)
    {
        RequireLength(clientNonce, NonceLength, nameof(clientNonce));
        var hello = new byte[ClientHelloLength];
        Magic.CopyTo(hello);
        hello[4] = Version;
        clientNonce.CopyTo(hello.AsSpan(8));
        return hello;
    }

    public static bool TryParseClientHello(ReadOnlySpan<byte> data, Span<byte> clientNonce)
    {
        if (data.Length != ClientHelloLength || clientNonce.Length != NonceLength ||
            !data[..4].SequenceEqual(Magic) || data[4] != Version || data[5] != 0 || data[6] != 0 || data[7] != 0)
        {
            return false;
        }

        data.Slice(8, NonceLength).CopyTo(clientNonce);
        return true;
    }

    public static byte[] CreateServerHello(ReadOnlySpan<byte> key, ReadOnlySpan<byte> clientNonce, ReadOnlySpan<byte> serverNonce)
    {
        RequireLength(key, KeyLength, nameof(key));
        RequireLength(clientNonce, NonceLength, nameof(clientNonce));
        RequireLength(serverNonce, NonceLength, nameof(serverNonce));
        var hello = new byte[ServerHelloLength];
        Magic.CopyTo(hello);
        hello[4] = Version;
        serverNonce.CopyTo(hello.AsSpan(8));
        Proof(key, ServerProofLabel, clientNonce, serverNonce, hello.AsSpan(40, ProofLength));
        return hello;
    }

    /// <summary>Checks the server hello and its proof of the pairing key (constant-time).</summary>
    public static bool TryVerifyServerHello(ReadOnlySpan<byte> key, ReadOnlySpan<byte> clientNonce, ReadOnlySpan<byte> data, Span<byte> serverNonce)
    {
        if (key.Length != KeyLength || clientNonce.Length != NonceLength || serverNonce.Length != NonceLength ||
            data.Length != ServerHelloLength || !data[..4].SequenceEqual(Magic) || data[4] != Version || data[5] != 0 || data[6] != 0 || data[7] != 0)
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[ProofLength];
        Proof(key, ServerProofLabel, clientNonce, data.Slice(8, NonceLength), expected);
        if (!CryptographicOperations.FixedTimeEquals(expected, data.Slice(40, ProofLength)))
        {
            return false;
        }

        data.Slice(8, NonceLength).CopyTo(serverNonce);
        return true;
    }

    public static byte[] CreateClientProof(ReadOnlySpan<byte> key, ReadOnlySpan<byte> clientNonce, ReadOnlySpan<byte> serverNonce)
    {
        RequireLength(key, KeyLength, nameof(key));
        var proof = new byte[ProofLength];
        Proof(key, ClientProofLabel, serverNonce, clientNonce, proof);
        return proof;
    }

    public static bool VerifyClientProof(ReadOnlySpan<byte> key, ReadOnlySpan<byte> clientNonce, ReadOnlySpan<byte> serverNonce, ReadOnlySpan<byte> proof)
    {
        if (key.Length != KeyLength || proof.Length != ProofLength)
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[ProofLength];
        Proof(key, ClientProofLabel, serverNonce, clientNonce, expected);
        return CryptographicOperations.FixedTimeEquals(expected, proof);
    }

    public static byte[] DeriveSessionKey(ReadOnlySpan<byte> key, ReadOnlySpan<byte> clientNonce, ReadOnlySpan<byte> serverNonce)
    {
        RequireLength(key, KeyLength, nameof(key));
        Span<byte> salt = stackalloc byte[NonceLength * 2];
        clientNonce.CopyTo(salt);
        serverNonce.CopyTo(salt[NonceLength..]);
        var sessionKey = new byte[KeyLength];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, key, sessionKey, salt, KeyInfo);
        return sessionKey;
    }

    public static byte[] SealRecord(AesGcm aes, ulong counter, in CompanionSample sample)
    {
        ArgumentNullException.ThrowIfNull(aes);
        var record = new byte[RecordLength];
        BinaryPrimitives.WriteUInt16LittleEndian(record, RecordBodyLength);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(2), counter);
        Span<byte> payload = stackalloc byte[PayloadLength];
        payload.Clear();
        payload[0] = (byte)sample.Kind;
        payload[1] = (byte)sample.Flags;
        BinaryPrimitives.WriteUInt64LittleEndian(payload[4..], sample.TimestampMicroseconds);
        BinaryPrimitives.WriteSingleLittleEndian(payload[12..], sample.Orientation.X);
        BinaryPrimitives.WriteSingleLittleEndian(payload[16..], sample.Orientation.Y);
        BinaryPrimitives.WriteSingleLittleEndian(payload[20..], sample.Orientation.Z);
        BinaryPrimitives.WriteSingleLittleEndian(payload[24..], sample.Orientation.W);
        BinaryPrimitives.WriteSingleLittleEndian(payload[28..], sample.RotationRateDegreesPerSecond);
        Span<byte> nonce = stackalloc byte[12];
        BuildNonce(counter, nonce);
        aes.Encrypt(nonce, payload, record.AsSpan(10, PayloadLength), record.AsSpan(10 + PayloadLength, 16), record.AsSpan(0, 10));
        return record;
    }

    /// <summary>
    /// Authenticates, decrypts and validates one record. Returns false for any
    /// tampering, a wrong length, or a payload outside the protocol's ranges.
    /// </summary>
    public static bool TryOpenRecord(AesGcm aes, ReadOnlySpan<byte> record, out ulong counter, out CompanionSample sample)
    {
        ArgumentNullException.ThrowIfNull(aes);
        counter = 0;
        sample = default;
        if (record.Length != RecordLength || BinaryPrimitives.ReadUInt16LittleEndian(record) != RecordBodyLength)
        {
            return false;
        }

        counter = BinaryPrimitives.ReadUInt64LittleEndian(record[2..]);
        Span<byte> nonce = stackalloc byte[12];
        BuildNonce(counter, nonce);
        Span<byte> payload = stackalloc byte[PayloadLength];
        try
        {
            aes.Decrypt(nonce, record.Slice(10, PayloadLength), record.Slice(10 + PayloadLength, 16), payload, record[..10]);
        }
        catch (AuthenticationTagMismatchException)
        {
            return false;
        }

        var kind = (CompanionSampleKind)payload[0];
        var flags = (CompanionSampleFlags)payload[1];
        var orientation = new Quaternion(
            BinaryPrimitives.ReadSingleLittleEndian(payload[12..]),
            BinaryPrimitives.ReadSingleLittleEndian(payload[16..]),
            BinaryPrimitives.ReadSingleLittleEndian(payload[20..]),
            BinaryPrimitives.ReadSingleLittleEndian(payload[24..]));
        var rate = BinaryPrimitives.ReadSingleLittleEndian(payload[28..]);
        var length = QuaternionMath.IsFinite(orientation) ? orientation.Length() : float.NaN;
        if (kind is not (CompanionSampleKind.HeadOrientation or CompanionSampleKind.AirPodsAttitude) ||
            (flags & ~(CompanionSampleFlags.PitchRoll | CompanionSampleFlags.Simulated)) != 0 ||
            payload[2] != 0 || payload[3] != 0 ||
            !(length is >= 0.98f and <= 1.02f) ||
            !(float.IsFinite(rate) && rate is >= 0f and <= MaximumRotationRate))
        {
            return false;
        }

        sample = new CompanionSample(kind, flags, BinaryPrimitives.ReadUInt64LittleEndian(payload[4..]), Quaternion.Normalize(orientation), rate);
        return true;
    }

    /// <summary>The pairing key as 52 Base32 characters in groups of four.</summary>
    public static string FormatPairingCode(ReadOnlySpan<byte> key)
    {
        RequireLength(key, KeyLength, nameof(key));
        var text = new StringBuilder();
        var buffer = 0;
        var bits = 0;
        var count = 0;
        foreach (var value in key)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return text.ToString();

        void Append(char c)
        {
            if (count > 0 && count % 4 == 0)
            {
                text.Append('-');
            }

            text.Append(c);
            count++;
        }
    }

    public static bool TryParsePairingCode(string? text, out byte[] key)
    {
        key = [];
        if (string.IsNullOrWhiteSpace(text) || text.Length > 128)
        {
            return false;
        }

        var bytes = new List<byte>(KeyLength);
        var buffer = 0;
        var bits = 0;
        var symbols = 0;
        foreach (var raw in text)
        {
            if (raw is '-' or ' ')
            {
                continue;
            }

            var index = Base32Alphabet.IndexOf(char.ToUpperInvariant(raw), StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            symbols++;
            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }

        if (symbols != 52 || bytes.Count != KeyLength)
        {
            return false;
        }

        key = [.. bytes];
        return true;
    }

    /// <summary>
    /// SSAP3 connects only to this PC, the local network or a private overlay:
    /// loopback, RFC 1918, link-local, carrier-grade NAT (100.64.0.0/10, used
    /// by Tailscale), and IPv6 unique-local and link-local addresses.
    /// </summary>
    public static bool IsAllowedAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10 ||
                (b[0] == 172 && b[1] is >= 16 and <= 31) ||
                (b[0] == 192 && b[1] == 168) ||
                (b[0] == 169 && b[1] == 254) ||
                (b[0] == 100 && b[1] is >= 64 and <= 127);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return address.IsIPv6LinkLocal || (b[0] & 0xFE) == 0xFC;
        }

        return false;
    }

    private static void Proof(ReadOnlySpan<byte> key, ReadOnlySpan<byte> label, ReadOnlySpan<byte> first, ReadOnlySpan<byte> second, Span<byte> destination)
    {
        Span<byte> message = stackalloc byte[label.Length + first.Length + second.Length];
        label.CopyTo(message);
        first.CopyTo(message[label.Length..]);
        second.CopyTo(message[(label.Length + first.Length)..]);
        HMACSHA256.HashData(key, message, destination);
    }

    private static void BuildNonce(ulong counter, Span<byte> nonce)
    {
        NoncePrefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], counter);
    }

    private static void RequireLength(ReadOnlySpan<byte> value, int length, string name)
    {
        if (value.Length != length)
        {
            throw new ArgumentException($"Expected {length} bytes.", name);
        }
    }
}
