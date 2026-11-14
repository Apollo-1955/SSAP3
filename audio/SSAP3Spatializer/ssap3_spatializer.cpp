// SSAP3 Spatial Engine: real-time binaural renderer hosted as a VST2 plug-in
// by Equalizer APO inside audiodg.exe.
//
// Real-time rules for Process(): no allocation, no locks, no file, registry or
// network access, no calls into managed code. The shared head-pose record is
// treated as untrusted input and validated on every read (SECURITY_AUDIT.md,
// finding S-04).
//
// Direct path: each virtual speaker is convolved with measured head-related
// impulse responses (MIT KEMAR, diffuse-field equalized; ssap3_hrir_data.h is
// compiled in, so nothing is parsed or loaded at run time). Per block, the
// four nearest measurements are blended bilinearly for the head-relative
// direction, resampled to the device rate with a band-limited kernel that
// also applies the sub-sample onset (ITD), and the output crossfades from the
// previous filters to the new ones, so head movement never clicks.

// Also passed on the command line; repeated so every build of this file is safe
// from the windows.h min/max macros.
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <xmmintrin.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <new>

#include "ssap3_hrir_data.h"
#include "ssap3_shared_state.h"
#include "vst2_abi.h"

namespace
{
namespace hrir = ssap3::hrir;

constexpr double kPiDouble = 3.14159265358979323846;
constexpr float kPi = 3.14159265358979323846f;
constexpr float kDegreesToRadians = kPi / 180.0f;
constexpr float kRadiansToDegrees = 180.0f / kPi;
constexpr int kDelayBufferSize = 32768; // power of two
constexpr int kDelayMask = kDelayBufferSize - 1;
constexpr float kMaxDelaySamples = static_cast<float>(kDelayBufferSize - 3);
constexpr int kRoomLineCount = 4;
constexpr std::uint64_t kStaleAfterMilliseconds = 750;
constexpr float kMixRampSeconds = 0.025f;
// The app's estimator already smooths; this only removes the ~50 Hz update
// staircase between audio blocks.
constexpr float kLiveOrientationTau = 0.02f;
constexpr float kReturnToCentreTau = 0.8f;
constexpr wchar_t kStateFileRelativePath[] = L"\\SSAP3\\Runtime\\head-pose.bin";
constexpr wchar_t kStatePathOverrideVariable[] = L"SSAP3_SPATIAL_STATE_PATH";

// HRIR renderer limits. 512 device-rate taps hold the whole 96-tap (2.2 ms)
// measurement up to 235 kHz; above that the tail is faded out.
constexpr int kMaxDeviceTaps = 512;
constexpr int kKernelPhases = 128;        // resampling kernel table resolution (linearly interpolated)
constexpr int kKernelZeroCrossings = 16;  // per side, at the source rate
constexpr int kMaxKernelHalfWidth = 48;   // source samples; bounds the kernel when downsampling
constexpr int kKernelTableSize = 2 * kMaxKernelHalfWidth * kKernelPhases + 2;
constexpr int kMaxDirectionWeights = 4;   // two azimuths on each of two elevation rings
constexpr float kMaxSampleRate = 384000.0f;
// Filters are rebuilt (and crossfaded) when a speaker's head-relative
// direction moves further than this from the one they were built for.
constexpr float kDirectionEpsilonDegrees = 0.02f;
// Direct-path level. With the diffuse-field unit-power table, the default
// +-30 degree stage at 48 kHz has a coherent near+far ear gain of 2.08 at
// 500 Hz and a white-noise power of 2.25 per ear; 0.40 brings both to the
// parametric renderer this replaced (0.82 and 0.37), so loudness and
// clipping headroom stay the same.
constexpr float kHrirTrim = 0.40f;

// Compile-time checks of the generated table: the lookup below relies on
// these properties, so a malformed table fails the build instead of reading
// out of bounds.
static_assert(hrir::kTaps > 0 && hrir::kTaps <= 2 * kMaxKernelHalfWidth, "Unexpected HRIR length.");
static_assert(hrir::kRingCount >= 2 && hrir::kEntryCount > 0, "The HRIR table needs at least two rings.");
static_assert([]() constexpr
{
    if (hrir::kRingStart[0] != 0 || hrir::kRingStart[hrir::kRingCount] != hrir::kEntryCount)
    {
        return false;
    }

    for (int ring = 0; ring < hrir::kRingCount; ++ring)
    {
        const int begin = hrir::kRingStart[ring];
        const int end = hrir::kRingStart[ring + 1];
        if (end <= begin || hrir::kAzimuth[begin] != 0 ||
            (end - begin > 1 && hrir::kAzimuth[end - 1] != 180) ||
            (ring > 0 && hrir::kRingElevation[ring] <= hrir::kRingElevation[ring - 1]))
        {
            return false;
        }

        for (int entry = begin + 1; entry < end; ++entry)
        {
            if (hrir::kAzimuth[entry] <= hrir::kAzimuth[entry - 1])
            {
                return false;
            }
        }
    }

    return true;
}(), "The HRIR table must list rings by rising elevation, each from azimuth 0 to 180.");

// Onsets are stored relative to the earliest one, which only removes a
// common delay.
constexpr int kCommonOnset = []() constexpr
{
    int minimum = 255;
    for (int entry = 0; entry < hrir::kEntryCount; ++entry)
    {
        minimum = std::min({minimum, static_cast<int>(hrir::kOnset[entry][0]), static_cast<int>(hrir::kOnset[entry][1])});
    }
    return minimum;
}();
constexpr int kMaxRelativeOnset = []() constexpr
{
    int maximum = 0;
    for (int entry = 0; entry < hrir::kEntryCount; ++entry)
    {
        maximum = std::max({maximum, static_cast<int>(hrir::kOnset[entry][0]), static_cast<int>(hrir::kOnset[entry][1])});
    }
    return maximum - kCommonOnset;
}();
constexpr float kMaxOnsetDelaySamples =
    static_cast<float>(kMaxRelativeOnset) * (kMaxSampleRate / static_cast<float>(hrir::kSourceRate)) + 1.0f;
static_assert(static_cast<int>(kMaxOnsetDelaySamples) + kMaxDeviceTaps + 2 < kDelayBufferSize,
    "The longest HRIR window must fit in the input history.");

using DelayLine = std::array<float, kDelayBufferSize>;

// One ear's filter for one speaker at the device rate.
struct EarFilter
{
    // Coefficients in reverse order, so the convolution is a forward dot
    // product over the input window from oldest to newest sample.
    std::array<float, kMaxDeviceTaps> reversed{};
    int length = 0;
    int wholeDelay = 0; // whole-sample part of the onset
};

using SpeakerFilters = std::array<EarFilter, 2>; // [ear]: 0 = left, 1 = right
using FilterBank = std::array<SpeakerFilters, 2>; // [speaker]: 0 = left input, 1 = right input

// Head-relative direction in degrees: azimuth positive to the listener's
// right, elevation positive up.
struct Direction
{
    float azimuth = 0.0f;
    float elevation = 0.0f;
};

struct DirectionWeights
{
    std::array<int, kMaxDirectionWeights> entry{};
    std::array<float, kMaxDirectionWeights> weight{};
    int count = 0;
    // KEMAR is measured on the right half; a source on the left uses the
    // mirror-image measurement with the ears swapped.
    bool mirrored = false;
};

struct Quaternion
{
    float w = 1.0f;
    float x = 0.0f;
    float y = 0.0f;
    float z = 0.0f;
};

struct Vector3
{
    float x = 0.0f;
    float y = 0.0f;
    float z = 0.0f;
};

struct StageSettings
{
    float speakerAzimuth = 30.0f;
    float speakerDistance = 1.0f;
    float speakerElevation = 0.0f;
    float roomMix = 1.0f;
    float intensity = 1.0f;
};

struct ValidatedState
{
    ssap3::SpatialMode mode = ssap3::SpatialMode::Off;
    bool trackedPoseUsable = false;
    Quaternion orientation{};
    StageSettings stage{};
};

// Flush denormals for the duration of one render call and restore the host's
// floating-point control state afterwards, so other effects in the same
// audio thread are unaffected.
class ScopedNoDenormals final
{
public:
    ScopedNoDenormals() : saved_(_mm_getcsr())
    {
        _mm_setcsr(saved_ | 0x8040u); // FTZ | DAZ
    }

