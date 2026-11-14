// SSAP3 Spatial Engine smoke and robustness tests.
//
// Loads SSAP3Spatializer.dll like Equalizer APO's VST host does and drives it
// with a private state file (SSAP3_SPATIAL_STATE_PATH) so the test never
// touches %ProgramData%. Simulated input only: no audio device is used.

// windows.h otherwise defines min/max macros that break std::min/std::max.
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <iostream>
#include <iterator>
#include <limits>
#include <stdexcept>
#include <string>
#include <vector>

#include "ssap3_shared_state.h"
#include "vst2_abi.h"

namespace
{
using PluginEntry = AEffect* (*)(AudioMasterCallback);
using Buffers = std::array<std::vector<float>, 2>;

constexpr float kNaN = std::numeric_limits<float>::quiet_NaN();
constexpr float kInfinity = std::numeric_limits<float>::infinity();
std::wstring g_statePath;

std::intptr_t AudioMaster(AEffect*, std::int32_t opcode, std::int32_t, std::intptr_t, void*, float)
{
    return opcode == 1 ? 2400 : 0;
}

struct StateRequest
{
    std::int32_t mode = 0;
    std::int32_t tracking = static_cast<std::int32_t>(ssap3::TrackingState::None);
    float yawDegrees = 0.0f;
    float quaternion[4] = {1.0f, 0.0f, 0.0f, 0.0f};
    bool useYaw = true;
    std::uint64_t tickOffsetMs = 0;
    float speakerAzimuth = 30.0f;
    float speakerDistance = 1.0f;
    float speakerElevation = 0.0f;
    float roomMix = 1.0f;
    float intensity = 1.0f;
};

void WriteState(const StateRequest& request)
{
    HANDLE file = CreateFileW(
        g_statePath.c_str(),
        GENERIC_READ | GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr,
        OPEN_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        throw std::runtime_error("Could not open the test state file.");
    }

    // Only grow the file: SetEndOfFile fails while the plug-in has it mapped.
    LARGE_INTEGER current{};
    if (!GetFileSizeEx(file, &current))
    {
        CloseHandle(file);
        throw std::runtime_error("Could not inspect the test state file.");
    }
    if (current.QuadPart < static_cast<LONGLONG>(ssap3::kSharedStateFileSize))
    {
        LARGE_INTEGER size{};
        size.QuadPart = ssap3::kSharedStateFileSize;
        if (!SetFilePointerEx(file, size, nullptr, FILE_BEGIN) || !SetEndOfFile(file))
        {
            CloseHandle(file);
            throw std::runtime_error("Could not size the test state file.");
        }
    }

    HANDLE mapping = CreateFileMappingW(file, nullptr, PAGE_READWRITE, 0, 0, nullptr);
    if (mapping == nullptr)
    {
        CloseHandle(file);
        throw std::runtime_error("Could not map the test state file.");
    }

    auto* state = static_cast<ssap3::SharedStateV2*>(
        MapViewOfFile(mapping, FILE_MAP_WRITE, 0, 0, sizeof(ssap3::SharedStateV2)));
    if (state == nullptr)
    {
        CloseHandle(mapping);
        CloseHandle(file);
        throw std::runtime_error("Could not view the test state file.");
    }

    const long odd = (state->sequence + 1) | 1;
    InterlockedExchange(&state->sequence, odd);
    MemoryBarrier();
    state->magic = ssap3::kSharedStateMagic;
    state->version = ssap3::kSharedStateVersion;
    state->mode = request.mode;
    state->trackingState = request.tracking;
    if (request.useYaw)
    {
        // Yaw is positive to the LEFT: rotation about +Z.
        const float half = request.yawDegrees * 3.14159265f / 360.0f;
        state->orientation[0] = std::cos(half);
        state->orientation[1] = 0.0f;
        state->orientation[2] = 0.0f;
        state->orientation[3] = std::sin(half);
    }
    else
    {
        for (int index = 0; index < 4; ++index)
        {
            state->orientation[index] = request.quaternion[index];
        }
    }
    state->yawDegrees = request.yawDegrees;
    state->pitchDegrees = 0.0f;
    state->rollDegrees = 0.0f;
    state->writerTickMs = GetTickCount64() - request.tickOffsetMs;
    state->speakerAzimuthDegrees = request.speakerAzimuth;
    state->speakerDistanceMeters = request.speakerDistance;
    state->roomMix = request.roomMix;
    state->intensity = request.intensity;
    state->speakerElevationDegrees = request.speakerElevation;
    MemoryBarrier();
    InterlockedExchange(&state->sequence, odd + 1);

    UnmapViewOfFile(state);
    CloseHandle(mapping);
    CloseHandle(file);
}

Buffers MakeInput(std::int32_t frames)
{
    Buffers input{std::vector<float>(frames), std::vector<float>(frames)};
    for (std::int32_t frame = 0; frame < frames; ++frame)
    {
        input[0][frame] = static_cast<float>(0.35 * std::sin(2.0 * 3.141592653589793 * 997.0 * frame / 48000.0));
        input[1][frame] = static_cast<float>(0.22 * std::sin(2.0 * 3.141592653589793 * 613.0 * frame / 48000.0));
    }
    return input;
}

class Plugin
{
public:
    explicit Plugin(PluginEntry entry, float sampleRate = 48000.0f) : effect_(entry(AudioMaster))
    {
        if (effect_ == nullptr || effect_->magic != kEffectMagic)
        {
            throw std::runtime_error("The VST entry point returned an invalid effect.");
        }
        effect_->dispatcher(effect_, effOpen, 0, 0, nullptr, 0.0f);
        effect_->dispatcher(effect_, effSetSampleRate, 0, 0, nullptr, sampleRate);
        effect_->dispatcher(effect_, effSetBlockSize, 0, 4096, nullptr, 0.0f);
        effect_->dispatcher(effect_, effMainsChanged, 0, 1, nullptr, 0.0f);
        effect_->dispatcher(effect_, effStartProcess, 0, 0, nullptr, 0.0f);
    }

