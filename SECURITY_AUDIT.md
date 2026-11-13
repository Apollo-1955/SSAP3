# SSAP3 security audit

Scope: the inherited SoundStage code at commit `114143c` (all of `src/`,
`driver/`, `audio/`, `installer/`, `.github/`, `third_party/`).
Method: manual source review on 2026-11-13. None of the installer or driver
paths were executed during the audit, because they require administrator
rights and change boot configuration. Every finding below comes from reading
the code; line numbers refer to the original files.

Severity scale: **Critical** means system-wide trust is compromised or a
standard user can become administrator. **High** means memory corruption or a
kernel hang/DoS reachable by a standard user, or a silent change to a
security setting. **Medium** means a limited integrity, privacy or DoS
impact. **Low** means hardening or hygiene.

Release rule: Critical and High findings block any SSAP3 production release.
Their remediation status is tracked in `SECURITY_REPORT.md`.

## 1. Threat model

### Assets

- Integrity of Windows trust: the certificate stores, boot configuration,
  Code Integrity, and the APO signature policy.
- Administrator privilege on the machine.
- Kernel stability, given that `ssap3aap.sys` runs in ring 0.
- Stability of `audiodg.exe`, which carries audio for every user.
- Privacy of the user's audio, microphone, motion data, Bluetooth
  identifiers and device names.
- Correctness of what the UI reports: listening mode, battery, tracking
  state.

### Trust boundaries

| # | Boundary | Untrusted side | Trusted side |
| --- | --- | --- | --- |
| TB1 | Bluetooth link | Any paired or spoofed device sending AACP payloads (≤ 512 B per packet) | Driver packet queue, app parsers |
| TB2 | Driver device interface | Every process of every interactive user (SDDL `BU:GRGW`) | Kernel driver |
| TB3 | Shared head-pose state | The unprivileged app, and any other local user who can write the file | DSP inside `audiodg.exe` |
| TB4 | BLE advertisements | Every nearby BLE advertiser | App battery and in-ear display |
| TB5 | Files the app loads | Settings JSON, and in future SOFA/HRIR files | App process |
| TB6 | Elevated installer/uninstaller | Files and registry keys a standard user can write | Administrator context |
| TB7 | Build and supply chain | NuGet, GitHub Actions, committed third-party binaries | Release artifacts |
| TB8 | Network | None exists today | — |

### STRIDE summary

| Boundary | Spoofing | Tampering | Repudiation | Information disclosure | DoS | Elevation |
| --- | --- | --- | --- | --- | --- | --- |
| TB1 | Malicious device on the AAP PDO | Malformed packets | — | — | Packet flood | Kernel bug → ring 0 |
| TB2 | Any local process opens sessions | Arbitrary AACP payloads | — | Bridge status words | Pool exhaustion (S-05), hangs (S-06) | Kernel memory bug |
| TB3 | Any user writes a pose | NaN/out-of-range values (S-04) | — | — | Audio engine crash | Code execution in `audiodg` |
| TB4 | Fake AirPods advertisements (S-12) | False battery levels | — | Strangers' device state shown | — | — |
| TB5 | — | Oversized or malformed files | — | — | Allocation bombs | Parser memory bugs |
| TB6 | Folder squatting in ProgramData | Replacing elevated scripts (S-02) | No install manifest | — | — | **Standard user → admin** |
| TB7 | Tampered binary | Unpinned dependencies (S-11) | No SBOM | — | — | Malicious code in a release |

## 2. Findings

### S-01 — Critical — Self-signed certificate trusted as a machine root CA

- `installer/Install-SoundStage.ps1:174-187` imports the package `.cer`
  into `LocalMachine\Root` **and** `LocalMachine\TrustedPublisher`.
- `Assert-Package` (`:234`) imports the certificate **before** it checks the
  SYS/CAT signatures (`:235-243`), so trust is granted even when
  verification then fails.
- `driver/New-TestSigningCertificate.ps1:15-24` creates the key with
  `-KeyExportPolicy Exportable` and no `basicConstraints CA:false`. The key
  lives in the release builder's `CurrentUser\My` store
  (`installer/build-installer.ps1:120-155`), which is outside the end user's
  control.