    ~ScopedNoDenormals()
    {
        _mm_setcsr(saved_);
    }

    ScopedNoDenormals(const ScopedNoDenormals&) = delete;
    ScopedNoDenormals& operator=(const ScopedNoDenormals&) = delete;

private:
    unsigned int saved_;
};

bool IsFinite(float value)
{
    return std::isfinite(value);
}

bool InRange(float value, float minimum, float maximum)
{
    return IsFinite(value) && value >= minimum && value <= maximum;
}

Quaternion Normalize(const Quaternion& q)
{
    const float norm = std::sqrt(q.w * q.w + q.x * q.x + q.y * q.y + q.z * q.z);
    if (!IsFinite(norm) || norm < 1.0e-6f)
    {
        return Quaternion{};
    }

    return Quaternion{q.w / norm, q.x / norm, q.y / norm, q.z / norm};
}

Quaternion Slerp(const Quaternion& from, Quaternion to, float t)
{
    float dot = from.w * to.w + from.x * to.x + from.y * to.y + from.z * to.z;
    if (dot < 0.0f)
    {
        dot = -dot;
        to = Quaternion{-to.w, -to.x, -to.y, -to.z};
    }

    if (dot > 0.9995f)
    {
        return Normalize(Quaternion{
            from.w + (to.w - from.w) * t,
            from.x + (to.x - from.x) * t,
            from.y + (to.y - from.y) * t,
            from.z + (to.z - from.z) * t});
    }

    const float theta = std::acos(std::clamp(dot, -1.0f, 1.0f));
    const float sinTheta = std::sin(theta);
    const float a = std::sin((1.0f - t) * theta) / sinTheta;
    const float b = std::sin(t * theta) / sinTheta;
    return Normalize(Quaternion{
        a * from.w + b * to.w,
        a * from.x + b * to.x,
        a * from.y + b * to.y,
        a * from.z + b * to.z});
}

// Rotates v by the inverse of the unit quaternion q (world -> head frame).
Vector3 RotateByInverse(const Quaternion& q, const Vector3& v)
{
    // v' = conj(q) * v * q, expanded for a unit quaternion.
    const float qx = -q.x;
    const float qy = -q.y;
    const float qz = -q.z;
    const float qw = q.w;
    const float tx = 2.0f * (qy * v.z - qz * v.y);
    const float ty = 2.0f * (qz * v.x - qx * v.z);
    const float tz = 2.0f * (qx * v.y - qy * v.x);
    return Vector3{
        v.x + qw * tx + (qy * tz - qz * ty),
        v.y + qw * ty + (qz * tx - qx * tz),
        v.z + qw * tz + (qx * ty - qy * tx)};
}

// World direction of a speaker. Azimuth is positive to the listener's right.
Vector3 SpeakerDirection(float azimuthDegrees, float elevationDegrees)
{
    const float azimuth = azimuthDegrees * kDegreesToRadians;
    const float elevation = elevationDegrees * kDegreesToRadians;
    return Vector3{
        std::sin(azimuth) * std::cos(elevation),
        std::cos(azimuth) * std::cos(elevation),
        std::sin(elevation)};
}

void AddWeight(DirectionWeights& weights, int entry, float weight)
{
    if (weight > 0.0f && weights.count < kMaxDirectionWeights)
    {
        weights.entry[weights.count] = entry;
        weights.weight[weights.count] = weight;
        ++weights.count;
    }
}

// Linear interpolation between the two measured azimuths around `azimuth`
// (0-180 degrees) on one elevation ring.
void AddRing(DirectionWeights& weights, int ring, float azimuth, float ringWeight)
{
    if (!(ringWeight > 0.0f))
    {
        return;
    }

    const int begin = hrir::kRingStart[ring];
    const int end = hrir::kRingStart[ring + 1];
    if (end - begin == 1)
    {
        // The zenith is a single measurement.
        AddWeight(weights, begin, ringWeight);
        return;
    }

    int lower = begin;
    while (lower + 2 < end && static_cast<float>(hrir::kAzimuth[lower + 1]) <= azimuth)
    {
        ++lower;
    }

    const float from = static_cast<float>(hrir::kAzimuth[lower]);
    const float to = static_cast<float>(hrir::kAzimuth[lower + 1]);
    const float fraction = std::clamp((azimuth - from) / (to - from), 0.0f, 1.0f);
    AddWeight(weights, lower, ringWeight * (1.0f - fraction));
    AddWeight(weights, lower + 1, ringWeight * fraction);
}

// Bilinear weights over the measured grid (elevation rings, then azimuth).
// Elevations below the lowest measured ring (-40 degrees) use that ring.
DirectionWeights MeasuredDirectionWeights(const Direction& direction)
{
    DirectionWeights weights{};
    float azimuth = IsFinite(direction.azimuth) ? std::fmod(direction.azimuth, 360.0f) : 0.0f;
    if (azimuth > 180.0f)
    {
        azimuth -= 360.0f;
    }
    else if (azimuth <= -180.0f)
    {
        azimuth += 360.0f;
    }

    // The measured median plane is identical for both ears, so the mirror
    // image meets the measured half without a seam at 0 and 180 degrees.
    weights.mirrored = azimuth < 0.0f;
    azimuth = std::min(std::abs(azimuth), 180.0f);

    const float lowest = static_cast<float>(hrir::kRingElevation[0]);
    const float highest = static_cast<float>(hrir::kRingElevation[hrir::kRingCount - 1]);
    const float elevation = std::clamp(IsFinite(direction.elevation) ? direction.elevation : 0.0f, lowest, highest);
    int lower = 0;
    while (lower + 2 < hrir::kRingCount && static_cast<float>(hrir::kRingElevation[lower + 1]) <= elevation)
    {
        ++lower;
    }

    const float from = static_cast<float>(hrir::kRingElevation[lower]);
    const float to = static_cast<float>(hrir::kRingElevation[lower + 1]);
    const float fraction = std::clamp((elevation - from) / (to - from), 0.0f, 1.0f);
    AddRing(weights, lower, azimuth, 1.0f - fraction);
    AddRing(weights, lower + 1, azimuth, fraction);
    return weights;
}

// SSE dot product (always available on x64), unaligned loads.
float Dot(const float* coefficients, const float* samples, int count)
{
    __m128 sum0 = _mm_setzero_ps();
    __m128 sum1 = _mm_setzero_ps();
    int index = 0;
    for (; index + 8 <= count; index += 8)
    {
        sum0 = _mm_add_ps(sum0, _mm_mul_ps(_mm_loadu_ps(coefficients + index), _mm_loadu_ps(samples + index)));
        sum1 = _mm_add_ps(sum1, _mm_mul_ps(_mm_loadu_ps(coefficients + index + 4), _mm_loadu_ps(samples + index + 4)));
    }

    float lanes[4] = {};
    _mm_storeu_ps(lanes, _mm_add_ps(sum0, sum1));
    float total = (lanes[0] + lanes[1]) + (lanes[2] + lanes[3]);
    for (; index < count; ++index)
    {
        total += coefficients[index] * samples[index];
    }

    return total;
}

bool Ssap3ValidateState(const ssap3::SharedStateV2& snapshot, ValidatedState& result)
{
    result = ValidatedState{};
    if (snapshot.magic != ssap3::kSharedStateMagic ||
        snapshot.version != ssap3::kSharedStateVersion ||
        snapshot.mode < static_cast<std::int32_t>(ssap3::SpatialMode::Off) ||
        snapshot.mode > static_cast<std::int32_t>(ssap3::SpatialMode::Tracked))
    {
        return false;
    }

    if (!InRange(snapshot.speakerAzimuthDegrees, ssap3::kMinSpeakerAzimuth, ssap3::kMaxSpeakerAzimuth) ||
        !InRange(snapshot.speakerDistanceMeters, ssap3::kMinSpeakerDistance, ssap3::kMaxSpeakerDistance) ||
        !InRange(snapshot.speakerElevationDegrees, ssap3::kMinSpeakerElevation, ssap3::kMaxSpeakerElevation) ||
        !InRange(snapshot.roomMix, 0.0f, 1.0f) ||
        !InRange(snapshot.intensity, 0.0f, 1.0f))
    {
        return false;
    }

    result.mode = static_cast<ssap3::SpatialMode>(snapshot.mode);
    result.stage.speakerAzimuth = snapshot.speakerAzimuthDegrees;
    result.stage.speakerDistance = snapshot.speakerDistanceMeters;
    result.stage.speakerElevation = snapshot.speakerElevationDegrees;
    result.stage.roomMix = snapshot.roomMix;
    result.stage.intensity = snapshot.intensity;

    // Live, Holding and Lost poses are all produced by the app's estimator
    // (Holding freezes the pose, Lost glides it back to centre), so the engine
    // follows them. "None" means there is no usable pose.
    if (result.mode != ssap3::SpatialMode::Tracked ||
        snapshot.trackingState < static_cast<std::int32_t>(ssap3::TrackingState::Live) ||
        snapshot.trackingState > static_cast<std::int32_t>(ssap3::TrackingState::Lost))
    {
        return true;
    }

    const Quaternion raw{
        snapshot.orientation[0],
        snapshot.orientation[1],
        snapshot.orientation[2],
        snapshot.orientation[3]};
    if (!IsFinite(raw.w) || !IsFinite(raw.x) || !IsFinite(raw.y) || !IsFinite(raw.z))
    {
        return true;
    }

    const float normSquared = raw.w * raw.w + raw.x * raw.x + raw.y * raw.y + raw.z * raw.z;
    if (!(normSquared > 0.81f && normSquared < 1.21f))
    {
        return true;
    }

    const std::uint64_t now = GetTickCount64();
    if (snapshot.writerTickMs == 0 ||
        snapshot.writerTickMs > now ||
        now - snapshot.writerTickMs > kStaleAfterMilliseconds)
    {
        return true;
    }

    result.orientation = Normalize(raw);
    result.trackedPoseUsable = true;
    return true;
}

class Spatializer final
{
public:
    Spatializer()
    {
        std::memset(&effect_, 0, sizeof(effect_));
        effect_.magic = kEffectMagic;
        effect_.dispatcher = Dispatch;
        effect_.process = ProcessReplacing;
        effect_.setParameter = SetParameter;
        effect_.getParameter = GetParameter;
        effect_.numPrograms = 1;
        effect_.numParams = 0;
        effect_.numInputs = 2;
        effect_.numOutputs = 2;
        effect_.flags = kEffectCanReplacing;
        effect_.initialDelay = 0;
        effect_.ioRatio = 1.0f;
        effect_.object = this;
        effect_.uniqueId = kSsap3UniqueId;
        effect_.version = 0x00020600;
        effect_.processReplacing = ProcessReplacing;
        ResetDsp();
    }

