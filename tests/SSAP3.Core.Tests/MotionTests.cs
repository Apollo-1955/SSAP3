using System.Numerics;
using SSAP3.Core.Motion;

namespace SSAP3.Core.Tests;

public class QuaternionMathTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(35, 0, 0)]
    [InlineData(-120, 20, 0)]
    [InlineData(10, -45, 15)]
    [InlineData(170, 60, -30)]
    public void Yaw_pitch_roll_round_trip(float yaw, float pitch, float roll)
    {
        var (y, p, r) = QuaternionMath.ToYawPitchRollDegrees(QuaternionMath.FromYawPitchRollDegrees(yaw, pitch, roll));
        Assert.Equal(yaw, y, 2);
        Assert.Equal(pitch, p, 2);
        Assert.Equal(roll, r, 2);
    }

    [Fact]
    public void Turning_left_moves_a_front_source_to_the_right_ear_side()
    {
        // Convention shared with the DSP: yaw +20 = head turned left;
        // a source straight ahead is then 20 degrees to the right of the nose.
        var head = QuaternionMath.FromYawPitchRollDegrees(20, 0, 0);
        var (azimuth, elevation) = QuaternionMath.HeadRelativeDirection(head, Vector3.UnitY);
        Assert.Equal(20f, azimuth, 3);
        Assert.Equal(0f, elevation, 3);
    }

    [Fact]
    public void Looking_up_lowers_a_front_source()
    {
        var head = QuaternionMath.FromYawPitchRollDegrees(0, 15, 0);
        var (_, elevation) = QuaternionMath.HeadRelativeDirection(head, Vector3.UnitY);
        Assert.Equal(-15f, elevation, 3);
    }

    [Fact]
    public void Relative_orientation_removes_the_reference()
    {
        var reference = QuaternionMath.FromYawPitchRollDegrees(40, 5, 0);
        var current = reference * QuaternionMath.FromYawPitchRollDegrees(-10, 0, 0);
        var (yaw, _, _) = QuaternionMath.ToYawPitchRollDegrees(QuaternionMath.Relative(reference, current));
        Assert.Equal(-10f, yaw, 2);
    }

    [Fact]
    public void Invalid_quaternions_become_identity()
    {
        Assert.Equal(Quaternion.Identity, QuaternionMath.NormalizeOrIdentity(new Quaternion(float.NaN, 0, 0, 1)));
        Assert.Equal(Quaternion.Identity, QuaternionMath.NormalizeOrIdentity(new Quaternion(0, 0, 0, 0)));
        Assert.Equal(Quaternion.Identity, QuaternionMath.Slerp(Quaternion.Identity, new Quaternion(float.PositiveInfinity, 0, 0, 0), 0.5f));
    }

    [Fact]
    public void Angle_between_is_symmetric_and_shortest()
    {
        var a = QuaternionMath.FromYawPitchRollDegrees(10, 0, 0);
        var b = QuaternionMath.FromYawPitchRollDegrees(-20, 0, 0);
        Assert.Equal(30f, QuaternionMath.AngleBetweenDegrees(a, b), 2);
        Assert.Equal(30f, QuaternionMath.AngleBetweenDegrees(b, Quaternion.Negate(a)), 2);
    }
}

public class HeadTrackingParserTests
{
    private static byte[] Frame(short o1, short o2, short o3, int length = 70)
    {
        var packet = new byte[length];
        new byte[] { 0x04, 0x00, 0x04, 0x00, 0x17, 0x00, 0x00, 0x00, 0x10, 0x00, 0x45, 0x00 }.CopyTo(packet, 0);
        BitConverter.TryWriteBytes(packet.AsSpan(43), o1);
        BitConverter.TryWriteBytes(packet.AsSpan(45), o2);
        BitConverter.TryWriteBytes(packet.AsSpan(47), o3);
        BitConverter.TryWriteBytes(packet.AsSpan(51), (short)-12);
        BitConverter.TryWriteBytes(packet.AsSpan(53), (short)34);
        return packet;
    }

    [Fact]
    public void Parses_documented_offsets()
    {
        Assert.True(HeadTrackingFrameParser.TryParse(Frame(1, 2, 3), out var frame));
        Assert.Equal((short)1, frame.Orientation1);
        Assert.Equal((short)2, frame.Orientation2);
        Assert.Equal((short)3, frame.Orientation3);
        Assert.Equal((short)-12, frame.HorizontalAcceleration);
        Assert.Equal((short)34, frame.VerticalAcceleration);
    }

