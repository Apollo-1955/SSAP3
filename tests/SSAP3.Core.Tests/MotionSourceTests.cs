using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using SSAP3.Core.Motion;
using SSAP3.Core.Motion.Companion;
using SSAP3.Core.Spatial;

namespace SSAP3.Core.Tests;

public class SimulatedPipelineTests
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(5);

    private static SimulatedMotionOptions Clean(SimulatedHeadMotion pattern, Func<TimeSpan, (float, float, float)>? script = null) => new()
    {
        Pattern = pattern,
        Script = script,
        DropoutEvery = TimeSpan.Zero,
        StallEvery = TimeSpan.Zero,
    };

    [Fact]
    public void Tracked_pipeline_follows_the_simulated_head_yaw()
    {
        var source = new SimulatedMotionSource(Clean(SimulatedHeadMotion.YawSweep));
        var estimator = new HeadPoseEstimator();
        var samples = new List<OrientationSample>();
        source.Start(TimeSpan.Zero);
        var errors = new List<double>();
        for (var now = TimeSpan.Zero; now < TimeSpan.FromSeconds(20); now += Step)
        {
            samples.Clear();
            source.Poll(now, samples);
            foreach (var sample in samples)
            {
                estimator.AddSample(sample);
            }

            if (now > TimeSpan.FromSeconds(2))
            {
                var truth = source.TrueAngles(now - TimeSpan.FromMilliseconds(20)).Yaw;
                errors.Add(estimator.GetPose(now).YawDegrees - truth);
            }
        }

        var rms = Math.Sqrt(errors.Average(e => e * e));
        Assert.InRange(rms, 0, 4.0);
        Assert.Equal(MotionSourceState.Streaming, source.GetHealth(TimeSpan.FromSeconds(20)).State);
        Assert.True(source.GetHealth(TimeSpan.FromSeconds(20)).IsSimulated);
    }

    [Fact]
    public void Nod_calibration_recovers_pitch_and_roll_for_an_arbitrary_mounting()
    {
        var target = (Yaw: 20f, Pitch: 15f, Roll: 10f);
        (float, float, float) Script(TimeSpan t) => t < TimeSpan.FromSeconds(7) ? (0f, 0f, 0f) : (target.Yaw, target.Pitch, target.Roll);
        var source = new SimulatedMotionSource(Clean(SimulatedHeadMotion.Scripted, Script) with { NoiseDegrees = 0.1 }, savedCalibration: null);
        var samples = new List<OrientationSample>();
        source.Start(TimeSpan.Zero);
        source.Poll(TimeSpan.FromSeconds(1), samples);
        Assert.True(source.BeginCalibration());
        for (var now = TimeSpan.FromSeconds(1); now < TimeSpan.FromSeconds(9); now += Step)
        {
            source.Poll(now, samples);
        }

        Assert.Equal(HeadAxisCalibrationState.Calibrated, source.CalibrationState);
        var last = samples[^1];
        Assert.True(last.HasRoll);
        var (yaw, pitch, roll) = QuaternionMath.ToYawPitchRollDegrees(last.Orientation);
        Assert.Equal(target.Yaw, yaw, 0);
        Assert.InRange(pitch, target.Pitch - 1.5f, target.Pitch + 1.5f);
        Assert.InRange(roll, target.Roll - 1.5f, target.Roll + 1.5f);

        // The saved axis works in a new session without nodding again (same fit).
        var second = new SimulatedMotionSource(Clean(SimulatedHeadMotion.Scripted, Script) with { NoiseDegrees = 0.1 }, savedCalibration: source.SavedCalibration);
        var fresh = new List<OrientationSample>();
        second.Start(TimeSpan.Zero);
        second.Poll(TimeSpan.FromSeconds(8), fresh);
        Assert.Equal(HeadAxisCalibrationState.Calibrated, second.CalibrationState);
        var (_, pitch2, roll2) = QuaternionMath.ToYawPitchRollDegrees(fresh[^1].Orientation);
        Assert.InRange(pitch2, target.Pitch - 1.5f, target.Pitch + 1.5f);
        Assert.InRange(roll2, target.Roll - 1.5f, target.Roll + 1.5f);
    }

    [Fact]
    public void Disconnections_degrade_and_recover_with_an_explicit_simulated_status()
    {
        var options = new SimulatedMotionOptions
        {
            Pattern = SimulatedHeadMotion.Natural,
            DropoutEvery = TimeSpan.FromSeconds(10),
            DropoutLength = TimeSpan.FromSeconds(3),
            StallEvery = TimeSpan.Zero,
        };
        var source = new SimulatedMotionSource(options);
        var estimator = new HeadPoseEstimator();
        var samples = new List<OrientationSample>();
        var kinds = new List<TrackingStatusKind>();
        source.Start(TimeSpan.Zero);
        for (var now = TimeSpan.Zero; now < TimeSpan.FromSeconds(16); now += Step)
        {
            samples.Clear();
            source.Poll(now, samples);
            foreach (var sample in samples)
            {
                estimator.AddSample(sample);
            }

            var status = TrackingStatusEvaluator.Evaluate(SpatialEngineMode.Tracked, true, source.GetHealth(now), estimator.GetPose(now));
            Assert.True(status.IsSimulated);
            Assert.StartsWith("SIMULATED", status.Summary, StringComparison.Ordinal);
            Assert.False(status.IsRealTracking);
            Assert.NotEqual(TrackingStatusKind.Unavailable, status.Kind);
            if (kinds.Count == 0 || kinds[^1] != status.Kind)
            {
                kinds.Add(status.Kind);
            }
        }

        var firstLive = kinds.IndexOf(TrackingStatusKind.Live);
        Assert.True(firstLive >= 0);
        var interruption = kinds.FindIndex(firstLive, kind => kind is TrackingStatusKind.Degraded or TrackingStatusKind.Recovering or TrackingStatusKind.Lost);
        Assert.True(interruption > firstLive);
        Assert.Contains(TrackingStatusKind.Live, kinds.Skip(interruption));
        Assert.Equal(TrackingStatusKind.Live, kinds[^1]);
    }

    [Fact]
    public void Calibration_fails_without_a_clean_nod_and_rejects_a_mismatched_saved_axis()
    {
        var mounting = Quaternion.Normalize(SensorPackets.About(new Vector3(1, 0.3f, 0), 35));
        HeadTrackingFrame Frame(Quaternion head, ulong ns)
        {
            var packet = SensorPackets.Motion(0x10, 16, SensorPackets.Report(Quaternion.Normalize(head * mounting), ns));
            Assert.True(HeadTrackingFrameParser.TryParse(packet, out var frame));
            return frame;
        }

        var still = new DeviceMotionOrientationDecoder();
        still.BeginCalibration();
        for (ulong ms = 0; ms <= 5000; ms += 20)
        {
            Assert.True(still.TryDecode(Frame(Quaternion.Identity, (ms * 1_000_000) + 1), out _));
        }

        Assert.Equal(HeadAxisCalibrationState.Failed, still.CalibrationState);
        Assert.False(still.ProvidesPitchRoll);

        // A saved "lateral" axis that is vertical in this session's world frame.
        var vertical = Vector3.Transform(Vector3.UnitZ, Quaternion.Conjugate(mounting));
        var mismatched = new DeviceMotionOrientationDecoder(lateralAxisInSensorFrame: vertical);
        Assert.True(mismatched.TryDecode(Frame(Quaternion.Identity, 1), out _));
        Assert.Equal(HeadAxisCalibrationState.Rejected, mismatched.CalibrationState);
        Assert.False(mismatched.ProvidesPitchRoll);
    }
}

