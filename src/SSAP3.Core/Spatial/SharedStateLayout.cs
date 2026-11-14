using System.Buffers.Binary;
using System.Numerics;
using SSAP3.Core.Motion;

namespace SSAP3.Core.Spatial;

public enum SpatialEngineMode
{
    Off = 0,
    Fixed = 1,
    Tracked = 2,
}

/// <summary>Everything the SSAP3 Spatial Engine needs for one update.</summary>
public readonly record struct SpatialEngineState(
    SpatialEngineMode Mode,
    TrackingState Tracking,
    Quaternion Orientation,
    SoundstageSettings Stage)
{
    public static SpatialEngineState Off { get; } =
        new(SpatialEngineMode.Off, TrackingState.None, Quaternion.Identity, SoundstageSettings.Default);
}

/// <summary>
/// Binary layout of the shared state record (version 2). Must stay byte-for-byte
/// identical to <c>ssap3::SharedStateV2</c> in
/// audio/SSAP3Spatializer/ssap3_shared_state.h.
/// </summary>
public static class SharedStateLayout
{
    public const uint Magic = 0x50483353; // "S3HP"
    public const uint Version = 2;
    public const int RecordSize = 96;
    public const int FileSize = 4096;

    public const int MagicOffset = 0;
    public const int VersionOffset = 4;
    public const int SequenceOffset = 8;
    public const int ModeOffset = 12;
    public const int TrackingStateOffset = 16;

    /// <summary>Was reserved (always 0, which is <see cref="SpatialRenderer.Measured"/>).</summary>
    public const int RendererOffset = 20;
    public const int OrientationOffset = 24;
    public const int YawOffset = 40;
    public const int PitchOffset = 44;
    public const int RollOffset = 48;
    public const int WriterTickOffset = 56;
    public const int SpeakerAzimuthOffset = 64;
    public const int SpeakerDistanceOffset = 68;
    public const int RoomMixOffset = 72;
    public const int IntensityOffset = 76;
    public const int SpeakerElevationOffset = 80;

    /// <summary>
    /// Writes the payload (everything except the sequence word) into <paramref name="record"/>.
    /// Non-finite or out-of-range inputs are sanitised so the engine never has to reject them.
    /// </summary>
    public static void WritePayload(Span<byte> record, in SpatialEngineState state, ulong writerTickMs)
    {
        if (record.Length < RecordSize)
        {
            throw new ArgumentException($"The record needs {RecordSize} bytes.", nameof(record));
        }

        var stage = (state.Stage ?? SoundstageSettings.Default).Sanitized();
        var orientation = QuaternionMath.NormalizeOrIdentity(state.Orientation);
        var (yaw, pitch, roll) = QuaternionMath.ToYawPitchRollDegrees(orientation);
        var mode = Enum.IsDefined(state.Mode) ? state.Mode : SpatialEngineMode.Off;
        var tracking = Enum.IsDefined(state.Tracking) ? state.Tracking : TrackingState.None;

        BinaryPrimitives.WriteUInt32LittleEndian(record[MagicOffset..], Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(record[VersionOffset..], Version);
        BinaryPrimitives.WriteInt32LittleEndian(record[ModeOffset..], (int)mode);
        BinaryPrimitives.WriteInt32LittleEndian(record[TrackingStateOffset..], (int)tracking);
        BinaryPrimitives.WriteInt32LittleEndian(record[RendererOffset..], (int)stage.Renderer);
        BinaryPrimitives.WriteSingleLittleEndian(record[OrientationOffset..], orientation.W);
        BinaryPrimitives.WriteSingleLittleEndian(record[(OrientationOffset + 4)..], orientation.X);
        BinaryPrimitives.WriteSingleLittleEndian(record[(OrientationOffset + 8)..], orientation.Y);
        BinaryPrimitives.WriteSingleLittleEndian(record[(OrientationOffset + 12)..], orientation.Z);
        BinaryPrimitives.WriteSingleLittleEndian(record[YawOffset..], yaw);
        BinaryPrimitives.WriteSingleLittleEndian(record[PitchOffset..], pitch);
        BinaryPrimitives.WriteSingleLittleEndian(record[RollOffset..], roll);
        BinaryPrimitives.WriteSingleLittleEndian(record[52..], 0f);
        BinaryPrimitives.WriteUInt64LittleEndian(record[WriterTickOffset..], writerTickMs);
        BinaryPrimitives.WriteSingleLittleEndian(record[SpeakerAzimuthOffset..], stage.SpeakerAzimuthDegrees);
        BinaryPrimitives.WriteSingleLittleEndian(record[SpeakerDistanceOffset..], stage.SpeakerDistanceMeters);
        BinaryPrimitives.WriteSingleLittleEndian(record[RoomMixOffset..], stage.RoomMix);
        BinaryPrimitives.WriteSingleLittleEndian(record[IntensityOffset..], stage.Intensity);
        BinaryPrimitives.WriteSingleLittleEndian(record[SpeakerElevationOffset..], stage.SpeakerElevationDegrees);
        record[84..RecordSize].Clear();
    }

    /// <summary>Reads a record back (used by diagnostics and tests). Returns false when it is not a valid v2 record.</summary>
    public static bool TryRead(ReadOnlySpan<byte> record, out SpatialEngineState state, out ulong writerTickMs)
    {
        state = SpatialEngineState.Off;
        writerTickMs = 0;
        if (record.Length < RecordSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(record[MagicOffset..]) != Magic ||
            BinaryPrimitives.ReadUInt32LittleEndian(record[VersionOffset..]) != Version)
        {
            return false;
        }

        var mode = (SpatialEngineMode)BinaryPrimitives.ReadInt32LittleEndian(record[ModeOffset..]);
        var tracking = (TrackingState)BinaryPrimitives.ReadInt32LittleEndian(record[TrackingStateOffset..]);
        var renderer = (SpatialRenderer)BinaryPrimitives.ReadInt32LittleEndian(record[RendererOffset..]);
        if (!Enum.IsDefined(mode) || !Enum.IsDefined(tracking) || !Enum.IsDefined(renderer))
        {
            return false;
        }

        var orientation = new Quaternion(
            BinaryPrimitives.ReadSingleLittleEndian(record[(OrientationOffset + 4)..]),
            BinaryPrimitives.ReadSingleLittleEndian(record[(OrientationOffset + 8)..]),
            BinaryPrimitives.ReadSingleLittleEndian(record[(OrientationOffset + 12)..]),
            BinaryPrimitives.ReadSingleLittleEndian(record[OrientationOffset..]));
        var stage = new SoundstageSettings
        {
            SpeakerAzimuthDegrees = BinaryPrimitives.ReadSingleLittleEndian(record[SpeakerAzimuthOffset..]),
            SpeakerDistanceMeters = BinaryPrimitives.ReadSingleLittleEndian(record[SpeakerDistanceOffset..]),
            RoomMix = BinaryPrimitives.ReadSingleLittleEndian(record[RoomMixOffset..]),
            Intensity = BinaryPrimitives.ReadSingleLittleEndian(record[IntensityOffset..]),
            SpeakerElevationDegrees = BinaryPrimitives.ReadSingleLittleEndian(record[SpeakerElevationOffset..]),
            Renderer = renderer,
        };
        writerTickMs = BinaryPrimitives.ReadUInt64LittleEndian(record[WriterTickOffset..]);
        state = new SpatialEngineState(mode, tracking, orientation, stage);
        return true;
    }
}
