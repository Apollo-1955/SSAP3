using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using SSAP3.Core.Aacp;
using SSAP3.Core.Motion;

namespace SSAP3.Core.Tests;

/// <summary>
/// Builds SensorDataWX packets in the layout documented by the community
/// reverse-engineering projects cited in SensorDataWx.cs. Shape:
/// <c>header | 08 seq | 10 02 | 3A len { 08 service | 1A len report }</c>.
/// With a 2-byte sequence varint this is 81 bytes and report offset 20 lands on
/// packet byte 43 (LibrePods' documented offset); with a 1-byte varint, 80 bytes.
/// </summary>
internal static class SensorPackets
{
    public static byte[] Report(
        Quaternion orientation,
        ulong timestampNs = 1_000_000,
        (short X, short Y, short Z) rate = default,
        (short X, short Y, short Z) gravity = default)
    {
        orientation = Quaternion.Normalize(orientation);
        if (orientation.W < 0)
        {
            // The device sends only the vector part, with w implied >= 0.
            orientation = Quaternion.Negate(orientation);
        }

        var report = new byte[DeviceMotionReport.MinimumLength];
        report[0] = 0x01;
        BinaryPrimitives.WriteUInt64LittleEndian(report.AsSpan(1), timestampNs);
        report[9] = 0x03;
        BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(20), Q15(orientation.X));
        BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(22), Q15(orientation.Y));
        BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(24), Q15(orientation.Z));
        BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(26), rate.X);
        BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(28), rate.Y);
        BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(30), rate.Z);
        BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(44), gravity.X);
        BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(46), gravity.Y);
        BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(48), gravity.Z);
        return report;
    }

    public static byte[] Motion(int sequence, int service, byte[] report, bool includeLogType = true)
    {
        var inner = new List<byte> { 0x08 };
        inner.AddRange(Varint((ulong)service));
        inner.Add(0x1A);
        inner.AddRange(Varint((ulong)report.Length));
        inner.AddRange(report);

        var body = new List<byte> { 0x08 };
        body.AddRange(Varint((ulong)sequence));
        if (includeLogType)
        {
            body.AddRange([0x10, 0x02]);
        }

        body.Add(0x3A);
        body.AddRange(Varint((ulong)inner.Count));
        body.AddRange(inner);
        return WithHeader(body);
    }

    public static byte[] Announcement(int service, string descriptorText)
    {
        var descriptor = Encoding.ASCII.GetBytes(descriptorText);
        var inner = new List<byte> { 0x08 };
        inner.AddRange(Varint((ulong)service));
        inner.Add(0x12);
        inner.AddRange(Varint((ulong)descriptor.Length));
        inner.AddRange(descriptor);

        var body = new List<byte> { 0x08, 0x05, 0x2A };
        body.AddRange(Varint((ulong)inner.Count));
        body.AddRange(inner);
        return WithHeader(body);
    }

    public static byte[] WithHeader(IReadOnlyCollection<byte> body, uint descriptor = SensorDataWx.Descriptor)
    {
        var packet = new byte[SensorDataWx.HeaderLength + body.Count];
        new byte[] { 0x04, 0x00, 0x04, 0x00, 0x17, 0x00 }.CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(6), descriptor);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(10), (ushort)body.Count);
        body.ToArray().CopyTo(packet, SensorDataWx.HeaderLength);
        return packet;
    }

    public static Quaternion Yaw(float degrees) =>
        Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees * MathF.PI / 180f);

    public static Quaternion About(Vector3 axis, float degrees) =>
        Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), degrees * MathF.PI / 180f);

    private static short Q15(float value) => (short)Math.Clamp(MathF.Round(value * 32768f), short.MinValue, short.MaxValue);

    private static IEnumerable<byte> Varint(ulong value)
    {
        do
        {
            var current = (byte)(value & 0x7F);
            value >>= 7;
            yield return value == 0 ? current : (byte)(current | 0x80);
        }
        while (value != 0);
    }
}