- Impact: whoever holds that private key can sign code that every installed
  machine treats as trusted, at minimum for code signing (Authenticode,
  driver catalogs while Test Mode is on, PowerShell `AllSigned`).
- Fix: production installs never add trust anchors. Development machines
  generate their **own** non-exportable, `CA:false`, code-signing-only
  certificate locally, and no certificate is ever distributed.

### S-02 — Critical — Local privilege escalation through user-writable elevated scripts

- `installer/Install-SpatialAudioEngine.ps1:541-546` runs
  `icacls %ProgramData%\SoundStage /grant *S-1-5-32-545:(OI)(CI)M /T`, which
  gives every member of `BUILTIN\Users` Modify on the whole tree.
- `installer/SoundStage.iss:50-53` places the entire elevated payload,
  including `Install-SoundStage.ps1` and `Uninstall-SoundStage.ps1`, in
  `{commonappdata}\SoundStage\package`.
  - `:58-59` (`[UninstallRun]`) runs
    `package\Uninstall-SoundStage.ps1` as administrator.
  - `:92-112` runs `package\Install-SoundStage.ps1` as administrator after
    copying it. New files inherit the Users-Modify ACE, so a standard user
    can race the copy.
  - `docs/INSTALL.md:156-162` tells users to run the ProgramData copy from
    an elevated shell.
- `install-state.json` sits in the same folder, and the elevated uninstaller
  trusts it: it chooses which certificate thumbprints to delete from
  `LocalMachine\Root` and whether to change Test Mode
  (`Uninstall-SoundStage.ps1:115-136, 172-178`).
- Even without the ACL grant, `%ProgramData%` lets standard users create
  sub-folders. A user can **pre-create** `%ProgramData%\SoundStage`
  (folder squatting) and own it. The app itself also creates that folder
  (`HeadTrackingAudioStateWriter.cs:104`).
- Impact: any standard user gains administrator code execution on the next
  install, repair or uninstall.
- Fix: elevated payload and state go under `%ProgramFiles%\SSAP3` (admin-only
  inheritance). The installer creates `%ProgramData%\SSAP3` with an
  **explicit protected ACL**, verifies the owner, and refuses to run if the
  folder is not admin-owned. Only one leaf folder (`Runtime`) is
  user-writable, and nothing elevated ever reads it.

### S-03 — Critical (policy) — Installation requires disabling platform security

- The installer refuses to run with Secure Boot on (`Install-SoundStage.ps1:224-232`).
- It suspends BitLocker (`:485-504`).
- It runs `bcdedit /set testsigning on` (`:534-539`).
- Test Mode lets **any** test-signed kernel driver load, not just SoundStage's.
- Fix: the production installer contains no Secure Boot, BCD or BitLocker
  logic. The driver installs only if its catalog carries a valid production
  signature. Otherwise the AAP features are reported as unavailable.

### S-04 — High — Out-of-bounds read in `audiodg.exe` from unvalidated shared state

- `audio/SoundStageSpatializer/soundstage_spatializer.cpp:546-547` copies
  `yaw`/`pitch` from the shared file without `isfinite` checks.
- A NaN survives `std::clamp` (`:229-230`) and the smoothing filter
  (`:233-234`). It reaches `CalculateParameters` and becomes a NaN ITD
  (`:399-400`).
- In `ReadDelay`, `static_cast<int>(NaN)` (`:451`) is undefined. On x64 it
  yields `INT_MIN`, and the index arithmetic (`:453-456`) becomes negative,
  so the read lands before `delayLeft_` or `delayRight_`.
- The NaN latches until the next mode change.
- The file is writable by the unprivileged app by design and by every
  local user (S-02).
- Impact: the audio engine crashes for all users, and memory outside the
  buffer is read.
- Fix: reject non-finite or out-of-range values, guard `ReadDelay` itself,
  use a versioned layout and monotonic timestamps.

### S-05 — High — Unbounded kernel allocations by any interactive user

- `driver/SoundStageKmdf/queue.c:95-98` allocates one
  `LIBREPODS_SESSION_ENTRY` per `OPEN` IOCTL. That is about 17 KiB of
  NonPagedPoolNx: a 32×512-byte queue plus an unused 512-byte `LastPacket`
  (`driver.h:23-49`).
