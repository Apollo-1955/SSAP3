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
| 5 | Spatial engine | **Implemented:** measured-HRIR (MIT KEMAR) convolution with crossfaded updates, configurable stage and room. Not yet heard on AirPods (reinstall pending). SOFA import not started |
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
| `4ba926e` | feat: protocol, motion and connection core; supervised control center; 115 tests |
| `b98e17d` | security(installer): manifest-driven setup, verifier, SBOM, dev tools, CI |
| `c1e151d` | docs: install/development/architecture guides, security report, status |

## Verification status

The status legend:

- **Tested (simulated):** passes automated tests without hardware.
- **Executed:** run for real on this PC.
- **Hardware-verified:** observed working with AirPods Pro 3. So far only
  the Fixed-mode soundstage (a subjective listening test) has reached this
  state.

| Area | Implemented | Tested (simulated) | Executed on this PC | Hardware-verified |
| --- | --- | --- | --- | --- |
| Managed build, 0 warnings, security analyzers | ✓ | n/a | ✓ | n/a |
| Core tests (186) | ✓ | ✓ | ✓ | — |
| Motion providers (AirPods sensors, companion, simulator) and tracking states | ✓ | ✓ (simulator end to end; companion over loopback TCP) | — (new app build not yet installed) | — (no signed driver, no companion app) |
| Pitch/roll nod calibration | ✓ | ✓ (arbitrary simulated mountings) | — | — |
| Windows motion-access investigation | ✓ | n/a | ✓ (`--investigate`: no user-mode path; see `docs/TECHNICAL_REPORT.md` §2) | n/a |
| Motion protocol (SensorDataWX protobuf, quaternion yaw, service discovery, MTU 2582) | ✓ | ✓ (synthetic packets in the documented layout; byte-exact vs 6 published commands) | — (needs the signed driver) | — |
| App launch, status rendering, `--tray` | ✓ | — | ✓ (Bluetooth off, no driver) | — |
| BLE advertisement probe | ✓ | — | ✓ (radio off, so 0 advertisements) | — |
| Spatial engine (C++) and its tests | ✓ | ✓ (CI run #4: `/W4 /WX` build, all tests pass) | ✗ (no MSVC locally) | — |
| Measured-HRIR renderer (KEMAR table, offline converter, crossfaded updates) | ✓ | design checked offline (C# port of the algorithm on the generated table); C++ build and tests run in CI | ✗ (no MSVC locally; the new DLL is not installed) | — (the installed engine is still the parametric build) |
| Driver (C) | ✓ | compiles, links, passes InfVerif, the HVCI image checks, Microsoft's driver CodeQL suites and Inf2Cat; submission CAB built (CI run #12). Not loaded or run anywhere | ✗ (no WDK locally) | — |
| Installer and uninstaller | ✓ | parse-checked; permission hardening and exact restore tested non-elevated on a scratch folder (S-23, S-27) | **Install ✓** (`Install-SSAP3Local.ps1 -EnableSpatialEngine`, 2026-11-13; rollback also exercised by a failed first attempt). Uninstall ✗ not yet run | — |
| Spatial engine deployed in the Equalizer APO host | ✓ | — | ✓ plug-in installed and scoped to the AirPods endpoint; the app writes fresh state (≈200 ms old, engine limit 750 ms) | **Fixed mode ✓:** the user heard a clear difference between Off and Fixed on AirPods Pro 3 (subjective listening test, 2026-11-13; state record confirmed `Mode=Fixed`). Tracked mode not verified (needs the signed AAP driver) |
| Package staging (`build-installer.ps1 -StageOnly`) | ✓ | — | ✓ (app published, SBOM written; engine DLL not yet present) | n/a |
| System-state verifier | ✓ | — | ✓ (`-Verify` pass) | n/a |
| CI workflow | ✓ | — | ✓ (runs on every push) | n/a |

## CI history

| Run | Commit | Result |
| --- | --- | --- |
| #1 | `414b091` | CodeQL C# and C/C++, gitleaks and script checks passed. The spatial engine compiled cleanly. Two failures: the managed test build (the runner used a newer SDK, and C# 14 binds `array.Reverse()` to the in-place span overload) and the spatial-engine tests (failure text only in the sign-in-gated log). Fixed with a `global.json` SDK pin, an explicit `Enumerable.Reverse`, test-failure annotations, and a separate driver job. |
| #2 | `fda1a94` | Managed job green. The spatializer test failed to compile (`C2589`: the `windows.h` `max` macro shadowed `std::max`). Driver failed with no detail. |
| #3 | `d56b0d3` | Spatial engine built and passed all tests. Driver: `queue.c(230)` `C4018` signed/unsigned compare against `FIELD_OFFSET`, fatal under `/WX`. |
| #4 | `f3cf186` | **All jobs green:** managed build and 115 tests, NuGet audit, spatial engine build and tests, driver compile, link and InfVerif, CodeQL C# and C/C++, gitleaks, script checks. |
| #9–#11 | `99555e1`–`dcf93eb` | New driver pre-submission jobs, fixed one by one:<br>- source scan read a build folder;<br>- the `microsoft/cpp-queries` dependency wasn't downloaded;<br>- no PDB was produced (now `/Z7` + `/DEBUG`);<br>- the CAB check misread `expand -D` output;<br>- 2 role-type warnings. |
| #12 | `7da096c` | **All jobs green**, including driver image checks, Microsoft's driver CodeQL suites (0 must-fix, 0 recommended), Inf2Cat and the attestation CAB artifact. |
| #16 | `4c4d99d` | **All jobs green** with the motion providers, tracking states, pitch/roll calibration, companion protocol and new UI (186 tests). |
| #14 | `99ed229` | **All jobs green** after the motion-protocol rewrite and driver protocol v3. Covers MTU 2582, the `0x17` allowlist and the structure-size checks. Results: 158 tests pass, Microsoft's driver CodeQL suites are clean, and the attestation CAB was rebuilt. |

## Blockers (need the maintainer)

1. **Microsoft attestation signing** for `ssap3aap.sys`. Without it the AAP
   features (head tracking, listening modes, AACP battery) cannot run on a
   Secure Boot PC, by design. The maintainer needs:
   - a registered organization;
   - an EV code-signing certificate;
   - a Windows Hardware Developer Program account.

   CI now builds the submission CAB (`ssap3-driver-attestation-cab-unsigned`)
   and gates it on driver image checks and Microsoft's must-fix driver CodeQL
   queries. Steps are in `driver/SIGNING.md`.
2. **Hardware validation:** turn Bluetooth on and run
   `SSAP3-TransportProbe --ble 30` to confirm model ID `0x2720`. AACP and
   motion tests need the signed driver, or a separate development PC
   (`tools/dev`).
3. **Code-signing certificate** for `SSAP3-Setup.exe`.

## Next steps

1. ~~Fixed-mode soundstage on this PC~~ **done 2026-11-13** (user listening
   test). Still to do: run `Uninstall-SSAP3Local.ps1` and compare with a
   `Test-Ssap3SystemState.ps1 -Snapshot`.
2. Load-test the driver on a development PC with Driver Verifier, and
   submit it for attestation signing.
3. Run the installer and uninstaller on a disposable Windows 11 VM with
   before/after snapshots.
4. ~~HRIR convolution renderer~~ **done 2026-11-13** (MIT KEMAR, compiled in).
   Still to do:
   - a listening test on AirPods after reinstalling;
   - SOFA import in the offline converter (ADR-5), with a fuzzed reader.
5. Wire the microphone VAD (non-AirPods input), and add exclusive-mode
   bypass detection.
6. Expand the app to the full multi-page window.