/// <summary>
/// C# mirror of the driver's outbound rules (Ssap3IsAllowedOutboundPacket and
/// Ssap3IsAllowedSensorSettingPacket in transport_bth_l2cap.c).
/// </summary>
internal static class DriverAllowlistMirror
{
    private static readonly byte[] Handshake = Convert.FromHexString("00000400010002000000000000000000");
    private static readonly byte[] FeatureFlags = Convert.FromHexString("040004004D00D700000000000000");
    private static readonly byte[] NotificationRequest = Convert.FromHexString("040004000F00FFFFFFFFFF");

    public static bool IsAllowedOutbound(ReadOnlySpan<byte> p)
    {
        if (p.Length < 6 || p.Length > 512)
        {
            return false;
        }

        if (p[0] == 0x00)
        {
            return p.SequenceEqual(Handshake);
        }

        if (p[0] != 4 || p[1] != 0 || p[2] != 4 || p[3] != 0)
        {
            return false;
        }

        return (p[4] | (p[5] << 8)) switch
        {
            0x0009 => p.Length == 11 && p[8] == 0 && p[9] == 0 && p[10] == 0 && p[6] switch
            {
                0x06 => p[7] <= 1,
                0x0D => p[7] is >= 1 and <= 4,
                0x28 => p[7] is 1 or 2,
                _ => false,
            },
            0x0017 => IsAllowedSensorSetting(p),
            0x000F => p.SequenceEqual(NotificationRequest),
            0x004D => p.SequenceEqual(FeatureFlags),
            _ => false,
        };
    }

    public static bool IsAllowedSensorSetting(ReadOnlySpan<byte> p)
    {
        if (p.Length < 12 || p.Length > 512 || p[0] != 4 || p[1] != 0 || p[2] != 4 || p[3] != 0 || p[4] != 0x17 || p[5] != 0)
        {
            return false;
        }

        if (p[6] != 0 || p[7] != 0 || p[8] != 0x10 || p[9] != 0 || (p[10] | (p[11] << 8)) != p.Length - 12)
        {
            return false;
        }

        var offset = 12;
        if (p[offset++] != 0x08)
        {
            return false;
        }

        if ((p[offset] & 0x80) != 0)
        {
            offset++;
            if ((p[offset] & 0x80) != 0)
            {
                return false;
            }
        }

        offset++;
        if (p.Length - offset != 13)
        {
            return false;
        }

        var tail = p[offset..];
        var interval = BinaryPrimitives.ReadUInt32LittleEndian(tail[9..]);
        return tail[0] == 0x42 && tail[1] == 0x0B && tail[2] == 0x08 && tail[3] is > 0 and < 0x80 &&
            tail[4] == 0x10 && tail[5] == 0x02 && tail[6] == 0x1A && tail[7] == 0x05 && tail[8] == 0x01 &&
            (interval == 0 || interval is >= 5000 and <= 1_000_000);
    }
}

public class SensorDataWxTests
{
    [Theory]
    // LibrePods "alternate" start/stop (service 16, 25 Hz).
    [InlineData(0x73, 16, 40000u, "040004001700000010000F000873420B081010021A0501409C0000")]
    [InlineData(0x75, 16, 0u, "040004001700000010000F000875420B081010021A050100000000")]
    // LibrePods original start (service 14, 25 Hz).
    [InlineData(0x121, 14, 40000u, "04000400170000001000100008A102420B080E10021A0501409C0000")]
    // FIocker/aap-head-tracker DEVMOTION6 50 Hz start, and the AirPods Max 2 service-6 start/stop.
    [InlineData(0x93, 16, 20000u, "040004001700000010001000089301420B081010021A0501204E0000")]
    [InlineData(0x98, 6, 20000u, "040004001700000010001000089801420B080610021A0501204E0000")]
    [InlineData(0x9A, 6, 0u, "040004001700000010001000089A01420B080610021A050100000000")]
    public void Service_setting_matches_published_packets(int requestId, int service, uint interval, string hex)
    {
        var packet = SensorDataWx.CreateServiceSetting(requestId, service, interval);
        Assert.Equal(hex, Convert.ToHexString(packet));
        Assert.True(DriverAllowlistMirror.IsAllowedSensorSetting(packet));
        Assert.True(SensorDataWx.TryParse(packet, out var message));
        Assert.Equal((ulong)requestId, message.RequestId);
        Assert.False(message.HasCommand);
    }

