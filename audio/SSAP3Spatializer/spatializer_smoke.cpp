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
#include <cstdint>
#include <iostream>
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
    state->speakerElevationDegrees = 0.0f;
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
    explicit Plugin(PluginEntry entry) : effect_(entry(AudioMaster))
    {
        if (effect_ == nullptr || effect_->magic != kEffectMagic)
        {
            throw std::runtime_error("The VST entry point returned an invalid effect.");
        }
        effect_->dispatcher(effect_, effOpen, 0, 0, nullptr, 0.0f);
        effect_->dispatcher(effect_, effSetSampleRate, 0, 0, nullptr, 48000.0f);
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

        StateRequest reset{};
        WriteState(reset);
        FreeLibrary(library);
        DeleteFileW(g_statePath.c_str());

        std::cout << "SSAP3 spatializer tests passed. fixed RMS delta=" << fixedDifference
                  << ", tracked RMS delta=" << trackedDifference << std::endl;
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
