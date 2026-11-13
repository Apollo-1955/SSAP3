# SSAP3 development status

- **Branch:** `development/ssap3` (from `main` at `114143c`)
- **Updated:** 2026-11-13

## Milestones

| # | Milestone | State |
| --- | --- | --- |
| 1 | Repository and architecture audit | **Done** |
| 2 | Project transformation (identity, security fixes, CI) | **Done in source.** Native code and CI not yet executed |
| 3 | Bluetooth and AAP | **Implemented and unit-tested.** Not hardware-verified (no signed driver) |
| 4 | Motion streaming and quaternion head tracking | **Implemented and unit-tested.** Not hardware-verified |
| 5 | Spatial engine | **Partial:** hardened parametric renderer with configurable stage. HRIR convolution and SOFA not started |
| 6 | Windows audio integration | **Partial:** opt-in Equalizer APO host with exact restore. Exclusive-mode detection not started |
| 7 | AirPods controls | **Partial:** confirmed listening modes; conversation switching on AirPods speech notifications. Mic VAD not wired; AACP battery parsed |
| 8 | App and tray | **Partial:** tray panel and a 5-tab settings window. Not the full 11-page app |
| 9 | Installer, updater, uninstaller | **Implemented, not executed.** No updater |
| 10 | Security and hardware validation | **Partial:** see `SECURITY_REPORT.md` §6 |
| 11 | Production release | **Blocked:** attestation signing, code-signing certificate, hardware validation |

## Commits on this branch

| Commit | Change |
| --- | --- |
| `6eb2c13` | docs: architecture, security and hardware audits |
| `8a3d870` | chore: rename project to SSAP3 (plus security analyzers, DLL search policy, `asInvoker`) |
| `e3b2074` | security(driver): bounded waits, session caps, PDO address binding, outbound allowlist |
| `a1e1fee` | security(audio): validated shared state, crossfaded mode changes |
| `8adcac9` | feat: protocol, motion and connection core; supervised control center; 115 tests |
| `983f8dc` | security(installer): manifest-driven setup, verifier, SBOM, dev tools, CI |
| next | docs: install/development/architecture guides, security report, status; `--tray` autostart |

## Verification status

The status legend:

- **Tested (simulated):** passes automated tests without hardware.
- **Executed:** run for real on this PC.
- **Hardware-verified:** observed working with AirPods Pro 3. Nothing has
  reached this state yet.

| Area | Implemented | Tested (simulated) | Executed on this PC | Hardware-verified |
| --- | --- | --- | --- | --- |
| Managed build, 0 warnings, security analyzers | ✓ | n/a | ✓ | n/a |
| Core tests (115) | ✓ | ✓ | ✓ | — |
| App launch, status rendering, `--tray` | ✓ | — | ✓ (Bluetooth off, no driver) | — |
| BLE advertisement probe | ✓ | — | ✓ (radio off, so 0 advertisements) | — |
| Spatial engine (C++) and its tests | ✓ | written, not compiled | ✗ (no MSVC) | — |
| Driver (C) | ✓ | — | ✗ (no WDK) | — |
| Installer and uninstaller | ✓ | parse-checked | ✗ (needs admin and changes the audio stack) | — |
| System-state verifier | ✓ | — | ✓ (`-Verify` pass) | n/a |
| CI workflow | ✓ | — | ✗ (Actions disabled on the fork) | n/a |

## Blockers (need the maintainer)

1. **Enable GitHub Actions** on the fork (Actions tab). This is needed for
   the first C/C++ compile, CodeQL and gitleaks results.
2. **Microsoft attestation signing** for `ssap3aap.sys`: EV certificate plus
   Partner Center. Without it the AAP features cannot run on a Secure Boot
   PC, by design.
3. **Hardware validation:** turn Bluetooth on and run
   `SSAP3-TransportProbe --ble 30` to confirm model ID `0x2720`. AACP and
   motion tests need the signed driver, or a separate development PC
   (`tools/dev`).
4. **Code-signing certificate** for `SSAP3-Setup.exe`.

## Next steps

1. Fix whatever the first CI run reports (native code has never been
   compiled).
2. Run the installer and uninstaller on a disposable Windows 11 VM with
   before/after snapshots.
3. HRIR convolution renderer and SOFA import (ADR-5), with a fuzzed loader.
4. Wire the microphone VAD (non-AirPods input), and add exclusive-mode
   bypass detection.
5. Expand the app to the full multi-page window.