    ~Spatializer()
    {
        CloseState();
    }

    Spatializer(const Spatializer&) = delete;
    Spatializer& operator=(const Spatializer&) = delete;

    AEffect* Effect()
    {
        return &effect_;
    }

private:
    static Spatializer* From(AEffect* effect)
    {
        return static_cast<Spatializer*>(effect->object);
    }

    static std::intptr_t Dispatch(
        AEffect* effect,
        std::int32_t opcode,
        std::int32_t,
        std::intptr_t dispatcherValue,
        void* pointer,
        float option)
    {
        auto* self = From(effect);
        switch (opcode)
        {
        case effOpen:
            self->OpenState();
            return 1;
        case effClose:
            delete self;
            return 1;
        case effSetSampleRate:
            if (IsFinite(option))
            {
                self->sampleRate_ = std::clamp(option, 8000.0f, 384000.0f);
            }
            self->ResetDsp();
            return 1;
        case effSetBlockSize:
            return 1;
        case effMainsChanged:
            if (dispatcherValue != 0)
            {
                self->OpenState();
                self->ResetDsp();
            }
            return 1;
        case effStartProcess:
            self->OpenState();
            return 1;
        case effStopProcess:
            return 1;
        case effGetEffectName:
            CopyText(pointer, "SSAP3 Spatial Engine", 64);
            return 1;
        case effGetProductString:
            CopyText(pointer, "SSAP3 Head-Tracked Soundstage", 64);
            return 1;
        case effGetVendorString:
            CopyText(pointer, "SSAP3 contributors", 64);
            return 1;
        case effGetVendorVersion:
            return 20600;
        case effCanDo:
            if (pointer == nullptr)
            {
                return 0;
            }
            return std::strcmp(static_cast<const char*>(pointer), "plugAsChannelInsert") == 0 ||
                    std::strcmp(static_cast<const char*>(pointer), "plugAsSend") == 0
                ? 1
                : 0;
        case effGetVstVersion:
            return 2400;
        default:
            return 0;
        }
    }