public class TrackingStatusEvaluatorTests
{
    private static MotionSourceHealth Health(MotionSourceState state, bool simulated = false, string? problem = null) =>
        new(MotionSourceKind.AirPodsSensors, state, simulated, "AirPods motion sensors", problem, 50, TimeSpan.Zero, 10, 0, false, HeadAxisCalibrationState.None, null);

    [Fact]
    public void Fixed_and_off_ignore_motion()
    {
        Assert.Equal(TrackingStatusKind.Fixed, TrackingStatusEvaluator.Evaluate(SpatialEngineMode.Fixed, true, Health(MotionSourceState.Streaming), HeadPose.Uncalibrated).Kind);
        Assert.Equal(TrackingStatusKind.Off, TrackingStatusEvaluator.Evaluate(SpatialEngineMode.Off, true, null, HeadPose.Uncalibrated).Kind);
    }

    [Fact]
    public void Tracked_without_a_usable_source_says_why()
    {
        var status = TrackingStatusEvaluator.Evaluate(SpatialEngineMode.Tracked, true, Health(MotionSourceState.Unavailable, problem: "The SSAP3 AAP driver is not installed."), HeadPose.Uncalibrated);
        Assert.Equal(TrackingStatusKind.Unavailable, status.Kind);
        Assert.Contains("driver is not installed", status.Detail, StringComparison.Ordinal);
        Assert.False(status.IsRealTracking);

        var disabled = TrackingStatusEvaluator.Evaluate(SpatialEngineMode.Tracked, false, Health(MotionSourceState.Streaming), HeadPose.Uncalibrated);
        Assert.Equal(TrackingStatusKind.Unavailable, disabled.Kind);
    }

