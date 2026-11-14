# Changelog

## SSAP3 0.5.0-dev (unreleased, branch `development/ssap3`)

- **Driver signing readiness** (`driver/SIGNING.md`,
  `docs/DRIVER_TEST_REPORT.md`). Microsoft's signature itself still needs
  the maintainer's organization, EV certificate and Partner Center account.
  - **Stricter signature rule** in packaging and Setup:
    - the root is pinned to Microsoft;
    - the driver-publisher EKU is required;
    - the `.sys` and `.inf` must be exactly the signed files;
    - the INF must be SSAP3's own.

    Tested on real WHQL and attestation catalogs; CI tests unsigned,
    tampered and spoofed-publisher packages.
  - `installer\Test-Ssap3Driver.ps1`: a read-only driver health check. It
    reports signature, system security, install, device state, control
    interface and head-tracking freshness, each level separately.
  - `driver\Sign-AttestationCab.ps1`: EV signing of the submission CAB.
  - Setup and uninstall now say when Windows needs a restart to finish a
    driver change.
  - **Driver protocol v4**, after an independent review with no critical
    or high findings:
    - access-checked IOCTLs;
    - an exact outbound packet allowlist;
    - capped parked sessions;
    - close requests that reach the Bluetooth stack during device stop;
    - `FILE_DEVICE_SECURE_OPEN`;
    - read-buffer and CLOSE fixes;
    - IRQL annotations.

- **Spatial engine 2.7.0: timbre correction and a renderer choice**, after a
  listening test of 2.6.0. On AirPods Pro 3, the measured renderer sounded
  thin, harsh or muffled, and spatially wrong. Measurement confirmed it:
  - the ±30° stage was 6–9 dB down below 100 Hz, a measurement artifact of
    128-tap HRIRs;
  - it was 3–10 dB off around 1.3, 4 and 8 kHz.

  Changes:
  - A fitted 6-biquad stage EQ now flattens the stage to within about ±1 dB
    (the 8 kHz notch is filled only partly). It applies to both ears alike,
    so the ITD is unchanged.
  - The trim is now 0.35, which keeps the bass headroom of the classic
    renderer.
  - **Settings → Soundstage → Renderer**: Measured or Classic (the earlier
    parametric model), switchable without clicks.
  - The choice uses the shared record's formerly reserved field. Older
    writers' 0 means Measured; any other value is rejected.
  - New tests cover tonal balance, the classic renderer, invalid renderer
    values and renderer-switch clicks.
- **Measured-HRTF soundstage** (`docs/TECHNICAL_REPORT.md` §5). The spatial
  engine (version 2.6.0) convolves each virtual speaker with measured
  head-related impulse responses (MIT KEMAR, credited in `THIRD_PARTY.md`)
  instead of the parametric model.
  - Directions are interpolated between measurements.
  - The ITD is exact to a fraction of a sample at any device rate.
  - Filter updates crossfade, so turning the head never clicks.
  - Loudness and headroom match the previous renderer.
  - The table is generated offline (`tools/hrtf`) and compiled in. The engine
    loads no files.
  - New C++ tests cover:
    - ITD/ILD at known directions, left/right mirroring, front/back and
      elevation differences;
    - consistency from 8 to 384 kHz;
    - loudness;
    - clicks while the head sweeps or the stage moves;
    - bit-exact Off after On;
    - real-time headroom.
- **Motion providers** (`docs/TECHNICAL_REPORT.md`). Tracked mode now runs on
  a provider interface (`IMotionSource`) with three implementations:
  - AirPods sensors over the AAP driver;
  - an authenticated, encrypted **companion** device
    (`docs/COMPANION_PROTOCOL.md`);
  - a clearly labelled **simulator** for development.

  Providers can be switched in Settings without restarting the audio engine.
- **Tracking states.** Tracked mode always shows an explicit state: Off,
  Fixed, Unavailable (with the reason), Starting, Live, Degraded, Lost or
  Recovering. It shows whether measurements are real or simulated, and it
  never silently behaves like Fixed.
- **Pitch and roll** from a guided nod calibration, saved per fit and
  rejected if the fit changes.
- **Diagnostics** show:
  - provider, real vs simulated, and state;
  - last measurement age, rate and transport delay;
  - calibration;
  - errors.
