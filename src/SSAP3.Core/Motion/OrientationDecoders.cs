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

public enum HeadAxisCalibrationState
{
    /// <summary>No lateral axis: yaw only.</summary>
    None,

    /// <summary>Collecting the guided nod.</summary>
    Collecting,

    /// <summary>Pitch and roll are decoded.</summary>
    Calibrated,

    /// <summary>The nod was too small or not clean enough: yaw only.</summary>
    Failed,

    /// <summary>A saved axis did not fit this session (the buds sit differently): yaw only.</summary>
    Rejected,
}

/// <summary>
/// Head orientation from the AirPods' fused attitude quaternion.
///
/// <para>Yaw. The attitude rotates sensor-frame vectors into a world frame
/// whose Z axis is vertical. Turning the head is therefore a rotation about
/// world Z, however the bud sits in the ear. The decoder takes the world-frame
/// rotation since the first sample, <c>r = q · conj(anchor)</c>. Without a
/// calibration it keeps only the twist of <c>r</c> about Z.</para>
///
/// <para>Pitch and roll. These need the head's left-right axis. A guided
/// calibration provides it (<see cref="BeginCalibration"/>): the listener nods
/// a few times, starting by looking up. The principal horizontal axis of the
/// nod's world-frame rotation increments is the lateral axis, and the first
/// motion gives its sign. The axis is stored in the bud's own sensor frame
/// (<see cref="LateralAxisInSensorFrame"/>) so later sessions can reuse it. A
/// saved axis that is no longer horizontal at the start of a session means the
/// fit changed; it is rejected and the decoder falls back to yaw only. With an
/// axis <c>e</c>, the head rotation is <c>C · r · conj(C)</c>, where <c>C</c> turns
/// <c>e</c> onto +X (SSAP3's "right").</para>
///
/// <para>Encoding and continuity. The Q15 encoding implies <c>w ≥ 0</c>, so the
/// whole quaternion can flip sign. <c>q</c> and <c>-q</c> are the same rotation,
/// so the result is unaffected. A jump of more than 30° in one report that the
/// measured rotation rate cannot explain, or a timestamp that goes backwards,
/// means the stream switched buds or restarted. The decoder then re-anchors so
/// the output continues smoothly instead of snapping.</para>
///
/// <para>Not verified on AirPods Pro 3 hardware yet.</para>
/// </summary>
public sealed class DeviceMotionOrientationDecoder : IOrientationDecoder
{
    public static readonly TimeSpan CalibrationWindow = TimeSpan.FromSeconds(4);

    private const float JumpMarginDegrees = 30f;
    private const float MaximumRotationRateDegreesPerSecond = 2000f;
    private const float RadiansToDegrees = 180f / MathF.PI;
    private const double MinimumNodRadians = 0.7; // about 40 degrees of accumulated nodding
    private const double MinimumAxisRatio = 4.0;
    private const float MinimumHorizontalComponent = 0.6f;

    private bool _hasAnchor;
    private Quaternion _anchor = Quaternion.Identity;
    private Quaternion _lastRaw = Quaternion.Identity;
    private Quaternion _lastRelative = Quaternion.Identity;
    private ulong _lastTimestamp;
    private Vector3? _lateralWorld;

    private ulong _calibrationStart;
    private Quaternion _calibrationPrevious;
    private bool _calibrationHasPrevious;
    private double _sxx;
    private double _sxy;
    private double _syy;
    private double _accumulated;
    private Vector2 _firstMotion;
    private bool _firstMotionFixed;

    public DeviceMotionOrientationDecoder(bool invertYaw = false, bool invertPitch = false, Vector3? lateralAxisInSensorFrame = null)
    {
        InvertYaw = invertYaw;
        InvertPitch = invertPitch;
        if (lateralAxisInSensorFrame is { } axis && float.IsFinite(axis.X) && float.IsFinite(axis.Y) && float.IsFinite(axis.Z) && axis.Length() > 0.5f)
        {
            LateralAxisInSensorFrame = Vector3.Normalize(axis);
        }
    }

    public bool InvertYaw { get; }

    public bool InvertPitch { get; }

    /// <summary>How many times the heading reference was moved after a discontinuity.</summary>
    public int Reanchors { get; private set; }

    public HeadAxisCalibrationState CalibrationState { get; private set; }

    /// <summary>The calibrated lateral axis in the bud's sensor frame (persist this), or null.</summary>
    public Vector3? LateralAxisInSensorFrame { get; private set; }

    public bool ProvidesPitchRoll => _lateralWorld is not null;

    public OrientationDecoderInfo Info => new(
        ProvidesPitchRoll ? "AirPods fused attitude (yaw, pitch, roll)" : "AirPods fused attitude (yaw only until calibrated)",
        HardwareVerified: false,
        ProvidesRoll: ProvidesPitchRoll,
        "SensorDataWX device-motion quaternion (Q15 vector part): yaw is its twist about the gravity-aligned Z axis; pitch and roll need the nod calibration. Unverified on AirPods Pro 3.");

    public void Reset()
    {
        _hasAnchor = false;
        _anchor = Quaternion.Identity;
        _lastRaw = Quaternion.Identity;
        _lastRelative = Quaternion.Identity;
        _lastTimestamp = 0;
        _lateralWorld = null;
        if (CalibrationState == HeadAxisCalibrationState.Collecting)
        {
            CalibrationState = HeadAxisCalibrationState.None;
        }
    }

