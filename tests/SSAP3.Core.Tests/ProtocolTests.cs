using SSAP3.Core;
using SSAP3.Core.Aacp;

namespace SSAP3.Core.Tests;

public class PacketFactoryTests
{
    [Theory]
    [InlineData(NoiseControlMode.Off, "0400040009000D01000000")]
    [InlineData(NoiseControlMode.NoiseCancellation, "0400040009000D02000000")]
    [InlineData(NoiseControlMode.Transparency, "0400040009000D03000000")]
    [InlineData(NoiseControlMode.Adaptive, "0400040009000D04000000")]
    public void Listening_mode_packets_match_documented_bytes(NoiseControlMode mode, string hex) =>
        Assert.Equal(hex, Convert.ToHexString(AacpPacketFactory.CreateNoiseControl(mode)));

    [Fact]
    public void Conversational_awareness_toggle_matches_documented_bytes()
    {
        Assert.Equal("0400040009002801000000", Convert.ToHexString(AacpPacketFactory.CreateConversationAwareness(true)));
        Assert.Equal("0400040009002802000000", Convert.ToHexString(AacpPacketFactory.CreateConversationAwareness(false)));
    }

    [Fact]
    public void Handshake_and_head_tracking_packets_are_unchanged()
    {
        Assert.Equal("00000400010002000000000000000000", Convert.ToHexString(AacpPacketFactory.CreateHandshake()));
        Assert.Equal(
            "040004001700000010000F000873420B081010021A0501409C0000",
            Convert.ToHexString(AacpPacketFactory.CreateAlternateStartHeadTracking()));
        Assert.Equal(
            "040004001700000010000F000875420B081010021A050100000000",
            Convert.ToHexString(AacpPacketFactory.CreateAlternateStopHeadTracking()));
        Assert.Equal(
            "04000400170000001000100008A102420B080E10021A0501409C0000",
            Convert.ToHexString(AacpPacketFactory.CreateStartHeadTracking()));
    }
}

