using System.Buffers.Binary;

namespace SSAP3.Core.Motion;

/// <summary>
/// One raw AACP head-tracking notification. The field names follow LibrePods'
/// "docs/AAP Definitions.md" ("orientation 1/2/3", horizontal and vertical
/// acceleration). Their physical meaning is NOT documented: they are opaque
/// values, not a quaternion.
/// </summary>
public readonly record struct HeadTrackingFrame(
    short Orientation1,
    short Orientation2,
    short Orientation3,
    short HorizontalAcceleration,
    short VerticalAcceleration,
    byte FrameKind,
    int PacketLength);

public static class HeadTrackingFrameParser
{
    public const int MinimumLength = 55;
    public const int MaximumLength = 512;

    private static ReadOnlySpan<byte> Prefix =>
        [0x04, 0x00, 0x04, 0x00, 0x17, 0x00, 0x00, 0x00, 0x10, 0x00];

    /// <summary>
    /// Strictly parses a head-tracking notification. Returns false for any packet
    /// that is too short, too long, or has an unexpected prefix or frame kind.
    /// It never throws on malformed input.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> packet, out HeadTrackingFrame frame)
    {
        frame = default;
        if (packet.Length is < MinimumLength or > MaximumLength ||
            !packet[..Prefix.Length].SequenceEqual(Prefix) ||
            packet[10] is not (0x44 or 0x45) ||
            packet[11] != 0x00)
        {
            return false;
        }

        frame = new HeadTrackingFrame(
            BinaryPrimitives.ReadInt16LittleEndian(packet[43..]),
            BinaryPrimitives.ReadInt16LittleEndian(packet[45..]),
            BinaryPrimitives.ReadInt16LittleEndian(packet[47..]),
            BinaryPrimitives.ReadInt16LittleEndian(packet[51..]),
            BinaryPrimitives.ReadInt16LittleEndian(packet[53..]),
            packet[10],
            packet.Length);
        return true;
    }
}