    /// <summary>Starts the guided nod calibration (look up first, then nod a few times).</summary>
    public void BeginCalibration()
    {
        CalibrationState = HeadAxisCalibrationState.Collecting;
        _calibrationStart = 0;
        _calibrationHasPrevious = false;
        _sxx = _sxy = _syy = _accumulated = 0;
        _firstMotion = Vector2.Zero;
        _firstMotionFixed = false;
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
            ApplySavedAxis(raw);
        }
        else if (IsDiscontinuity(frame.Report, raw))
        {
            // Carry the lateral axis into the new frame by the heading part of the jump.
            if (_lateralWorld is { } lateral)
            {
                var jump = QuaternionMath.NormalizeOrIdentity(raw * Quaternion.Conjugate(_lastRaw));
                var heading = TwistAboutVerticalDegrees(jump) / RadiansToDegrees;
                _lateralWorld = Vector3.Transform(lateral, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, heading));
            }

            // Keep the previous output: raw · conj(newAnchor) == lastRelative.
            _anchor = QuaternionMath.NormalizeOrIdentity(Quaternion.Conjugate(_lastRelative) * raw);
            Reanchors++;
        }

        _lastRaw = raw;
        _lastTimestamp = frame.Report.TimestampNanoseconds;
        var relative = QuaternionMath.NormalizeOrIdentity(raw * Quaternion.Conjugate(_anchor));
        _lastRelative = relative;

        if (CalibrationState == HeadAxisCalibrationState.Collecting)
        {
            CollectCalibration(relative, frame.Report.TimestampNanoseconds);
        }

        float yaw;
        float pitch = 0f;
        float roll = 0f;
        if (_lateralWorld is { } axis)
        {
            var toHead = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.Atan2(axis.Y, axis.X));
            var head = QuaternionMath.NormalizeOrIdentity(toHead * relative * Quaternion.Conjugate(toHead));
            (yaw, pitch, roll) = QuaternionMath.ToYawPitchRollDegrees(head);
        }
        else
        {
            yaw = TwistAboutVerticalDegrees(relative);
        }

        if (InvertYaw)
        {
            yaw = -yaw;
        }

        if (InvertPitch)
        {
            pitch = -pitch;
        }

        orientation = QuaternionMath.FromYawPitchRollDegrees(yaw, pitch, roll);
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

    /// <summary>Rotation vector (axis times angle, radians) of a rotation, using the shortest arc.</summary>
    public static Vector3 RotationVector(Quaternion rotation)
    {
        rotation = QuaternionMath.NormalizeOrIdentity(rotation);
        if (rotation.W < 0)
        {
            rotation = Quaternion.Negate(rotation);
        }

        var vector = new Vector3(rotation.X, rotation.Y, rotation.Z);
        var sinHalf = vector.Length();
        if (sinHalf < 1e-7f)
        {
            return 2f * vector;
        }

        var angle = 2f * MathF.Atan2(sinHalf, rotation.W);
        return vector * (angle / sinHalf);
    }

    private void ApplySavedAxis(Quaternion neutral)
    {
        if (LateralAxisInSensorFrame is not { } saved)
        {
            return;
        }

        var world = Vector3.Transform(saved, neutral);
        var horizontal = new Vector3(world.X, world.Y, 0f);
        if (horizontal.Length() < MinimumHorizontalComponent)
        {
            _lateralWorld = null;
            CalibrationState = HeadAxisCalibrationState.Rejected;
            return;
        }

        _lateralWorld = Vector3.Normalize(horizontal);
        CalibrationState = HeadAxisCalibrationState.Calibrated;
    }

    private void CollectCalibration(Quaternion relative, ulong timestamp)
    {
        if (!_calibrationHasPrevious)
        {
            _calibrationPrevious = relative;
            _calibrationHasPrevious = true;
            _calibrationStart = timestamp;
            return;
        }

        var increment = RotationVector(relative * Quaternion.Conjugate(_calibrationPrevious));
        _calibrationPrevious = relative;
        var horizontal = new Vector2(increment.X, increment.Y);
        _sxx += horizontal.X * horizontal.X;
        _sxy += horizontal.X * horizontal.Y;
        _syy += horizontal.Y * horizontal.Y;
        _accumulated += horizontal.Length();
        if (!_firstMotionFixed)
        {
            _firstMotion += horizontal;
            if (_firstMotion.Length() > 5f / RadiansToDegrees)
            {
                _firstMotionFixed = true;
            }
        }

        if (timestamp < _calibrationStart || timestamp - _calibrationStart < (ulong)CalibrationWindow.Ticks * 100UL)
        {
            return;
        }

        var half = (_sxx - _syy) / 2.0;
        var spread = Math.Sqrt((half * half) + (_sxy * _sxy));
        var mean = (_sxx + _syy) / 2.0;
        var major = mean + spread;
        var minor = Math.Max(mean - spread, 1e-12);
        var theta = 0.5 * Math.Atan2(2.0 * _sxy, _sxx - _syy);
        var axis = new Vector3((float)Math.Cos(theta), (float)Math.Sin(theta), 0f);
        if (_accumulated < MinimumNodRadians || major / minor < MinimumAxisRatio || !_firstMotionFixed)
        {
            CalibrationState = HeadAxisCalibrationState.Failed;
            return;
        }

        // Looking up is a positive rotation about +X (right), so the first motion fixes the sign.
        if (Vector2.Dot(new Vector2(axis.X, axis.Y), _firstMotion) < 0)
        {
            axis = -axis;
        }

        _lateralWorld = axis;
        LateralAxisInSensorFrame = Vector3.Normalize(Vector3.Transform(axis, Quaternion.Conjugate(_anchor)));
        CalibrationState = HeadAxisCalibrationState.Calibrated;
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
