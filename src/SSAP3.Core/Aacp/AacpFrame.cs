using System.Buffers.Binary;

namespace SSAP3.Core.Aacp;

/// <summary>AACP opcodes used by SSAP3 (LibrePods docs/opcodes.md).</summary>
public static class AacpOpcodes
{
    public const ushort Battery = 0x0004;
    public const ushort ControlCommand = 0x0009;
    public const ushort NotificationRequest = 0x000F;
    public const ushort HeadTracking = 0x0017;
    public const ushort ConversationAwarenessLevel = 0x004B;
    public const ushort FeatureFlags = 0x004D;
}

/// <summary>Control-command identifiers (LibrePods docs/control_commands.md).</summary>
public static class AacpControlIdentifiers
{
    public const byte OwnsConnection = 0x06;
    public const byte ListeningMode = 0x0D;
    public const byte ConversationalAwareness = 0x28;
}

/// <summary>
/// A validated AACP data packet: <c>04 00 04 00 | opcode (u16 LE) | payload</c>.
/// Packets come from an untrusted Bluetooth peer, so parsing is total. It
/// never throws, and it bounds both length and structure.
/// </summary>
public readonly ref struct AacpFrame
{
    public const int HeaderLength = 6;

    /// <summary>The driver's inbound limit: the L2CAP receive MTU iOS uses.</summary>
    public const int MaximumLength = 2582;

    private AacpFrame(ushort opcode, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> raw)
    {
        Opcode = opcode;
        Payload = payload;
        Raw = raw;
    }

    public ushort Opcode { get; }

    public ReadOnlySpan<byte> Payload { get; }

    public ReadOnlySpan<byte> Raw { get; }

    public static bool TryParse(ReadOnlySpan<byte> packet, out AacpFrame frame)
    {
        frame = default;
        if (packet.Length is < HeaderLength or > MaximumLength ||
            packet[0] != 0x04 || packet[1] != 0x00 || packet[2] != 0x04 || packet[3] != 0x00)
        {
            return false;
        }

        frame = new AacpFrame(
            BinaryPrimitives.ReadUInt16LittleEndian(packet[4..]),
            packet[HeaderLength..],
            packet);
        return true;
    }
}

/// <summary>A control-command notification: <c>opcode 0x0009 | identifier | 4 value bytes</c>.</summary>
public readonly record struct ControlNotification(byte Identifier, byte Value1, byte Value2, byte Value3, byte Value4)
{
    public static bool TryParse(in AacpFrame frame, out ControlNotification notification)
    {
        notification = default;
        if (frame.Opcode != AacpOpcodes.ControlCommand || frame.Payload.Length < 5)
        {
            return false;
        }

        var payload = frame.Payload;
        notification = new ControlNotification(payload[0], payload[1], payload[2], payload[3], payload[4]);
        return true;
    }
}

/// <summary>
/// Conversational-awareness speaking level (opcode 0x004B):
/// <c>02 00 01 [level]</c>. Per LibrePods, levels 1-2 mean the wearer started
/// speaking, 3 means they stopped, and 8-9 mean normal volume.
/// </summary>
public readonly record struct ConversationAwarenessLevel(byte Level)
{
    public bool IndicatesSpeaking => Level is 1 or 2;

    public static bool TryParse(in AacpFrame frame, out ConversationAwarenessLevel level)
    {
        level = default;
        var payload = frame.Payload;
        if (frame.Opcode != AacpOpcodes.ConversationAwarenessLevel ||
            payload.Length < 4 ||
            payload[0] != 0x02 || payload[1] != 0x00 || payload[2] != 0x01 ||
            payload[3] is 0 or > 0x10)
        {
            return false;
        }

        level = new ConversationAwarenessLevel(payload[3]);
        return true;
    }
}

public static class ListeningModeWire
{
    /// <summary>Wire values (LibrePods): Off 1, Noise Cancellation 2, Transparency 3, Adaptive 4.</summary>
    public static byte ToWire(NoiseControlMode mode) => mode switch
    {
        NoiseControlMode.Off => 0x01,
        NoiseControlMode.NoiseCancellation => 0x02,
        NoiseControlMode.Transparency => 0x03,
        NoiseControlMode.Adaptive => 0x04,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown listening mode."),
    };

    public static bool TryFromWire(byte value, out NoiseControlMode mode)
    {
        mode = value switch
        {
            0x01 => NoiseControlMode.Off,
            0x02 => NoiseControlMode.NoiseCancellation,
            0x03 => NoiseControlMode.Transparency,
            0x04 => NoiseControlMode.Adaptive,
            _ => default,
        };
        return value is >= 0x01 and <= 0x04;
    }
}