    [Theory]
    [InlineData(0, 16, 20000u)]
    [InlineData(16_384, 16, 20000u)]
    [InlineData(1, 0, 20000u)]
    [InlineData(1, 128, 20000u)]
    [InlineData(1, 16, 4999u)]
    [InlineData(1, 16, 1_000_001u)]
    public void Service_setting_rejects_values_the_driver_would_refuse(int requestId, int service, uint interval) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SensorDataWx.CreateServiceSetting(requestId, service, interval));

    [Theory]
    [InlineData(0x10, 80)]
    [InlineData(0x110, 81)]
    public void Motion_report_is_found_in_both_packet_lengths(int sequence, int expectedLength)
    {
        var orientation = Quaternion.Normalize(new Quaternion(0.1f, -0.2f, 0.3f, 0.9f));
        var packet = SensorPackets.Motion(sequence, 16, SensorPackets.Report(orientation, rate: (100, -200, 300), gravity: (10, -20, -990)));
        Assert.Equal(expectedLength, packet.Length);
        Assert.Equal(expectedLength - 12, packet[10]);

        Assert.True(HeadTrackingFrameParser.TryParse(packet, out var frame));
        Assert.Equal(16, frame.Service);
        Assert.Equal(expectedLength, frame.PacketLength);
        Assert.Equal((short)100, frame.Report.RotationRateX);
        Assert.Equal((short)-990, frame.Report.GravityZ);
        Assert.True(QuaternionMath.AngleBetweenDegrees(orientation, frame.Report.ToQuaternion()) < 0.05f);
    }

    [Fact]
    public void Fixed_offset_43_misreads_the_80_byte_variant()
    {
        // Why SSAP3 no longer reads fixed offsets: in the 81-byte form the
        // quaternion x component sits at packet byte 43 (LibrePods' offset), but a
        // sequence number below 128 shortens the varint and shifts it to 42.
        var report = SensorPackets.Report(Quaternion.Normalize(new Quaternion(0.25f, 0f, 0f, 0.97f)));
        var expected = BinaryPrimitives.ReadInt16LittleEndian(report.AsSpan(20));
        var long81 = SensorPackets.Motion(0x110, 16, report);
        var short80 = SensorPackets.Motion(0x10, 16, report);

        Assert.Equal(expected, BinaryPrimitives.ReadInt16LittleEndian(long81.AsSpan(43)));
        Assert.NotEqual(expected, BinaryPrimitives.ReadInt16LittleEndian(short80.AsSpan(43)));
        Assert.True(HeadTrackingFrameParser.TryParse(short80, out var frame));
        Assert.Equal(expected, frame.Report.QuaternionX);
    }

    [Fact]
    public void Motion_report_without_the_log_type_field_still_parses()
    {
        var packet = SensorPackets.Motion(0x22, 16, SensorPackets.Report(Quaternion.Identity * SensorPackets.About(Vector3.UnitX, 40)), includeLogType: false);
        Assert.True(HeadTrackingFrameParser.TryParse(packet, out var frame));
        Assert.Equal(16, frame.Service);
    }

    [Fact]
    public void Announcement_reveals_a_dynamic_devmotion6_service()
    {
        var packet = SensorPackets.Announcement(6, "AccessoryService=DevMotion6 MaxReportSize=191");
        Assert.True(SensorDataWx.TryParse(packet, out var message));
        Assert.True(message.HasAnnouncement);
        Assert.Equal(6, message.AnnouncedService);
        Assert.True(SensorDataWx.AnnouncesDeviceMotion(message.AnnouncementPayload));

        Assert.True(SensorDataWx.TryParse(SensorPackets.Announcement(12, "AccessoryService=gyro"), out var other));
        Assert.False(SensorDataWx.AnnouncesDeviceMotion(other.AnnouncementPayload));
    }

    [Fact]
    public void Rejects_other_descriptors_and_inconsistent_lengths()
    {
        var good = SensorPackets.Motion(0x10, 16, SensorPackets.Report(Quaternion.Normalize(new Quaternion(0.1f, 0.1f, 0.1f, 0.98f))));
        Assert.True(SensorDataWx.TryParse(good, out _));

        // AirPods 3 RemoteHID (0x800) is a different protocol.
        var remoteHid = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(remoteHid.AsSpan(6), 0x00000800);
        Assert.False(SensorDataWx.TryParse(remoteHid, out _));

        var overlong = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(overlong.AsSpan(10), (ushort)(good.Length - 11));
        Assert.False(SensorDataWx.TryParse(overlong, out _));

        Assert.False(SensorDataWx.TryParse(good.AsSpan(0, 11), out _));
        Assert.False(SensorDataWx.TryParse(new byte[AacpFrame.MaximumLength + 1], out _));
    }

    [Theory]
    [InlineData("08FFFFFFFFFFFFFFFFFFFF01")] // 11-byte varint
    [InlineData("3A05080110")]                // length-delimited field runs past the end
    [InlineData("0B01")]                      // wire type 3 (group start) is not supported
    [InlineData("0001")]                      // field number 0
    [InlineData("3A0408808004")]              // service id above 65535
    public void Rejects_malformed_protobuf(string bodyHex)
    {
        var packet = SensorPackets.WithHeader(Convert.FromHexString(bodyHex));
        Assert.False(SensorDataWx.TryParse(packet, out _));
        Assert.False(HeadTrackingFrameParser.TryParse(packet, out _));
    }

    [Fact]
    public void Protobuf_reader_reports_clean_end_and_malformed_input()
    {
        var clean = new ProtobufReader(Convert.FromHexString("0801120161"));
        Assert.True(clean.TryRead(out var first));
        Assert.Equal(1, first.Number);
        Assert.Equal(1UL, first.Varint);
        Assert.True(clean.TryRead(out var second));
        Assert.Equal(2, second.Number);
        Assert.Equal("a", Encoding.ASCII.GetString(second.Bytes));
        Assert.False(clean.TryRead(out _));
        Assert.False(clean.Malformed);

        var truncated = new ProtobufReader(Convert.FromHexString("1205"));
        Assert.False(truncated.TryRead(out _));
        Assert.True(truncated.Malformed);
    }
}

