// SSAP3 Spatial Engine: real-time binaural renderer hosted as a VST2 plug-in
// by Equalizer APO inside audiodg.exe.
//
// Real-time rules for Process(): no allocation, no locks, no file, registry or
// network access, no calls into managed code. The shared head-pose record is
// treated as untrusted input and validated on every read (SECURITY_AUDIT.md,
// finding S-04).

#include <windows.h>
#include <xmmintrin.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <new>

#include "ssap3_shared_state.h"
#include "vst2_abi.h"

namespace
{
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

using DelayLine = std::array<float, kDelayBufferSize>;

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

struct RenderParameters
{
    float gainLeft = 0.0f;
    float gainRight = 0.0f;
    float delayLeft = 0.0f;
    float delayRight = 0.0f;
    float alphaLeft = 1.0f;
    float alphaRight = 1.0f;
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
        effect_.version = 0x00020500;
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
            return 20500;
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
        const RenderParameters leftTarget = SourceParameters(-stage.speakerAzimuth, stage);
        const RenderParameters rightTarget = SourceParameters(stage.speakerAzimuth, stage);
        if (!parametersPrimed_)
        {
            leftParameters_ = leftTarget;
            rightParameters_ = rightTarget;
            parametersPrimed_ = true;
        }

        const float directGain = std::clamp(1.0f / stage.speakerDistance, 0.35f, 1.5f);
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

            // Interpolate per-sample from the previous block's parameters so
            // head movement never steps the delay lines (no zipper noise).
            const float t = static_cast<float>(frame + 1) * inverseFrames;
            const RenderParameters left = Lerp(leftParameters_, leftTarget, t);
            const RenderParameters right = Lerp(rightParameters_, rightTarget, t);

            const float leftToLeft = FilterContribution(
                ReadDelay(delayLeft_, left.delayLeft), leftLowPass_[0], left.alphaLeft);
            const float leftToRight = FilterContribution(
                ReadDelay(delayLeft_, left.delayRight), leftLowPass_[1], left.alphaRight);
            const float rightToLeft = FilterContribution(
                ReadDelay(delayRight_, right.delayLeft), rightLowPass_[0], right.alphaLeft);
            const float rightToRight = FilterContribution(
                ReadDelay(delayRight_, right.delayRight), rightLowPass_[1], right.alphaRight);

            const auto early = CalculateEarlyReflections();
            const auto late = ProcessRoom(0.5f * (delayLeft_[writeIndex_] + delayRight_[writeIndex_]));
            constexpr float kDirectTrim = 0.65f;
            const float wetLeft =
                (leftToLeft * left.gainLeft + rightToLeft * right.gainLeft) * kDirectTrim * directGain +
                early[0] * earlyMix + late[0] * lateMix;
            const float wetRight =
                (leftToRight * left.gainRight + rightToRight * right.gainRight) * kDirectTrim * directGain +
                early[1] * earlyMix + late[1] * lateMix;

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

        leftParameters_ = leftTarget;
        rightParameters_ = rightTarget;
        if (!RenderStateIsFinite())
        {
            ResetFilterState();
        }
    }

    RenderParameters SourceParameters(float worldAzimuth, const StageSettings& stage) const
    {
        const Vector3 head = RotateByInverse(
            orientation_,
            SpeakerDirection(worldAzimuth, stage.speakerElevation));
        const float azimuth = std::atan2(head.x, head.y) * kRadiansToDegrees;
        const float elevation = std::asin(std::clamp(head.z, -1.0f, 1.0f)) * kRadiansToDegrees;
        return CalculateParameters(azimuth, elevation);
    }

    // Azimuth in degrees, positive to the listener's right; elevation in degrees.
    RenderParameters CalculateParameters(float azimuthDegrees, float elevationDegrees) const
    {
        const float azimuth = IsFinite(azimuthDegrees) ? azimuthDegrees : 0.0f;
        const float elevation = IsFinite(elevationDegrees) ? elevationDegrees : 0.0f;
        const float radians = azimuth * kDegreesToRadians;
        const float side = std::sin(radians);
        const float rear = std::max(0.0f, -std::cos(radians));
        const float lateral = std::abs(side);
        const float height = std::sin(std::clamp(elevation, -90.0f, 90.0f) * kDegreesToRadians);

        const float itdSamples = lateral * std::cos(elevation * kDegreesToRadians) * 0.00067f * sampleRate_;
        const float brightness = 1.0f + 0.15f * height;
        const float farEarGain = 0.59f - 0.19f * lateral - 0.06f * rear;
        const float nearEarGain = 0.72f + 0.14f * lateral - 0.04f * rear;
        const float farCutoff = std::clamp(
            (7600.0f - 4800.0f * lateral - 1700.0f * rear) * brightness, 1500.0f, 8800.0f);
        const float nearCutoff = std::clamp(
            (16500.0f - 3500.0f * rear) * brightness, 9000.0f, 18000.0f);

        RenderParameters parameters{};
        if (side >= 0.0f)
        {
            parameters.gainLeft = farEarGain;
            parameters.gainRight = nearEarGain;
            parameters.delayLeft = itdSamples;
            parameters.delayRight = 0.0f;
            parameters.alphaLeft = LowPassAlpha(farCutoff);
            parameters.alphaRight = LowPassAlpha(nearCutoff);
        }
        else
        {
            parameters.gainLeft = nearEarGain;
            parameters.gainRight = farEarGain;
            parameters.delayLeft = 0.0f;
            parameters.delayRight = itdSamples;
            parameters.alphaLeft = LowPassAlpha(nearCutoff);
            parameters.alphaRight = LowPassAlpha(farCutoff);
        }
        return parameters;
    }

    static RenderParameters Lerp(const RenderParameters& a, const RenderParameters& b, float t)
    {
        return RenderParameters{
            a.gainLeft + (b.gainLeft - a.gainLeft) * t,
            a.gainRight + (b.gainRight - a.gainRight) * t,
            a.delayLeft + (b.delayLeft - a.delayLeft) * t,
            a.delayRight + (b.delayRight - a.delayRight) * t,
            a.alphaLeft + (b.alphaLeft - a.alphaLeft) * t,
            a.alphaRight + (b.alphaRight - a.alphaRight) * t};
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

    static float FilterContribution(float input, float& state, float alpha)
    {
        state += alpha * (input - state);
        return state;
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
        bool finite = IsFinite(roomInputBass_);
        for (const float value : leftLowPass_) finite = finite && IsFinite(value);
        for (const float value : rightLowPass_) finite = finite && IsFinite(value);
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
        leftLowPass_.fill(0.0f);
        rightLowPass_.fill(0.0f);
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
        parametersPrimed_ = false;
    }

    void ResetDsp()
    {
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
    bool parametersPrimed_ = false;
    Quaternion orientation_{};
    RenderParameters leftParameters_{};
    RenderParameters rightParameters_{};
    DelayLine delayLeft_{};
    DelayLine delayRight_{};
    std::array<DelayLine, kRoomLineCount> roomDelay_{};
    std::array<float, 2> leftLowPass_{};
    std::array<float, 2> rightLowPass_{};
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
