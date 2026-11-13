# SSAP3 security report

Status as of 2026-11-13, branch `development/ssap3`. This report says exactly
what was checked and what remains unverified. **SSAP3 is not claimed to be
vulnerability-free**, and no software can guarantee immunity to unknown
(zero-day) flaws.

## 1. Threat model

The full threat model (assets, trust boundaries TB1–TB8, STRIDE table) is in
`SECURITY_AUDIT.md` §1. Changes since the audit:

- **TB3 (app → DSP):** the state record is now versioned and validated field
  by field.
- **TB6 (installer):** the boundary is now admin-only; nothing elevated reads
  user-writable data.

## 2. Remediation status of audit findings

| ID | Severity | Finding | Status | Where / how verified |
| --- | --- | --- | --- | --- |
| S-01 | Critical | Self-signed certificate trusted as root CA | **Fixed** | No trust-store writes anywhere in `installer/`, enforced by a CI grep. Dev certificate is non-exportable, `CA:false`, dev-PC only (`tools/dev`). |
| S-02 | Critical | LPE via user-writable elevated scripts and state | **Fixed (not executed)** | Elevated payload in `%ProgramFiles%\SSAP3\setup`. Ownership and ACL checks before use. `%ProgramData%\SSAP3` created with protected ACLs and refused if planted. Manifest admin-only. Parse-checked; not run, because it needs elevation. |
| S-03 | Critical | Install requires disabling Secure Boot / Test Mode / BitLocker | **Fixed** | No BCD, Secure Boot or BitLocker code in production setup (CI grep). The driver is installed only from a Microsoft-signed catalog. |
| S-04 | High | NaN shared state → OOB read in `audiodg.exe` | **Fixed (compiled in CI only)** | `Ssap3ValidateState`, masked `ReadDelay`, input sanitising. Regression tests for NaN, Inf, non-unit, stale and out-of-range records in `spatializer_smoke.cpp`. **Not compiled locally (no MSVC).** |
| S-05 | High | Unbounded kernel sessions | **Fixed (compiled in CI only)** | 1 session per handle, 4 per device. Dead 512-byte buffer removed. |
| S-06 | High | Untimed BRBs and reader-stop race | **Fixed (compiled in CI only)** | Timeouts: open 10 s, close 3 s, send 2 s, queries 5 s. Dekker-style stop with post-send cancel and `KeFlushQueuedDpcs`. Zombie list instead of freeing memory a late completion can touch. **Needs Driver Verifier on hardware.** |
| S-07 | High | Silent `DisableProtectedAudioDG` and ACL changes | **Fixed (not executed)** | Explicit opt-in task with a consent page. Prior value, FX values and key SDDL recorded and restored. |
| S-08 | Medium | Caller-chosen remote address | **Fixed** | Sessions always target the PDO address; a mismatch is rejected. |
| S-09 | Medium | Arbitrary AACP relay | **Fixed** | Device SDDL is `SY/BA:GA, IU:GRGW`. Outbound allowlist: handshake; opcodes `0x09` (ids `0x06`/`0x0D`/`0x28` only), `0x0F`, `0x17`, `0x4D`. |
| S-10 | Medium | Executable search in parent folders | **Fixed** | Installed path only, plus a SHA-256 pin check before every launch. |
| S-11 | Medium | Unpinned third-party binaries | **Partly fixed** | SoundVolumeView pinned in code, build script and `THIRD_PARTY.md`. Equalizer APO runtime files are not yet hash-pinned file by file (they are not in the repository). |
| S-12 | Medium | Strangers' AirPods shown as yours | **Fixed** | `AdvertisementFilter`: paired model ID (from Windows' Device ID) plus RSSI floor. UI labels unverified advertisements as "nearby". Unit-tested. |
| S-13 | Medium | Unredacted diagnostics | **Fixed** | `DiagnosticRedactor` applied to reports and the app log (addresses, device names, profile paths). Opt-in `--include-identifiers`. Unit-tested. |
| S-14 | Medium | Unconfirmed mode changes reported as success | **Fixed** | `ListeningModeController` reports Confirmed / Rejected / Unconfirmed. Unit-tested. |
| S-15 | Low | Name-based process killing | **Fixed** | Stops only processes whose image is under `%ProgramFiles%\SSAP3`. |
| S-16 | Low | HKLM Run and elevated desktop shortcut | **Changed** | Autostart is an explicit Setup task, recorded and removed on uninstall. Desktop shortcut off by default. |
| S-17 | Low | Substring-matched forced driver deletion | **Fixed** | Matched by `pnputil` original name **and** provider; `/force` dropped. |
| S-18 | Low | Unbounded log and settings | **Fixed** | Log rotates at 1 MiB and is redacted. Settings capped at 64 KiB and validated. |
| S-19 | Low | Wall-clock IPC timestamps | **Fixed** | `GetTickCount64` on both sides. |
| S-20 | Low | CI hardening gaps | **Fixed (not yet run)** | Actions pinned by SHA. Least-privilege permissions. CodeQL (C#, C/C++), gitleaks, dependency review, NuGet audit, script checks. **Actions are disabled on the fork until the owner enables them.** |
| S-21 | Info | Unsigned artifacts | **Open** | Needs an OV/EV code-signing certificate. |
| S-22 | Medium | INF set device security in the software key, where PnP ignores it | **Fixed (compiled in CI only)** | `Security`, `DeviceCharacteristics` (`FILE_DEVICE_SECURE_OPEN`) and `DeviceType` moved to the `.HW` section. Runtime effect needs verification on hardware. |

No **critical or high** finding is open in source. Several fixes are **not
yet compiled or executed** (see §6). Until they are, they are not
release-verified, and a production release stays blocked.

## 3. Attack-surface inventory

| Surface | Exposure | Controls |
| --- | --- | --- |
| AACP packets from the AirPods (≤ 512 B) | Kernel queues them without interpreting them; user mode parses them | Strict total parsers, randomized tests, bounded queue (oldest dropped) |
| BLE advertisements from any nearby device | User-mode parser | 255-byte cap, total parser, model and RSSI filter |
| Driver IOCTL interface | Interactive users only | 5 IOCTLs, size and ownership checks, session caps, allowlist, timeouts |
| Shared state file | Interactive users write; `audiodg` reads | Protected ACL (no create or delete); DSP validation; seqlock |
| Settings JSON (per user) | Same user | 64 KiB cap, typed deserialisation, range validation |
| Installer and uninstaller | Administrators | Admin-only paths, ownership checks, manifest, rollback |
| SoundVolumeView child process | App launches it | Installed path plus SHA-256 pin |
| Network | **None** | No HTTP, socket or DNS APIs in the app, driver or DSP (source search) |

## 4. Privilege inventory

| Code | Runs as |
| --- | --- |
| `SSAP3.exe` and diagnostics | Signed-in standard user (`asInvoker` manifest) |
| `ssap3aap.sys` | Kernel (only when Microsoft-signed) |
| `SSAP3Spatializer.dll` | Inside `audiodg.exe` (LOCAL SERVICE), opt-in |
| `Install-SSAP3.ps1` / `Uninstall-SSAP3.ps1` | Administrator, once per install or uninstall |

SSAP3 installs no services and no scheduled tasks, and nothing runs as SYSTEM
after setup.

## 5. Network, privacy and dependency inventory

- **Network:** none at runtime. The build and CI download from nuget.org and
  GitHub only. There is no update checker yet. When one is added it must be
  opt-out, HTTPS-only, and send no device data.
- **Privacy:** no telemetry. Audio, microphone, motion data and Bluetooth
  identifiers stay local. Conversation switching uses the AirPods' own
  speaking notifications and records nothing.
- **Shipped dependencies** (from the SBOM generated by
  `tools/release/New-Sbom.ps1`):
  - `Microsoft.Windows.SDK.NET.Ref` 10.0.19041.56 (Microsoft projection);
  - the .NET 8 runtime (self-contained publish);
  - SoundVolumeView 2.53 (pinned);
  - Equalizer APO 1.4.2 (optional, GPL-2.0-or-later, source included).
  - **No third-party NuGet packages ship in the app.**
- **Test-only dependencies:** xunit 2.9.2, xunit.runner.visualstudio 2.8.2,
  Microsoft.NET.Test.Sdk 17.11.1.
- **Vulnerability scan:** `dotnet list package --vulnerable
  --include-transitive` on 2026-11-13 found **no vulnerable packages** in any
  of the 6 projects.

## 6. Checks performed (2026-11-13, development PC)

| Check | Result |
| --- | --- |
| .NET analyzers, `AnalysisModeSecurity=All`, warnings as errors | **Pass.** 0 warnings in all managed projects, after fixing the CA5392 (DLL search path) findings on every P/Invoke with an assembly-level System32-only policy, and the 2 CA5358 findings (single-block AES, justified and centralised). |
| Unit and simulated tests (xUnit) | **115/115 pass** |
| Randomized-input tests | 3 × 200 000 parser inputs plus 50 000 hostile estimator samples, 0 failures |
| Bluetooth Core Spec `ah` test vector | Pass |
| PowerShell parse check (14 scripts) | 0 errors |
| Forbidden-operation scan of production setup | 0 hits |
| `Test-Ssap3SystemState.ps1 -Verify` (read-only) | Pass. Baseline: Secure Boot on, Memory Integrity on, Defender real-time on, `DisableProtectedAudioDG` absent |
| Secret scan (`git grep` for key headers and tokens) | 0 secrets |
| App launch on real Windows 11 26200 | Starts; honest states shown (Bluetooth off, AAP driver missing, engine not installed). A TimeSpan overflow crash found by this run was **fixed**. |
| **Not performed** | C/C++ compilation (no MSVC/WDK locally); CodeQL; gitleaks in CI; Driver Verifier; Application Verifier; ASan/UBSan; coverage-guided fuzzing; installer and uninstaller execution; any AirPods hardware test |

## 7. Driver security review (source)

- METHOD_BUFFERED only. Input and output aliasing is handled by copying
  fields before writing output. Every structure is size-checked before use.
- No arbitrary memory access, no executable allocations (NonPagedPoolNx,
  `ExInitializeDriverRuntime(DrvRtPoolNxOptIn)`), no callbacks into user
  mode.
- Session ownership is checked per file object, and cleanup closes every
  session the handle owns.
- Device removal: sessions are closed before the profile interface is
  dereferenced. Sessions whose reader cannot be confirmed stopped are parked
  and freed at device cleanup, never freed early.
- **Open items:**
  - Driver Verifier and HVCI-compatibility testing on hardware (this PC has
    Memory Integrity on, so a signed driver must be HVCI-compatible);
  - running Microsoft's Windows driver CodeQL queries;
  - attestation signing.

## 8. Installer and uninstaller review (source)

- Reviewed against S-01/S-02/S-03/S-07/S-15/S-16/S-17. Every change is
  recorded before it is made, rollback runs on failure, and uninstall is
  idempotent and keeps user data unless a complete uninstall is chosen.
- **Open items:**
  - execute both on a disposable VM with before/after snapshots;
  - hash-pin the Equalizer APO runtime files;
  - Authenticode-sign `SSAP3-Setup.exe`.

## 9. Remaining limitations

- The AAP features need a Microsoft-signed driver, which does not exist yet.
- The head-tracking decoder is unverified on AirPods Pro 3.
- The renderer is parametric. HRTF/SOFA support is not implemented yet.
- The microphone VAD exists in Core but is not wired to an audio input.
- Exclusive-mode and ASIO audio bypass the engine.
- The opt-in engine requires `DisableProtectedAudioDG`, which weakens a
  system-wide audio protection while it is installed.
