/*
 * SSAP3 head-pose / soundstage state shared between the unprivileged SSAP3
 * Control Center (writer) and the SSAP3 Spatial Engine running inside
 * audiodg.exe (reader).
 *
 * The record is UNTRUSTED input for the reader: every field is validated
 * before use (see Ssap3ValidateState in ssap3_spatializer.cpp).
 *
 * Layout (little-endian, natural alignment, 96 bytes at offset 0 of a
 * 4096-byte file). It must stay identical to SharedStateV2Layout in
 * src/SSAP3.Platform.Windows/HeadPoseStateWriter.cs.
 *
 * Coordinate system (right-handed):
 *   +X = listener's right, +Y = forward (towards the monitor), +Z = up.
 *   The orientation quaternion (w, x, y, z) rotates head-frame vectors into
 *   the calibrated world frame. Yaw is positive when turning LEFT
 *   (counter-clockwise seen from above, about +Z). Pitch is positive when
 *   looking UP (about +X). Roll is positive when the RIGHT ear tilts down
 *   (about +Y).
 *
 * Synchronisation: a sequence lock. The writer makes `sequence` odd,
 * writes the payload, then makes it even. Readers retry while the sequence
 * is odd or changed during the copy.
 *
 * Staleness: `writerTickMs` is GetTickCount64() at write time, which is
 * monotonic and system-wide.
 */
#pragma once

#include <cstddef>
#include <cstdint>

namespace ssap3
{
constexpr std::uint32_t kSharedStateMagic = 0x50483353u; // "S3HP"
constexpr std::uint32_t kSharedStateVersion = 2u;
constexpr std::uint32_t kSharedStateFileSize = 4096u;

enum class SpatialMode : std::int32_t
{
    Off = 0,
    Fixed = 1,
    Tracked = 2,
};

enum class TrackingState : std::int32_t
{
    None = 0,
    Live = 1,
    Holding = 2,
    Lost = 3,
};

// Direct-path renderer. Occupies what was reserved0: writers before engine
// 2.7.0 wrote 0 there, which selects the measured renderer, so the v2 layout
// and older writers stay compatible. Any other value rejects the record.
enum class Renderer : std::int32_t
{
    Measured = 0, // MIT KEMAR HRIR convolution with the stage timbre correction
    Classic = 1,  // the parametric model of engine 2.5 (ITD, level, head-shadow low-pass)
};

struct SharedStateV2
{
    std::uint32_t magic;
    std::uint32_t version;
    volatile long sequence;
    std::int32_t mode;
    std::int32_t trackingState;
    std::int32_t renderer;
    float orientation[4];
    float yawDegrees;
    float pitchDegrees;
    float rollDegrees;
    float reserved1;
    std::uint64_t writerTickMs;
    float speakerAzimuthDegrees;
    float speakerDistanceMeters;
    float roomMix;
    float intensity;
    float speakerElevationDegrees;
    float reserved2[3];
};

static_assert(sizeof(long) == 4, "LONG must be 32-bit (Windows LLP64).");
static_assert(offsetof(SharedStateV2, sequence) == 8);
static_assert(offsetof(SharedStateV2, mode) == 12);
static_assert(offsetof(SharedStateV2, trackingState) == 16);
static_assert(offsetof(SharedStateV2, renderer) == 20);
static_assert(offsetof(SharedStateV2, orientation) == 24);
static_assert(offsetof(SharedStateV2, yawDegrees) == 40);
static_assert(offsetof(SharedStateV2, writerTickMs) == 56);
static_assert(offsetof(SharedStateV2, speakerAzimuthDegrees) == 64);
static_assert(offsetof(SharedStateV2, speakerElevationDegrees) == 80);
static_assert(sizeof(SharedStateV2) == 96);

// Accepted parameter ranges. Out-of-range records are rejected as a whole.
constexpr float kMinSpeakerAzimuth = 10.0f;
constexpr float kMaxSpeakerAzimuth = 90.0f;
constexpr float kMinSpeakerDistance = 0.3f;
constexpr float kMaxSpeakerDistance = 5.0f;
constexpr float kMinSpeakerElevation = -30.0f;
constexpr float kMaxSpeakerElevation = 30.0f;
} // namespace ssap3
