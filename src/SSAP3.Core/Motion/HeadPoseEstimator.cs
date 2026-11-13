using System.Numerics;

namespace SSAP3.Core.Motion;

public enum SampleDisposition
{
    Accepted,
    Calibrating,
    RejectedInvalid,
    RejectedTimestamp,
    RejectedOutlier,
}

/// <summary>Head orientation relative to the calibrated forward direction (the monitor).</summary>
public readonly record struct HeadPose(
    Quaternion Orientation,
    float YawDegrees,
    float PitchDegrees,
    float RollDegrees,
    TrackingState State,
    TimeSpan SampleAge,
    bool Calibrated)
{
    public static HeadPose Uncalibrated { get; } =
        new(Quaternion.Identity, 0f, 0f, 0f, TrackingState.None, TimeSpan.Zero, false);
}

public sealed record HeadPoseEstimatorOptions
{
    /// <summary>Samples averaged to define "forward" after start or recenter.</summary>
    public int CalibrationSampleCount { get; init; } = 8;

    /// <summary>One-Euro minimum cut-off: smoothing strength while the head is still.</summary>
    public double MinCutoffHz { get; init; } = 1.5;

    /// <summary>One-Euro speed coefficient: cut-off rises by this many Hz per degree/second.</summary>
    public double Beta { get; init; } = 0.05;

    public double MaxCutoffHz { get; init; } = 20;

    public double DerivativeCutoffHz { get; init; } = 1.0;

    /// <summary>Faster apparent motion than this is physically implausible and treated as a glitch.</summary>
    public double MaxAngularSpeedDegreesPerSecond { get; init; } = 1500;

    public int MaxConsecutiveOutliers { get; init; } = 3;

    /// <summary>Larger gaps between samples restart the filter and blend instead of interpolating.</summary>
    public TimeSpan MaxSampleGap { get; init; } = TimeSpan.FromMilliseconds(300);