    ~Plugin()
    {
        effect_->dispatcher(effect_, effStopProcess, 0, 0, nullptr, 0.0f);
        effect_->dispatcher(effect_, effMainsChanged, 0, 0, nullptr, 0.0f);
        effect_->dispatcher(effect_, effClose, 0, 0, nullptr, 0.0f);
    }

    Plugin(const Plugin&) = delete;
    Plugin& operator=(const Plugin&) = delete;

    void Process(Buffers& input, Buffers& output, std::int32_t offset, std::int32_t frames)
    {
        float* inputs[] = {input[0].data() + offset, input[1].data() + offset};
        float* outputs[] = {output[0].data() + offset, output[1].data() + offset};
        effect_->processReplacing(effect_, inputs, outputs, frames);
    }

private:
    AEffect* effect_;
};

Buffers Render(PluginEntry entry, const StateRequest& request, Buffers input)
{
    WriteState(request);
    Plugin plugin(entry);
    const auto frames = static_cast<std::int32_t>(input[0].size());
    Buffers output{std::vector<float>(frames), std::vector<float>(frames)};
    plugin.Process(input, output, 0, frames);
    return output;
}

double Difference(const Buffers& left, const Buffers& right)
{
    double squared = 0;
    std::size_t count = 0;
    for (std::size_t channel = 0; channel < left.size(); ++channel)
    {
        for (std::size_t frame = 0; frame < left[channel].size(); ++frame)
        {
            const double delta = left[channel][frame] - right[channel][frame];
            squared += delta * delta;
            ++count;
        }
    }
    return std::sqrt(squared / static_cast<double>(count));
}

bool AllFinite(const Buffers& buffers)
{
    for (const auto& channel : buffers)
    {
        for (const float value : channel)
        {
            if (!std::isfinite(value) || std::abs(value) > 1.0f)
            {
                return false;
            }
        }
    }
    return true;
}

void Require(bool condition, const char* message)
{
    if (!condition)
    {
        throw std::runtime_error(message);
    }
}

float MaxStep(const std::vector<float>& signal, std::size_t begin, std::size_t end)
{
    float maximum = 0.0f;
    for (std::size_t index = std::max<std::size_t>(begin, 1); index < end; ++index)
    {
        maximum = std::max(maximum, std::abs(signal[index] - signal[index - 1]));
    }
    return maximum;
}

float MaxStepBoth(const Buffers& buffers, std::size_t begin)
{
    return std::max(MaxStep(buffers[0], begin, buffers[0].size()), MaxStep(buffers[1], begin, buffers[1].size()));
}

bool FiniteSignal(const std::vector<float>& signal)
{
    return std::all_of(signal.begin(), signal.end(), [](float value) { return std::isfinite(value) && std::abs(value) <= 1.0f; });
}

// Two tones per channel, all multiples of 150 Hz, so at 48 kHz the signal
// repeats exactly every 320 samples: any window longer than that contains its
// true largest sample-to-sample step, which makes step comparisons between
// renders fair.
Buffers MakeTones(std::int32_t frames)
{
    Buffers input{std::vector<float>(frames), std::vector<float>(frames)};
    constexpr double kTwoPi = 2.0 * 3.141592653589793;
    for (std::int32_t frame = 0; frame < frames; ++frame)
    {
        const double t = frame / 48000.0;
        input[0][frame] = static_cast<float>(0.25 * std::sin(kTwoPi * 450.0 * t) + 0.12 * std::sin(kTwoPi * 1200.0 * t));
        input[1][frame] = static_cast<float>(0.22 * std::sin(kTwoPi * 600.0 * t) + 0.10 * std::sin(kTwoPi * 1500.0 * t));
    }
    return input;
}

bool SameState(const StateRequest& a, const StateRequest& b)
{
    return a.mode == b.mode && a.tracking == b.tracking && a.yawDegrees == b.yawDegrees && a.useYaw == b.useYaw &&
        std::equal(std::begin(a.quaternion), std::end(a.quaternion), std::begin(b.quaternion)) &&
        a.tickOffsetMs == b.tickOffsetMs && a.speakerAzimuth == b.speakerAzimuth &&
        a.speakerDistance == b.speakerDistance && a.speakerElevation == b.speakerElevation &&
        a.roomMix == b.roomMix && a.intensity == b.intensity;
}

// Deterministic, uncorrelated white noise in each channel.
Buffers MakeNoise(std::int32_t frames, float amplitude)
{
    Buffers input{std::vector<float>(frames), std::vector<float>(frames)};
    std::uint32_t seed = 0x5EED1234u;
    for (auto& channel : input)
    {
        for (auto& value : channel)
        {
            seed = seed * 1664525u + 1013904223u;
            value = amplitude * (static_cast<float>(seed >> 8) / 8388608.0f - 1.0f);
        }
    }
    return input;
}

double Rms(const Buffers& buffers, std::size_t begin)
{
    double sum = 0.0;
    std::size_t count = 0;
    for (const auto& channel : buffers)
    {
        for (std::size_t index = begin; index < channel.size(); ++index)
        {
            sum += static_cast<double>(channel[index]) * channel[index];
            ++count;
        }
    }
    return std::sqrt(sum / static_cast<double>(std::max<std::size_t>(count, 1)));
}

// Processes `input` in fixed-size blocks. stateForBlock(index) is written
// before a block when it changed, and every 32 blocks regardless, which keeps
// a tracked pose well inside the engine's 750 ms freshness limit.
template <typename StateForBlock>
Buffers Stream(PluginEntry entry, Buffers input, std::int32_t block, StateForBlock stateForBlock, float sampleRate = 48000.0f)
{
    const auto frames = static_cast<std::int32_t>(input[0].size());
    Buffers output{std::vector<float>(frames), std::vector<float>(frames)};
    StateRequest written = stateForBlock(0);
    WriteState(written);
    Plugin plugin(entry, sampleRate);
    for (std::int32_t index = 0; index * block + block <= frames; ++index)
    {
        if (index > 0)
        {
            const StateRequest next = stateForBlock(index);
            if (!SameState(next, written) || index % 32 == 0)
            {
                WriteState(next);
                written = next;
            }
        }
        plugin.Process(input, output, index * block, block);
    }
    return output;
}

struct EarResponse
{
    std::vector<float> left;
    std::vector<float> right;
    float sampleRate = 48000.0f;
};

// What each ear receives from an impulse on one input channel, with the room
// off (direct HRIR path only), after the 25 ms mix ramp and any head glide
// have settled.
EarResponse ImpulseResponse(PluginEntry entry, StateRequest request, int channel, float sampleRate = 48000.0f)
{
    request.roomMix = 0.0f;
    constexpr std::int32_t kBlock = 256;
    constexpr std::int32_t kResponseFrames = 2048;
    const auto settle = static_cast<std::int32_t>(std::ceil(0.4 * sampleRate / kBlock)) * kBlock;
    Buffers input{std::vector<float>(settle + kResponseFrames), std::vector<float>(settle + kResponseFrames)};
    input[static_cast<std::size_t>(channel)][static_cast<std::size_t>(settle)] = 0.5f;
    const Buffers output = Stream(entry, input, kBlock, [&request](std::int32_t) { return request; }, sampleRate);

    EarResponse response{};
    response.sampleRate = sampleRate;
    for (std::size_t frame = static_cast<std::size_t>(settle); frame < output[0].size(); ++frame)
    {
        response.left.push_back(output[0][frame] * 2.0f);
        response.right.push_back(output[1][frame] * 2.0f);
    }
    Require(FiniteSignal(response.left) && FiniteSignal(response.right), "An impulse response is not finite and bounded.");
    return response;
}

double Energy(const std::vector<float>& signal)
{
    double sum = 0.0;
    for (const float value : signal)
    {
        sum += static_cast<double>(value) * value;
    }
    return sum;
}

// Arrival time: the first sample at 15 % of the peak (the same rule the
// table builder used to align the measurements).
double OnsetMs(const std::vector<float>& signal, float sampleRate)
{
    float peak = 0.0f;
    for (const float value : signal)
    {
        peak = std::max(peak, std::abs(value));
    }
    for (std::size_t index = 0; index < signal.size(); ++index)
    {
        if (std::abs(signal[index]) >= 0.15f * peak)
        {
            return 1000.0 * static_cast<double>(index) / sampleRate;
        }
    }
    return 0.0;
}

// Interaural level difference, positive when the right ear is louder.
double IldDb(const EarResponse& response)
{
    const double left = Energy(response.left);
    const double right = Energy(response.right);
    Require(left > 1.0e-12 && right > 1.0e-12, "The direct path produced no sound at one ear.");
    return 10.0 * std::log10(right / left);
}

// Interaural time difference, positive when the right ear hears it first.
double ItdMs(const EarResponse& response)
{
    return OnsetMs(response.left, response.sampleRate) - OnsetMs(response.right, response.sampleRate);
}

double NormalizedDifference(const std::vector<float>& reference, const std::vector<float>& other)
{
    double difference = 0.0;
    double energy = 0.0;
    for (std::size_t index = 0; index < std::min(reference.size(), other.size()); ++index)
    {
        const double delta = static_cast<double>(reference[index]) - other[index];
        difference += delta * delta;
        energy += static_cast<double>(reference[index]) * reference[index];
    }
    return std::sqrt(difference / std::max(energy, 1.0e-30));
}
} // namespace

