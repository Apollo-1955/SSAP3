namespace SSAP3.Core;

// Packet layout adapted from the GPL-3.0 LibrePods Windows port.
public static class AacpPacketFactory
{
    private static readonly byte[] Header = [0x04, 0x00, 0x04, 0x00];

    public static byte[] CreateHandshake() =>
        Convert.FromHexString("00000400010002000000000000000000");

    public static byte[] CreateFeatureFlags() =>
        Convert.FromHexString("040004004D00D700000000000000");

    public static byte[] CreateNotificationRequest() =>
        Convert.FromHexString("040004000F00FFFFFFFFFF");

    public static byte[] CreateNoiseControl(NoiseControlMode mode) =>
        CreateControlCommand(0x0D, (byte)((int)mode + 1));

    public static byte[] CreateConversationAwareness(bool enabled) =>
        CreateControlCommand(0x28, enabled ? (byte)0x01 : (byte)0x02);

    public static byte[] CreateOwnsConnection(bool ownsConnection) =>
        CreateControlCommand(0x06, ownsConnection ? (byte)0x01 : (byte)0x00);

    // Head-tracking start/stop commands are built by Motion.SensorDataWx.CreateServiceSetting
    // (and sequenced by Motion.MotionStreamController), which reproduces the published
    // LibrePods/RTBuddy packets byte for byte.

    private static byte[] CreateControlCommand(byte identifier, byte value) =>
        CreateDataPacket([0x09, 0x00, identifier, value, 0x00, 0x00, 0x00]);

    private static byte[] CreateDataPacket(ReadOnlySpan<byte> data)
    {
        var packet = new byte[Header.Length + data.Length];
        Header.CopyTo(packet, 0);
        data.CopyTo(packet.AsSpan(Header.Length));
        return packet;
    }
}
