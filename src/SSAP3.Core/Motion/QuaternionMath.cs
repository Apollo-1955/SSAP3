using System.Numerics;

namespace SSAP3.Core.Motion;

/// <summary>
/// Quaternion helpers for SSAP3's head-tracking frame.
///
/// Coordinate system (right-handed): +X = listener's right, +Y = forward
/// (towards the monitor), +Z = up. Orientations rotate head-frame vectors into
/// the calibrated world frame. Euler angles use intrinsic Z-X'-Y'' order:
/// yaw about +Z (positive = turn LEFT), then pitch about +X (positive = look
/// UP), then roll about +Y (positive = RIGHT ear down).
/// </summary>
public static class QuaternionMath
{
    private const float DegreesToRadians = MathF.PI / 180f;
    private const float RadiansToDegrees = 180f / MathF.PI;

    public static Quaternion FromYawPitchRollDegrees(float yaw, float pitch, float roll)
    {
        var qYaw = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yaw * DegreesToRadians);
        var qPitch = Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitch * DegreesToRadians);
        var qRoll = Quaternion.CreateFromAxisAngle(Vector3.UnitY, roll * DegreesToRadians);
        return Quaternion.Normalize(qYaw * qPitch * qRoll);
    }

    public static (float Yaw, float Pitch, float Roll) ToYawPitchRollDegrees(Quaternion q)
    {
        q = NormalizeOrIdentity(q);
        var (w, x, y, z) = (q.W, q.X, q.Y, q.Z);
        var r21 = 2f * ((y * z) + (w * x));
        var pitch = MathF.Asin(Math.Clamp(r21, -1f, 1f));
        var yaw = MathF.Atan2(-2f * ((x * y) - (w * z)), 1f - (2f * ((x * x) + (z * z))));
        var roll = MathF.Atan2(-2f * ((x * z) - (w * y)), 1f - (2f * ((x * x) + (y * y))));
        return (yaw * RadiansToDegrees, pitch * RadiansToDegrees, roll * RadiansToDegrees);
    }

    public static bool IsFinite(Quaternion q) =>
        float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W);

    public static Quaternion NormalizeOrIdentity(Quaternion q)
    {
        if (!IsFinite(q))
        {
            return Quaternion.Identity;
        }

        var length = q.Length();
        return length < 1e-6f || !float.IsFinite(length)
            ? Quaternion.Identity
            : Quaternion.Multiply(q, 1f / length);
    }

    /// <summary>Smallest rotation angle between two orientations, in degrees (0..180).</summary>
    public static float AngleBetweenDegrees(Quaternion a, Quaternion b)
    {
        var dot = MathF.Abs(Quaternion.Dot(NormalizeOrIdentity(a), NormalizeOrIdentity(b)));
        return 2f * MathF.Acos(Math.Clamp(dot, 0f, 1f)) * RadiansToDegrees;
    }

    /// <summary>Orientation of <paramref name="current"/> expressed relative to <paramref name="reference"/>.</summary>
    public static Quaternion Relative(Quaternion reference, Quaternion current) =>
        NormalizeOrIdentity(Quaternion.Conjugate(NormalizeOrIdentity(reference)) * NormalizeOrIdentity(current));

    /// <summary>Spherical interpolation along the shortest arc; t is clamped to [0, 1].</summary>
    public static Quaternion Slerp(Quaternion from, Quaternion to, float t) =>
        NormalizeOrIdentity(Quaternion.Slerp(
            NormalizeOrIdentity(from),
            NormalizeOrIdentity(to),
            float.IsFinite(t) ? Math.Clamp(t, 0f, 1f) : 0f));

    /// <summary>Sign-aligned mean of nearby orientations (valid for small spreads, as during calibration).</summary>
    public static Quaternion Average(IReadOnlyList<Quaternion> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            return Quaternion.Identity;
        }

        var first = NormalizeOrIdentity(samples[0]);
        var sum = Vector4.Zero;
        foreach (var sample in samples)
        {
            var q = NormalizeOrIdentity(sample);
            if (Quaternion.Dot(first, q) < 0f)
            {
                q = Quaternion.Negate(q);
            }

            sum += new Vector4(q.X, q.Y, q.Z, q.W);
        }

        return NormalizeOrIdentity(new Quaternion(sum.X, sum.Y, sum.Z, sum.W));
    }

    /// <summary>Rotates a world-frame direction into the head frame of <paramref name="orientation"/>.</summary>
    public static Vector3 WorldToHead(Quaternion orientation, Vector3 worldDirection) =>
        Vector3.Transform(worldDirection, Quaternion.Conjugate(NormalizeOrIdentity(orientation)));

    /// <summary>Head-relative azimuth (positive to the RIGHT) and elevation of a world direction, in degrees.</summary>
    public static (float Azimuth, float Elevation) HeadRelativeDirection(Quaternion orientation, Vector3 worldDirection)
    {
        var head = WorldToHead(orientation, worldDirection);
        var azimuth = MathF.Atan2(head.X, head.Y) * RadiansToDegrees;
        var elevation = MathF.Asin(Math.Clamp(head.Z / MathF.Max(head.Length(), 1e-6f), -1f, 1f)) * RadiansToDegrees;
        return (azimuth, elevation);
    }

    /// <summary>World direction of a source at <paramref name="azimuthDegrees"/> (positive right) and elevation.</summary>
    public static Vector3 DirectionFromAzimuthElevation(float azimuthDegrees, float elevationDegrees)
    {
        var azimuth = azimuthDegrees * DegreesToRadians;
        var elevation = elevationDegrees * DegreesToRadians;
        return new Vector3(
            MathF.Sin(azimuth) * MathF.Cos(elevation),
            MathF.Cos(azimuth) * MathF.Cos(elevation),
            MathF.Sin(elevation));
    }
}
