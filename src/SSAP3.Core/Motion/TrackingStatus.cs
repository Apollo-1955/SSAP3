using SSAP3.Core.Spatial;

namespace SSAP3.Core.Motion;

public enum TrackingStatusKind
{
    /// <summary>Spatial audio off.</summary>
    Off,

    /// <summary>Fixed soundstage: head movement is intentionally ignored.</summary>
    Fixed,

    /// <summary>Tracked was selected but no motion source can run. The stage stays centred.</summary>
    Unavailable,

    /// <summary>Connecting, waiting for the first measurement, or calibrating the reference.</summary>
    Starting,

    /// <summary>Measurements arrive and drive the stage.</summary>
    Live,

    /// <summary>Measurements paused briefly: the last orientation is held.</summary>
    Degraded,

    /// <summary>Measurements stopped: the stage eases back to centre while the source recovers.</summary>
    Lost,

    /// <summary>The source is reconnecting or restarting its stream.</summary>
    Recovering,
}

/// <summary>The single tracking state shown to the user and written to diagnostics.</summary>
public sealed record TrackingStatus(
    TrackingStatusKind Kind,
    bool IsSimulated,
    string Summary,
    string? Detail,
    MotionSourceKind? Source)
{
    /// <summary>True only when real (not simulated) measurements are driving the stage.</summary>
    public bool IsRealTracking => Kind == TrackingStatusKind.Live && !IsSimulated;
}

/// <summary>
/// Combines the active motion source's health and the estimator's pose into one
/// explicit tracking state. Audio playback never depends on it: the engine
/// keeps rendering, holds the last orientation while <see cref="TrackingStatusKind.Degraded"/>,
/// and eases to centre while <see cref="TrackingStatusKind.Lost"/>,
/// <see cref="TrackingStatusKind.Recovering"/> or <see cref="TrackingStatusKind.Unavailable"/>.
/// Tracked mode never silently behaves like Fixed mode: in those states the
/// status says why tracking is not live.
/// </summary>
public static class TrackingStatusEvaluator
{
    public static TrackingStatus Evaluate(SpatialEngineMode mode, bool headTrackingEnabled, MotionSourceHealth? health, HeadPose pose)
    {
        switch (mode)
        {
            case SpatialEngineMode.Off:
                return new(TrackingStatusKind.Off, false, "Spatial audio off", null, null);
            case SpatialEngineMode.Fixed:
                return new(TrackingStatusKind.Fixed, false, "Fixed soundstage", "Head movement is ignored by design.", null);
        }

        if (!headTrackingEnabled)
        {
            return new(TrackingStatusKind.Unavailable, false, "Head tracking disabled", "Turn head tracking on in Settings. The stage stays centred.", null);
        }

        if (health is null || health.State == MotionSourceState.Stopped)
        {
            return new(TrackingStatusKind.Starting, false, "Starting head tracking", null, health?.Kind);
        }

        var simulated = health.IsSimulated;
        var prefix = simulated ? "SIMULATED: " : string.Empty;
        TrackingStatus Make(TrackingStatusKind kind, string summary, string? detail) =>
            new(kind, simulated, prefix + summary, detail, health.Kind);

        switch (health.State)
        {
            case MotionSourceState.Unavailable:
                return Make(TrackingStatusKind.Unavailable, "Head tracking unavailable", (health.Problem ?? "The motion source cannot run.") + " The stage stays centred.");
            case MotionSourceState.Connecting:
            case MotionSourceState.WaitingForData:
                return Make(TrackingStatusKind.Starting, "Starting head tracking", health.Description);
            case MotionSourceState.Reconnecting:
            case MotionSourceState.Stalled:
            case MotionSourceState.Failed:
                var kind = pose.State == TrackingState.Holding ? TrackingStatusKind.Degraded : TrackingStatusKind.Recovering;
                return Make(kind, kind == TrackingStatusKind.Degraded ? "Tracking paused (holding)" : "Reconnecting head tracking", health.Problem);
        }

        if (!pose.Calibrated)
        {
            return Make(TrackingStatusKind.Starting, "Calibrating reference", "Face your screen for a moment.");
        }

        return pose.State switch
        {
            TrackingState.Live => Make(
                TrackingStatusKind.Live,
                health.ProvidesPitchRoll ? "Head tracking live" : "Head tracking live (yaw only)",
                health.ProvidesPitchRoll ? health.Description : health.Description + ". Calibrate to add pitch and roll."),
            TrackingState.Holding => Make(TrackingStatusKind.Degraded, "Tracking paused (holding)", "Measurements paused; holding the last orientation."),
            TrackingState.Lost => Make(TrackingStatusKind.Lost, "Tracking lost", "Easing back to centre until measurements return."),
            _ => Make(TrackingStatusKind.Starting, "Starting head tracking", health.Description),
        };
    }
}