public class DeviceMotionReportTests
{
    [Fact]
    public void Reconstructs_w_from_the_q15_vector()
    {
        var expected = SensorPackets.Yaw(60);
        Assert.True(DeviceMotionReport.TryParse(SensorPackets.Report(expected), out var report));
        Assert.True(QuaternionMath.AngleBetweenDegrees(expected, report.ToQuaternion()) < 0.05f);
        Assert.InRange(report.ToQuaternion().Length(), 0.999f, 1.001f);
    }

    [Fact]
    public void Rejects_short_wrong_type_zero_and_oversized_vectors()
    {
        var valid = SensorPackets.Report(SensorPackets.Yaw(10));
        Assert.True(DeviceMotionReport.TryParse(valid, out _));
        Assert.False(DeviceMotionReport.TryParse(valid.AsSpan(0, DeviceMotionReport.MinimumLength - 1), out _));

        var wrongType = (byte[])valid.Clone();
        wrongType[0] = 0x02;
        Assert.False(DeviceMotionReport.TryParse(wrongType, out _));

        var zero = (byte[])valid.Clone();
        zero.AsSpan(20, 6).Clear();
        Assert.False(DeviceMotionReport.TryParse(zero, out _));

        var tooLong = (byte[])valid.Clone();
        BinaryPrimitives.WriteInt16LittleEndian(tooLong.AsSpan(20), 30000);
        BinaryPrimitives.WriteInt16LittleEndian(tooLong.AsSpan(22), 30000);
        Assert.False(DeviceMotionReport.TryParse(tooLong, out _));
    }