    static void CopyText(void* destination, const char* value, std::size_t capacity)
    {
        if (destination == nullptr || capacity == 0)
        {
            return;
        }

        strncpy_s(static_cast<char*>(destination), capacity, value, _TRUNCATE);
    }

    static void SetParameter(AEffect*, std::int32_t, float)
    {
    }

    static float GetParameter(AEffect*, std::int32_t)
    {
        return 0.0f;
    }

    static void ProcessReplacing(
        AEffect* effect,
        float** inputs,
        float** outputs,
        std::int32_t frameCount)
    {
        From(effect)->Process(inputs, outputs, frameCount);
    }

    void Process(float** inputs, float** outputs, std::int32_t frameCount)
    {
        if (inputs == nullptr || outputs == nullptr ||
            inputs[0] == nullptr || inputs[1] == nullptr ||
            outputs[0] == nullptr || outputs[1] == nullptr ||
            frameCount <= 0)
        {
            return;
        }

        const ScopedNoDenormals noDenormals;
        ValidatedState state{};
        ReadState(state);

        const bool active = state.mode != ssap3::SpatialMode::Off;
        const float targetMix = active ? state.stage.intensity : 0.0f;
        if (mix_ <= 0.0f && targetMix <= 0.0f)
        {
            // Fully bypassed: bit-exact pass-through. The delay lines keep
            // running so that a later fade-in starts from recent audio.
            for (std::int32_t frame = 0; frame < frameCount; ++frame)
            {
                const float inputLeft = inputs[0][frame];
                const float inputRight = inputs[1][frame];
                delayLeft_[writeIndex_] = Sanitize(inputLeft);
                delayRight_[writeIndex_] = Sanitize(inputRight);
                outputs[0][frame] = inputLeft;
                outputs[1][frame] = inputRight;
                writeIndex_ = (writeIndex_ + 1) & kDelayMask;
            }
            wasBypassed_ = true;
            return;
        }

        if (wasBypassed_)
        {
            ResetRenderState();
            wasBypassed_ = false;
        }

        const float blockSeconds = static_cast<float>(frameCount) / sampleRate_;
        const Quaternion targetOrientation =
            state.trackedPoseUsable ? state.orientation : Quaternion{};
        const float tau = state.trackedPoseUsable ? kLiveOrientationTau : kReturnToCentreTau;
        orientation_ = Slerp(orientation_, targetOrientation, 1.0f - std::exp(-blockSeconds / tau));

        const auto& stage = state.stage;
        const std::array<Direction, 2> directions{
            HeadRelativeDirection(-stage.speakerAzimuth, stage.speakerElevation),
            HeadRelativeDirection(stage.speakerAzimuth, stage.speakerElevation)};
        bool crossfade = false;
        if (!filtersPrimed_)
        {
            BuildFilters(directions, banks_[activeBank_]);
            filterDirections_ = directions;
            filtersPrimed_ = true;
        }
        else if (DirectionsChanged(directions))
        {
            // The new filters go into the other bank; this block fades from
            // the old bank's output to the new one's.
            activeBank_ ^= 1;
            BuildFilters(directions, banks_[activeBank_]);
            filterDirections_ = directions;
            crossfade = true;
        }

        const FilterBank& current = banks_[activeBank_];
        const FilterBank& previous = banks_[activeBank_ ^ 1];
        const float directGain = kHrirTrim * std::clamp(1.0f / stage.speakerDistance, 0.35f, 1.5f);
        const float earlyMix = 0.285f * stage.roomMix;
        const float lateMix = 0.16f * stage.roomMix;
        const float mixStep = 1.0f / std::max(1.0f, kMixRampSeconds * sampleRate_);
        const float inverseFrames = 1.0f / static_cast<float>(frameCount);

        for (std::int32_t frame = 0; frame < frameCount; ++frame)
        {
            const float inputLeft = inputs[0][frame];
            const float inputRight = inputs[1][frame];
            delayLeft_[writeIndex_] = Sanitize(inputLeft);
            delayRight_[writeIndex_] = Sanitize(inputRight);

            std::array<float, 2> direct{};
            for (int ear = 0; ear < 2; ++ear)
            {
                float value = Convolve(delayLeft_, current[0][ear]) + Convolve(delayRight_, current[1][ear]);
                if (crossfade)
                {
                    const float progress = static_cast<float>(frame + 1) * inverseFrames;
                    const float old = Convolve(delayLeft_, previous[0][ear]) + Convolve(delayRight_, previous[1][ear]);
                    value = old + (value - old) * progress;
                }
                direct[ear] = value;
            }

            const auto early = CalculateEarlyReflections();
            const auto late = ProcessRoom(0.5f * (delayLeft_[writeIndex_] + delayRight_[writeIndex_]));
            const float wetLeft = direct[0] * directGain + early[0] * earlyMix + late[0] * lateMix;
            const float wetRight = direct[1] * directGain + early[1] * earlyMix + late[1] * lateMix;

            if (mix_ < targetMix)
            {
                mix_ = std::min(targetMix, mix_ + mixStep);
            }
            else if (mix_ > targetMix)
            {
                mix_ = std::max(targetMix, mix_ - mixStep);
            }

            const float dryLeft = Sanitize(inputLeft);
            const float dryRight = Sanitize(inputRight);
            outputs[0][frame] = Limit(dryLeft + (wetLeft - dryLeft) * mix_);
            outputs[1][frame] = Limit(dryRight + (wetRight - dryRight) * mix_);
            writeIndex_ = (writeIndex_ + 1) & kDelayMask;
        }

        if (!RenderStateIsFinite())
        {
            ResetFilterState();
        }
    }

