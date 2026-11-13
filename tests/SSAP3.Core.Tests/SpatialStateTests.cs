using System.Buffers.Binary;
using System.Numerics;
using SSAP3.Core.Motion;
using SSAP3.Core.Spatial;

namespace SSAP3.Core.Tests;

public class SharedStateLayoutTests
{
    [Fact]
    public void Offsets_match_the_native_contract()
    {
        // Mirrors the static_asserts in audio/SSAP3Spatializer/ssap3_shared_state.h.
        Assert.Equal(8, SharedStateLayout.SequenceOffset);
        Assert.Equal(12, SharedStateLayout.ModeOffset);
        Assert.Equal(16, SharedStateLayout.TrackingStateOffset);
        Assert.Equal(24, SharedStateLayout.OrientationOffset);
        Assert.Equal(40, SharedStateLayout.YawOffset);
        Assert.Equal(56, SharedStateLayout.WriterTickOffset);
        Assert.Equal(64, SharedStateLayout.SpeakerAzimuthOffset);
        Assert.Equal(80, SharedStateLayout.SpeakerElevationOffset);
        Assert.Equal(96, SharedStateLayout.RecordSize);
        Assert.Equal(0x50483353u, SharedStateLayout.Magic);
    }

    [Fact]
    public void Round_trips_a_tracked_state()
    {
        var record = new byte[SharedStateLayout.RecordSize];
        var orientation = QuaternionMath.FromYawPitchRollDegrees(25, -5, 3);
        var stage = new SoundstageSettings { SpeakerAzimuthDegrees = 40, SpeakerDistanceMeters = 1.5f, RoomMix = 0.3f, Intensity = 0.8f };
        SharedStateLayout.WritePayload(record, new SpatialEngineState(SpatialEngineMode.Tracked, TrackingState.Live, orientation, stage), 123456);

        Assert.True(SharedStateLayout.TryRead(record, out var state, out var tick));
        Assert.Equal(123456ul, tick);
        Assert.Equal(SpatialEngineMode.Tracked, state.Mode);
        Assert.Equal(TrackingState.Live, state.Tracking);
        Assert.True(QuaternionMath.AngleBetweenDegrees(orientation, state.Orientation) < 0.01f);
        Assert.Equal(stage, state.Stage);
        Assert.Equal(25f, BinaryPrimitives.ReadSingleLittleEndian(record.AsSpan(SharedStateLayout.YawOffset)), 2);
    }

    [Fact]
    public void Sanitises_untrusted_values_before_publishing()
    {
        var record = new byte[SharedStateLayout.RecordSize];
        var stage = new SoundstageSettings { SpeakerAzimuthDegrees = float.NaN, SpeakerDistanceMeters = 99, RoomMix = -3, Intensity = float.PositiveInfinity };
        SharedStateLayout.WritePayload(
            record,
            new SpatialEngineState((SpatialEngineMode)42, (TrackingState)(-1), new Quaternion(float.NaN, 0, 0, 0), stage),
            1);

        Assert.True(SharedStateLayout.TryRead(record, out var state, out _));
        Assert.Equal(SpatialEngineMode.Off, state.Mode);
        Assert.Equal(TrackingState.None, state.Tracking);
        Assert.Equal(Quaternion.Identity, state.Orientation);
        Assert.Equal(30f, state.Stage.SpeakerAzimuthDegrees);
        Assert.Equal(SoundstageSettings.MaxSpeakerDistance, state.Stage.SpeakerDistanceMeters);
        Assert.Equal(0f, state.Stage.RoomMix);
        Assert.Equal(1f, state.Stage.Intensity);
    }

    [Fact]
    public void Payload_never_touches_the_sequence_word()
    {
        var record = new byte[SharedStateLayout.RecordSize];
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(SharedStateLayout.SequenceOffset), 0x7777);
        SharedStateLayout.WritePayload(record, SpatialEngineState.Off, 1);
        Assert.Equal(0x7777, BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(SharedStateLayout.SequenceOffset)));
    }

    [Fact]
    public void Rejects_foreign_records()
    {
        Assert.False(SharedStateLayout.TryRead(new byte[SharedStateLayout.RecordSize], out _, out _));
        Assert.False(SharedStateLayout.TryRead(new byte[10], out _, out _));
        Assert.Throws<ArgumentException>(() => SharedStateLayout.WritePayload(new byte[10], SpatialEngineState.Off, 0));
    }
}
