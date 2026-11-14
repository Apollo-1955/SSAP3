using System.Buffers.Binary;
using System.Text;
using SSAP3.Core.Aacp;

namespace SSAP3.Core.Motion;

/// <summary>One protobuf field. <see cref="Bytes"/> is empty for varint fields.</summary>
public readonly ref struct ProtobufField
{
    public ProtobufField(int number, int wireType, ulong varint, ReadOnlySpan<byte> bytes)
    {
        Number = number;
        WireType = wireType;
        Varint = varint;
        Bytes = bytes;
    }

    public int Number { get; }

    public int WireType { get; }

    public ulong Varint { get; }

    public ReadOnlySpan<byte> Bytes { get; }
}

/// <summary>
/// Minimal protobuf reader for the untrusted RTBuddy messages AirPods send.
/// It never throws and never allocates. It stops at the first malformed field
/// (an overlong or truncated varint, field number 0, an unsupported wire type,
/// or a length past the end) and reports that through <see cref="Malformed"/>.
/// </summary>
public ref struct ProtobufReader
{
    private const int MaximumVarintBytes = 10;
    private readonly ReadOnlySpan<byte> _data;
    private int _offset;

    public ProtobufReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _offset = 0;
        Malformed = false;
    }

    /// <summary>True when reading stopped on malformed data rather than at a clean end.</summary>
    public bool Malformed { get; private set; }

    public bool TryRead(out ProtobufField field)
    {
        field = default;
        if (Malformed || _offset >= _data.Length)
        {
            return false;
        }

        if (!TryReadVarint(out var key))
        {
            return Fail();
        }

        var number = key >> 3;
        var wireType = (int)(key & 0x07);
        if (number is 0 or > 536_870_911)
        {
            return Fail();
        }

        switch (wireType)
        {
            case 0:
                if (!TryReadVarint(out var value))
                {
                    return Fail();
                }

                field = new ProtobufField((int)number, wireType, value, default);
                return true;
            case 1:
                return TryReadFixed((int)number, wireType, 8, out field);
            case 2:
                if (!TryReadVarint(out var length) || length > (ulong)(_data.Length - _offset))
                {
                    return Fail();
                }

                var bytes = _data.Slice(_offset, (int)length);
                _offset += (int)length;
                field = new ProtobufField((int)number, wireType, 0, bytes);
                return true;
            case 5:
                return TryReadFixed((int)number, wireType, 4, out field);
            default:
                return Fail();
        }
    }

    private bool Fail()
    {
        Malformed = true;
        return false;
    }

    private bool TryReadFixed(int number, int wireType, int length, out ProtobufField field)
    {
        field = default;
        if (_data.Length - _offset < length)
        {
            return Fail();
        }

        field = new ProtobufField(number, wireType, 0, _data.Slice(_offset, length));
        _offset += length;
        return true;
    }

    private bool TryReadVarint(out ulong value)
    {
        value = 0;
        for (var index = 0; index < MaximumVarintBytes; index++)
        {
            if (_offset >= _data.Length)
            {
                return false;
            }

            var current = _data[_offset++];
            value |= (ulong)(current & 0x7F) << (7 * index);
            if ((current & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>A parsed SensorDataWX message (RTBuddy descriptor 0x00100000).</summary>
public readonly ref struct SensorDataWxMessage
{
    public SensorDataWxMessage(
        ulong? requestId,
        int commandService,
        ReadOnlySpan<byte> commandPayload,
        int announcedService,
        ReadOnlySpan<byte> announcementPayload)
    {
        RequestId = requestId;
        CommandService = commandService;
        CommandPayload = commandPayload;
        AnnouncedService = announcedService;
        AnnouncementPayload = announcementPayload;
    }

    /// <summary>Field 1: the sender's request or sequence identifier.</summary>
    public ulong? RequestId { get; }

    /// <summary>Field 7.1: the service a sensor report belongs to, or -1.</summary>
    public int CommandService { get; }

    /// <summary>Field 7.3: the sensor report.</summary>
    public ReadOnlySpan<byte> CommandPayload { get; }

    /// <summary>Field 5.1: a service the AirPods announce, or -1.</summary>
    public int AnnouncedService { get; }

    /// <summary>Field 5.2: that service's descriptor.</summary>
    public ReadOnlySpan<byte> AnnouncementPayload { get; }

    public bool HasCommand => CommandService >= 0 && !CommandPayload.IsEmpty;

    public bool HasAnnouncement => AnnouncedService >= 0;
}

/// <summary>
/// The AirPods' RTBuddy sensor channel on AACP opcode 0x0017, as documented by
/// community reverse engineering (pabloaul/apple-wireshark RTBuddy grammar,
/// LibrePods, FIocker/aap-head-tracker, ShiratsuYudachi/airpods-head-track,
/// starman-tech/mousepods). Layout:
/// <c>04 00 04 00 17 00 | 00 00 10 00 (SensorDataWX) | u16 body length | protobuf body</c>.
/// </summary>
public static class SensorDataWx
{
    public const uint Descriptor = 0x00100000;
    public const int HeaderLength = 12;

    /// <summary>The fixed DEVMOTION6 service ID that AirPods Pro 2 and AirPods 4 use.</summary>
    public const int ServiceDeviceMotion = 16;

    /// <summary>The service ID in LibrePods' original start packet.</summary>
    public const int ServiceLegacy = 14;

    /// <summary>50 Hz, verified on AirPods Max 2 (FIocker/aap-head-tracker).</summary>
    public const uint DefaultIntervalMicroseconds = 20_000;

    /// <summary>Request IDs are one- or two-byte varints (the driver allowlist accepts no more).</summary>
    public const int MaximumRequestId = 16_383;

    public const uint MinimumIntervalMicroseconds = 5_000;
    public const uint MaximumIntervalMicroseconds = 1_000_000;

    private static ReadOnlySpan<byte> Prefix => [0x04, 0x00, 0x04, 0x00, 0x17, 0x00];

    private static ReadOnlySpan<byte> DeviceMotionName => "devmotion6"u8;

    /// <summary>
    /// Strictly parses a SensorDataWX message. Returns false for anything else,
    /// including other RTBuddy descriptors and malformed protobuf. Never throws.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> packet, out SensorDataWxMessage message)
    {
        message = default;
        if (packet.Length < HeaderLength || packet.Length > AacpFrame.MaximumLength ||
            !packet[..Prefix.Length].SequenceEqual(Prefix) ||
            BinaryPrimitives.ReadUInt32LittleEndian(packet[6..]) != Descriptor)
        {
            return false;
        }

        int declaredLength = BinaryPrimitives.ReadUInt16LittleEndian(packet[10..]);
        if (declaredLength > packet.Length - HeaderLength)
        {
            return false;
        }

        ulong? requestId = null;
        var commandService = -1;
        ReadOnlySpan<byte> commandPayload = default;
        var announcedService = -1;
        ReadOnlySpan<byte> announcementPayload = default;

        var reader = new ProtobufReader(packet.Slice(HeaderLength, declaredLength));
        while (reader.TryRead(out var field))
        {
            switch (field.Number)
            {
                case 1 when field.WireType == 0:
                    requestId = field.Varint;
                    break;
                case 5 when field.WireType == 2:
                    if (!TryParseServiceMessage(field.Bytes, payloadField: 2, out announcedService, out announcementPayload))
                    {
                        return false;
                    }

                    break;
                case 7 when field.WireType == 2:
                    if (!TryParseServiceMessage(field.Bytes, payloadField: 3, out commandService, out commandPayload))
                    {
                        return false;
                    }

                    break;
            }
        }

        if (reader.Malformed)
        {
            return false;
        }

        message = new SensorDataWxMessage(requestId, commandService, commandPayload, announcedService, announcementPayload);
        return true;
    }

    /// <summary>
    /// Builds the ServiceSetting command that starts (interval &gt; 0) or stops
    /// (interval 0) a sensor stream:
    /// <c>08 &lt;request id&gt; 42 0B 08 &lt;service&gt; 10 02 1A 05 01 &lt;u32 interval µs&gt;</c>.
    /// Produces byte-identical copies of the published start and stop packets.
    /// </summary>
    public static byte[] CreateServiceSetting(int requestId, int service, uint intervalMicroseconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(requestId, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(requestId, MaximumRequestId);
        ArgumentOutOfRangeException.ThrowIfLessThan(service, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(service, 127);
        if (intervalMicroseconds != 0 &&
            intervalMicroseconds is < MinimumIntervalMicroseconds or > MaximumIntervalMicroseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalMicroseconds), intervalMicroseconds, "Use 0 (stop) or 5 ms to 1 s.");
        }

        Span<byte> body = stackalloc byte[16];
        var length = 0;
        body[length++] = 0x08;
        if (requestId < 0x80)
        {
            body[length++] = (byte)requestId;
        }
        else
        {
            body[length++] = (byte)((requestId & 0x7F) | 0x80);
            body[length++] = (byte)(requestId >> 7);
        }

        body[length++] = 0x42;
        body[length++] = 0x0B;
        body[length++] = 0x08;
        body[length++] = (byte)service;
        body[length++] = 0x10;
        body[length++] = 0x02;
        body[length++] = 0x1A;
        body[length++] = 0x05;
        body[length++] = 0x01;
        BinaryPrimitives.WriteUInt32LittleEndian(body[length..], intervalMicroseconds);
        length += 4;

        var packet = new byte[HeaderLength + length];
        Prefix.CopyTo(packet);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(6), Descriptor);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(10), (ushort)length);
        body[..length].CopyTo(packet.AsSpan(HeaderLength));
        return packet;
    }

    /// <summary>True when a service descriptor names the fused-motion service ("devmotion6").</summary>
    public static bool AnnouncesDeviceMotion(ReadOnlySpan<byte> descriptor)
    {
        if (descriptor.Length < DeviceMotionName.Length)
        {
            return false;
        }

        for (var start = 0; start <= descriptor.Length - DeviceMotionName.Length; start++)
        {
            var match = true;
            for (var index = 0; index < DeviceMotionName.Length; index++)
            {
                var value = descriptor[start + index];
                if (value is >= (byte)'A' and <= (byte)'Z')
                {
                    value = (byte)(value | 0x20);
                }

                if (value != DeviceMotionName[index])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Hex preview for diagnostics (first bytes only, never addresses).</summary>
    public static string Describe(ReadOnlySpan<byte> packet)
    {
        if (!TryParse(packet, out var message))
        {
            return "not SensorDataWX";
        }

        var text = new StringBuilder();
        text.Append(System.Globalization.CultureInfo.InvariantCulture, $"SensorDataWX len={packet.Length}");
        if (message.RequestId is { } id)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $" id={id}");
        }

        if (message.HasAnnouncement)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $" announce=service {message.AnnouncedService} ({message.AnnouncementPayload.Length} B{(AnnouncesDeviceMotion(message.AnnouncementPayload) ? ", devmotion6" : string.Empty)})");
        }

        if (message.HasCommand)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $" report=service {message.CommandService} ({message.CommandPayload.Length} B)");
        }

        return text.ToString();
    }

    private static bool TryParseServiceMessage(ReadOnlySpan<byte> data, int payloadField, out int service, out ReadOnlySpan<byte> payload)
    {
        service = -1;
        payload = default;
        var reader = new ProtobufReader(data);
        while (reader.TryRead(out var field))
        {
            if (field.Number == 1 && field.WireType == 0)
            {
                if (field.Varint > ushort.MaxValue)
                {
                    return false;
                }

                service = (int)field.Varint;
            }
            else if (field.Number == payloadField && field.WireType == 2)
            {
                payload = field.Bytes;
            }
        }

        return !reader.Malformed;
    }
}