    Direction HeadRelativeDirection(float worldAzimuth, float worldElevation) const
    {
        const Vector3 head = RotateByInverse(orientation_, SpeakerDirection(worldAzimuth, worldElevation));
        Direction direction{};
        direction.azimuth = std::atan2(head.x, head.y) * kRadiansToDegrees;
        direction.elevation = std::asin(std::clamp(head.z, -1.0f, 1.0f)) * kRadiansToDegrees;
        return direction;
    }

    bool DirectionsChanged(const std::array<Direction, 2>& directions) const
    {
        for (std::size_t speaker = 0; speaker < directions.size(); ++speaker)
        {
            // Written so that a NaN also counts as a change.
            if (!(std::abs(directions[speaker].azimuth - filterDirections_[speaker].azimuth) <= kDirectionEpsilonDegrees) ||
                !(std::abs(directions[speaker].elevation - filterDirections_[speaker].elevation) <= kDirectionEpsilonDegrees))
            {
                return true;
            }
        }
        return false;
    }

    void BuildFilters(const std::array<Direction, 2>& directions, FilterBank& bank) const
    {
        for (std::size_t speaker = 0; speaker < directions.size(); ++speaker)
        {
            const DirectionWeights weights = MeasuredDirectionWeights(directions[speaker]);
            for (int ear = 0; ear < 2; ++ear)
            {
                BuildFilter(weights, ear, bank[speaker][ear]);
            }
        }
    }