    [Fact]
    public void Rotation_rate_uses_the_measured_scale()
    {
        Assert.True(DeviceMotionReport.TryParse(SensorPackets.Report(SensorPackets.Yaw(5), rate: (0, 0, 1000)), out var report));
        Assert.Equal(122f, report.RotationRateDegreesPerSecond, 1);
    }
}

public class DeviceMotionOrientationDecoderTests
{
    // How the bud sits in the ear: an arbitrary fixed tilt of the sensor frame.
    private static readonly Quaternion Mounting =
        Quaternion.Normalize(SensorPackets.About(new Vector3(1, 0.3f, 0), 35) * SensorPackets.About(Vector3.UnitY, -20));

    private static HeadTrackingFrame Frame(Quaternion headInWorld, ulong timestampNs, short rateZ = 0)
    {
        var sensorToWorld = Quaternion.Normalize(headInWorld * Mounting);
        var packet = SensorPackets.Motion(0x10, 16, SensorPackets.Report(sensorToWorld, timestampNs, rate: (0, 0, rateZ)));
        Assert.True(HeadTrackingFrameParser.TryParse(packet, out var frame));
        return frame;
    }

    private static float Yaw(Quaternion orientation) => QuaternionMath.ToYawPitchRollDegrees(orientation).Yaw;

    [Theory]
    [InlineData(0f)]
    [InlineData(30f)]
    [InlineData(-45f)]
    [InlineData(90f)]
    [InlineData(170f)]
    public void Head_turns_give_yaw_regardless_of_mounting(float degrees)
    {
        var decoder = new DeviceMotionOrientationDecoder();
        Assert.True(decoder.TryDecode(Frame(Quaternion.Identity, 1_000_000), out _));
        Assert.True(decoder.TryDecode(Frame(SensorPackets.Yaw(degrees), 900_000_000), out var orientation));
        Assert.Equal(degrees, Yaw(orientation), 0);
    }

    [Theory]
    [InlineData(25f)]
    [InlineData(-30f)]
    public void Nodding_and_tilting_do_not_turn_the_stage(float degrees)
    {
        var decoder = new DeviceMotionOrientationDecoder();
        Assert.True(decoder.TryDecode(Frame(Quaternion.Identity, 1_000_000), out _));
        Assert.True(decoder.TryDecode(Frame(SensorPackets.About(Vector3.UnitX, degrees), 900_000_000), out var nod));
        Assert.InRange(Yaw(nod), -0.5f, 0.5f);
        Assert.True(decoder.TryDecode(Frame(SensorPackets.About(Vector3.UnitY, degrees), 1_800_000_000), out var tilt));
        Assert.InRange(Yaw(tilt), -0.5f, 0.5f);
    }

    [Fact]
    public void Yaw_survives_a_simultaneous_nod()
    {
        // Turn about world vertical, then nod about the head's own lateral axis.
        var decoder = new DeviceMotionOrientationDecoder();
        Assert.True(decoder.TryDecode(Frame(Quaternion.Identity, 1_000_000), out _));
        var head = SensorPackets.Yaw(40) * SensorPackets.About(Vector3.UnitX, -25);
        Assert.True(decoder.TryDecode(Frame(head, 900_000_000), out var orientation));
        Assert.Equal(40f, Yaw(orientation), 0);
    }

