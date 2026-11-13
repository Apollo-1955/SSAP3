# Changelog

## SSAP3 0.5.0-dev (unreleased, branch `development/ssap3`)

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