    // Blends the measured responses at the source rate, then resamples the
    // blend to the device rate. The resampling positions are shifted by the
    // fractional part of the onset, so the sub-sample ITD is band-limited
    // (no interpolation loss); the whole-sample part becomes a plain delay.
    // Runs on the audio thread: fixed-size stack arrays only.
    void BuildFilter(const DirectionWeights& weights, int ear, EarFilter& filter) const
    {
        std::array<float, hrir::kTaps> measured{};
        float onset = 0.0f;
        const int sourceEar = weights.mirrored ? 1 - ear : ear;
        for (int index = 0; index < weights.count; ++index)
        {
            const int entry = weights.entry[index];
            const float weight = weights.weight[index];
            const float gain = weight * hrir::kScale;
            const std::int16_t* response = hrir::kResponse[entry][sourceEar];
            for (int tap = 0; tap < hrir::kTaps; ++tap)
            {
                measured[tap] += gain * static_cast<float>(response[tap]);
            }
            onset += weight * static_cast<float>(hrir::kOnset[entry][sourceEar] - kCommonOnset);
        }

        const float delay = std::clamp(onset * rateRatio_, 0.0f, kMaxOnsetDelaySamples);
        const int whole = static_cast<int>(delay);
        const float fraction = delay - static_cast<float>(whole);
        const int halfWidth = kernelHalfWidth_;
        const int tableLast = 2 * halfWidth * kKernelPhases;
        const int length = std::clamp(deviceTaps_, 1, kMaxDeviceTaps);
        for (int tap = 0; tap < length; ++tap)
        {
            // Source-rate position of this device-rate tap.
            const float position = (static_cast<float>(tap) - fraction) / rateRatio_;
            const int floorPosition = static_cast<int>(std::floor(position));
            const int first = std::max(0, floorPosition - halfWidth + 1);
            const int last = std::min(hrir::kTaps - 1, floorPosition + halfWidth);
            float sum = 0.0f;
            if (first <= last)
            {
                // Kernel argument for `first`; each later source sample is
                // exactly one source sample (kKernelPhases entries) closer.
                const float phase = (position - static_cast<float>(first) + static_cast<float>(halfWidth)) *
                    static_cast<float>(kKernelPhases);
                int table = std::clamp(static_cast<int>(phase), 0, tableLast);
                const float blend = std::clamp(phase - static_cast<float>(table), 0.0f, 1.0f);
                for (int source = first; source <= last && table >= 0; ++source, table -= kKernelPhases)
                {
                    const float kernel = kernelTable_[table] + (kernelTable_[table + 1] - kernelTable_[table]) * blend;
                    sum += measured[source] * kernel;
                }
            }
            filter.reversed[length - 1 - tap] = sum * tapFade_[tap];
        }

        filter.length = length;
        filter.wholeDelay = whole;
    }

    // y[n] = sum_k h[k] x[n - wholeDelay - k], read straight from the input
    // history (masked, so it cannot leave the buffer; the window is at most
    // kMaxOnsetDelaySamples + kMaxDeviceTaps samples, checked at compile time).
    float Convolve(const DelayLine& history, const EarFilter& filter) const
    {
        const int length = std::clamp(filter.length, 0, kMaxDeviceTaps);
        const int start = (writeIndex_ - filter.wholeDelay - (length - 1)) & kDelayMask;
        const int contiguous = std::min(length, kDelayBufferSize - start);
        float sum = Dot(filter.reversed.data(), history.data() + start, contiguous);
        if (contiguous < length)
        {
            sum += Dot(filter.reversed.data() + contiguous, history.data(), length - contiguous);
        }
        return sum;
    }

    // Windowed-sinc kernel (Blackman, kKernelZeroCrossings per side) from the
    // 44.1 kHz measurements to the device rate, tabulated once per rate
    // change (dispatcher thread, never Process). Upsampling keeps the source
    // band; downsampling low-passes to the new Nyquist. The 1/ratio factor
    // keeps the frequency response, not the tap sum, unchanged.
    void ConfigureResampler()
    {
        rateRatio_ = sampleRate_ / static_cast<float>(hrir::kSourceRate);
        const double ratio = static_cast<double>(rateRatio_);
        const double cutoff = std::min(1.0, ratio);
        kernelHalfWidth_ = std::clamp(
            static_cast<int>(std::ceil(static_cast<double>(kKernelZeroCrossings) / cutoff)), 1, kMaxKernelHalfWidth);
        const int wanted = static_cast<int>(std::ceil(static_cast<double>(hrir::kTaps) * ratio)) + 1;
        deviceTaps_ = std::clamp(wanted, 1, kMaxDeviceTaps);

        kernelTable_.fill(0.0f);
        const int centre = kernelHalfWidth_ * kKernelPhases;
        const double halfWidth = static_cast<double>(kernelHalfWidth_);
        for (int index = 1; index < 2 * centre; ++index)
        {
            const double x = static_cast<double>(index - centre) / static_cast<double>(kKernelPhases);
            double sinc = 1.0;
            if (index != centre)
            {
                // Exact zeros at whole source samples when not band-limiting
                // further, so 44.1 kHz reproduces the measurements exactly.
                const double argument = kPiDouble * cutoff * x;
                sinc = (cutoff >= 1.0 && (index - centre) % kKernelPhases == 0) ? 0.0 : std::sin(argument) / argument;
            }

            const double window = 0.42 + 0.5 * std::cos(kPiDouble * x / halfWidth) +
                0.08 * std::cos(2.0 * kPiDouble * x / halfWidth);
            kernelTable_[index] = static_cast<float>(cutoff / ratio * sinc * window);
        }

        const int fadeStart = wanted > kMaxDeviceTaps ? kMaxDeviceTaps - kMaxDeviceTaps / 8 : kMaxDeviceTaps;
        for (int tap = 0; tap < kMaxDeviceTaps; ++tap)
        {
            tapFade_[tap] = tap < fadeStart
                ? 1.0f
                : static_cast<float>(0.5 * (1.0 + std::cos(kPiDouble * static_cast<double>(tap - fadeStart + 1) /
                    static_cast<double>(deviceTaps_ - fadeStart + 1))));
        }
    }