    [Fact]
    public void Sign_flip_of_the_q15_encoding_does_not_change_yaw()
    {
        // 200 degrees of sensor rotation makes the true w negative; the encoding flips the vector.
        var decoder = new DeviceMotionOrientationDecoder();
        Assert.True(decoder.TryDecode(Frame(Quaternion.Identity, 1_000_000), out _));
        Assert.True(decoder.TryDecode(Frame(SensorPackets.Yaw(-160), 900_000_000), out var orientation));
        Assert.Equal(-160f, Yaw(orientation), 0);
    }

    [Fact]
    public void Inverted_yaw_and_reset()
    {
        var decoder = new DeviceMotionOrientationDecoder(invertYaw: true);
        Assert.True(decoder.TryDecode(Frame(Quaternion.Identity, 1_000_000), out _));
        Assert.True(decoder.TryDecode(Frame(SensorPackets.Yaw(30), 900_000_000), out var inverted));
        Assert.Equal(-30f, Yaw(inverted), 0);

        decoder.Reset();
        Assert.True(decoder.TryDecode(Frame(SensorPackets.Yaw(30), 1_000_000_000), out var fresh));
        Assert.Equal(0f, Yaw(fresh), 0);
    }

    [Fact]
    public void Unexplained_jump_reanchors_instead_of_snapping()
    {
        // A bud switch: the attitude jumps 90 degrees in 20 ms with no rotation measured.
        var decoder = new DeviceMotionOrientationDecoder();
        Assert.True(decoder.TryDecode(Frame(Quaternion.Identity, 1_000_000_000), out _));
        Assert.True(decoder.TryDecode(Frame(SensorPackets.Yaw(20), 1_400_000_000), out var before));
        Assert.True(decoder.TryDecode(Frame(SensorPackets.Yaw(110), 1_420_000_000), out var after));
        Assert.Equal(20f, Yaw(before), 0);
        Assert.Equal(20f, Yaw(after), 0);
        Assert.Equal(1, decoder.Reanchors);

        // Motion continues relative to the new reference.
        Assert.True(decoder.TryDecode(Frame(SensorPackets.Yaw(120), 1_440_000_000), out var later));
        Assert.Equal(30f, Yaw(later), 0);
    }

    [Fact]
    public void Fast_turn_backed_by_the_gyro_is_not_a_discontinuity()
    {
        // 40 degrees in 20 ms at a measured 2000 deg/s (16393 counts * 0.122).
        var decoder = new DeviceMotionOrientationDecoder();
        Assert.True(decoder.TryDecode(Frame(Quaternion.Identity, 1_000_000_000), out _));
        Assert.True(decoder.TryDecode(Frame(SensorPackets.Yaw(40), 1_020_000_000, rateZ: 16393), out var orientation));
        Assert.Equal(40f, Yaw(orientation), 0);
        Assert.Equal(0, decoder.Reanchors);
    }

    [Fact]
    public void Clock_reset_reanchors_but_a_long_gap_keeps_the_heading()
    {
        var decoder = new DeviceMotionOrientationDecoder();
        Assert.True(decoder.TryDecode(Frame(Quaternion.Identity, 5_000_000_000), out _));

        // After a 2 s gap, a 70 degree turn is plausible: keep it.
        Assert.True(decoder.TryDecode(Frame(SensorPackets.Yaw(70), 7_000_000_000), out var afterGap));
        Assert.Equal(70f, Yaw(afterGap), 0);
        Assert.Equal(0, decoder.Reanchors);

        // A timestamp that goes backwards with a large jump means the stream restarted.
        Assert.True(decoder.TryDecode(Frame(SensorPackets.Yaw(-20), 1_000_000), out var afterReset));
        Assert.Equal(70f, Yaw(afterReset), 0);
        Assert.Equal(1, decoder.Reanchors);
    }