- Each open also creates an L2CAP channel and a continuous reader.
- There is no per-handle or per-device limit, and the device SDDL grants
  `BU` read/write (`driver.c:226`, `INF:66`).
- Fix: at most one session per file object and four per device; remove the
  dead buffer.

### S-06 — High — Kernel waits with no timeout, plus a reader-stop race

- BRBs are submitted synchronously with no request timeout
  (`transport_bth_l2cap.c:173-181`). The send BRB uses `Timeout = 0`
  (`:740-743`) while the device-wide `SessionLock` is held
  (`queue.c:264-278`) in a **sequential** queue (`driver.c:272`). A stalled
  Bluetooth stack therefore blocks every client and the file-cleanup path,
  so processes cannot exit.
- `LibrepodsStopContinuousReader` waits on `ReaderStoppedEvent` with no
  timeout (`:500-505`).
- If the resubmit DPC passes its `ReaderStopping` check (`:298`) before the
  stop path sets the flag, and sets `ReaderStarted = 1` (`:316`) only after
  the stop path read it as 0 (`:496`), the new read is never cancelled.
- Because `L2CA_CLOSE_CHANNEL` is issued only after that wait (`:687-695`),
  close, cleanup and PnP removal can hang indefinitely.
- Fix: bounded `WDF_REQUEST_SEND_OPTIONS` timeouts, a stop-and-cancel
  sequence that tolerates the race, `KeFlushQueuedDpcs`, and a bounded wait
  with channel close as the fallback.

### S-07 — High — Silent, unrestored changes to Windows security settings

- `Install-SpatialAudioEngine.ps1:488-493` sets
  `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Audio\DisableProtectedAudioDG=1`.
  Equalizer APO's own developer documentation (bundled source,
  `Wiki/Developer.txt`) says this "disables the signature check for APOs,
  so that unsigned APOs will be loaded" and can break secure audio paths.
- `:213-268` and `:584-588` take ownership of MMDevices endpoint keys and
  grant Administrators FullControl. The original owner and DACL are never
  recorded or restored.
- The uninstaller deletes `DisableProtectedAudioDG` whenever SoundStage owns
  Equalizer APO, even if the value existed before installation
  (`Uninstall-SpatialAudioEngine.ps1:118-121`).
- Fix: make the system-wide engine an explicit opt-in with consent text,
  record prior values and SDDL in the install manifest, and restore them
  exactly.

### S-08 — Medium — Driver can be pointed at arbitrary Bluetooth addresses

`transport_bth_l2cap.c:592-602`: when the PDO's remote-device query fails,
the driver opens PSM `0x1001` to an address string supplied by the caller.
Fix: require the PDO's own remote address and ignore caller input.

### S-09 — Medium — Arbitrary AACP payload relay for any local process

`queue.c:219-285` forwards any 1–512-byte payload. Any process in any
interactive session can change listening modes or start motion streaming.
Fix: restrict the interface to `SY`, `BA` and `IU`, and check AACP framing
(known header plus allow-listed opcodes) before sending.

### S-10 — Medium — Executable search in parent directories

`WindowsSpatialAudioController.cs:254-283` walks every parent of the app
directory looking for `third_party\SoundVolumeView\SoundVolumeView.exe` and
then executes it, which allows binary planting when the app runs from a user
folder. Fix: only the installed path, plus a pinned hash check.

### S-11 — Medium — Unpinned third-party binaries

- `third_party/SoundVolumeView/SoundVolumeView.exe` (200 192 B, freeware,
  unsigned) is committed to the repository. `THIRD_PARTY.md:33-34` records
  the hash of the *archive*, not of the executable.
- `driver/package-driver.ps1:22, 51-53` packages Equalizer APO binaries from
  `third_party/EqualizerAPO/bin`. That folder is not in the repository, and
  its contents are never checked against the upstream installer hash in
  `third_party/EqualizerAPO/README.md`.
- Fix: a hash-pinned manifest that the build verifies, plus an SBOM.

### S-12 — Medium — Any nearby AirPods (or spoofer) is shown as the user's device