    [Fact]
    public void Only_real_live_measurements_count_as_real_tracking()
    {
        var live = new HeadPose(Quaternion.Identity, 0, 0, 0, TrackingState.Live, TimeSpan.Zero, true);
        Assert.True(TrackingStatusEvaluator.Evaluate(SpatialEngineMode.Tracked, true, Health(MotionSourceState.Streaming), live).IsRealTracking);
        Assert.False(TrackingStatusEvaluator.Evaluate(SpatialEngineMode.Tracked, true, Health(MotionSourceState.Streaming, simulated: true), live).IsRealTracking);
        Assert.Equal(TrackingStatusKind.Recovering, TrackingStatusEvaluator.Evaluate(SpatialEngineMode.Tracked, true, Health(MotionSourceState.Stalled), live with { State = TrackingState.Lost }).Kind);
    }
}

public class CompanionProtocolTests
{
    [Fact]
    public void Handshake_authenticates_both_sides_and_derives_the_same_key()
    {
        var key = CompanionProtocol.CreatePairingKey();
        var clientNonce = CompanionProtocol.CreateNonce();
        var serverNonce = CompanionProtocol.CreateNonce();
        var hello = CompanionProtocol.CreateClientHello(clientNonce);
        var parsedClientNonce = new byte[32];
        Assert.True(CompanionProtocol.TryParseClientHello(hello, parsedClientNonce));
        Assert.Equal(clientNonce, parsedClientNonce);

        var serverHello = CompanionProtocol.CreateServerHello(key, clientNonce, serverNonce);
        var parsedServerNonce = new byte[32];
        Assert.True(CompanionProtocol.TryVerifyServerHello(key, clientNonce, serverHello, parsedServerNonce));
        Assert.Equal(serverNonce, parsedServerNonce);
        Assert.False(CompanionProtocol.TryVerifyServerHello(CompanionProtocol.CreatePairingKey(), clientNonce, serverHello, parsedServerNonce));
        Assert.False(CompanionProtocol.TryVerifyServerHello(key, CompanionProtocol.CreateNonce(), serverHello, parsedServerNonce));

        var proof = CompanionProtocol.CreateClientProof(key, clientNonce, serverNonce);
        Assert.True(CompanionProtocol.VerifyClientProof(key, clientNonce, serverNonce, proof));
        Assert.False(CompanionProtocol.VerifyClientProof(key, serverNonce, clientNonce, proof));
        Assert.Equal(CompanionProtocol.DeriveSessionKey(key, clientNonce, serverNonce), CompanionProtocol.DeriveSessionKey(key, clientNonce, serverNonce));
        Assert.NotEqual(CompanionProtocol.DeriveSessionKey(key, clientNonce, serverNonce), CompanionProtocol.DeriveSessionKey(key, serverNonce, clientNonce));
    }

    [Fact]
    public void Records_are_authenticated_and_validated()
    {
        using var aes = new AesGcm(RandomNumberGenerator.GetBytes(32), 16);
        var sample = new CompanionSample(CompanionSampleKind.HeadOrientation, CompanionSampleFlags.PitchRoll, 123456, QuaternionMath.FromYawPitchRollDegrees(30, 10, 5), 42f);
        var record = CompanionProtocol.SealRecord(aes, 7, sample);
        Assert.Equal(CompanionProtocol.RecordLength, record.Length);
        Assert.True(CompanionProtocol.TryOpenRecord(aes, record, out var counter, out var opened));
        Assert.Equal(7UL, counter);
        Assert.Equal(sample.TimestampMicroseconds, opened.TimestampMicroseconds);
        Assert.True(QuaternionMath.AngleBetweenDegrees(sample.Orientation, opened.Orientation) < 0.01f);

        for (var index = 0; index < record.Length; index++)
        {
            var tampered = (byte[])record.Clone();
            tampered[index] ^= 0x01;
            Assert.False(CompanionProtocol.TryOpenRecord(aes, tampered, out _, out _));
        }

        Assert.False(CompanionProtocol.TryOpenRecord(aes, record.AsSpan(0, record.Length - 1), out _, out _));
        var invalid = CompanionProtocol.SealRecord(aes, 8, sample with { Orientation = new Quaternion(1, 1, 1, 1) });
        Assert.False(CompanionProtocol.TryOpenRecord(aes, invalid, out _, out _));
        var nan = CompanionProtocol.SealRecord(aes, 9, sample with { RotationRateDegreesPerSecond = float.NaN });
        Assert.False(CompanionProtocol.TryOpenRecord(aes, nan, out _, out _));
    }

