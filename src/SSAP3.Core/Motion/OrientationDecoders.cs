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

/// <summary>Turns raw AACP head-tracking frames into orientations. One implementation per verified device behaviour.</summary>
public interface IOrientationDecoder
{
    OrientationDecoderInfo Info { get; }

    bool TryDecode(in HeadTrackingFrame frame, out Quaternion orientation);
}

/// <summary>
/// The heuristic inherited from the LibrePods ecosystem via upstream
/// SoundStage: pitch = (o2 + o3) / 2 and yaw = (o2 - o3) / 2, scaled by
/// 180 / 32000. It was tuned by upstream on AirPods 4 (ANC) and is
/// <b>not verified on AirPods Pro 3</b>. Roll is not available. Upstream's
/// yaw was positive for a RIGHT turn, so it is negated into SSAP3's
/// convention (positive = left). Both axes can be inverted from
/// Calibration settings if a device turns out to use the opposite sign.
/// </summary>
public sealed class LegacyOpaqueOrientationDecoder : IOrientationDecoder
{
    private const float Scale = 180f / 32000f;

    public LegacyOpaqueOrientationDecoder(bool invertYaw = false, bool invertPitch = false)
    {
        InvertYaw = invertYaw;
        InvertPitch = invertPitch;
    }

    public bool InvertYaw { get; }

    public bool InvertPitch { get; }

    public OrientationDecoderInfo Info { get; } = new(
        "LibrePods opaque-field heuristic",
        HardwareVerified: false,
        ProvidesRoll: false,
        "Yaw/pitch derived from undocumented orientation fields 2 and 3; unverified on AirPods Pro 3.");

    public bool TryDecode(in HeadTrackingFrame frame, out Quaternion orientation)
    {
        var o2 = (float)frame.Orientation2;
        var o3 = (float)frame.Orientation3;
        var legacyYaw = (o2 - o3) * 0.5f * Scale;
        var pitch = (o2 + o3) * 0.5f * Scale;
        var yaw = InvertYaw ? legacyYaw : -legacyYaw;
        if (InvertPitch)
        {
            pitch = -pitch;
        }

        yaw = Math.Clamp(yaw, -180f, 180f);
        pitch = Math.Clamp(pitch, -89f, 89f);
        orientation = QuaternionMath.FromYawPitchRollDegrees(yaw, pitch, 0f);
        return QuaternionMath.IsFinite(orientation);
    }
}