`WindowsBleAirPodsScanner.cs:132-163` forwards every Apple advertisement
that parses, and `IdentityResolvingKey` is never set anywhere in `src/`. In
a café or classroom, the app can display another person's battery and
in-ear state, and a BLE spoofer can inject arbitrary values. Fix: filter by
the paired device's model and by RSSI, and label unresolved advertisements
as "nearby" rather than "yours".

### S-13 — Medium — Diagnostic bundles are not redacted

`SoundStageDiagnosticCollector.cs:53-92, 308-315` stores full Bluetooth
addresses, PnP instance IDs, device friendly names (which often contain the
owner's name), the full driver inventory and BCD output. The bundles are
local only and never uploaded, but users are asked to attach them to public
issues. Fix: redact by default, with an explicit opt-in to keep identifiers.

### S-14 — Medium — UI reports unconfirmed listening-mode changes

`MainWindow.xaml.cs:260-267` shows "AirPods mode changed" as soon as the
IOCTL returns. LibrePods documents that the AirPods echo
`04 00 04 00 09 00 0D <mode> 00 00 00` once the mode has changed, and echo
a different mode if the request was refused. Fix: wait for the echo, with a
timeout and an "unconfirmed" state.

### S-15 — Low — Name-based process killing

`Install-SoundStage.ps1:591-602` and `Uninstall-SoundStage.ps1:98-99` kill
every process named `SoundStage`. Fix: match by image path under the
install directory.

### S-16 — Low — Machine-wide autostart and elevated-context desktop shortcut

`Install-SoundStage.ps1:635-666` writes an HKLM Run value that affects every
user, and creates the desktop shortcut on the *elevated* account's desktop.
Fix: per-user autostart owned by the app setting (HKCU), with no desktop
shortcut by default.

### S-17 — Low — Substring-matched forced driver deletion

`Install-SoundStage.ps1:292-308, 407-412` runs
`pnputil /delete-driver /uninstall /force` on any `oem*.inf` whose text
contains a catalog file name. Fix: match `pnputil /enum-drivers` output on
original INF name **and** provider.

### S-18 — Low — Unbounded log and settings files

`SoundStageAppLog.cs` appends forever and writes exception text that
includes device addresses. `SoundStageUserSettings.cs:84-87` deserializes
without a size limit. Fix: rotate and redact the log, cap the settings file
size, validate values.

### S-19 — Low — Wall-clock timestamps across the IPC boundary

`HeadTrackingAudioStateWriter.cs:170-172` and
`soundstage_spatializer.cpp:531-544` use UTC wall-clock time. A clock change
can freeze or reject tracking. Fix: `GetTickCount64` on both sides.

### S-20 — Low — CI hardening gaps

`.github/workflows/ci.yml` pins actions by tag, not by SHA, and has no
static analysis, dependency, secret, driver or installer checks. Actions
are not enabled on this fork (0 runs reported by the GitHub API). The
workflow already uses `permissions: contents: read`, which is good.

### S-21 — Informational — Unsigned release artifacts

Neither the setup executable nor the app is Authenticode-signed, so users
see "Unknown publisher". This needs an OV/EV code-signing certificate, which
the project does not yet have.

## 3. Positive observations (keep)

- No network code anywhere in the runtime, and no telemetry or analytics.
- The driver does not parse protocol content. It exposes only five
  `METHOD_BUFFERED` IOCTLs with structure-size checks, preserves aliased
  input fields (`queue.c:322-328`), checks session ownership per file
  object, uses NonPagedPoolNx, and cleans up sessions on handle close.
- The DSP render callback does not allocate memory or take locks.
- Endpoint-effect backups refuse to treat an Equalizer APO GUID as an
  "original" effect, which prevents recursive chaining.
- The repository ignores `*.pfx`, `*.pvk` and `*.snk`. A `git grep` of all
  tracked text files found no private keys or secrets. It searched for
  private-key PEM headers, `password`/`secret`/`token`/`api_key`
  assignments, and GitHub and AWS token formats, and the only matches were
  `CancellationToken` parameters. The only tracked binaries are two brand
  images, the Equalizer APO source ZIP, and SoundVolumeView's `.exe`/`.chm`.