    [Fact]
    public void Pairing_code_round_trips_and_rejects_bad_input()
    {
        // A fixed key keeps every check deterministic; a fresh random key is
        // round-tripped as well.
        var fixedKey = Enumerable.Range(0, CompanionProtocol.KeyLength).Select(index => (byte)((index * 37) + 11)).ToArray();
        foreach (var key in new[] { fixedKey, CompanionProtocol.CreatePairingKey() })
        {
            var code = CompanionProtocol.FormatPairingCode(key);
            Assert.Equal(52 + 12, code.Length);
            Assert.True(CompanionProtocol.TryParsePairingCode(code.ToLowerInvariant(), out var parsed));
            Assert.Equal(key, parsed);
            Assert.False(CompanionProtocol.TryParsePairingCode(code[..^1], out _));

            // '1' is never a Base32 (RFC 4648) character. The earlier check
            // replaced 'A' with '1', which changed nothing, and so failed,
            // whenever a random code had no 'A' (about 19 % of keys).
            Assert.False(CompanionProtocol.TryParsePairingCode("1" + code[1..], out _));
        }

        Assert.False(CompanionProtocol.TryParsePairingCode("not a code", out _));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.20.1.1", true)]
    [InlineData("100.100.1.1", true)]
    [InlineData("169.254.3.4", true)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("100.128.0.1", false)]
    [InlineData("2001:4860:4860::8888", false)]
    public void Connects_only_to_local_or_private_addresses(string address, bool allowed) =>
        Assert.Equal(allowed, CompanionProtocol.IsAllowedAddress(IPAddress.Parse(address)));
}

public class CompanionMotionSourceTests
{
    private static TimeSpan Now() => TimeSpan.FromTicks(System.Diagnostics.Stopwatch.GetTimestamp() * TimeSpan.TicksPerSecond / System.Diagnostics.Stopwatch.Frequency);

    /// <summary>Minimal reference server: one session, streams head-orientation samples.</summary>
    private static async Task ServeAsync(TcpListener listener, byte[] key, CompanionSampleFlags flags, int count, bool replay, CancellationToken token)
    {
        using var client = await listener.AcceptTcpClientAsync(token);
        var stream = client.GetStream();
        var hello = new byte[CompanionProtocol.ClientHelloLength];
        await stream.ReadExactlyAsync(hello, token);
        var clientNonce = new byte[32];
        Assert.True(CompanionProtocol.TryParseClientHello(hello, clientNonce));
        var serverNonce = CompanionProtocol.CreateNonce();
        await stream.WriteAsync(CompanionProtocol.CreateServerHello(key, clientNonce, serverNonce), token);
        var proof = new byte[32];
        await stream.ReadExactlyAsync(proof, token);
        if (!CompanionProtocol.VerifyClientProof(key, clientNonce, serverNonce, proof))
        {
            return;
        }

        using var aes = new AesGcm(CompanionProtocol.DeriveSessionKey(key, clientNonce, serverNonce), 16);
        for (var index = 1; index <= count; index++)
        {
            var counter = replay && index > 5 ? 5UL : (ulong)index;
            var sample = new CompanionSample(CompanionSampleKind.HeadOrientation, flags, (ulong)index * 20_000, QuaternionMath.FromYawPitchRollDegrees(30, 0, 0), 0);
            await stream.WriteAsync(CompanionProtocol.SealRecord(aes, counter, sample), token);
            await Task.Delay(20, token);
        }

        await Task.Delay(500, token);
    }

    private static async Task<MotionSourceHealth> RunAsync(byte[] serverKey, byte[] clientKey, CompanionSampleFlags flags, bool replay, List<OrientationSample> samples)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = ServeAsync(listener, serverKey, flags, 25, replay, cancel.Token);
        using var source = new CompanionMotionSource("127.0.0.1", port, clientKey, Now);
        source.Start(Now());
        var deadline = Now() + TimeSpan.FromSeconds(4);
        MotionSourceHealth health = source.GetHealth(Now());
        while (Now() < deadline)
        {
            source.Poll(Now(), samples);
            health = source.GetHealth(Now());
            if (samples.Count >= 20 || (replay && health.Problem is not null && health.State == MotionSourceState.Reconnecting) ||
                (!serverKey.AsSpan().SequenceEqual(clientKey) && health.Problem is not null))
            {
                break;
            }

            await Task.Delay(10);
        }