    std::array<float, 2> CalculateEarlyReflections()
    {
        // Dense, short, asymmetric taps suggest a compact, acoustically
        // treated listening room without a distinct echo.
        const auto tap = [this](const DelayLine& buffer, float milliseconds)
        {
            return ReadDelay(buffer, milliseconds * sampleRate_ * 0.001f);
        };

        const float rawLeft =
            tap(delayLeft_, 3.7f) * 0.34f +
            tap(delayRight_, 4.9f) * 0.21f -
            tap(delayLeft_, 6.1f) * 0.16f +
            tap(delayRight_, 8.3f) * 0.15f +
            tap(delayLeft_, 10.7f) * 0.13f -
            tap(delayRight_, 13.9f) * 0.10f +
            tap(delayLeft_, 17.3f) * 0.09f +
            tap(delayRight_, 22.9f) * 0.07f;
        const float rawRight =
            tap(delayRight_, 4.1f) * 0.34f +
            tap(delayLeft_, 5.3f) * 0.21f -
            tap(delayRight_, 6.7f) * 0.16f +
            tap(delayLeft_, 8.9f) * 0.15f +
            tap(delayRight_, 11.3f) * 0.13f -
            tap(delayLeft_, 14.9f) * 0.10f +
            tap(delayRight_, 18.7f) * 0.09f +
            tap(delayLeft_, 24.1f) * 0.07f;
        return {BandLimitReflection(rawLeft, 0), BandLimitReflection(rawRight, 1)};
    }

    float BandLimitReflection(float input, int channel)
    {
        earlyRoomLowPass_[channel] += earlyLowPassAlpha_ * (input - earlyRoomLowPass_[channel]);
        earlyRoomBass_[channel] += earlyBassAlpha_ * (earlyRoomLowPass_[channel] - earlyRoomBass_[channel]);
        return earlyRoomLowPass_[channel] - earlyRoomBass_[channel];
    }

    std::array<float, 2> ProcessRoom(float input)
    {
        roomInputBass_ += roomBassAlpha_ * (input - roomInputBass_);
        input -= roomInputBass_;

        std::array<float, kRoomLineCount> delayed{};
        for (int line = 0; line < kRoomLineCount; ++line)
        {
            delayed[line] = roomDelay_[line][roomIndex_[line]];
            // Air absorption in the feedback path avoids a metallic tail.
            roomDamping_[line] += 0.24f * (delayed[line] - roomDamping_[line]);
        }

        const float a = roomDamping_[0];
        const float b = roomDamping_[1];
        const float c = roomDamping_[2];
        const float d = roomDamping_[3];
        constexpr float kMatrixScale = 0.5f;
        constexpr float kFeedback = 0.625f;
        const std::array<float, kRoomLineCount> feedback{
            (a + b + c + d) * kMatrixScale,
            (a - b + c - d) * kMatrixScale,
            (a + b - c - d) * kMatrixScale,
            (a - b - c + d) * kMatrixScale};
        constexpr std::array<float, kRoomLineCount> inputPolarity{1.0f, -1.0f, 1.0f, 1.0f};
        for (int line = 0; line < kRoomLineCount; ++line)
        {
            roomDelay_[line][roomIndex_[line]] = input * inputPolarity[line] + feedback[line] * kFeedback;
            roomIndex_[line] = (roomIndex_[line] + 1) % roomLength_[line];
        }

        return {(a + b - c - d) * kMatrixScale, (a - b + c - d) * kMatrixScale};
    }

    float LowPassAlpha(float cutoff) const
    {
        return 1.0f - std::exp(-2.0f * kPi * cutoff / sampleRate_);
    }

    // Bounded fractional-delay read. Non-finite or negative delays read the
    // newest sample; the index is masked, so it can never leave the buffer.
    float ReadDelay(const DelayLine& buffer, float delaySamples) const
    {
        if (!(delaySamples >= 0.0f))
        {
            delaySamples = 0.0f;
        }
        else if (delaySamples > kMaxDelaySamples)
        {
            delaySamples = kMaxDelaySamples;
        }

        const int whole = static_cast<int>(delaySamples);
        const float fraction = delaySamples - static_cast<float>(whole);
        const int newer = (writeIndex_ - whole) & kDelayMask;
        const int older = (newer - 1) & kDelayMask;
        return buffer[newer] * (1.0f - fraction) + buffer[older] * fraction;
    }

    static float Sanitize(float value)
    {
        return IsFinite(value) ? value : 0.0f;
    }

    static float Limit(float value)
    {
        return IsFinite(value) ? std::clamp(value, -1.0f, 1.0f) : 0.0f;
    }

    bool RenderStateIsFinite() const
    {
        // The HRIR path keeps no recursive state: its input history is
        // sanitised on write and its coefficients come from the fixed table.
        bool finite = IsFinite(roomInputBass_);
        for (const float value : earlyRoomLowPass_) finite = finite && IsFinite(value);
        for (const float value : earlyRoomBass_) finite = finite && IsFinite(value);
        for (const float value : roomDamping_) finite = finite && IsFinite(value);
        return finite;
    }