    [Fact]
    public void Rejects_wrong_prefix_kind_and_length()
    {
        var wrongKind = Frame(0, 0, 0);
        wrongKind[10] = 0x46;
        Assert.False(HeadTrackingFrameParser.TryParse(wrongKind, out _));
        var wrongPrefix = Frame(0, 0, 0);
        wrongPrefix[4] = 0x18;
        Assert.False(HeadTrackingFrameParser.TryParse(wrongPrefix, out _));
        Assert.False(HeadTrackingFrameParser.TryParse(Frame(0, 0, 0, HeadTrackingFrameParser.MinimumLength).AsSpan(0, 54), out _));
        Assert.True(HeadTrackingFrameParser.TryParse(Frame(0, 0, 0, HeadTrackingFrameParser.MinimumLength), out _));
        Assert.False(HeadTrackingFrameParser.TryParse(new byte[513], out _));
    }

    [Fact]
    public void Legacy_decoder_uses_ssap3_yaw_sign_and_supports_inversion()
    {
        Assert.True(HeadTrackingFrameParser.TryParse(Frame(0, 4000, -4000), out var frame));
        Assert.True(new LegacyOpaqueOrientationDecoder().TryDecode(frame, out var normal));
        Assert.True(new LegacyOpaqueOrientationDecoder(invertYaw: true).TryDecode(frame, out var inverted));
        var (yaw, pitch, _) = QuaternionMath.ToYawPitchRollDegrees(normal);
        var (invertedYaw, _, _) = QuaternionMath.ToYawPitchRollDegrees(inverted);

        // Upstream: (o2 - o3) > 0 meant a RIGHT turn; SSAP3 yaw is positive LEFT.
        Assert.Equal(-22.5f, yaw, 2);
        Assert.Equal(22.5f, invertedYaw, 2);
        Assert.Equal(0f, pitch, 2);
        Assert.False(new LegacyOpaqueOrientationDecoder().Info.HardwareVerified);
    }

    [Fact]
    public void Legacy_decoder_clamps_extreme_values()
    {
        Assert.True(HeadTrackingFrameParser.TryParse(Frame(0, short.MaxValue, short.MaxValue), out var frame));
        Assert.True(new LegacyOpaqueOrientationDecoder().TryDecode(frame, out var orientation));
        var (_, pitch, _) = QuaternionMath.ToYawPitchRollDegrees(orientation);
        Assert.InRange(pitch, -89.01f, 89.01f);
    }
}

