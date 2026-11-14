using System.Buffers.Binary;
using System.Numerics;
using SSAP3.Core.Aacp;

namespace SSAP3.Core.Motion;

/// <summary>
/// The 58-byte fused device-motion report carried in a SensorDataWX command
/// (field 7.3). Layout from LibrePods PR #887, with units measured by
/// starman-tech/mousepods and the quaternion encoding confirmed independently by
/// ShiratsuYudachi/airpods-head-track (AirPods 4) and FIocker/aap-head-tracker
/// (AirPods Max 2):
/// <code>
/// [0]      0x01
/// [1:9]    u64 timestamp (ns)
/// [20:26]  attitude quaternion x, y, z, signed Q15; w = sqrt(1 - |v|^2) &gt;= 0.
///          Sensor-to-world rotation; the world frame has Z vertical (up).
/// [26:32]  rotation rate x, y, z (about 0.122 deg/s per LSB)
/// [38:44]  user acceleration x, y, z (about 1000 per g)
/// [44:50]  gravity x, y, z (about 1000 per g)
/// </code>
/// None of it is verified on AirPods Pro 3 yet.
/// </summary>
public readonly record struct DeviceMotionReport(
    ulong TimestampNanoseconds,
    short QuaternionX,
    short QuaternionY,
    short QuaternionZ,
    short RotationRateX,
    short RotationRateY,
    short RotationRateZ,
    short UserAccelerationX,
    short UserAccelerationY,
    short UserAccelerationZ,
    short GravityX,
    short GravityY,
    short GravityZ)
{
    public const int MinimumLength = 58;
    public const float QuaternionScale = 32768f;
    public const float DegreesPerSecondPerCount = 0.122f;
    public const float CountsPerG = 1000f;

    /// <summary>
    /// Parses a report. Rejects anything shorter than the documented block, a
    /// wrong report type, an all-zero attitude (no data yet) and a vector part
    /// longer than a unit quaternion allows. Never throws.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> report, out DeviceMotionReport parsed)
    {
        parsed = default;
        if (report.Length is < MinimumLength or > AacpFrame.MaximumLength || report[0] != 0x01)
        {
            return false;
        }

        var x = BinaryPrimitives.ReadInt16LittleEndian(report[20..]);
        var y = BinaryPrimitives.ReadInt16LittleEndian(report[22..]);
        var z = BinaryPrimitives.ReadInt16LittleEndian(report[24..]);
        if (x == 0 && y == 0 && z == 0)
        {
            return false;
        }

        // Q15 rounding can push |v| slightly past 1; anything larger is not a quaternion.
        var squared = ((double)x * x + (double)y * y + (double)z * z) / (QuaternionScale * (double)QuaternionScale);
        if (squared > 1.02)
        {
            return false;
        }

        parsed = new DeviceMotionReport(
            BinaryPrimitives.ReadUInt64LittleEndian(report[1..]),
            x,
            y,
            z,
            BinaryPrimitives.ReadInt16LittleEndian(report[26..]),
            BinaryPrimitives.ReadInt16LittleEndian(report[28..]),
            BinaryPrimitives.ReadInt16LittleEndian(report[30..]),
            BinaryPrimitives.ReadInt16LittleEndian(report[38..]),
            BinaryPrimitives.ReadInt16LittleEndian(report[40..]),
            BinaryPrimitives.ReadInt16LittleEndian(report[42..]),
            BinaryPrimitives.ReadInt16LittleEndian(report[44..]),
            BinaryPrimitives.ReadInt16LittleEndian(report[46..]),
            BinaryPrimitives.ReadInt16LittleEndian(report[48..]));
        return true;
    }

    /// <summary>The sensor-to-world attitude, with the implied non-negative w.</summary>
    public Quaternion ToQuaternion()
    {
        var x = QuaternionX / QuaternionScale;
        var y = QuaternionY / QuaternionScale;
        var z = QuaternionZ / QuaternionScale;
        var w = MathF.Sqrt(MathF.Max(0f, 1f - ((x * x) + (y * y) + (z * z))));
        return QuaternionMath.NormalizeOrIdentity(new Quaternion(x, y, z, w));
    }

    /// <summary>Magnitude of the rotation rate, in degrees per second.</summary>
    public float RotationRateDegreesPerSecond =>
        MathF.Sqrt(((float)RotationRateX * RotationRateX) + ((float)RotationRateY * RotationRateY) + ((float)RotationRateZ * RotationRateZ)) *
        DegreesPerSecondPerCount;
}

/// <summary>One fused-motion report from the AirPods' sensor stream, with the service it came from.</summary>
public readonly record struct HeadTrackingFrame(int Service, DeviceMotionReport Report, int PacketLength);

public static class HeadTrackingFrameParser
{
    /// <summary>
    /// Parses a SensorDataWX packet that carries a device-motion report. The
    /// report is located by parsing the protobuf body, not at a fixed offset:
    /// a varint before it changes size, so packets are 80 or 81 bytes and a fixed
    /// offset misreads one of the two. Never throws.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> packet, out HeadTrackingFrame frame)
    {
        frame = default;
        if (!SensorDataWx.TryParse(packet, out var message) || !message.HasCommand ||
            !DeviceMotionReport.TryParse(message.CommandPayload, out var report))
        {
            return false;
        }

        frame = new HeadTrackingFrame(message.CommandService, report, packet.Length);
        return true;
    }
}