    void ReadState(ValidatedState& result)
    {
        result = ValidatedState{};
        if (sharedState_ == nullptr)
        {
            return;
        }

        ssap3::SharedStateV2 snapshot{};
        bool stable = false;
        for (int attempt = 0; attempt < 4 && !stable; ++attempt)
        {
            const long first = sharedState_->sequence;
            MemoryBarrier();
            if ((first & 1) != 0)
            {
                continue;
            }

            std::memcpy(&snapshot, const_cast<const ssap3::SharedStateV2*>(sharedState_), sizeof(snapshot));
            MemoryBarrier();
            stable = sharedState_->sequence == first;
        }

        if (!stable || !Ssap3ValidateState(snapshot, result))
        {
            result = ValidatedState{};
        }
    }

    void OpenState()
    {
        if (sharedState_ != nullptr)
        {
            return;
        }

        wchar_t path[MAX_PATH]{};
        const DWORD overrideLength = GetEnvironmentVariableW(kStatePathOverrideVariable, path, MAX_PATH);
        if (overrideLength == 0 || overrideLength >= MAX_PATH)
        {
            const DWORD rootLength = GetEnvironmentVariableW(L"ProgramData", path, MAX_PATH);
            if (rootLength == 0 || rootLength >= MAX_PATH ||
                wcscat_s(path, MAX_PATH, kStateFileRelativePath) != 0)
            {
                return;
            }
        }

        stateFile_ = CreateFileW(
            path,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            nullptr,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);
        if (stateFile_ == INVALID_HANDLE_VALUE)
        {
            return;
        }

        LARGE_INTEGER size{};
        if (!GetFileSizeEx(stateFile_, &size) ||
            size.QuadPart < static_cast<LONGLONG>(sizeof(ssap3::SharedStateV2)))
        {
            CloseState();
            return;
        }

        stateMapping_ = CreateFileMappingW(stateFile_, nullptr, PAGE_READONLY, 0, 0, nullptr);
        if (stateMapping_ == nullptr)
        {
            CloseState();
            return;
        }

        sharedState_ = static_cast<const volatile ssap3::SharedStateV2*>(
            MapViewOfFile(stateMapping_, FILE_MAP_READ, 0, 0, sizeof(ssap3::SharedStateV2)));
        if (sharedState_ == nullptr)
        {
            CloseState();
        }
    }

    void CloseState()
    {
        if (sharedState_ != nullptr)
        {
            UnmapViewOfFile(const_cast<const ssap3::SharedStateV2*>(sharedState_));
            sharedState_ = nullptr;
        }
        if (stateMapping_ != nullptr)
        {
            CloseHandle(stateMapping_);
            stateMapping_ = nullptr;
        }
        if (stateFile_ != INVALID_HANDLE_VALUE)
        {
            CloseHandle(stateFile_);
            stateFile_ = INVALID_HANDLE_VALUE;
        }
    }

    void ResetFilterState()
    {
        earlyRoomLowPass_.fill(0.0f);
        earlyRoomBass_.fill(0.0f);
        roomDamping_.fill(0.0f);
        roomInputBass_ = 0.0f;
        for (auto& line : roomDelay_)
        {
            line.fill(0.0f);
        }
    }

    void ResetRenderState()
    {
        ResetFilterState();
        roomIndex_.fill(0);
        filtersPrimed_ = false;
    }

    void ResetDsp()
    {
        ConfigureResampler();
        delayLeft_.fill(0.0f);
        delayRight_.fill(0.0f);
        ResetRenderState();
        writeIndex_ = 0;
        mix_ = 0.0f;
        wasBypassed_ = true;
        orientation_ = Quaternion{};

        constexpr std::array<float, kRoomLineCount> roomSeconds{0.0197f, 0.0239f, 0.0293f, 0.0329f};
        for (int line = 0; line < kRoomLineCount; ++line)
        {
            roomLength_[line] = std::clamp(static_cast<int>(roomSeconds[line] * sampleRate_), 3, kDelayBufferSize);
        }

        earlyLowPassAlpha_ = LowPassAlpha(4800.0f);
        earlyBassAlpha_ = LowPassAlpha(140.0f);
        roomBassAlpha_ = LowPassAlpha(160.0f);
    }

    AEffect effect_{};
    float sampleRate_ = 48000.0f;
    float mix_ = 0.0f;
    bool wasBypassed_ = true;
    bool filtersPrimed_ = false;
    Quaternion orientation_{};
    std::array<FilterBank, 2> banks_{};
    int activeBank_ = 0;
    std::array<Direction, 2> filterDirections_{};
    std::array<float, kKernelTableSize> kernelTable_{};
    std::array<float, kMaxDeviceTaps> tapFade_{};
    float rateRatio_ = 1.0f;
    int kernelHalfWidth_ = 1;
    int deviceTaps_ = 1;
    DelayLine delayLeft_{};
    DelayLine delayRight_{};
    std::array<DelayLine, kRoomLineCount> roomDelay_{};
    std::array<float, 2> earlyRoomLowPass_{};
    std::array<float, 2> earlyRoomBass_{};
    std::array<float, kRoomLineCount> roomDamping_{};
    std::array<int, kRoomLineCount> roomIndex_{};
    std::array<int, kRoomLineCount> roomLength_{};
    float roomInputBass_ = 0.0f;
    float earlyLowPassAlpha_ = 0.0f;
    float earlyBassAlpha_ = 0.0f;
    float roomBassAlpha_ = 0.0f;
    int writeIndex_ = 0;
    HANDLE stateFile_ = INVALID_HANDLE_VALUE;
    HANDLE stateMapping_ = nullptr;
    const volatile ssap3::SharedStateV2* sharedState_ = nullptr;
};
} // namespace

extern "C" __declspec(dllexport) AEffect* VSTPluginMain(AudioMasterCallback)
{
    auto* spatializer = new (std::nothrow) Spatializer();
    return spatializer == nullptr ? nullptr : spatializer->Effect();
}

BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID)
{
    return TRUE;
}