int wmain(int argc, wchar_t** argv)
{
    try
    {
        Require(argc == 2, "Usage: spatializer_smoke.exe <SSAP3Spatializer.dll>");

        wchar_t tempDirectory[MAX_PATH]{};
        Require(GetTempPathW(MAX_PATH, tempDirectory) != 0, "GetTempPathW failed.");
        g_statePath = std::wstring(tempDirectory) + L"ssap3-spatializer-smoke-" +
            std::to_wstring(GetCurrentProcessId()) + L".bin";
        Require(SetEnvironmentVariableW(L"SSAP3_SPATIAL_STATE_PATH", g_statePath.c_str()) != 0,
            "Could not set SSAP3_SPATIAL_STATE_PATH.");

        HMODULE library = LoadLibraryW(argv[1]);
        Require(library != nullptr, "Could not load the spatializer DLL.");
        const auto entry = reinterpret_cast<PluginEntry>(GetProcAddress(library, "VSTPluginMain"));
        Require(entry != nullptr, "VSTPluginMain was not exported.");

        const Buffers input = MakeInput(4096);
        StateRequest off{};
        const Buffers offOutput = Render(entry, off, input);
        Require(offOutput[0] == input[0] && offOutput[1] == input[1], "Off mode is not bit-exact.");

        StateRequest fixed{};
        fixed.mode = 1;
        const Buffers fixedOutput = Render(entry, fixed, input);
        const double fixedDifference = Difference(offOutput, fixedOutput);
        Require(fixedDifference > 0.01, "Fixed soundstage did not alter the signal.");
        Require(AllFinite(fixedOutput), "Fixed output is not finite and bounded.");

        StateRequest tracked = fixed;
        tracked.mode = 2;
        tracked.tracking = static_cast<std::int32_t>(ssap3::TrackingState::Live);
        tracked.yawDegrees = 70.0f;
        const Buffers trackedOutput = Render(entry, tracked, input);
        const double trackedDifference = Difference(fixedOutput, trackedOutput);
        Require(trackedDifference > 0.005, "A tracked head turn did not change the rendering.");

        // S-04 regression: a NaN orientation must not poison or crash the engine.
        StateRequest nanPose = tracked;
        nanPose.useYaw = false;
        nanPose.quaternion[0] = kNaN;
        const Buffers nanPoseOutput = Render(entry, nanPose, input);
        Require(AllFinite(nanPoseOutput), "NaN orientation produced non-finite output.");
        Require(Difference(nanPoseOutput, fixedOutput) < 1.0e-6, "NaN orientation was not ignored.");

        // Non-unit quaternion is ignored as well.
        StateRequest hugePose = tracked;
        hugePose.useYaw = false;
        hugePose.quaternion[0] = 40.0f;
        Require(Difference(Render(entry, hugePose, input), fixedOutput) < 1.0e-6,
            "A non-unit quaternion was not ignored.");

        // Invalid stage parameters reject the whole record (engine stays bypassed).
        for (const float bad : {kNaN, kInfinity, -kInfinity, 1000.0f})
        {
            StateRequest badStage = fixed;
            badStage.speakerAzimuth = bad;
            const Buffers badOutput = Render(entry, badStage, input);
            Require(badOutput[0] == input[0] && badOutput[1] == input[1],
                "An invalid stage parameter was not rejected.");
        }

        // A stale tracked pose falls back to the calibrated forward direction.
        StateRequest stale = tracked;
        stale.tickOffsetMs = 5000;
        Require(Difference(Render(entry, stale, input), fixedOutput) < 1.0e-6,
            "A stale head pose was not ignored.");

        // Non-finite audio input must not leak into the output.
        Buffers poisoned = input;
        poisoned[0][100] = kNaN;
        poisoned[1][200] = kInfinity;
        Require(AllFinite(Render(entry, fixed, poisoned)), "Non-finite input leaked into the output.");

        // Mode switches crossfade instead of stepping (no clicks).
        {
            constexpr std::int32_t block = 256;
            constexpr std::int32_t blocks = 32;
            Buffers streamInput = MakeInput(block * blocks);
            Buffers streamOutput{std::vector<float>(block * blocks), std::vector<float>(block * blocks)};
            WriteState(off);
            Plugin plugin(entry);
            for (std::int32_t index = 0; index < blocks; ++index)
            {
                if (index == blocks / 2)
                {
                    WriteState(fixed);
                }
                plugin.Process(streamInput, streamOutput, index * block, block);
            }

            const std::size_t switchFrame = static_cast<std::size_t>(block * (blocks / 2));
            const float inputStep = MaxStep(streamInput[0], 0, streamInput[0].size());
            const float transitionStep = MaxStep(streamOutput[0], switchFrame - 8, switchFrame + 2400);
            Require(transitionStep < inputStep * 2.5f, "Switching Off -> Fixed produced a step (click).");
        }

        // ---- Measured HRIR direct path (MIT KEMAR, diffuse-field equalized) ----
        // Reference values are KEMAR's own (table builder and docs/TECHNICAL_REPORT.md
        // section 5): ILD 9.5 dB / ITD 0.23-0.25 ms at 30 degrees, 14.9 dB / 0.68 ms at 90.
        const StateRequest stage = fixed; // speakers at -30 and +30 degrees, 1 m
        const EarResponse left30 = ImpulseResponse(entry, stage, 0);
        const EarResponse right30 = ImpulseResponse(entry, stage, 1);
        const double ild30 = IldDb(left30);
        const double itd30 = ItdMs(left30);
        Require(ild30 < -6.0 && ild30 > -13.0, "Left speaker at -30 degrees: the left ear is not 6-13 dB louder.");
        Require(itd30 < -0.15 && itd30 > -0.35, "Left speaker at -30 degrees: the left ear does not lead by 0.15-0.35 ms.");
        Require(std::abs(ild30 + IldDb(right30)) < 0.01 && std::abs(itd30 + ItdMs(right30)) < 1.0e-9,
            "The left and right speakers are not mirror images.");

        StateRequest turned = tracked; // a live head pose
        turned.yawDegrees = 30.0f;     // turning left by 30 degrees puts the left speaker straight ahead
        const EarResponse ahead = ImpulseResponse(entry, turned, 0);
        Require(std::abs(IldDb(ahead)) < 0.5 && std::abs(ItdMs(ahead)) < 0.03, "A source straight ahead is not centred.");
        turned.yawDegrees = 210.0f;    // ... and directly behind
        const EarResponse behind = ImpulseResponse(entry, turned, 0);
        Require(std::abs(IldDb(behind)) < 0.5 && std::abs(ItdMs(behind)) < 0.03, "A source directly behind is not centred.");
        const double frontBackDb = 10.0 * std::log10(Energy(ahead.left) / Energy(behind.left));
        Require(frontBackDb > 1.5, "Front and back sound the same (KEMAR: the front is about 5 dB stronger).");
        Require(NormalizedDifference(ahead.left, behind.left) > 0.3, "Front and back responses are not distinct.");

        turned.yawDegrees = -60.0f;    // turning right by 60 degrees puts the left speaker at -90
        const EarResponse leftSide = ImpulseResponse(entry, turned, 0);
        Require(IldDb(leftSide) < -10.0 && ItdMs(leftSide) < -0.55 && ItdMs(leftSide) > -0.80,
            "A source at -90 degrees does not reach the left ear 10+ dB louder and 0.55-0.80 ms earlier.");
        turned.yawDegrees = 60.0f;     // and the right speaker at +90
        const EarResponse rightSide = ImpulseResponse(entry, turned, 1);
        Require(IldDb(rightSide) > 10.0 && ItdMs(rightSide) > 0.55 && ItdMs(rightSide) < 0.80,
            "A source at +90 degrees does not reach the right ear 10+ dB louder and 0.55-0.80 ms earlier.");

        // ITD and ILD grow with the stage width (right speaker at +10, +30, +60, +90).
        {
            constexpr std::array<float, 4> azimuths{10.0f, 30.0f, 60.0f, 90.0f};
            double previousItd = 0.0;
            std::array<double, 4> ild{};
            for (std::size_t index = 0; index < azimuths.size(); ++index)
            {
                StateRequest wide = stage;
                wide.speakerAzimuth = azimuths[index];
                const EarResponse response = ImpulseResponse(entry, wide, 1);
                const double itd = ItdMs(response);
                Require(itd > previousItd + 0.02, "The ITD does not grow with azimuth (10, 30, 60, 90 degrees).");
                previousItd = itd;
                ild[index] = IldDb(response);
            }
            Require(ild[3] > ild[0] + 6.0, "The ILD at 90 degrees is not well above the ILD at 10 degrees.");
        }

        StateRequest raised = stage;
        raised.speakerElevation = 30.0f;
        Require(NormalizedDifference(left30.left, ImpulseResponse(entry, raised, 0).left) > 0.2,
            "Speaker elevation does not change the measured response.");

        // The same cues at every device rate (the table is 44.1 kHz).
        for (const float rate : {44100.0f, 96000.0f, 192000.0f})
        {
            const EarResponse atRate = ImpulseResponse(entry, stage, 0, rate);
            Require(std::abs(ItdMs(atRate) - itd30) < 0.05, "The ITD depends on the sample rate.");
            Require(std::abs(IldDb(atRate) - ild30) < 1.0, "The ILD depends on the sample rate.");
            // For the same frequency response, energy per tap scales with 1/rate.
            const double energyRatio = Energy(atRate.left) * rate / (Energy(left30.left) * 48000.0);
            Require(energyRatio > 0.8 && energyRatio < 1.25, "The direct-path level depends on the sample rate.");
        }
        for (const float rate : {8000.0f, 16000.0f, 384000.0f})
        {
            Require(IldDb(ImpulseResponse(entry, stage, 0, rate)) < -3.0, "Rendering at 8, 16 or 384 kHz lost the stage.");
        }

        // Loudness sanity: broadband noise through the full renderer (room on).
        double loudnessDb = 0.0;
        {
            const Buffers noise = MakeNoise(48000, 0.1f);
            const Buffers rendered = Render(entry, fixed, noise);
            Require(AllFinite(rendered), "Rendered noise is not finite and bounded.");
            loudnessDb = 20.0 * std::log10(Rms(rendered, 4800) / Rms(noise, 4800));
            Require(loudnessDb > -12.0 && loudnessDb < 3.0, "Fixed-mode loudness is not within -12..+3 dB of the input.");
        }

        // No clicks while the head turns: a full turn in 2 s, then two abrupt
        // jumps, must not step more than a stationary head at any angle does.
        double sweepRatio = 0.0;
        {
            constexpr std::int32_t block = 128;
            constexpr std::int32_t settleBlocks = 188; // 0.5 s
            StateRequest still = tracked;
            still.roomMix = 0.0f;
            float baseline = 0.0f;
            for (int angle = 0; angle < 360; angle += 5)
            {
                still.yawDegrees = static_cast<float>(angle);
                const Buffers output =
                    Stream(entry, MakeTones(block * (settleBlocks + 64)), block, [&still](std::int32_t) { return still; });
                baseline = std::max(baseline, MaxStepBoth(output, static_cast<std::size_t>(block) * settleBlocks));
            }

            constexpr std::int32_t sweepBlocks = 750;
            constexpr std::int32_t holdBlocks = 94;
            StateRequest moving = still;
            const Buffers swept = Stream(entry, MakeTones(block * (settleBlocks + sweepBlocks + 2 * holdBlocks)), block,
                [&](std::int32_t index)
                {
                    if (index < settleBlocks)
                    {
                        moving.yawDegrees = 0.0f;
                    }
                    else if (index < settleBlocks + sweepBlocks)
                    {
                        moving.yawDegrees = 360.0f * static_cast<float>(index - settleBlocks) / static_cast<float>(sweepBlocks);
                    }
                    else
                    {
                        moving.yawDegrees = index < settleBlocks + sweepBlocks + holdBlocks ? 120.0f : -90.0f;
                    }
                    return moving;
                });
            Require(AllFinite(swept), "Turning the head produced non-finite output.");
            const float sweepStep = MaxStepBoth(swept, static_cast<std::size_t>(block) * settleBlocks);
            sweepRatio = sweepStep / baseline;
            Require(sweepStep < baseline * 1.3f, "Turning the head produced a step (click).");
        }

        // No clicks when the virtual speakers move (no orientation glide, so
        // the new filters arrive in one block and the crossfade alone smooths it).
        {
            constexpr std::int32_t block = 256;
            StateRequest still = stage;
            still.roomMix = 0.0f;
            float baseline = 0.0f;
            for (const float azimuth : {10.0f, 30.0f, 90.0f})
            {
                still.speakerAzimuth = azimuth;
                const Buffers output = Stream(entry, MakeTones(block * 64), block, [&still](std::int32_t) { return still; });
                baseline = std::max(baseline, MaxStepBoth(output, static_cast<std::size_t>(block) * 16));
            }

            StateRequest jumping = still;
            const Buffers jumped = Stream(entry, MakeTones(block * 96), block, [&jumping](std::int32_t index)
            {
                jumping.speakerAzimuth = index < 32 ? 30.0f : (index < 64 ? 90.0f : 10.0f);
                return jumping;
            });
            Require(MaxStepBoth(jumped, static_cast<std::size_t>(block) * 16) < baseline * 1.3f,
                "Moving the virtual speakers produced a step (click).");
        }

        // Off is bit-exact again once the fade-out after Fixed has finished.
        {
            constexpr std::int32_t block = 256;
            const Buffers tones = MakeTones(block * 64);
            const Buffers output =
                Stream(entry, tones, block, [&fixed, &off](std::int32_t index) { return index < 32 ? fixed : off; });
            const auto from = static_cast<std::ptrdiff_t>(block) * 40; // 25 ms ramp ends within 8 blocks
            Require(std::equal(output[0].begin() + from, output[0].end(), tones[0].begin() + from) &&
                    std::equal(output[1].begin() + from, output[1].end(), tones[1].begin() + from),
                "Off is not bit-exact after the soundstage was on.");
        }

        // Real-time headroom: 10 s at 48 kHz in 10 ms blocks with the head
        // turning continuously (filters rebuilt and crossfaded every block).
        double realTimeFactor = 0.0;
        {
            constexpr std::int32_t block = 480;
            constexpr std::int32_t blocks = 1000;
            Buffers tones = MakeTones(block * blocks);
            Buffers output{std::vector<float>(tones[0].size()), std::vector<float>(tones[1].size())};
            StateRequest moving = tracked;
            WriteState(moving);
            Plugin plugin(entry);
            LARGE_INTEGER frequency{};
            QueryPerformanceFrequency(&frequency);
            long long ticks = 0;
            for (std::int32_t index = 0; index < blocks; ++index)
            {
                if (index % 2 == 0) // the app writes the pose at about 50 Hz
                {
                    moving.yawDegrees = 1.8f * static_cast<float>(index);
                    WriteState(moving);
                }
                LARGE_INTEGER started{};
                LARGE_INTEGER finished{};
                QueryPerformanceCounter(&started);
                plugin.Process(tones, output, index * block, block);
                QueryPerformanceCounter(&finished);
                ticks += finished.QuadPart - started.QuadPart;
            }
            Require(AllFinite(output), "Continuous head motion produced non-finite output.");
            realTimeFactor = static_cast<double>(ticks) / static_cast<double>(frequency.QuadPart) / 10.0;
            Require(realTimeFactor < 0.25, "The renderer needs more than 25 % of one core in real time.");
        }

        StateRequest reset{};
        WriteState(reset);
        FreeLibrary(library);
        DeleteFileW(g_statePath.c_str());

        std::cout << "SSAP3 spatializer tests passed. fixed RMS delta=" << fixedDifference
                  << ", tracked RMS delta=" << trackedDifference << std::endl
                  << "HRIR at -30 deg: ILD=" << ild30 << " dB, ITD=" << itd30 << " ms; at -90 deg: ILD="
                  << IldDb(leftSide) << " dB, ITD=" << ItdMs(leftSide) << " ms; front/back=" << frontBackDb
                  << " dB" << std::endl
                  << "Loudness vs input=" << loudnessDb << " dB, head-sweep step ratio=" << sweepRatio
                  << ", real-time factor=" << realTimeFactor << std::endl;
        return 0;
    }
    catch (const std::exception& exception)
    {
        if (!g_statePath.empty())
        {
            DeleteFileW(g_statePath.c_str());
        }
        std::cerr << "FAILED: " << exception.what() << std::endl;
        return 1;
    }
}
