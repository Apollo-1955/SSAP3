# Developing SSAP3

## Toolchain

| Component | Needs |
| --- | --- |
| Managed app and tests | .NET 8 SDK (user-scope install is fine) |
| Spatial engine (`audio/`) | Visual Studio 2022+ Build Tools with MSVC x64 and the Windows 10/11 SDK |
| Driver (`driver/`) | MSVC, plus the pinned WDK NuGet packages (`dotnet restore driver\WdkPackages.proj`) |
| Installer | Inno Setup 6 |

Set `DOTNET_CLI_TELEMETRY_OPTOUT=1` to turn off .NET CLI telemetry; CI does
this.

## Build and test

```powershell
dotnet build .\src\SSAP3.App\SSAP3.App.csproj -c Release
dotnet test  .\tests\SSAP3.Core.Tests\SSAP3.Core.Tests.csproj -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File .\audio\build-spatializer.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\audio\test-spatializer.ps1
dotnet restore .\driver\WdkPackages.proj
powershell -NoProfile -ExecutionPolicy Bypass -File .\driver\build-driver-msvc.ps1
```

Build rules:

- Managed warnings, including the full .NET **security** analyzer set
  (`AnalysisModeSecurity=All`), are build errors.
- C/C++ builds with `/W4 /WX /guard:cf`.
- The driver links with `/guard:cf`, `/dynamicbase` and `/nxcompat`, and the
  INF must pass InfVerif.

## What the tests cover

`tests/SSAP3.Core.Tests` (xUnit):

- AACP packet bytes, checked against LibrePods' documentation;
- strict parsers and listening-mode acknowledgement;
- quaternion math and the head-pose estimator: calibration, jitter, glitch,
  hold/lost/resume, recenter and drift;
- the connection supervisor: backoff, single-flight, sleep/resume, missing
  driver;
- conversation switching hysteresis and the voice-activity detector;
- the shared-state binary layout and the redactor;
- seeded randomized-input robustness tests (see [FUZZING.md](FUZZING.md)).

`audio/SSAP3Spatializer/spatializer_smoke.cpp`:

- bit-exact Off;
- Fixed and Tracked produce audible differences;
- NaN, Inf and non-unit poses, stale poses and invalid stage parameters are
  rejected;
- non-finite input samples are sanitised;
- a click test for mode switches.

None of these tests use AirPods hardware. Hardware validation is a separate,
manual procedure in `HARDWARE_COMPATIBILITY.md`.

## Hardware tools

```powershell
# Bluetooth LE advertisements (driverless, output redacted):
SSAP3-TransportProbe --ble 30
# AACP transport (needs the AAP driver):
SSAP3-TransportProbe <address>
SSAP3-TransportProbe <address> Transparency
SSAP3-TransportProbe <address> Motion
```

## Driver development

Production packages carry only Microsoft-signed drivers (attestation or
WHQL). `driver\package-driver.ps1 -AttestationCab` builds the submission CAB.
Testing an unsigned driver on real hardware is a deliberate development-PC
decision, documented in [tools/dev/README.md](../tools/dev/README.md).

## Coding rules

- Treat every Bluetooth packet, BLE advertisement, settings file and shared
  state record as untrusted: bound lengths, validate ranges, never throw on
  malformed input.
- No protocol parsing in kernel mode.
- The DSP render callback must not allocate, lock, or do I/O.
- No network access, telemetry or analytics. Ever.
- Elevated code never reads user-writable files.
- Every installer change is recorded in the manifest before it is made, and
  has an exact undo.
- Never claim hardware behaviour that was not observed. Label inferred or
  unverified facts as such.