public class HeadPoseEstimatorTests
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(20);

    private static TimeSpan Feed(HeadPoseEstimator estimator, TimeSpan start, int count, Func<int, float> yaw)
    {
        var t = start;
        for (var index = 0; index < count; index++)
        {
            t += Step;
            estimator.AddSample(new OrientationSample(t, QuaternionMath.FromYawPitchRollDegrees(yaw(index), 0, 0), false));
        }

        return t;
    }

    /// <summary>A realistic head turn: about 300 degrees/second, then hold.</summary>
    private static TimeSpan Turn(HeadPoseEstimator estimator, TimeSpan start, float from, float to, int holdSamples)
    {
        var steps = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(to - from) / 6f));
        var t = Feed(estimator, start, steps, index => from + ((to - from) * (index + 1) / steps));
        return Feed(estimator, t, holdSamples, _ => to);
    }

    private static HeadPoseEstimator NewEstimator(bool driftCorrection = false) =>
        new(new HeadPoseEstimatorOptions { DriftCorrection = driftCorrection });

    private static double StandardDeviation(IReadOnlyCollection<float> values)
    {
        var mean = values.Average();
        return Math.Sqrt(values.Sum(value => (value - mean) * (value - mean)) / values.Count);
    }

    [Fact]
    public void First_samples_define_forward()
    {
        var estimator = NewEstimator();
        var t = Feed(estimator, TimeSpan.Zero, 7, _ => 50);
        Assert.False(estimator.IsCalibrated);
        Assert.Equal(TrackingState.None, estimator.GetPose(t).State);

        t = Feed(estimator, t, 20, _ => 50);
        var pose = estimator.GetPose(t);
        Assert.True(estimator.IsCalibrated);
        Assert.Equal(TrackingState.Live, pose.State);
        Assert.Equal(0f, pose.YawDegrees, 1);
    }

    [Fact]
    public void Follows_a_deliberate_turn_quickly()
    {
        var estimator = NewEstimator();
        var t = Feed(estimator, TimeSpan.Zero, 20, _ => 0);
        t = Turn(estimator, t, 0, 30, holdSamples: 15);
        Assert.InRange(estimator.GetPose(t).YawDegrees, 27f, 30.5f);
    }

    [Fact]
    public void Suppresses_small_jitter()
    {
        var estimator = NewEstimator();
        var random = new Random(1234);
        var t = Feed(estimator, TimeSpan.Zero, 20, _ => 0);
        var inputs = new List<float>();
        var outputs = new List<float>();
        for (var index = 0; index < 400; index++)
        {
            t += Step;
            var noisyYaw = (float)(random.NextDouble() - 0.5);
            inputs.Add(noisyYaw);
            estimator.AddSample(new OrientationSample(t, QuaternionMath.FromYawPitchRollDegrees(noisyYaw, 0, 0), false));
            outputs.Add(estimator.GetPose(t).YawDegrees);
        }

        var ratio = StandardDeviation(outputs) / StandardDeviation(inputs);
        Assert.True(ratio < 0.5, $"Jitter was only reduced to {ratio:P0} of the input.");
    }

    [Fact]
    public void Rejects_a_single_implausible_spike()
    {
        var estimator = NewEstimator();
        var t = Feed(estimator, TimeSpan.Zero, 20, _ => 0);
        t += Step;
        var disposition = estimator.AddSample(new OrientationSample(t, QuaternionMath.FromYawPitchRollDegrees(150, 0, 0), false));
        Assert.Equal(SampleDisposition.RejectedOutlier, disposition);
        t = Feed(estimator, t, 5, _ => 0);
        Assert.Equal(0f, estimator.GetPose(t).YawDegrees, 1);
    }

    [Fact]
    public void Accepts_a_persistent_jump_after_the_outlier_budget()
    {
        var estimator = NewEstimator();
        var t = Feed(estimator, TimeSpan.Zero, 20, _ => 0);
        var dispositions = new List<SampleDisposition>();
        for (var index = 0; index < 5; index++)
        {
            t += Step;
            dispositions.Add(estimator.AddSample(new OrientationSample(t, QuaternionMath.FromYawPitchRollDegrees(150, 0, 0), false)));
        }

        Assert.Equal(3, dispositions.Count(d => d == SampleDisposition.RejectedOutlier));
        Assert.Contains(SampleDisposition.Accepted, dispositions);
    }

    [Fact]
    public void Rejects_invalid_and_out_of_order_samples()
    {
        var estimator = NewEstimator();
        var t = Feed(estimator, TimeSpan.Zero, 20, _ => 0);
        Assert.Equal(SampleDisposition.RejectedInvalid,
            estimator.AddSample(new OrientationSample(t + Step, new Quaternion(float.NaN, 0, 0, 1), false)));
        Assert.Equal(SampleDisposition.RejectedInvalid,
            estimator.AddSample(new OrientationSample(t + Step, new Quaternion(0, 0, 0, 3), false)));
        Assert.Equal(SampleDisposition.RejectedTimestamp,
            estimator.AddSample(new OrientationSample(t, Quaternion.Identity, false)));
        Assert.Equal(SampleDisposition.RejectedTimestamp,
            estimator.AddSample(new OrientationSample(t - Step, Quaternion.Identity, false)));
    }

    [Fact]
    public void Holds_then_eases_back_to_centre_when_samples_stop()
    {
        var estimator = NewEstimator();
        var t = Feed(estimator, TimeSpan.Zero, 20, _ => 0);
        t = Turn(estimator, t, 0, 40, holdSamples: 30);
        var live = estimator.GetPose(t);
        Assert.Equal(TrackingState.Live, live.State);

        var holding = estimator.GetPose(t + TimeSpan.FromMilliseconds(600));
        Assert.Equal(TrackingState.Holding, holding.State);
        Assert.Equal(live.YawDegrees, holding.YawDegrees, 3);

        var lostSoon = estimator.GetPose(t + TimeSpan.FromSeconds(1.6));
        var lostLater = estimator.GetPose(t + TimeSpan.FromSeconds(5));
        Assert.Equal(TrackingState.Lost, lostSoon.State);
        Assert.True(MathF.Abs(lostSoon.YawDegrees) < MathF.Abs(live.YawDegrees));
        Assert.True(MathF.Abs(lostLater.YawDegrees) < 2f);
    }

    [Fact]
    public void Resumes_without_a_jump_after_tracking_loss()
    {
        var estimator = NewEstimator();
        var t = Feed(estimator, TimeSpan.Zero, 20, _ => 0);
        t = Turn(estimator, t, 0, 40, holdSamples: 30);
        t += TimeSpan.FromSeconds(4);
        var previous = estimator.GetPose(t).YawDegrees;
        var maxStep = 0f;
        for (var index = 0; index < 40; index++)
        {
            t += Step;
            estimator.AddSample(new OrientationSample(t, QuaternionMath.FromYawPitchRollDegrees(40, 0, 0), false));
            var yaw = estimator.GetPose(t).YawDegrees;
            maxStep = MathF.Max(maxStep, MathF.Abs(yaw - previous));
            previous = yaw;
        }

        Assert.True(maxStep < 6f, $"Largest per-sample step after resume was {maxStep:0.0} degrees.");
        Assert.InRange(previous, 37f, 41f);
    }

    [Fact]
    public void Recenter_moves_forward_smoothly_to_the_current_direction()
    {
        var estimator = NewEstimator();
        var t = Feed(estimator, TimeSpan.Zero, 20, _ => 0);
        t = Turn(estimator, t, 0, 35, holdSamples: 30);
        estimator.RequestRecenter();
        var previous = estimator.GetPose(t).YawDegrees;
        var maxStep = 0f;
        for (var index = 0; index < 40; index++)
        {
            t += Step;
            estimator.AddSample(new OrientationSample(t, QuaternionMath.FromYawPitchRollDegrees(35, 0, 0), false));
            var yaw = estimator.GetPose(t).YawDegrees;
            maxStep = MathF.Max(maxStep, MathF.Abs(yaw - previous));
            previous = yaw;
        }

        Assert.False(estimator.IsRecentering);
        Assert.Equal(0f, previous, 1);
        Assert.True(maxStep < 6f, $"Recenter stepped {maxStep:0.0} degrees in one sample.");
    }

    [Fact]
    public void Drift_correction_recentres_small_steady_offsets_only()
    {
        var drifting = NewEstimator(driftCorrection: true);
        var t = Feed(drifting, TimeSpan.Zero, 20, _ => 0);
        t = Turn(drifting, t, 0, 8, holdSamples: 1000);
        Assert.True(MathF.Abs(drifting.GetPose(t).YawDegrees) < 6f, "A steady 8 degree offset should be partly corrected after 20 s.");

        var lookingAway = NewEstimator(driftCorrection: true);
        var t2 = Feed(lookingAway, TimeSpan.Zero, 20, _ => 0);
        t2 = Turn(lookingAway, t2, 0, 30, holdSamples: 1000);
        Assert.Equal(30f, lookingAway.GetPose(t2).YawDegrees, 0);
    }
}