    public TimeSpan HoldAfter { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan LostAfter { get; init; } = TimeSpan.FromMilliseconds(1500);

    public TimeSpan ReturnToCenterTime { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan ResumeBlendTime { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Slowly re-centres small, steady yaw offsets to cancel sensor drift.</summary>
    public bool DriftCorrection { get; init; } = true;

    public float DriftWindowDegrees { get; init; } = 12f;

    public float DriftStillSpeedDegreesPerSecond { get; init; } = 6f;

    public TimeSpan DriftSettleTime { get; init; } = TimeSpan.FromSeconds(4);

    public float DriftRateDegreesPerSecond { get; init; } = 0.5f;
}

/// <summary>
/// Turns timestamped orientation samples into a stable, computer-anchored head
/// pose: recenter calibration, timestamp-aware One-Euro smoothing on the
/// quaternion (SLERP), glitch rejection, gap handling, and defined
/// degraded states (Holding, then Lost with a smooth return to centre, then a
/// blended resume). Not thread-safe; drive it from a single motion thread.
/// </summary>
public sealed class HeadPoseEstimator
{
    private readonly HeadPoseEstimatorOptions _options;
    private readonly List<Quaternion> _calibration = [];
    private Quaternion _reference = Quaternion.Identity;
    private Quaternion _filtered = Quaternion.Identity;
    private Quaternion _lastRaw = Quaternion.Identity;
    private TimeSpan _lastTimestamp;
    private double _speedFiltered;
    private int _consecutiveOutliers;
    private bool _hasSample;
    private bool _recenterRequested = true;
    private bool _resumePending;
    private TimeSpan _resumeStart;
    private Quaternion _resumeFrom = Quaternion.Identity;
    private bool _still;
    private TimeSpan _stillSince;

    public HeadPoseEstimator(HeadPoseEstimatorOptions? options = null)
    {
        _options = options ?? new HeadPoseEstimatorOptions();
        if (_options.CalibrationSampleCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "At least one calibration sample is required.");
        }
    }

    public bool IsCalibrated { get; private set; }

    public bool IsRecentering => _recenterRequested;

    public int AcceptedSampleCount { get; private set; }

    public int RejectedSampleCount { get; private set; }

    /// <summary>Exponentially averaged sample rate of accepted samples.</summary>
    public double SampleRateHz { get; private set; }

    /// <summary>Starts a new calibration; the pose keeps using the previous reference until it completes.</summary>
    public void RequestRecenter()
    {
        _recenterRequested = true;
        _calibration.Clear();
    }

    public void Reset()
    {
        _calibration.Clear();
        _reference = Quaternion.Identity;
        _filtered = Quaternion.Identity;
        _lastRaw = Quaternion.Identity;
        _lastTimestamp = TimeSpan.Zero;
        _speedFiltered = 0;
        _consecutiveOutliers = 0;
        _hasSample = false;
        _recenterRequested = true;
        _resumePending = false;
        _still = false;
        IsCalibrated = false;
        SampleRateHz = 0;
    }

    public SampleDisposition AddSample(in OrientationSample sample)
    {
        var length = sample.Orientation.Length();
        if (!QuaternionMath.IsFinite(sample.Orientation) || !float.IsFinite(length) || length is < 0.9f or > 1.1f)
        {
            RejectedSampleCount++;
            return SampleDisposition.RejectedInvalid;
        }

        if (_hasSample && sample.Timestamp <= _lastTimestamp)
        {
            RejectedSampleCount++;
            return SampleDisposition.RejectedTimestamp;
        }

        var measured = QuaternionMath.NormalizeOrIdentity(sample.Orientation);
        var now = sample.Timestamp;

        if (!_hasSample)
        {
            StartFilter(measured, now, blend: false);
        }
        else
        {
            var dt = (now - _lastTimestamp).TotalSeconds;
            if (now - _lastTimestamp > _options.MaxSampleGap)
            {
                StartFilter(measured, now, blend: IsCalibrated);
            }
            else
            {
                var speed = QuaternionMath.AngleBetweenDegrees(_lastRaw, measured) / dt;
                if (speed > _options.MaxAngularSpeedDegreesPerSecond)
                {
                    _consecutiveOutliers++;
                    if (_consecutiveOutliers <= _options.MaxConsecutiveOutliers)
                    {
                        RejectedSampleCount++;
                        return SampleDisposition.RejectedOutlier;
                    }

                    // The "outliers" persist, so the sensor genuinely moved: re-acquire smoothly.
                    StartFilter(measured, now, blend: IsCalibrated);
                }
                else
                {
                    _consecutiveOutliers = 0;
                    _speedFiltered += Alpha(_options.DerivativeCutoffHz, dt) * (speed - _speedFiltered);
                    var cutoff = Math.Min(_options.MaxCutoffHz, _options.MinCutoffHz + (_options.Beta * _speedFiltered));
                    _filtered = QuaternionMath.Slerp(_filtered, measured, (float)Alpha(cutoff, dt));
                    var instantRate = 1.0 / dt;
                    SampleRateHz = SampleRateHz <= 0 ? instantRate : SampleRateHz + (0.1 * (instantRate - SampleRateHz));
                    ApplyDriftCorrection(now, dt);
                }
            }
        }

        _lastRaw = measured;
        _lastTimestamp = now;
        _hasSample = true;
        AcceptedSampleCount++;

        if (_recenterRequested)
        {
            _calibration.Add(measured);
            if (_calibration.Count < _options.CalibrationSampleCount)
            {
                return SampleDisposition.Calibrating;
            }

            var previousOutput = IsCalibrated ? CurrentRelative(now) : Quaternion.Identity;
            _reference = QuaternionMath.Average(_calibration);
            _calibration.Clear();
            _recenterRequested = false;
            _still = false;
            if (IsCalibrated)
            {
                BeginResumeBlend(previousOutput, now);
            }

            IsCalibrated = true;
        }

        return SampleDisposition.Accepted;
    }

    public HeadPose GetPose(TimeSpan now)
    {
        if (!IsCalibrated || !_hasSample)
        {
            return HeadPose.Uncalibrated;
        }

        var age = now > _lastTimestamp ? now - _lastTimestamp : TimeSpan.Zero;
        var live = QuaternionMath.Relative(_reference, _filtered);
        TrackingState state;
        Quaternion orientation;
        if (age <= _options.HoldAfter)
        {
            state = TrackingState.Live;
            orientation = live;
        }
        else if (age <= _options.LostAfter)
        {
            state = TrackingState.Holding;
            orientation = live;
        }
        else
        {
            state = TrackingState.Lost;
            var excess = (age - _options.LostAfter).TotalSeconds;
            var k = 1.0 - Math.Exp(-excess / Math.Max(0.001, _options.ReturnToCenterTime.TotalSeconds));
            orientation = QuaternionMath.Slerp(live, Quaternion.Identity, (float)k);
        }

        if (_resumePending)
        {
            var progress = (now - _resumeStart).TotalSeconds / Math.Max(0.001, _options.ResumeBlendTime.TotalSeconds);
            if (progress >= 1)
            {
                _resumePending = false;
            }
            else
            {
                var eased = (float)SmoothStep(Math.Max(0, progress));
                orientation = QuaternionMath.Slerp(_resumeFrom, orientation, eased);
            }
        }

        var (yaw, pitch, roll) = QuaternionMath.ToYawPitchRollDegrees(orientation);
        return new HeadPose(orientation, yaw, pitch, roll, state, age, true);
    }

    private Quaternion CurrentRelative(TimeSpan now) => GetPose(now).Orientation;

    private void StartFilter(Quaternion measured, TimeSpan now, bool blend)
    {
        if (blend)
        {
            BeginResumeBlend(CurrentRelative(now), now);
        }

        _filtered = measured;
        _speedFiltered = 0;
        _consecutiveOutliers = 0;
        _still = false;
    }

    private void BeginResumeBlend(Quaternion from, TimeSpan now)
    {
        _resumeFrom = from;
        _resumeStart = now;
        _resumePending = true;
    }

    private void ApplyDriftCorrection(TimeSpan now, double dt)
    {
        if (!_options.DriftCorrection || !IsCalibrated || _recenterRequested)
        {
            return;
        }

        var relative = QuaternionMath.Relative(_reference, _filtered);
        var (yaw, _, _) = QuaternionMath.ToYawPitchRollDegrees(relative);
        if (_speedFiltered > _options.DriftStillSpeedDegreesPerSecond || MathF.Abs(yaw) > _options.DriftWindowDegrees)
        {
            _still = false;
            return;
        }

        if (!_still)
        {
            _still = true;
            _stillSince = now;
            return;
        }

        if (now - _stillSince < _options.DriftSettleTime)
        {
            return;
        }

        var step = MathF.Min(MathF.Abs(yaw), _options.DriftRateDegreesPerSecond * (float)dt) * MathF.Sign(yaw);
        var correction = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, step * MathF.PI / 180f);
        _reference = QuaternionMath.NormalizeOrIdentity(_reference * correction);
    }

    private static double Alpha(double cutoffHz, double dt)
    {
        var tau = 1.0 / (2.0 * Math.PI * Math.Max(1e-3, cutoffHz));
        return dt / (dt + tau);
    }

    private static double SmoothStep(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - (2 * x));
    }
}
