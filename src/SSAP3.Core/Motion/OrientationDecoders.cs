using System.Numerics;

namespace SSAP3.Core.Motion;

public enum TrackingState
{
    None = 0,
    Live = 1,
    Holding = 2,
    Lost = 3,
}

/// <summary>A decoded, timestamped orientation measurement.</summary>
/// <param name="Timestamp">Monotonic receive time (not wall-clock).</param>
public readonly record struct OrientationSample(TimeSpan Timestamp, Quaternion Orientation, bool HasRoll);

public sealed record OrientationDecoderInfo(string Name, bool HardwareVerified, bool ProvidesRoll, string Notes);

/// <summary>Turns AirPods motion reports into head orientations in SSAP3's frame.</summary>
public interface IOrientationDecoder
{
    OrientationDecoderInfo Info { get; }

    bool TryDecode(in HeadTrackingFrame frame, out Quaternion orientation);

    /// <summary>Forgets the heading reference (call when a stream starts or restarts).</summary>
    void Reset();
}

/// <summary>
/// Head yaw from the AirPods' fused attitude quaternion.
///
/// The attitude rotates sensor-frame vectors into a world frame whose Z axis is
/// vertical, so turning the head is a rotation about world Z no matter how the
/// bud sits in the ear. The decoder takes the world-frame rotation since the
/// first sample, <c>q · conj(anchor)</c>, and keeps only its twist about Z. Q15's
/// implied <c>w ≥ 0</c> flips the sign of the whole quaternion when the true w
/// crosses zero; <c>q</c> and <c>-q</c> are the same rotation, so the twist angle is
/// unaffected.
///
/// A jump of more than 30° in one report that the measured rotation rate cannot
/// explain, or a timestamp that goes backwards, means the stream switched buds or
/// restarted. The decoder then re-anchors so the output continues smoothly
/// instead of snapping.
///
/// Pitch and roll are not decoded: they need the head's forward axis, which the
/// attitude alone does not give. Not verified on AirPods Pro 3 hardware yet.
/// </summary>
public sealed class DeviceMotionOrientationDecoder : IOrientationDecoder
{
    private const float JumpMarginDegrees = 30f;
    private const float MaximumRotationRateDegreesPerSecond = 2000f;
    private const float RadiansToDegrees = 180f / MathF.PI;

    private bool _hasAnchor;
    private Quaternion _anchor = Quaternion.Identity;
    private Quaternion _lastRaw = Quaternion.Identity;
    private Quaternion _lastRelative = Quaternion.Identity;
    private ulong _lastTimestamp;

    public DeviceMotionOrientationDecoder(bool invertYaw = false)
    {
        InvertYaw = invertYaw;
    }

    public bool InvertYaw { get; }

    /// <summary>How many times the heading reference was moved after a discontinuity.</summary>
    public int Reanchors { get; private set; }

    public OrientationDecoderInfo Info { get; } = new(
        "AirPods fused attitude (yaw about world vertical)",
        HardwareVerified: false,
        ProvidesRoll: false,
        "SensorDataWX device-motion quaternion (Q15 vector part); yaw is its twist about the gravity-aligned Z axis. Unverified on AirPods Pro 3.");

    public void Reset()
    {
        _hasAnchor = false;
        _anchor = Quaternion.Identity;
        _lastRaw = Quaternion.Identity;
        _lastRelative = Quaternion.Identity;
        _lastTimestamp = 0;
    }

    public bool TryDecode(in HeadTrackingFrame frame, out Quaternion orientation)
    {
        orientation = Quaternion.Identity;
        var raw = frame.Report.ToQuaternion();
        if (!QuaternionMath.IsFinite(raw))
        {
            return false;
        }

        if (!_hasAnchor)
        {
            _anchor = raw;
            _hasAnchor = true;
        }
        else if (IsDiscontinuity(frame.Report, raw))
        {
            // Keep the previous output: raw · conj(newAnchor) == lastRelative.
            _anchor = QuaternionMath.NormalizeOrIdentity(Quaternion.Conjugate(_lastRelative) * raw);
            Reanchors++;
        }

        _lastRaw = raw;
        _lastTimestamp = frame.Report.TimestampNanoseconds;
        var relative = QuaternionMath.NormalizeOrIdentity(raw * Quaternion.Conjugate(_anchor));
        _lastRelative = relative;

        var yaw = TwistAboutVerticalDegrees(relative);
        if (InvertYaw)
        {
            yaw = -yaw;
        }

        orientation = QuaternionMath.FromYawPitchRollDegrees(yaw, 0f, 0f);
        return QuaternionMath.IsFinite(orientation);
    }

    /// <summary>Signed rotation angle about +Z contained in a rotation (swing-twist split), -180..180.</summary>
    public static float TwistAboutVerticalDegrees(Quaternion rotation)
    {
        rotation = QuaternionMath.NormalizeOrIdentity(rotation);
        if (MathF.Abs(rotation.W) < 1e-6f && MathF.Abs(rotation.Z) < 1e-6f)
        {
            return 0f;
        }

        var angle = 2f * MathF.Atan2(rotation.Z, rotation.W) * RadiansToDegrees;
        if (angle > 180f)
        {
            angle -= 360f;
        }
        else if (angle <= -180f)
        {
            angle += 360f;
        }

        return angle;
    }

    private bool IsDiscontinuity(in DeviceMotionReport report, Quaternion raw)
    {
        var jump = QuaternionMath.AngleBetweenDegrees(_lastRaw, raw);
        if (jump <= JumpMarginDegrees)
        {
            return false;
        }

        if (report.TimestampNanoseconds <= _lastTimestamp)
        {
            return true;
        }

        var seconds = (report.TimestampNanoseconds - _lastTimestamp) / 1e9;
        if (seconds > 0.5)
        {
            // After a real gap, a large turn is plausible: keep the world heading.
            return false;
        }

        var rate = MathF.Min(report.RotationRateDegreesPerSecond, MaximumRotationRateDegreesPerSecond);
        return jump > (rate * seconds) + JumpMarginDegrees;
    }
}