public class MotionStreamMonitorTests
{
    [Fact]
    public void Reports_real_stream_states()
    {
        var monitor = new MotionStreamMonitor(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(6));
        Assert.Equal(MotionStreamStatus.Inactive, monitor.Evaluate(TimeSpan.Zero));

        monitor.Start(TimeSpan.Zero);
        Assert.Equal(MotionStreamStatus.WaitingForFirstFrame, monitor.Evaluate(TimeSpan.FromSeconds(2)));
        for (var index = 1; index <= 50; index++)
        {
            monitor.OnFrame(TimeSpan.FromSeconds(2) + (index * TimeSpan.FromMilliseconds(20)));
        }

        Assert.Equal(MotionStreamStatus.Streaming, monitor.Evaluate(TimeSpan.FromSeconds(3.1)));
        Assert.InRange(monitor.FrameRateHz, 40, 60);
        Assert.Equal(MotionStreamStatus.Stalled, monitor.Evaluate(TimeSpan.FromSeconds(4.5)));
        Assert.Equal(1, monitor.StallCount);
    }

    [Fact]
    public void Reports_unsupported_when_no_frame_ever_arrives()
    {
        var monitor = new MotionStreamMonitor(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(6));
        monitor.Start(TimeSpan.FromSeconds(10));
        Assert.Equal(MotionStreamStatus.WaitingForFirstFrame, monitor.Evaluate(TimeSpan.FromSeconds(15)));
        Assert.Equal(MotionStreamStatus.Unsupported, monitor.Evaluate(TimeSpan.FromSeconds(16.5)));
    }
}
