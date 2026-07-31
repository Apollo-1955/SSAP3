# Changelog

## 0.4.0 — Compatibility and diagnostics

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

## 0.3.0 — Experimental prerelease

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
