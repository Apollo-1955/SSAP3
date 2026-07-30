#include <windows.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>

#include "vst2_abi.h"

namespace
{
constexpr std::uint32_t kStateMagic = 0x54485353; // "SSHT"
constexpr std::uint32_t kStateVersion = 1;
constexpr float kPi = 3.14159265358979323846f;
constexpr int kDelayBufferSize = 32768;
constexpr int kRoomLineCount = 4;
constexpr wchar_t kStatePath[] =
    L"C:\\ProgramData\\SoundStage\\head-tracking-state.bin";

enum class SpatialMode : std::int32_t
{
    Off = 0,
    Fixed = 1,
    Tracked = 2,
};

struct SharedState
{
    std::uint32_t magic;
    std::uint32_t version;
    volatile LONG sequence;
    std::int32_t mode;
    float yaw;
    float pitch;
    float roll;
    float reserved;
    std::int64_t timestampUnixMilliseconds;
};

static_assert(offsetof(SharedState, mode) == 12);
static_assert(offsetof(SharedState, timestampUnixMilliseconds) == 32);

struct RenderParameters
{
    float gainLeft;
    float gainRight;
    float delayLeft;
    float delayRight;
    float alphaLeft;
    float alphaRight;
};

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
        effect_.uniqueId = kSoundStageUniqueId;
        effect_.version = 0x00020400;
        effect_.processReplacing = ProcessReplacing;
    }

    ~Spatializer()
    {
        CloseState();
    }

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
            return 1;
        case effClose:
            delete self;
            return 1;
        case effSetSampleRate:
            self->sampleRate_ = std::clamp(option, 8000.0f, 384000.0f);
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
            else
            {
                self->CloseState();
            }
            return 1;
        case effStartProcess:
            self->OpenState();
            self->ResetDsp();
            return 1;
        case effStopProcess:
            return 1;
        case effGetEffectName:
            CopyText(pointer, "SoundStage Spatializer", 64);
            return 1;
        case effGetProductString:
            CopyText(pointer, "SoundStage Head-Tracked HRTF", 64);
            return 1;
        case effGetVendorString:
            CopyText(pointer, "OpenAI Codex", 64);
            return 1;
        case effGetVendorVersion:
            return 20400;
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

        auto* text = static_cast<char*>(destination);
        strncpy_s(text, capacity, value, _TRUNCATE);
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

        SpatialMode mode = SpatialMode::Off;
        float targetYaw = 0.0f;
        float targetPitch = 0.0f;
        ReadState(mode, targetYaw, targetPitch);

        if (mode != previousMode_)
        {
            ResetDsp();
            previousMode_ = mode;
        }

        if (mode == SpatialMode::Off)
        {
            if (outputs[0] != inputs[0])
            {
                std::memcpy(outputs[0], inputs[0], frameCount * sizeof(float));
            }
            if (outputs[1] != inputs[1])
            {
                std::memcpy(outputs[1], inputs[1], frameCount * sizeof(float));
            }
            return;
        }

        if (mode == SpatialMode::Fixed)
        {
            targetYaw = 0.0f;
            targetPitch = 0.0f;
        }

        targetYaw = std::clamp(targetYaw, -120.0f, 120.0f);
        targetPitch = std::clamp(targetPitch, -45.0f, 45.0f);
        const auto blockSeconds = static_cast<float>(frameCount) / sampleRate_;
        const auto smoothing = 1.0f - std::exp(-blockSeconds / 0.055f);
        smoothedYaw_ += WrapDegrees(targetYaw - smoothedYaw_) * smoothing;
        smoothedPitch_ += (targetPitch - smoothedPitch_) * smoothing;

        // Apple describes its automatic stereo spatializer as a pair of
        // detached virtual speakers in front of the listener plus a room
        // response. A moderately large +/- 38 degree stage makes that
        // externalized presentation audible without pushing the sources
        // unnaturally behind the listener.
        const auto leftSource = CalculateParameters(-38.0f - smoothedYaw_);
        const auto rightSource = CalculateParameters(38.0f - smoothedYaw_);
        const auto elevationGain = std::clamp(
            1.0f + (smoothedPitch_ / 45.0f) * 0.035f,
            0.965f,
            1.035f);

        for (std::int32_t frame = 0; frame < frameCount; ++frame)
        {
            const float inputLeft = inputs[0][frame];
            const float inputRight = inputs[1][frame];
            delayLeft_[delayWriteIndex_] = inputLeft;
            delayRight_[delayWriteIndex_] = inputRight;

            const float leftToLeft = FilterContribution(
                ReadDelay(delayLeft_, leftSource.delayLeft),
                leftLowPass_[0],
                leftSource.alphaLeft);
            const float leftToRight = FilterContribution(
                ReadDelay(delayLeft_, leftSource.delayRight),
                leftLowPass_[1],
                leftSource.alphaRight);
            const float rightToLeft = FilterContribution(
                ReadDelay(delayRight_, rightSource.delayLeft),
                rightLowPass_[0],
                rightSource.alphaLeft);
            const float rightToRight = FilterContribution(
                ReadDelay(delayRight_, rightSource.delayRight),
                rightLowPass_[1],
                rightSource.alphaRight);

            const auto earlyRoom = CalculateEarlyReflections();
            const auto lateRoom =
                ProcessRoom(0.5f * (inputLeft + inputRight));
            constexpr float kDirectTrim = 0.65f;
            constexpr float kEarlyRoomMix = 0.285f;
            constexpr float kLateRoomMix = 0.16f;
            outputs[0][frame] = SoftLimit(
                (leftToLeft * leftSource.gainLeft +
                 rightToLeft * rightSource.gainLeft) *
                    kDirectTrim * elevationGain +
                earlyRoom[0] * kEarlyRoomMix +
                lateRoom[0] * kLateRoomMix);
            outputs[1][frame] = SoftLimit(
                (leftToRight * leftSource.gainRight +
                 rightToRight * rightSource.gainRight) *
                    kDirectTrim * elevationGain +
                earlyRoom[1] * kEarlyRoomMix +
                lateRoom[1] * kLateRoomMix);

            delayWriteIndex_ = (delayWriteIndex_ + 1) % kDelayBufferSize;
        }
    }

    std::array<float, 2> CalculateEarlyReflections()
    {
        // A dense set of short, asymmetric taps suggests a compact,
        // acoustically treated listening room without a distinct echo.
        const auto tap = [this](
                             const std::array<float, kDelayBufferSize>& buffer,
                             float milliseconds)
        {
            return ReadDelay(buffer, milliseconds * sampleRate_ / 1000.0f);
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
        return {
            BandLimitReflection(rawLeft, 0),
            BandLimitReflection(rawRight, 1)};
    }

    float BandLimitReflection(float input, int channel)
    {
        const float lowPassAlpha =
            1.0f - std::exp(-2.0f * kPi * 4800.0f / sampleRate_);
        const float bassAlpha =
            1.0f - std::exp(-2.0f * kPi * 140.0f / sampleRate_);
        earlyRoomLowPass_[channel] +=
            lowPassAlpha * (input - earlyRoomLowPass_[channel]);
        earlyRoomBass_[channel] +=
            bassAlpha *
            (earlyRoomLowPass_[channel] - earlyRoomBass_[channel]);
        return earlyRoomLowPass_[channel] - earlyRoomBass_[channel];
    }

    std::array<float, 2> ProcessRoom(float input)
    {
        const float bassAlpha =
            1.0f - std::exp(-2.0f * kPi * 160.0f / sampleRate_);
        roomInputBass_ += bassAlpha * (input - roomInputBass_);
        input -= roomInputBass_;

        std::array<float, kRoomLineCount> delayed{};
        for (int line = 0; line < kRoomLineCount; ++line)
        {
            delayed[line] = roomDelay_[line][roomIndex_[line]];
            // Air absorption in the feedback path prevents a metallic,
            // over-bright tail.
            roomDamping_[line] +=
                0.24f * (delayed[line] - roomDamping_[line]);
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

        constexpr std::array<float, kRoomLineCount> inputPolarity{
            1.0f,
            -1.0f,
            1.0f,
            1.0f};
        for (int line = 0; line < kRoomLineCount; ++line)
        {
            roomDelay_[line][roomIndex_[line]] =
                input * inputPolarity[line] +
                feedback[line] * kFeedback;
            roomIndex_[line] =
                (roomIndex_[line] + 1) % roomLength_[line];
        }

        return {
            (a + b - c - d) * kMatrixScale,
            (a - b + c - d) * kMatrixScale};
    }

    RenderParameters CalculateParameters(float azimuthDegrees) const
    {
        const auto azimuth = WrapDegrees(azimuthDegrees);
        const auto radians = azimuth * kPi / 180.0f;
        const auto side = std::sin(radians);
        const auto rear = std::max(0.0f, -std::cos(radians));
        const auto lateral = std::abs(side);

        const auto itdSamples =
            lateral * 0.00067f * sampleRate_;
        // A slightly stronger far-ear path moves the virtual pair away from
        // the ears and toward a detached loudspeaker presentation.
        const auto farEarGain =
            0.59f - 0.19f * lateral - 0.06f * rear;
        const auto nearEarGain =
            0.72f + 0.14f * lateral - 0.04f * rear;
        const auto farCutoff =
            std::clamp(7600.0f - 4800.0f * lateral - 1700.0f * rear,
                       1500.0f,
                       7600.0f);
        const auto nearCutoff =
            std::clamp(16500.0f - 3500.0f * rear,
                       9000.0f,
                       18000.0f);

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

    float LowPassAlpha(float cutoff) const
    {
        return 1.0f - std::exp(-2.0f * kPi * cutoff / sampleRate_);
    }

    float ReadDelay(
        const std::array<float, kDelayBufferSize>& buffer,
        float delaySamples) const
    {
        const auto clamped = std::clamp(
            delaySamples,
            0.0f,
            static_cast<float>(kDelayBufferSize - 3));
        const auto whole = static_cast<int>(clamped);
        const auto fraction = clamped - static_cast<float>(whole);
        const auto newerIndex =
            (delayWriteIndex_ - whole + kDelayBufferSize) % kDelayBufferSize;
        const auto olderIndex =
            (newerIndex - 1 + kDelayBufferSize) % kDelayBufferSize;
        return buffer[newerIndex] * (1.0f - fraction) +
            buffer[olderIndex] * fraction;
    }

    static float FilterContribution(float input, float& state, float alpha)
    {
        state += alpha * (input - state);
        return state;
    }

    static float SoftLimit(float value)
    {
        return std::clamp(value, -1.0f, 1.0f);
    }

    static float WrapDegrees(float value)
    {
        while (value > 180.0f)
        {
            value -= 360.0f;
        }
        while (value < -180.0f)
        {
            value += 360.0f;
        }
        return value;
    }

    void ReadState(SpatialMode& mode, float& yaw, float& pitch)
    {
        mode = SpatialMode::Off;
        yaw = 0.0f;
        pitch = 0.0f;
        if (sharedState_ == nullptr)
        {
            return;
        }

        SharedState snapshot{};
        for (int attempt = 0; attempt < 3; ++attempt)
        {
            const LONG first = sharedState_->sequence;
            MemoryBarrier();
            if ((first & 1) != 0)
            {
                continue;
            }

            std::memcpy(
                &snapshot,
                const_cast<const SharedState*>(sharedState_),
                sizeof(snapshot));
            MemoryBarrier();
            const LONG second = sharedState_->sequence;
            if (first == second && (second & 1) == 0)
            {
                break;
            }
        }

        if (snapshot.magic != kStateMagic ||
            snapshot.version != kStateVersion ||
            snapshot.mode < static_cast<int>(SpatialMode::Off) ||
            snapshot.mode > static_cast<int>(SpatialMode::Tracked))
        {
            return;
        }

        mode = static_cast<SpatialMode>(snapshot.mode);
        if (mode != SpatialMode::Tracked)
        {
            return;
        }

        FILETIME fileTime{};
        GetSystemTimeAsFileTime(&fileTime);
        ULARGE_INTEGER current{};
        current.LowPart = fileTime.dwLowDateTime;
        current.HighPart = fileTime.dwHighDateTime;
        constexpr std::uint64_t kWindowsToUnixMilliseconds = 11644473600000ULL;
        const auto nowUnixMilliseconds =
            static_cast<std::int64_t>(current.QuadPart / 10000ULL -
                                      kWindowsToUnixMilliseconds);
        if (snapshot.timestampUnixMilliseconds <= 0 ||
            nowUnixMilliseconds - snapshot.timestampUnixMilliseconds > 1200)
        {
            return;
        }

        yaw = snapshot.yaw;
        pitch = snapshot.pitch;
    }

    void OpenState()
    {
        if (sharedState_ != nullptr)
        {
            return;
        }

        stateFile_ = CreateFileW(
            kStatePath,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);
        if (stateFile_ == INVALID_HANDLE_VALUE)
        {
            return;
        }

        stateMapping_ = CreateFileMappingW(
            stateFile_,
            nullptr,
            PAGE_READONLY,
            0,
            0,
            nullptr);
        if (stateMapping_ == nullptr)
        {
            CloseHandle(stateFile_);
            stateFile_ = INVALID_HANDLE_VALUE;
            return;
        }

        sharedState_ = static_cast<const volatile SharedState*>(
            MapViewOfFile(
                stateMapping_,
                FILE_MAP_READ,
                0,
                0,
                sizeof(SharedState)));
        if (sharedState_ == nullptr)
        {
            CloseHandle(stateMapping_);
            CloseHandle(stateFile_);
            stateMapping_ = nullptr;
            stateFile_ = INVALID_HANDLE_VALUE;
        }
    }

    void CloseState()
    {
        if (sharedState_ != nullptr)
        {
            UnmapViewOfFile(const_cast<const SharedState*>(sharedState_));
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

    void ResetDsp()
    {
        delayLeft_.fill(0.0f);
        delayRight_.fill(0.0f);
        for (auto& line : roomDelay_)
        {
            line.fill(0.0f);
        }
        leftLowPass_.fill(0.0f);
        rightLowPass_.fill(0.0f);
        earlyRoomLowPass_.fill(0.0f);
        earlyRoomBass_.fill(0.0f);
        roomDamping_.fill(0.0f);
        roomIndex_.fill(0);
        roomInputBass_ = 0.0f;
        constexpr std::array<float, kRoomLineCount> roomSeconds{
            0.0197f,
            0.0239f,
            0.0293f,
            0.0329f};
        for (int line = 0; line < kRoomLineCount; ++line)
        {
            roomLength_[line] = std::clamp(
                static_cast<int>(roomSeconds[line] * sampleRate_),
                3,
                kDelayBufferSize);
        }
        delayWriteIndex_ = 0;
        smoothedYaw_ = 0.0f;
        smoothedPitch_ = 0.0f;
    }

    AEffect effect_{};
    float sampleRate_ = 48000.0f;
    float smoothedYaw_ = 0.0f;
    float smoothedPitch_ = 0.0f;
    SpatialMode previousMode_ = SpatialMode::Off;
    std::array<float, kDelayBufferSize> delayLeft_{};
    std::array<float, kDelayBufferSize> delayRight_{};
    std::array<
        std::array<float, kDelayBufferSize>,
        kRoomLineCount> roomDelay_{};
    std::array<float, 2> leftLowPass_{};
    std::array<float, 2> rightLowPass_{};
    std::array<float, 2> earlyRoomLowPass_{};
    std::array<float, 2> earlyRoomBass_{};
    std::array<float, kRoomLineCount> roomDamping_{};
    std::array<int, kRoomLineCount> roomIndex_{};
    std::array<int, kRoomLineCount> roomLength_{};
    float roomInputBass_ = 0.0f;
    int delayWriteIndex_ = 0;
    HANDLE stateFile_ = INVALID_HANDLE_VALUE;
    HANDLE stateMapping_ = nullptr;
    const volatile SharedState* sharedState_ = nullptr;
};
} // namespace

extern "C" __declspec(dllexport) AEffect* VSTPluginMain(
    AudioMasterCallback)
{
    return (new Spatializer())->Effect();
}

BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID)
{
    return TRUE;
}
