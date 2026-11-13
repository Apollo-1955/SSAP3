using SSAP3.Core;
using SSAP3.Core.Aacp;
using SSAP3.Core.Motion;
using SSAP3.Core.Spatial;

namespace SSAP3.Core.Tests;

/// <summary>
/// Seeded randomized-input tests for every parser on an untrusted boundary
/// (Bluetooth AACP, BLE advertisements, shared state). Each test feeds random
/// buffers and mutations of valid packets and checks that parsers never throw
/// and never accept structurally invalid data. This is randomized mutation
/// testing, not coverage-guided fuzzing; see docs/FUZZING.md for the planned
/// libFuzzer/SharpFuzz harness.
/// </summary>
public class RandomizedRobustnessTests
{
    private const int Iterations = 200_000;

    private static readonly byte[][] Seeds =
    [
        Convert.FromHexString("0400040009000D03000000"),
        Convert.FromHexString("040004004B0002000101"),
        Convert.FromHexString("04000400040003020164020104016301010801110201"),
        HeadTrackingSeed(),
    ];

    private static byte[] HeadTrackingSeed()
    {
        var packet = new byte[72];
        new byte[] { 0x04, 0x00, 0x04, 0x00, 0x17, 0x00, 0x00, 0x00, 0x10, 0x00, 0x44, 0x00 }.CopyTo(packet, 0);
        return packet;
    }

    private static byte[] NextInput(Random random)
    {
        if (random.Next(3) == 0)
        {
            var buffer = new byte[random.Next(0, 600)];
            random.NextBytes(buffer);
            return buffer;
        }

        var seed = Seeds[random.Next(Seeds.Length)];
        var mutated = seed.ToList();
        var mutations = random.Next(1, 6);
        for (var index = 0; index < mutations; index++)
        {
            switch (random.Next(4))
            {
                case 0 when mutated.Count > 0:
                    mutated[random.Next(mutated.Count)] = (byte)random.Next(256);
                    break;
                case 1 when mutated.Count > 0:
                    mutated.RemoveAt(random.Next(mutated.Count));
                    break;
                case 2:
                    mutated.Insert(random.Next(mutated.Count + 1), (byte)random.Next(256));
                    break;
                default:
                    if (mutated.Count > 1)
                    {
                        mutated.RemoveRange(random.Next(mutated.Count), 1);
                    }

                    break;
            }
        }

        return [.. mutated];
    }

    [Fact]
    public void Aacp_parsers_are_total_and_bounded()
    {
        var random = new Random(20261113);
        var battery = new AirPodsBatteryPacketParser();
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var input = NextInput(random);
            if (AacpFrame.TryParse(input, out var frame))
            {
                Assert.InRange(input.Length, AacpFrame.HeaderLength, AacpFrame.MaximumLength);
                Assert.Equal(input.Length - AacpFrame.HeaderLength, frame.Payload.Length);
                if (ControlNotification.TryParse(frame, out _))
                {
                    Assert.Equal(AacpOpcodes.ControlCommand, frame.Opcode);
                }

                if (ConversationAwarenessLevel.TryParse(frame, out var level))
                {
                    Assert.InRange(level.Level, (byte)1, (byte)0x10);
                }
            }

            if (battery.TryParse(input, out var update))
            {
                Assert.NotNull(update);
                foreach (var value in new[] { update.LeftLevel, update.RightLevel, update.CaseLevel })
                {
                    if (value is { } percent)
                    {
                        Assert.InRange(percent, 0, 100);
                    }
                }
            }

            if (HeadTrackingFrameParser.TryParse(input, out var motion))
            {
                Assert.InRange(motion.PacketLength, HeadTrackingFrameParser.MinimumLength, HeadTrackingFrameParser.MaximumLength);
                Assert.True(new LegacyOpaqueOrientationDecoder().TryDecode(motion, out var orientation));
                Assert.True(QuaternionMath.IsFinite(orientation));
            }
        }
    }

    [Fact]
    public void Advertisement_parser_is_total()
    {
        var random = new Random(42);
        var parser = new AirPodsAdvertisementParser();
        var key = new byte[16];
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var input = new byte[random.Next(0, 64)];
            random.NextBytes(input);
            if (input.Length > 0 && random.Next(2) == 0)
            {
                input[0] = 0x07;
            }

            random.NextBytes(key);
            var useKey = random.Next(4) == 0;
            if (parser.TryParse("12:34:56:78:9A:BC", input, null, useKey ? key : null, out var status))
            {
                Assert.NotNull(status);
                foreach (var value in new[] { status.LeftBattery, status.RightBattery })
                {
                    if (value is { } percent)
                    {
                        Assert.InRange(percent, 0, 127);
                    }
                }
            }
        }
    }

    [Fact]
    public void Shared_state_reader_is_total()
    {
        var random = new Random(7);
        var buffer = new byte[SharedStateLayout.RecordSize];
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            random.NextBytes(buffer);
            if (random.Next(2) == 0)
            {
                BitConverter.TryWriteBytes(buffer.AsSpan(0), SharedStateLayout.Magic);
                BitConverter.TryWriteBytes(buffer.AsSpan(4), SharedStateLayout.Version);
            }

            if (SharedStateLayout.TryRead(buffer, out var state, out _))
            {
                Assert.True(Enum.IsDefined(state.Mode));
                Assert.True(Enum.IsDefined(state.Tracking));
                var sanitized = state.Stage.Sanitized();
                Assert.InRange(sanitized.SpeakerAzimuthDegrees, SoundstageSettings.MinSpeakerAzimuth, SoundstageSettings.MaxSpeakerAzimuth);
                Assert.InRange(sanitized.Intensity, 0f, 1f);
            }
        }
    }

    [Fact]
    public void Estimator_survives_hostile_sample_streams()
    {
        var random = new Random(99);
        var estimator = new HeadPoseEstimator();
        var t = TimeSpan.Zero;
        for (var iteration = 0; iteration < 50_000; iteration++)
        {
            t += TimeSpan.FromMilliseconds(random.Next(-5, 400));
            var q = new System.Numerics.Quaternion(
                RandomFloat(random),
                RandomFloat(random),
                RandomFloat(random),
                RandomFloat(random));
            if (random.Next(2) == 0)
            {
                q = QuaternionMath.NormalizeOrIdentity(q);
            }

            if (random.Next(500) == 0)
            {
                estimator.RequestRecenter();
            }

            estimator.AddSample(new OrientationSample(t, q, false));
            var pose = estimator.GetPose(t + TimeSpan.FromMilliseconds(random.Next(0, 3000)));
            Assert.True(QuaternionMath.IsFinite(pose.Orientation));
            Assert.True(float.IsFinite(pose.YawDegrees) && float.IsFinite(pose.PitchDegrees) && float.IsFinite(pose.RollDegrees));
            Assert.InRange(pose.Orientation.Length(), 0.999f, 1.001f);
        }
    }

    private static float RandomFloat(Random random) => random.Next(20) switch
    {
        0 => float.NaN,
        1 => float.PositiveInfinity,
        2 => float.NegativeInfinity,
        3 => float.MaxValue,
        _ => (float)((random.NextDouble() * 2) - 1),
    };
}