    [Fact]
    public void Twist_helper_wraps_to_plus_minus_180()
    {
        Assert.Equal(0f, DeviceMotionOrientationDecoder.TwistAboutVerticalDegrees(Quaternion.Identity), 3);
        Assert.Equal(0f, DeviceMotionOrientationDecoder.TwistAboutVerticalDegrees(Quaternion.Negate(Quaternion.Identity)), 3);
        Assert.Equal(-90f, DeviceMotionOrientationDecoder.TwistAboutVerticalDegrees(SensorPackets.Yaw(270)), 2);
        Assert.Equal(0f, DeviceMotionOrientationDecoder.TwistAboutVerticalDegrees(SensorPackets.About(Vector3.UnitX, 180)), 3);
    }
}

public class MotionStreamControllerTests
{
    private static int ServiceOf(byte[] command) => command[^10];

    private static uint IntervalOf(byte[] command) => BinaryPrimitives.ReadUInt32LittleEndian(command.AsSpan(command.Length - 4));

    [Fact]
    public void Starts_devmotion6_then_falls_back_to_the_legacy_service()
    {
        var controller = new MotionStreamController();
        var start = controller.Start(TimeSpan.Zero);
        Assert.Equal(16, ServiceOf(Assert.Single(start)));
        Assert.Equal(SensorDataWx.DefaultIntervalMicroseconds, IntervalOf(start[0]));

        Assert.Empty(controller.Tick(TimeSpan.FromSeconds(1), MotionStreamStatus.WaitingForFirstFrame));
        var fallback = controller.Tick(TimeSpan.FromSeconds(2), MotionStreamStatus.WaitingForFirstFrame);
        Assert.Equal(14, ServiceOf(Assert.Single(fallback)));

        // Every candidate tried: wait, then a new cycle.
        Assert.Empty(controller.Tick(TimeSpan.FromSeconds(4.5), MotionStreamStatus.WaitingForFirstFrame));
        var retry = controller.Tick(TimeSpan.FromSeconds(6), MotionStreamStatus.WaitingForFirstFrame);
        Assert.Equal(16, ServiceOf(Assert.Single(retry)));
    }

    [Fact]
    public void Announced_dynamic_service_is_tried_first()
    {
        var controller = new MotionStreamController();
        _ = controller.Start(TimeSpan.Zero);
        Assert.True(SensorDataWx.TryParse(SensorPackets.Announcement(6, "AccessoryService=devmotion6"), out var message));
        controller.OnAnnouncement(message.AnnouncedService, message.AnnouncementPayload);

        var commands = controller.Tick(TimeSpan.FromMilliseconds(100), MotionStreamStatus.WaitingForFirstFrame);
        Assert.Equal(6, ServiceOf(Assert.Single(commands)));
        Assert.Equal(6, controller.DiscoveredService);
    }

    [Fact]
    public void Locks_onto_the_first_service_that_streams()
    {
        var controller = new MotionStreamController();
        Assert.False(controller.AcceptReport(16));
        _ = controller.Start(TimeSpan.Zero);
        _ = controller.Tick(TimeSpan.FromSeconds(2), MotionStreamStatus.WaitingForFirstFrame);

        Assert.False(controller.AcceptReport(99));
        Assert.True(controller.AcceptReport(14));
        Assert.Equal(14, controller.ActiveService);
        Assert.False(controller.AcceptReport(16));

        // Locked: no more candidates are started.
        Assert.Empty(controller.Tick(TimeSpan.FromSeconds(10), MotionStreamStatus.Streaming));
    }