        source.Stop();
        try
        {
            await server;
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException)
        {
        }

        return health;
    }

    [Fact]
    public async Task Streams_authenticated_samples_and_flags_simulated_senders()
    {
        var key = CompanionProtocol.CreatePairingKey();
        var samples = new List<OrientationSample>();
        var health = await RunAsync(key, key, CompanionSampleFlags.Simulated, replay: false, samples);
        Assert.True(samples.Count >= 20);
        Assert.Equal(30f, QuaternionMath.ToYawPitchRollDegrees(samples[^1].Orientation).Yaw, 1);
        Assert.True(health.IsSimulated);
        Assert.Contains("SIMULATED", health.Description, StringComparison.Ordinal);
        Assert.True(samples.Zip(samples.Skip(1)).All(pair => pair.Second.Timestamp > pair.First.Timestamp));
    }

    [Fact]
    public async Task Rejects_a_companion_without_the_pairing_key()
    {
        var samples = new List<OrientationSample>();
        var health = await RunAsync(CompanionProtocol.CreatePairingKey(), CompanionProtocol.CreatePairingKey(), CompanionSampleFlags.None, replay: false, samples);
        Assert.Empty(samples);
        Assert.Contains("pairing key", health.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Drops_the_connection_on_a_replayed_record()
    {
        var key = CompanionProtocol.CreatePairingKey();
        var samples = new List<OrientationSample>();
        var health = await RunAsync(key, key, CompanionSampleFlags.None, replay: true, samples);
        Assert.InRange(samples.Count, 1, 5);
        Assert.False(health.IsSimulated);
    }

    [Fact]
    public void Refuses_public_addresses()
    {
        var source = new CompanionMotionSource("8.8.8.8", CompanionProtocol.DefaultPort, CompanionProtocol.CreatePairingKey(), Now);
        source.Start(Now());
        var deadline = Now() + TimeSpan.FromSeconds(3);
        while (Now() < deadline && source.GetHealth(Now()).Problem is null)
        {
            Thread.Sleep(10);
        }

        Assert.Contains("only connects to local companions", source.GetHealth(Now()).Problem, StringComparison.Ordinal);
        source.Dispose();
    }
}

public class AirPodsSensorMotionSourceTests
{
    [Fact]
    public void Sends_owns_connection_then_a_start_command_and_decodes_reports()
    {
        var sent = new List<byte[]>();
        var source = new AirPodsSensorMotionSource(invertYaw: false, invertPitch: false, savedCalibration: null);
        source.Start(TimeSpan.Zero);
        Assert.Equal(MotionSourceState.Unavailable, source.GetHealth(TimeSpan.Zero).State);

        source.AttachChannel(sent.Add);
        var samples = new List<OrientationSample>();
        source.Poll(TimeSpan.FromMilliseconds(10), samples);
        Assert.Equal("0400040009000601000000", Convert.ToHexString(sent[0]));
        source.Poll(TimeSpan.FromMilliseconds(400), samples);
        Assert.Equal(2, sent.Count);
        Assert.True(DriverAllowlistMirror.IsAllowedSensorSetting(sent[1]));

        // An exactly-identity attitude (x = y = z = 0) means "no data yet" and is rejected, so start at 5 degrees.
        Assert.True(source.OnPacket(SensorPackets.Motion(0x10, 16, SensorPackets.Report(SensorPackets.Yaw(5), 1_000_000)), TimeSpan.FromMilliseconds(420)));
        Assert.True(source.OnPacket(SensorPackets.Motion(0x11, 16, SensorPackets.Report(SensorPackets.Yaw(30), 900_000_000)), TimeSpan.FromMilliseconds(440)));
        source.Poll(TimeSpan.FromMilliseconds(450), samples);
        Assert.Equal(2, samples.Count);
        Assert.Equal(25f, QuaternionMath.ToYawPitchRollDegrees(samples[^1].Orientation).Yaw, 0);
        Assert.Equal(MotionSourceState.Streaming, source.GetHealth(TimeSpan.FromMilliseconds(460)).State);
        Assert.False(source.OnPacket(Convert.FromHexString("0400040009000D03000000"), TimeSpan.FromMilliseconds(470)));

        source.DetachChannel("AirPods disconnected.");
        var health = source.GetHealth(TimeSpan.FromMilliseconds(500));
        Assert.Equal(MotionSourceState.Unavailable, health.State);
        Assert.Equal("AirPods disconnected.", health.Problem);
    }
}