public class AacpFrameTests
{
    [Fact]
    public void Parses_opcode_and_payload()
    {
        var packet = Convert.FromHexString("0400040009000D03000000");
        Assert.True(AacpFrame.TryParse(packet, out var frame));
        Assert.Equal(AacpOpcodes.ControlCommand, frame.Opcode);
        Assert.Equal(5, frame.Payload.Length);
        Assert.True(ControlNotification.TryParse(frame, out var notification));
        Assert.Equal(AacpControlIdentifiers.ListeningMode, notification.Identifier);
        Assert.Equal(0x03, notification.Value1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0400")]
    [InlineData("0400040009")]
    [InlineData("0500040009000D03000000")]
    [InlineData("0400050009000D03000000")]
    public void Rejects_malformed_headers(string hex) =>
        Assert.False(AacpFrame.TryParse(Convert.FromHexString(hex), out _));

    [Fact]
    public void Rejects_oversized_packets() =>
        Assert.False(AacpFrame.TryParse(new byte[AacpFrame.MaximumLength + 1], out _));

    [Fact]
    public void Control_notification_requires_full_value()
    {
        Assert.True(AacpFrame.TryParse(Convert.FromHexString("0400040009000D03"), out var frame));
        Assert.False(ControlNotification.TryParse(frame, out _));
    }

    [Theory]
    [InlineData("040004004B0002000101", true)]
    [InlineData("040004004B0002000102", true)]
    [InlineData("040004004B0002000103", false)]
    [InlineData("040004004B0002000108", false)]
    public void Parses_conversation_awareness_levels(string hex, bool speaking)
    {
        Assert.True(AacpFrame.TryParse(Convert.FromHexString(hex), out var frame));
        Assert.True(ConversationAwarenessLevel.TryParse(frame, out var level));
        Assert.Equal(speaking, level.IndicatesSpeaking);
    }

    [Theory]
    [InlineData("040004004B00020001")]
    [InlineData("040004004B0003000101")]
    [InlineData("040004004B0002000100")]
    [InlineData("040004004B00020001FF")]
    public void Rejects_malformed_conversation_awareness_levels(string hex)
    {
        Assert.True(AacpFrame.TryParse(Convert.FromHexString(hex), out var frame));
        Assert.False(ConversationAwarenessLevel.TryParse(frame, out _));
    }

    [Theory]
    [InlineData(0x00, false)]
    [InlineData(0x05, false)]
    [InlineData(0x01, true)]
    [InlineData(0x04, true)]
    public void Listening_mode_wire_values_are_bounded(byte value, bool valid) =>
        Assert.Equal(valid, ListeningModeWire.TryFromWire(value, out _));
}

public class BatteryParserTests
{
    [Fact]
    public void Parses_documented_battery_notification()
    {
        var packet = new byte[]
        {
            0x04, 0x00, 0x04, 0x00, 0x04, 0x00, 0x03,
            0x04, 0x01, 78, 0x02, 0x01,
            0x02, 0x01, 81, 0x01, 0x01,
            0x08, 0x01, 64, 0x02, 0x01,
        };
        Assert.True(new AirPodsBatteryPacketParser().TryParse(packet, out var update));
        Assert.NotNull(update);
        Assert.Equal(78, update.LeftLevel);
        Assert.Equal(81, update.RightLevel);
        Assert.True(update.IsRightCharging);
        Assert.Equal(64, update.CaseLevel);
        Assert.False(update.IsCaseCharging);
    }

    [Fact]
    public void Disconnected_component_is_reported_as_unavailable()
    {
        var packet = new byte[] { 0x04, 0x00, 0x04, 0x00, 0x04, 0x00, 0x01, 0x08, 0x01, 50, 0x04, 0x01 };
        Assert.True(new AirPodsBatteryPacketParser().TryParse(packet, out var update));
        Assert.Null(update!.CaseLevel);
    }

    [Theory]
    [InlineData(new byte[] { 0x04, 0x00, 0x04, 0x00, 0x04, 0x00, 0x00 })]
    [InlineData(new byte[] { 0x04, 0x00, 0x04, 0x00, 0x04, 0x00, 0x04, 1, 1, 1, 1, 1 })]
    [InlineData(new byte[] { 0x04, 0x00, 0x04, 0x00, 0x04, 0x00, 0x01, 0x08, 0x02, 50, 0x02, 0x01 })]
    [InlineData(new byte[] { 0x04, 0x00, 0x04, 0x00, 0x04, 0x00, 0x01, 0x08, 0x01, 50, 0x02 })]
    public void Rejects_malformed_battery_packets(byte[] packet) =>
        Assert.False(new AirPodsBatteryPacketParser().TryParse(packet, out _));
}

public class AdvertisementTests
{
    [Fact]
    public void Parses_airpods_4_anc_advertisement()
    {
        var advertisement = new byte[] { 0x07, 0x19, 0x01, 0x1B, 0x20, 0x20, 0x85, 0x19, 0x00, 0x00, 0x05 };
        Assert.True(new AirPodsAdvertisementParser().TryParse("02:00:00:00:00:01", advertisement, null, null, out var status));
        Assert.Equal("AirPods 4 (ANC)", status!.Model);
        Assert.Equal(0x1B20, status.ModelId);
    }

    [Fact]
    public void Recognises_airpods_pro_3_model_id()
    {
        Assert.Equal(AirPodsAdvertisementParser.AirPodsPro3ModelId, AirPodsAdvertisementParser.ModelIdFromProductId(0x2027));
        var advertisement = new byte[] { 0x07, 0x19, 0x01, 0x27, 0x20, 0x20, 0x85, 0x19, 0x00, 0x00, 0x05 };
        Assert.True(new AirPodsAdvertisementParser().TryParse("02:00:00:00:00:02", advertisement, null, null, out var status));
        Assert.Equal("AirPods Pro 3", status!.Model);
    }

    [Theory]
    [InlineData(new byte[] { 0x07, 0x19, 0x01 })]
    [InlineData(new byte[] { 0x10, 0x19, 0x01, 0x1B, 0x20, 0x20, 0x85, 0x19, 0x00, 0x00, 0x05 })]
    public void Rejects_short_or_foreign_advertisements(byte[] data) =>
        Assert.False(new AirPodsAdvertisementParser().TryParse("02:00:00:00:00:01", data, null, null, out _));

    [Fact]
    public void Filter_rejects_other_models_and_weak_signals()
    {
        var advertisement = new byte[] { 0x07, 0x19, 0x01, 0x27, 0x20, 0x20, 0x85, 0x19, 0x00, 0x00, 0x05 };
        Assert.True(new AirPodsAdvertisementParser().TryParse("02:00:00:00:00:02", advertisement, null, null, out var status));
        var filter = new AdvertisementFilter(AirPodsAdvertisementParser.AirPodsPro3ModelId, minimumRssi: -70);
        Assert.Equal(AdvertisementVerdict.Accepted, filter.Evaluate(status!, -50));
        Assert.Equal(AdvertisementVerdict.RejectedSignal, filter.Evaluate(status!, -90));
        filter.PairedModelId = 0x1B20;
        Assert.Equal(AdvertisementVerdict.RejectedModel, filter.Evaluate(status!, -40));
    }
}

public class BluetoothCryptographyTests
{
    [Fact]
    public void Ah_matches_bluetooth_core_specification_sample()
    {
        // Bluetooth Core Specification, Vol 3, Part H, Appendix D (ah):
        // IRK ec0234a357c8ad05341010a60a397d9b, prand 708194 -> hash 0dfbaa.
        var irkMsbFirst = Convert.FromHexString("ec0234a357c8ad05341010a60a397d9b");
        // Explicit LINQ call: with C# 14 span conversions, array.Reverse() binds to
        // the in-place MemoryExtensions.Reverse (void).
        var irkLsbFirst = Enumerable.Reverse(irkMsbFirst).ToArray();
        Assert.True(BluetoothCryptography.VerifyResolvablePrivateAddress("70:81:94:0D:FB:AA", irkLsbFirst));
        Assert.False(BluetoothCryptography.VerifyResolvablePrivateAddress("70:81:94:0D:FB:AB", irkLsbFirst));
    }

    [Theory]
    [InlineData("")]
    [InlineData("70:81:94:0D:FB")]
    [InlineData("70:81:94:0D:FB:ZZ")]
    [InlineData("708:1:94:0D:FB:AA")]
    public void Rejects_malformed_addresses(string address) =>
        Assert.False(BluetoothCryptography.VerifyResolvablePrivateAddress(address, new byte[16]));

    [Fact]
    public void Single_block_helper_rejects_other_sizes()
    {
        Assert.Throws<ArgumentException>(() => BluetoothCryptography.EncryptBlock(new byte[16], new byte[15]));
        Assert.Throws<ArgumentException>(() => BluetoothCryptography.EncryptBlock(new byte[8], new byte[16]));
        var block = new byte[16];
        var key = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        Assert.Equal(block, BluetoothCryptography.DecryptBlock(key, BluetoothCryptography.EncryptBlock(key, block)));
    }
}
