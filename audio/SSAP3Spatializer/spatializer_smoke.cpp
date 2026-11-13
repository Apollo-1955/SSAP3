#include <windows.h>

#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <iostream>
#include <vector>

#include "vst2_abi.h"

namespace
{
constexpr std::uint32_t kStateMagic = 0x54485353;
constexpr std::uint32_t kStateVersion = 1;
constexpr std::int64_t kStateCapacity = 4096;
constexpr wchar_t kStateDirectory[] = L"C:\\ProgramData\\SSAP3";
constexpr wchar_t kStatePath[] =
    L"C:\\ProgramData\\SSAP3\\head-tracking-state.bin";

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

using PluginEntry = AEffect* (*)(AudioMasterCallback);

std::intptr_t AudioMaster(
    AEffect*,
    std::int32_t opcode,
    std::int32_t,
    std::intptr_t,
    void*,
    float)
{
    return opcode == 1 ? 2400 : 0;
}

void SetState(std::int32_t mode, float yaw)
{
    if (!CreateDirectoryW(kStateDirectory, nullptr) &&
        GetLastError() != ERROR_ALREADY_EXISTS)
    {
        throw std::runtime_error("Could not create the SSAP3 state directory.");
    }

    HANDLE file = CreateFileW(
        kStatePath,
        GENERIC_READ | GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr,
        OPEN_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        throw std::runtime_error("Could not open the SSAP3 state file.");
    }

    LARGE_INTEGER currentSize{};
    if (!GetFileSizeEx(file, &currentSize))
    {
        CloseHandle(file);
        throw std::runtime_error("Could not inspect the SSAP3 state file.");
    }
    if (currentSize.QuadPart < kStateCapacity)
    {
        LARGE_INTEGER requiredSize{};
        requiredSize.QuadPart = kStateCapacity;
        if (!SetFilePointerEx(file, requiredSize, nullptr, FILE_BEGIN) ||
            !SetEndOfFile(file))
        {
            CloseHandle(file);
            throw std::runtime_error("Could not size the SSAP3 state file.");
        }
    }

    HANDLE mapping =
        CreateFileMappingW(file, nullptr, PAGE_READWRITE, 0, 0, nullptr);
    if (mapping == nullptr)
    {
        CloseHandle(file);
        throw std::runtime_error("Could not map the SSAP3 state file.");
    }

    auto* state = static_cast<SharedState*>(
        MapViewOfFile(mapping, FILE_MAP_WRITE, 0, 0, sizeof(SharedState)));
    if (state == nullptr)
    {
        CloseHandle(mapping);
        CloseHandle(file);
        throw std::runtime_error("Could not view the SSAP3 state file.");
    }

    const LONG odd = InterlockedIncrement(&state->sequence) | 1;
    InterlockedExchange(&state->sequence, odd);
    MemoryBarrier();
    state->magic = kStateMagic;
    state->version = kStateVersion;
    state->mode = mode;
    state->yaw = yaw;
    state->pitch = 0.0f;
    state->roll = 0.0f;
    state->reserved = 0.0f;
    state->timestampUnixMilliseconds =
        std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::system_clock::now().time_since_epoch())
            .count();
    MemoryBarrier();
    InterlockedExchange(&state->sequence, (odd + 1) & ~1);

    UnmapViewOfFile(state);
    CloseHandle(mapping);
    CloseHandle(file);
}

std::array<std::vector<float>, 2> Render(
    PluginEntry entry,
    std::int32_t mode,
    float yaw)
{
    SetState(mode, yaw);
    AEffect* effect = entry(AudioMaster);
    if (effect == nullptr || effect->magic != kEffectMagic)
    {
        throw std::runtime_error("The VST entry point returned an invalid effect.");
    }

    constexpr std::int32_t frameCount = 4096;
    std::array<std::vector<float>, 2> input{
        std::vector<float>(frameCount),
        std::vector<float>(frameCount)};
    std::array<std::vector<float>, 2> output{
        std::vector<float>(frameCount),
        std::vector<float>(frameCount)};
    for (std::int32_t frame = 0; frame < frameCount; ++frame)
    {
        input[0][frame] =
            static_cast<float>(
                0.35 * std::sin(
                    2.0 * 3.141592653589793 * 997.0 * frame / 48000.0));
        input[1][frame] =
            static_cast<float>(
                0.22 * std::sin(
                    2.0 * 3.141592653589793 * 613.0 * frame / 48000.0));
    }

    float* inputs[] = {input[0].data(), input[1].data()};
    float* outputs[] = {output[0].data(), output[1].data()};
    effect->dispatcher(effect, effOpen, 0, 0, nullptr, 0.0f);
    effect->dispatcher(effect, effSetSampleRate, 0, 0, nullptr, 48000.0f);
    effect->dispatcher(effect, effSetBlockSize, 0, frameCount, nullptr, 0.0f);
    effect->dispatcher(effect, effMainsChanged, 0, 1, nullptr, 0.0f);
    effect->dispatcher(effect, effStartProcess, 0, 0, nullptr, 0.0f);
    effect->processReplacing(effect, inputs, outputs, frameCount);
    effect->dispatcher(effect, effStopProcess, 0, 0, nullptr, 0.0f);
    effect->dispatcher(effect, effMainsChanged, 0, 0, nullptr, 0.0f);
    effect->dispatcher(effect, effClose, 0, 0, nullptr, 0.0f);

    if (mode == 0 && (output[0] != input[0] || output[1] != input[1]))
    {
        throw std::runtime_error("Off mode is not bit-exact.");
    }
    return output;
}

double Difference(
    const std::array<std::vector<float>, 2>& left,
    const std::array<std::vector<float>, 2>& right)
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
} // namespace

int wmain(int argc, wchar_t** argv)
{
    try
    {
        if (argc != 2)
        {
            throw std::runtime_error("Usage: spatializer_smoke.exe <plugin.dll>");
        }
        HMODULE library = LoadLibraryW(argv[1]);
        if (library == nullptr)
        {
            throw std::runtime_error("Could not load the spatializer DLL.");
        }
        const auto entry = reinterpret_cast<PluginEntry>(
            GetProcAddress(library, "VSTPluginMain"));
        if (entry == nullptr)
        {
            FreeLibrary(library);
            throw std::runtime_error("VSTPluginMain was not exported.");
        }

        const auto off = Render(entry, 0, 0.0f);
        const auto fixed = Render(entry, 1, 0.0f);
        const auto tracked = Render(entry, 2, 70.0f);
        const double fixedDifference = Difference(off, fixed);
        const double trackedDifference = Difference(fixed, tracked);
        SetState(0, 0.0f);
        FreeLibrary(library);

        if (fixedDifference < 0.01)
        {
            throw std::runtime_error("Fixed HRTF did not alter the signal.");
        }
        if (trackedDifference < 0.005)
        {
            throw std::runtime_error("Tracked yaw did not alter the HRTF.");
        }

        std::cout << "Spatializer smoke test passed. Fixed RMS delta="
                  << fixedDifference << ", tracked RMS delta="
                  << trackedDifference << std::endl;
        return 0;
    }
    catch (const std::exception& exception)
    {
        try
        {
            SetState(0, 0.0f);
        }
        catch (...)
        {
        }
        std::cerr << exception.what() << std::endl;
        return 1;
    }
}
