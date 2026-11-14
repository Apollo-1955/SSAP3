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
| S-04 | High | NaN shared state → OOB read in `audiodg.exe` | **Fixed (compiled in CI, run #4)** | `Ssap3ValidateState`, masked `ReadDelay`, input sanitising. Regression tests for NaN, Inf, non-unit, stale and out-of-range records in `spatializer_smoke.cpp` **pass in CI** (run #4). |
| S-05 | High | Unbounded kernel sessions | **Fixed (compiled in CI, run #4)** | 1 session per handle, 4 per device. Dead 512-byte buffer removed. |
| S-06 | High | Untimed BRBs and reader-stop race | **Fixed (compiled in CI, run #4)** | Timeouts: open 10 s, close 3 s, send 2 s, queries 5 s. Dekker-style stop with post-send cancel and `KeFlushQueuedDpcs`. Zombie list instead of freeing memory a late completion can touch. **Needs Driver Verifier on hardware.** |
| S-07 | High | Silent `DisableProtectedAudioDG` and ACL changes | **Fixed (not executed)** | Explicit opt-in task with a consent page. Prior value, FX values and key SDDL recorded and restored. |
| S-08 | Medium | Caller-chosen remote address | **Fixed** | Sessions always target the PDO address; a mismatch is rejected. |
| S-09 | Medium | Arbitrary AACP relay | **Fixed** | Device SDDL is `SY/BA:GA, IU:GRGW`. Outbound allowlist: handshake; opcodes `0x09` (ids `0x06`/`0x0D`/`0x28` only), `0x0F`, `0x17`, `0x4D`. |
| S-10 | Medium | Executable search in parent folders | **Fixed** | Installed path only, plus a SHA-256 pin check before every launch. |
| S-11 | Medium | Unpinned third-party binaries | **Partly fixed** | SoundVolumeView pinned in code, build script and `THIRD_PARTY.md`. The official Equalizer APO 1.4.2 x64 installer is pinned by SHA-256 (`THIRD_PARTY.md`). CI-built engine DLLs are accepted only if the zip matches the digest GitHub recorded at upload (`tools/dev/Get-SpatialEngineArtifact.ps1`). The installed Equalizer APO runtime files are not pinned file by file. |
| S-12 | Medium | Strangers' AirPods shown as yours | **Fixed** | `AdvertisementFilter`: paired model ID (from Windows' Device ID) plus RSSI floor. UI labels unverified advertisements as "nearby". Unit-tested. |
| S-13 | Medium | Unredacted diagnostics | **Fixed** | `DiagnosticRedactor` applied to reports and the app log (addresses, device names, profile paths). Opt-in `--include-identifiers`. Unit-tested. |
| S-14 | Medium | Unconfirmed mode changes reported as success | **Fixed** | `ListeningModeController` reports Confirmed / Rejected / Unconfirmed. Unit-tested. |
| S-15 | Low | Name-based process killing | **Fixed** | Stops only processes whose image is under `%ProgramFiles%\SSAP3`. |
| S-16 | Low | HKLM Run and elevated desktop shortcut | **Changed** | Autostart is an explicit Setup task, recorded and removed on uninstall. Desktop shortcut off by default. |
| S-17 | Low | Substring-matched forced driver deletion | **Fixed** | Matched by `pnputil` original name **and** provider; `/force` dropped. |
| S-18 | Low | Unbounded log and settings | **Fixed** | Log rotates at 1 MiB and is redacted. Settings capped at 64 KiB and validated. |
| S-19 | Low | Wall-clock IPC timestamps | **Fixed** | `GetTickCount64` on both sides. |
| S-20 | Low | CI hardening gaps | **Fixed** | Actions pinned by SHA. Least-privilege permissions. CodeQL (C#, C/C++), gitleaks, dependency review, NuGet audit, script checks. First run (#1, `414b091`): CodeQL C# and C/C++, gitleaks and script checks passed. |
| S-21 | Info | Unsigned artifacts | **Open** | Needs an OV/EV code-signing certificate. |
| S-22 | Medium | INF set device security in the software key, where PnP ignores it | **Fixed (compiled in CI, run #4)** | `Security`, `DeviceCharacteristics` (`FILE_DEVICE_SECURE_OPEN`) and `DeviceType` moved to the `.HW` section. Runtime effect needs verification on hardware. |
| S-23 | High | Equalizer APO's own installer grants `BUILTIN\Users` full control of its `config` folder (`Setup.nsi`: `AccessControl::GrantOnFile "$INSTDIR\config" "(S-1-5-32-545)" "FullAccess"`). `config.txt` accepts absolute plug-in paths (`VSTPluginFilterFactory.cpp`), so any standard user could load a DLL into `audiodg.exe` (LOCAL SERVICE). | **Mitigated by SSAP3 Setup (executed on the development PC; see §6)** | The engine task removes write access for everyone except SYSTEM, Administrators and TrustedInstaller on the config folder and every file in it, keeps read access, refuses links and junctions, and records each security descriptor for exact restore. Tested non-elevated on a scratch copy of the same ACL: write removed from all 4 objects, read kept, all 4 descriptors restored byte-for-byte. Upstream issue, not yet reported to Equalizer APO. |
| S-24 | Low | Equalizer APO's optional update checker runs at every sign-in, contacts `equalizerapo.sourceforge.io`, and opens a URL taken from the server's reply (`UpdateChecker.cpp`) | **Fixed (not executed)** | The engine task exports and removes the `EqualizerAPOUpdateChecker` scheduled task if present, and restores it from the exported XML on uninstall. The verifier reports it. |
| S-25 | Medium | Setup would overwrite Equalizer APO's saved original effects (`Child APOs`) for an endpoint already attached with Device Selector, and uninstall would then delete that record | **Fixed** | Endpoints that already use Equalizer APO are left untouched. |
| S-26 | High | DLL planting: `audiodg.exe` loads every DLL in the Equalizer APO folder; a custom install folder outside Program Files may be user-writable | **Fixed (not executed)** | Setup refuses to continue unless the Equalizer APO folder, each DLL in it, `VSTPlugins`, `HKLM\SOFTWARE\EqualizerAPO` and its `Child APOs` key are admin-only, and `ConfigPath` is inside the install folder. |
| S-27 | Medium | Setup's own admin-only check was wrong in two ways: composite rights (`Modify`, `FullControl`, `WriteKey`) include read bits, so read-only ACEs counted as write access; and translating ACE names to SIDs throws for capability SIDs (ALL APPLICATION PACKAGES) | **Fixed** | The check now reads SIDs directly and uses modify-only bits (plus unmapped `GENERIC_WRITE`/`GENERIC_ALL`). Tested: `Program Files`, `dotnet.exe` and an HKLM key pass; the user profile is refused. Before the fix, every Program Files path would have been refused, so Setup could never have completed. Registry ACLs are read through the key object because `Get-Acl -LiteralPath` fails on registry paths in Windows PowerShell 5.1. |
| S-28 | Low | The engine's `Device: AirPods` filter matched any device whose name contains "AirPods", such as a second pair | **Fixed** | Scoped to the endpoint GUIDs that Setup attached. |
| S-29 | Medium | The driver forwarded **any** opcode-`0x17` payload from user mode, so a local process with access to the device could send arbitrary RTBuddy messages to the AirPods | **Fixed (compiled, CodeQL-clean in CI run #14)** | `Ssap3IsAllowedSensorSettingPacket`: only the documented sensor ServiceSetting command is allowed. Checks: exact body length, a 1–2-byte request id, service 1–127, mode 2, and interval 0 or 5 ms–1 s. A C# mirror of the rule is unit-tested against every command the app can emit. |
| S-30 | Info | Inbound L2CAP MTU raised from 512 to 2582 bytes (iOS's value) so the AirPods announce their motion service | **Bounded** | Inbound and outbound limits are separate constants (outbound stays 512). Queue depth went from 32 to 16 (about 41 KB of `NonPagedPoolNx` per session, at most 4 sessions per device). Every copy is length-checked. Protocol bumped to v3. Compile-time size checks keep the C structures and the C# client in step. |
| S-32 | Medium | New network surface: the optional companion motion connection (`docs/COMPANION_PROTOCOL.md`) | **Mitigated by design (loopback-tested)** | Off unless the user selects Companion and pairs. Controls:<br>- outbound only (no listening socket, no firewall rule);<br>- only loopback, private, link-local, CGNAT/Tailscale and IPv6 ULA/link-local addresses;<br>- mutual HMAC-SHA256 key proof over fresh nonces (constant-time);<br>- AES-256-GCM records with an HKDF session key and strictly increasing counters;<br>- fixed-size validated records, at most 400 per second;<br>- pairing key in DPAPI (current user, app entropy), never in settings or logs;<br>- simulated senders labelled.<br>Tested: tamper on every byte, replay, wrong key, public-address refusal, and loopback sessions. |
| S-31 | Medium | The motion parser read fixed offsets inside a protobuf message, so it misread 80-byte packets | **Fixed** | `ProtobufReader` and `SensorDataWx.TryParse` are total and allocation-free. They reject overlong varints, unsupported wire types, field number 0, lengths past the end and service ids above 65535. Unit tests cover each case, and 200 000 randomized inputs run through the protobuf reader and the sensor parsers. |
| S-34 | Medium | Setup accepted a driver catalog whose signature was valid and whose signer *name* was Microsoft's hardware publisher. It did not pin the root, check the driver-publisher EKU, check that the `.sys`/`.inf` were the signed files, or check that the package was SSAP3's at all, so a different Microsoft-signed driver renamed `ssap3aap.*` would have been installed | **Fixed (tested on real catalogs; CI run #29)** | `Get-Ssap3CatalogSignature`:<br>- pins the chain (built with the catalog's own certificates) to three Microsoft roots;<br>- requires the Windows Hardware Driver Verification EKU (attestation adds `.5.1`);<br>- `Test-Ssap3CatalogMember` checks the Authenticode image hash of the `.sys` and the hash of the `.inf`;<br>- `Test-Ssap3DriverInfIdentity` requires SSAP3's provider, service and Apple AAP/UARP hardware IDs only.<br>Used by packaging and by Setup.<br>Tested on 85 real driver catalogs on the development PC: 79 WHQL and 1 attestation accepted, 5 third-party-signed refused, 67/67 image hashes matched, a one-bit code change detected, and a renamed foreign attestation-signed package refused by the INF identity check. CI adds a self-signed certificate named like Microsoft's publisher, which is refused (`driver/Test-SigningPipeline.ps1`). |
| S-35 | Medium | Independent review of the driver before signing (no critical or high findings) | **Fixed in source (built, image-checked and CodeQL-clean in CI run #29; not yet run on hardware)** | Changes:<br>- close-channel BRBs are sent with `WDF_REQUEST_SEND_OPTION_IGNORE_TARGET_STATE`, so closes from ReleaseHardware or cleanup reach the Bluetooth stack instead of timing out in a stopped target;<br>- `FILE_DEVICE_SECURE_OPEN` is set explicitly;<br>- **protocol v4**: IOCTLs require `FILE_READ_ACCESS` (status, read) or `FILE_WRITE_ACCESS` (open, close, send), so a read-only handle can no longer drive the device (codes checked at compile time);<br>- parked sessions are counted and capped at 4 (completes S-05);<br>- CLOSE reports success once the session is destroyed;<br>- READ requires the full 2582-byte buffer (no head-of-queue stall);<br>- outbound packets are pinned to the exact handshake, feature-flag, notification and control-command packets SSAP3 sends (completes S-09; a C# mirror is tested against every packet the app emits);<br>- size checks for the open/close structures, and IRQL annotations.<br>**Not changed (documented):** the session lock is still held across the 10 s open-channel request, so an interactive user can delay other session operations on the same device by up to 10 s. |
| S-36 | Medium | New development-only capability: temporary self-signed driver signing and test mode on a dedicated development PC (`tools/dev/Ssap3DevSigning.ps1`). Test mode lets **any** test-signed kernel driver load. While the development key exists, anything running as the developer can use it | **Mitigated by design; not part of production setup (CI run #35: 93 checks, including a kernel load on a disposable test-mode runner; `docs/DRIVER_TEST_REPORT.md`, Test F)** | Controls:<br>- refused while Secure Boot is on (Windows forbids test signing then); firmware, Memory Integrity, Defender and Code Integrity policy are never changed; `NOINTEGRITYCHECKS` is refused;<br>- every system change needs elevation, an explicit `-DevelopmentMachine` and a confirmation prompt, plus `-RecoveryKeyReady` when BitLocker is on;<br>- certificate: RSA 3072/SHA-256, non-exportable key, code signing only, `CA:false`, 1–90 days, an SSAP3 marker extension; only its public part is trusted machine-wide; `-DiscardKey` deletes the key right after signing;<br>- acts only on its own certificates (subject plus marker), its own packages (provider SSAP3, signer "SSAP3 Temporary Driver Signing") and its own recorded test-signing change; look-alikes are left alone even when a tampered state file names them;<br>- elevated installs use an administrators-only staging copy, verified there;<br>- SSAP3 Setup and the production health check refuse the package (`NotTrusted`, or `NotMicrosoft` once trusted);<br>- CI: production setup must not reference the tool, and published driver artifacts must be unsigned.<br>**Residual (documented):** while test mode is on, any test-signed driver loads; the key can be used, though not copied, by the developer's own processes until it is removed. |
| S-33 | Info | New data in the DSP: measured HRIR responses (MIT KEMAR) for the convolution renderer | **No new runtime input (by design). Compiled `/W4 /WX /guard:cf` and CodeQL-clean in CI run #19** | Nothing is loaded or parsed in `audiodg.exe`. The offline converter (`tools/hrtf/HrirTableBuilder`, developer PC only) validates the source set, which it reads from a SHA-256-pinned archive, and emits a C++ header that is compiled in as constant data. In the engine:<br>- `static_assert`s check the table's layout (ring order, azimuth range 0–180), and that the longest convolution window (onset bound × 384 kHz + 512 taps) fits the masked 32 768-sample history;<br>- every index is clamped or masked;<br>- the per-block filter build uses fixed-size stack arrays, with no allocation, locks or I/O;<br>- the kernel table is computed only on the dispatcher thread when the sample rate changes.<br>Engine 2.7.0 adds:<br>- the renderer field (offset 20, formerly reserved): only 0 or 1 is accepted, and any other value rejects the whole record;<br>- the stage-EQ biquads, designed on the dispatcher thread; any filter at or above 0.45 × the rate, or with an implausible gain, is skipped; the IIR state is included in the finite-state check that resets the filters.<br>Otherwise the shared-state validation (S-04) is unchanged. |

No **critical or high** finding is open in source. Several fixes are **not
yet compiled or executed** (see §6). Until they are, they are not
release-verified, and a production release stays blocked.

## 3. Attack-surface inventory

| Surface | Exposure | Controls |
| --- | --- | --- |
| AACP packets from the AirPods (≤ 2582 B) | Kernel queues them without interpreting them; user mode parses them, including protobuf on the sensor channel | Strict total parsers, randomized tests, bounded queue of 16 (oldest dropped) |
| Commands user mode asks the driver to send (≤ 512 B) | Any interactive user on this PC | Opcode allowlist; `0x17` limited to sensor start/stop with a bounded interval (S-29) |
| BLE advertisements from any nearby device | User-mode parser | 255-byte cap, total parser, model and RSSI filter |
| Driver IOCTL interface | Interactive users only | 5 IOCTLs with I/O-manager access checks (protocol v4: read for status/read, write for open/close/send); size and ownership checks; session and parked-session caps; exact outbound packet allowlist; timeouts |
| Shared state file | Interactive users write; `audiodg` reads | Protected ACL (no create or delete); DSP validation; seqlock |
| HRIR data | None at run time: compiled into the engine DLL as constant data | Generated offline from a hash-pinned archive; layout and bounds checked at compile time (S-33) |
| Settings JSON (per user) | Same user | 64 KiB cap, typed deserialisation, range validation |
| Installer and uninstaller | Administrators | Admin-only paths, ownership checks, manifest, rollback |
| SoundVolumeView child process | App launches it | Installed path plus SHA-256 pin |
| Network | Optional companion connection only (S-32) | Outbound TCP to a paired companion on a local or private address; no listening sockets; no HTTP. The driver and DSP have no network code. |

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

- **Network:** at runtime, only the optional companion connection (S-32). It
  is outbound, local or private only, authenticated and encrypted. The build
  and CI download from nuget.org and GitHub only. There is no update checker
  yet. When one is added it must be opt-out, HTTPS-only, and send no device
  data.
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
| Unit and simulated tests (xUnit) | **158/158 pass** (2026-11-13, after the motion-protocol rewrite) |
| Randomized-input tests | 4 × 200 000 parser inputs (AACP, sensor protobuf, BLE, shared state) plus 50 000 hostile estimator samples, 0 failures |
| Bluetooth Core Spec `ah` test vector | Pass |
| PowerShell parse check (14 scripts) | 0 errors |
| Forbidden-operation scan of production setup | 0 hits |
| `Test-Ssap3SystemState.ps1 -Verify` (read-only) | Pass. Baseline: Secure Boot on, Memory Integrity on, Defender real-time on, `DisableProtectedAudioDG` absent |
| Secret scan (`git grep` for key headers and tokens) | 0 secrets |
| App launch on real Windows 11 26200 | Starts; honest states shown (Bluetooth off, AAP driver missing, engine not installed). A TimeSpan overflow crash found by this run was **fixed**. |
| CI run #1 (GitHub Actions, `414b091`) | CodeQL `security-extended` for C# and C/C++: pass. gitleaks: pass. Script parse and forbidden-operation checks: pass. Spatial engine compiled under `/W4 /WX /guard:cf`. Spatial-engine tests and the managed test build failed (fixed in the next commits; see `DEVELOPMENT_STATUS.md`). |
| CI run #4 (`f3cf186`) | **All jobs pass.** Driver compiles (`/W4 /WX /guard:cf`), links and passes InfVerif; spatial-engine tests pass. |
| CI run #12 (`7da096c`): driver pre-submission checks | **All pass.**<br>- `Test-DriverImage.ps1` on the real `ssap3aap.sys`: page-aligned sections, no writable+executable section, NX/CFG/ASLR, imports only `WDFLDR.SYS` and `ntoskrnl.exe`, non-executable pool only.<br>- Microsoft `microsoft/windows-drivers` 1.10.0: **0 must-fix and 0 recommended results**. The recommended suite had reported 2 missing role-type declarations in run #11; both are fixed.<br>- Inf2Cat signability test: no errors or warnings.<br>- Attestation CAB built and its layout verified (`ssap3aap\` with `.sys`, `.inf`, `.pdb`, `.cat`). |
| CI run #19 (`40f968f`): HRIR renderer (S-33) | **Pass.** The engine builds under `/W4 /WX /guard:cf`, including the `static_assert` table checks, and CodeQL C/C++ is clean. Engine tests pass:<br>- NaN/Inf/stale rejection and bit-exact Off (also after On);<br>- HRIR cue ranges and the 8–384 kHz checks;<br>- head-sweep and stage-jump click tests;<br>- real-time headroom: 1.3 % of one core. |
| Setup permission logic (S-23, S-27), non-elevated, scratch folder | Pass. Reproduced Equalizer APO's `Users: FullAccess` grant; after hardening, `Users` can write 0 of 4 objects and read all 4; exact restore of all 4 security descriptors. Owner change not exercised (needs elevation). |
| `Test-Ssap3SystemState.ps1 -Verify` after the S-23/S-24 additions | Pass. Equalizer APO not installed on this PC yet. |
| **Elevated install on this PC** (`Install-SSAP3Local.ps1 -EnableSpatialEngine`, Equalizer APO 1.4.2 from the official installer, hash-verified) | **Executed.** First attempt failed after 3 steps on a setup bug (the log function wrote to the output stream), and **rollback ran and reverted all 3 steps**. Second attempt completed. Checked independently afterwards:<br>- S-23: before, all 7 config objects were writable by standard users; after, 0 of 8, a write attempt from the standard-user token was denied, and read access still works;<br>- S-24: the update-checker task was present and is now removed;<br>- S-26: load-path check passed;<br>- S-25: the Device Selector attachment was left as is;<br>- S-28: `ssap3.txt` is scoped to one endpoint GUID;<br>- the plug-in hash matches the CI artifact, and the plug-in is admin-only;<br>- the state file ACL is as designed.<br>The engine then worked in Fixed mode: the user heard the difference from Off. Uninstall not executed yet. |
| **Not performed** | Loading the driver anywhere; Driver Verifier; Application Verifier; ASan/UBSan; coverage-guided fuzzing; installer and uninstaller execution; any AirPods hardware test |

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
- **Static pre-submission checks (in CI):**
  - `driver/Test-DriverImage.ps1` checks the HVCI-relevant image properties:
    - page-aligned sections and no writable+executable section;
    - NX, CFG and ASLR flags;
    - kernel-only imports;
    - non-executable pool only in the sources.

    The checker was validated on Windows' own `null.sys`, `beep.sys` and
    `wdf01000.sys`, and it rejects a user-mode DLL.
  - Microsoft's `microsoft/windows-drivers` 1.10.0 CodeQL pack: the
    recommended suite goes to code scanning, and any must-fix result fails
    CI.
- **Independent review (2026-11-14):** a separate reviewer read every
  driver source file and the INF. It found nothing critical or high. It
  confirmed:
  - buffer aliasing handling and zeroed outputs (no kernel memory reaches
    user mode);
  - exact byte counts and single completion of every request;
  - the reader stop handshake, and DISPATCH-safe DDIs on the DPC and
    completion paths;
  - NonPagedPoolNx with tags;
  - an INF that would not block attestation.

  Its medium and low findings are fixed (S-35).
- **Open items:**
  - Driver Verifier and runtime HVCI testing on hardware (this PC has
    Memory Integrity on);
  - attestation signing, which needs an organization and an EV certificate
    (`driver/SIGNING.md`);
  - two review items that need hardware to settle: how KMDF treats the
    default target at ReleaseHardware, and whether the Bluetooth stack
    honours cancel for open-channel requests.

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
- The renderer uses one generic, non-personalised dummy-head HRTF (MIT
  KEMAR). SOFA import is not implemented; when it is, the parser must live
  in the offline converter, not in the engine (ADR-5).
- The microphone VAD exists in Core but is not wired to an audio input.
- Exclusive-mode and ASIO audio bypass the engine.
- The opt-in engine requires `DisableProtectedAudioDG`, which weakens a
  system-wide audio protection while it is installed.
- With the engine installed, any administrator, and any code an
  administrator runs, can still load DLLs into `audiodg.exe` through
  Equalizer APO. S-23 and S-26 close that path for standard users only.
- Uninstall restores Equalizer APO's original permissions exactly, including
  its user-writable config folder (S-23), because SSAP3 does not own Equalizer
  APO. Uninstall Equalizer APO too, or keep the folder admin-only by hand.