- `SSAP3-TransportProbe --investigate`: a read-only check of every Windows
  interface for AirPods motion access. `--companion-sim`: a simulated
  reference companion.
- `driver/SIGNING.md` now has a full signing checklist.

- **Head tracking decoder rebuilt from the documented motion protocol**
  (`HARDWARE_COMPATIBILITY.md`, *Motion protocol*):
  - motion packets are parsed as RTBuddy SensorDataWX protobuf. The old fixed
    offsets misread every 80-byte packet;
  - yaw comes from the fused attitude quaternion (Q15) as its twist about the
    gravity-aligned axis, so it works however the bud sits in the ear. The
    old heuristic is gone;
  - the AirPods' sensor-service announcements are used to find a dynamically
    numbered `devmotion6` service. Otherwise the app tries 16, then 14, and
    locks onto the one that streams;
  - unexplained attitude jumps re-anchor instead of snapping (for example a
    bud switch, detected with the rotation-rate field);
  - `SSAP3-TransportProbe <address> Motion` prints the decoded stream for
    first-run hardware checks.
- **Driver protocol v3:**
  - inbound L2CAP MTU raised to 2582 (iOS's value) so the AirPods announce
    the motion service; the queue stays bounded;
  - opcode `0x17` from user mode is limited to the documented sensor
    start/stop command (S-29);
  - compile-time structure-size checks.

- **Driver signing preparation:**
  - CI builds the Partner Center attestation CAB
    (`ssap3-driver-attestation-cab-unsigned`), with `.sys`, `.inf`, `.pdb`
    and an Inf2Cat catalog in one `ssap3aap\` folder.
  - CI runs `driver/Test-DriverImage.ps1`, which checks the HVCI-relevant PE
    properties and that only non-executable pool is used.
  - CI runs Microsoft's Windows driver CodeQL queries (WHCP 26H1 pack
    1.10.0) and fails on any must-fix result.
  - `driver/SIGNING.md` walks the maintainer through the EV certificate,
    Hardware Developer Program registration, signing, submission and first
    install.
- **Fixed-mode soundstage verified** on the development PC with AirPods Pro
  3. Installed with `Install-SSAP3Local.ps1`, now with a transcript at
  `artifacts\install-local.log`. Two installer bugs found by that first run
  are fixed: `$PSScriptRoot` in parameter defaults, and the setup log writing
  into function return values.

- **Spatial engine on an existing Equalizer APO:** the engine task can now
  use an Equalizer APO 1.4.2 that you installed with its official installer,
  instead of a bundled copy. It hardens that install:
  - the config folder is made admin-only for writing (S-23);
  - the update-checker task is removed (S-24);
  - Equalizer APO's load paths must be admin-only (S-26);
  - Device Selector attachments are kept (S-25);
  - the plug-in is scoped to the AirPods endpoints by GUID (S-28).

  Every change is recorded and restored on uninstall.
- **Fixed** Setup's admin-only check (S-27). It counted read-only ACEs as
  write access, failed on capability SIDs, and could not read registry ACLs
  in Windows PowerShell 5.1. Each of these would have stopped every install.
- Added `installer/Install-SSAP3Local.ps1` and `Uninstall-SSAP3Local.ps1` to
  install a staged package without Inno Setup, plus `build-installer.ps1
  -StageOnly`.
- Added `tools/dev/Get-SpatialEngineArtifact.ps1`. It accepts a CI-built
  engine DLL only if its zip matches the digest GitHub recorded, and checks
  that the DLL is a PE32+ x64 DLL.
- `build-installer.ps1` now finds a per-user .NET SDK when the machine-wide
  `dotnet.exe` has no SDK.

- Renamed the product to **SSAP3** (Spatial Sound for AirPods Pro 3). This
  covers assemblies, namespaces, executables, driver, DSP, installer and
  brand mark. Upstream attribution is kept in `THIRD_PARTY.md`.
- Added the repository audit: `ARCHITECTURE_AUDIT.md`, `SECURITY_AUDIT.md`,
  `HARDWARE_COMPATIBILITY.md`, `IMPLEMENTATION_ROADMAP.md` and
  `DEVELOPMENT_STATUS.md`.
- Enabled the .NET security analyzers (`AnalysisModeSecurity=All`) as build
  errors. P/Invokes now load native DLLs from System32 only, and AES usage
  is confined to a validated single-block helper.
- The app manifest now declares `asInvoker`, so the app never runs elevated.
- **Security:** fixed all critical and high findings from `SECURITY_AUDIT.md`
  in source:
  - root-CA trust, the ProgramData LPE and the Test Mode installer are gone;
  - the DSP out-of-bounds read is fixed;
  - the driver has session caps, bounded waits, a race-free reader stop, PDO
    address binding and an outbound allowlist, and its INF device security
    is now in the `.HW` section;
  - the system-wide engine's security change is opt-in and restored exactly.

  `SECURITY_REPORT.md` tracks the status of each finding.
- **Protocol:** strict AACP framing. Listening-mode changes are reported only
  when the AirPods confirm them. Conversation-awareness notifications are
  parsed. AirPods Pro 3 IDs are added: Bluetooth product ID `0x2027` was
  observed; the advertisement model ID `0x2720` is inferred.
- **Motion:** quaternion head-pose estimator with recenter, timestamped
  One-Euro SLERP smoothing, glitch rejection, hold/lost/resume states and
  drift correction. Kernel receive timestamps; measured driver-to-app
  latency.
- **Spatial engine:** v2 validated state record, per-sample parameter
  interpolation, crossfaded mode changes, configurable speaker angle,
  distance, height, room and intensity.
- **App:** a supervised motion service with bounded reconnect backoff,
  sleep/resume handling and explicit "Bluetooth off" / "audio only" /
  "motion stalled" states. Adds Recenter, a live top-down soundstage view,
  a settings window, SSAP3's own conversation switching, `--tray` autostart
  and redacted diagnostics.
- **Installer:** manifest-driven install with rollback, idempotent standard
  and complete uninstall, and a before/after system-state verifier. The
  driver is installed only when Microsoft-signed.
- **Engineering:** 115 xUnit tests including randomized-input tests, a
  CycloneDX SBOM generator, and CI for builds, tests, native code, CodeQL,
  gitleaks, dependency review and script checks, with SHA-pinned actions.

## SoundStage 0.4.0 (upstream) — Compatibility and diagnostics

- Fixed a boot-state bug that could leave Test Mode active for one session but
  disabled in BCD for the next restart, causing driver problem code 52.
- Added persistent Test Mode verification and repair to both setup phases.
- Added the Apple UARP service as a fallback driver PDO when Windows does not
  recreate the AAP PDO after an AirPods reconnect.
- Added automatic and manual AAP/UARP service refresh through the documented
  Windows Bluetooth service API.
- Added periodic classic connection-state reconciliation without treating a
  randomized BLE advertisement address as the paired control address.
- Added robust BLE watcher restart and passive-scan fallback for vendor
  adapters that reject active scanning.
- Added **Repair** and **Report** actions to the tray panel.
- Added a one-click diagnostic ZIP containing Windows, Bluetooth, audio,
  Code Integrity, BCD, PnP, service, installer, and application state.
- Added explicit diagnostics for Windows 10, Windows 11 Insider builds,
  TP-Link/BARROT adapters, Apple AAP/UARP presence, and the A2DP/HFP route.
- Kept the v0.3 spatial renderer and head-tracking tuning unchanged.

## SoundStage 0.3.0 (upstream) — Experimental prerelease

- Added a compact English WPF tray control center.
- Added ANC, Transparency, Adaptive, and Off AAP controls.
- Added generic Apple AAP profile matching for known AirPods product IDs.
- Added live left/right/case battery and charging telemetry.
- Added a system-wide Fixed binaural room renderer.
- Added calibrated and adaptively smoothed AirPods head tracking.
- Added reversible Equalizer APO endpoint-effect chaining.
- Added two-stage test-driver install with BCD backup and BitLocker handling.
- Added reversible driver, certificate, audio-effect, and Test Mode uninstall.
- Added a single `SoundStage-Setup.exe` with Apps & Features integration.
- Added automatic migration from pre-SoundStage development builds.
- Added single-instance and responsive tray behavior.
- Added the SoundStage AirPods-in-case visual identity.