    [Fact]
    public void Restarts_a_stalled_stream_and_stops_every_started_service()
    {
        var controller = new MotionStreamController();
        _ = controller.Start(TimeSpan.Zero);
        _ = controller.Tick(TimeSpan.FromSeconds(2), MotionStreamStatus.WaitingForFirstFrame);
        Assert.True(controller.AcceptReport(16));

        Assert.Empty(controller.Tick(TimeSpan.FromSeconds(3), MotionStreamStatus.Stalled));
        var restart = controller.Tick(TimeSpan.FromSeconds(6), MotionStreamStatus.Stalled);
        Assert.Equal(16, ServiceOf(Assert.Single(restart)));

        var stops = controller.Stop();
        Assert.Equal([16, 14], stops.Select(ServiceOf));
        Assert.All(stops, stop => Assert.Equal(0u, IntervalOf(stop)));
        Assert.False(controller.IsRunning);
        Assert.False(controller.AcceptReport(16));
    }

    [Fact]
    public void Every_command_passes_the_driver_allowlist_and_ids_wrap()
    {
        var controller = new MotionStreamController(firstRequestId: SensorDataWx.MaximumRequestId);
        var commands = new List<byte[]>();
        commands.AddRange(controller.Start(TimeSpan.Zero));
        for (var seconds = 2; seconds < 60; seconds += 2)
        {
            commands.AddRange(controller.Tick(TimeSpan.FromSeconds(seconds), MotionStreamStatus.WaitingForFirstFrame));
        }

        commands.AddRange(controller.Stop());
        Assert.True(commands.Count > 10);
        Assert.All(commands, command => Assert.True(DriverAllowlistMirror.IsAllowedSensorSetting(command)));

        // The first id is the maximum two-byte varint; the next wraps to 1.
        Assert.True(SensorDataWx.TryParse(commands[0], out var first));
        Assert.Equal((ulong)SensorDataWx.MaximumRequestId, first.RequestId);
        Assert.True(SensorDataWx.TryParse(commands[1], out var second));
        Assert.Equal(1UL, second.RequestId);
    }

    [Fact]
    public void Every_packet_the_app_sends_passes_the_exact_driver_allowlist()
    {
        var packets = new List<byte[]>
        {
            AacpPacketFactory.CreateHandshake(),
            AacpPacketFactory.CreateFeatureFlags(),
            AacpPacketFactory.CreateNotificationRequest(),
            AacpPacketFactory.CreateOwnsConnection(true),
            AacpPacketFactory.CreateOwnsConnection(false),
            AacpPacketFactory.CreateConversationAwareness(true),
            AacpPacketFactory.CreateConversationAwareness(false),
        };
        packets.AddRange(Enum.GetValues<NoiseControlMode>().Select(AacpPacketFactory.CreateNoiseControl));
        packets.AddRange(new MotionStreamController().Start(TimeSpan.Zero));

        Assert.All(packets, packet => Assert.True(DriverAllowlistMirror.IsAllowedOutbound(packet), Convert.ToHexString(packet)));
    }

    [Theory]
    [InlineData("0000040001000200000000000000000000")] // handshake with a trailing byte
    [InlineData("00000400010002000000000000000001")]   // handshake with a changed byte
    [InlineData("040004004D00FF00000000000000")]       // other feature flags
    [InlineData("040004000F00FFFFFFFFFF00")]           // notification request with a trailing byte
    [InlineData("0400040009000602000000")]             // owns connection, value 2
    [InlineData("040004000900060100000000")]           // control command with a trailing byte
    [InlineData("0400040009000601000001")]             // control command, non-zero tail
    [InlineData("0400040009000D00000000")]             // listening mode 0 (not a mode)
    [InlineData("0400040009000D05000000")]             // listening mode 5
    [InlineData("0400040009002803000000")]             // conversation awareness value 3
    [InlineData("0400040009000701000000")]             // an unused control identifier
    [InlineData("040004001000000000000000")]           // an opcode SSAP3 never sends
    public void Near_misses_are_refused_by_the_driver_allowlist(string hex)
    {
        Assert.False(DriverAllowlistMirror.IsAllowedOutbound(Convert.FromHexString(hex)));
    }
}
