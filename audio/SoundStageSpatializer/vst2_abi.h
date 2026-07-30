/*
 * Minimal VST 2 ABI declarations required by the SoundStage spatializer.
 *
 * The layout and constants are compatible with the VeSTige header used by
 * Equalizer APO. VeSTige is distributed under GPL-2.0-or-later; SoundStage is
 * distributed under GPL-3.0-or-later.
 */
#pragma once

#include <cstdint>

struct AEffect;

using AudioMasterCallback = std::intptr_t (*)(
    AEffect*,
    std::int32_t,
    std::int32_t,
    std::intptr_t,
    void*,
    float);

using DispatcherProc = std::intptr_t (*)(
    AEffect*,
    std::int32_t,
    std::int32_t,
    std::intptr_t,
    void*,
    float);
using ProcessProc = void (*)(AEffect*, float**, float**, std::int32_t);
using SetParameterProc = void (*)(AEffect*, std::int32_t, float);
using GetParameterProc = float (*)(AEffect*, std::int32_t);
using ProcessDoubleProc = void (*)(AEffect*, double**, double**, std::int32_t);

struct AEffect
{
    std::int32_t magic;
    DispatcherProc dispatcher;
    ProcessProc process;
    SetParameterProc setParameter;
    GetParameterProc getParameter;
    std::int32_t numPrograms;
    std::int32_t numParams;
    std::int32_t numInputs;
    std::int32_t numOutputs;
    std::int32_t flags;
    void* reserved1;
    void* reserved2;
    std::int32_t initialDelay;
    std::int32_t realQualities;
    std::int32_t offQualities;
    float ioRatio;
    void* object;
    void* user;
    std::int32_t uniqueId;
    std::int32_t version;
    ProcessProc processReplacing;
    ProcessDoubleProc processDoubleReplacing;
    char future[56];
};

constexpr std::int32_t MakeFourCC(char a, char b, char c, char d)
{
    return (static_cast<std::int32_t>(a) << 24) |
        (static_cast<std::int32_t>(b) << 16) |
        (static_cast<std::int32_t>(c) << 8) |
        static_cast<std::int32_t>(d);
}

constexpr std::int32_t kEffectMagic = MakeFourCC('V', 's', 't', 'P');
constexpr std::int32_t kSoundStageUniqueId = MakeFourCC('S', 'S', 'H', 'T');
constexpr std::int32_t kEffectCanReplacing = 1 << 4;

constexpr std::int32_t effOpen = 0;
constexpr std::int32_t effClose = 1;
constexpr std::int32_t effSetSampleRate = 10;
constexpr std::int32_t effSetBlockSize = 11;
constexpr std::int32_t effMainsChanged = 12;
constexpr std::int32_t effGetEffectName = 45;
constexpr std::int32_t effGetVendorString = 47;
constexpr std::int32_t effGetProductString = 48;
constexpr std::int32_t effGetVendorVersion = 49;
constexpr std::int32_t effCanDo = 51;
constexpr std::int32_t effGetVstVersion = 58;
constexpr std::int32_t effStartProcess = 71;
constexpr std::int32_t effStopProcess = 72;
