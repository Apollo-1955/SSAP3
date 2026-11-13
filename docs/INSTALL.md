# Installing and removing SSAP3

> [!NOTE]
> No SSAP3 release has been published yet. This guide describes the setup
> that `installer/` builds on the `development/ssap3` branch.

## What Setup never does

SSAP3 Setup **never**:

- turns off Secure Boot, Memory Integrity, Microsoft Defender or driver
  signature enforcement;
- enables Test Mode or edits the boot configuration;
- suspends BitLocker;
- adds certificates to any Windows trust store;
- installs a kernel driver that is not signed by Microsoft;
- creates services, scheduled tasks, firewall rules or environment variables;
- unpairs or re-pairs your AirPods.

## What Setup installs

| Component | When | Where |
| --- | --- | --- |
| SSAP3 Control Center (`SSAP3.exe`, self-contained .NET 8) | Always | `%ProgramFiles%\SSAP3` |
| Setup and uninstall engine, install manifest, install log | Always | `%ProgramFiles%\SSAP3\setup` (admin-only) |
| Runtime state file for the spatial engine | Always | `%ProgramData%\SSAP3\Runtime` (protected ACL) |
| Start Menu shortcut | Always | Start Menu |
| Start at sign-in | Optional task (on by default) | `HKLM\…\Run\SSAP3` |
| Desktop shortcut | Optional task (off by default) | Public desktop |
| AAP driver (`ssap3aap.sys`) | Only if the package carries a **Microsoft** signature | Driver Store |
| System-wide 3D soundstage engine | **Opt-in** task only | `%ProgramFiles%\EqualizerAPO` |

Your settings and logs live in `%LocalAppData%\SSAP3`. Diagnostic reports go
to `Documents\SSAP3\Reports`.

### About the AAP driver

Listening modes, AACP battery, Conversation switching and head tracking need a
small kernel driver. Windows loads a kernel driver on a normal PC only after
Microsoft has signed it. Until an attestation-signed SSAP3 driver exists,
Setup skips the driver and the app shows **Connected (audio only)**, with
the reason. Battery from Bluetooth LE advertisements and the fixed
soundstage still work.

### About the opt-in system-wide engine

The 3D soundstage runs inside the Windows audio service through the
Equalizer APO host. Equalizer APO requires
`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Audio\DisableProtectedAudioDG = 1`.
That turns off Windows' signature check for audio-processing components
**for all devices**, and some protected-content playback may refuse to play
while it is set.

Setup explains this on its own page and changes nothing unless you select the
task. When you do, Setup records the previous value, every audio endpoint
effect it touches, and the owner and permissions of any registry key it has
to take over. Uninstall restores all of them exactly.

## Install

1. Pair your AirPods Pro 3 in **Settings > Bluetooth & devices**, and connect
   them once so Windows creates the stereo endpoint.
2. Run `SSAP3-Setup-<version>.exe` and approve the administrator prompt.
3. Pick the optional tasks you want, then finish.

If any step fails, Setup reverts every change it recorded and reports the
error. The log is in `%ProgramFiles%\SSAP3\setup\install.log`.

If the upstream **SoundStage for AirPods** is installed, Setup refuses to
continue. Uninstall SoundStage first: its driver, root certificate and Test
Mode change must be removed by its own uninstaller.

## Update or repair

Run a newer Setup. It reverts the previous installation's recorded system
changes, then applies the new ones under a fresh manifest.

## Uninstall

Go to **Settings > Apps > Installed apps > SSAP3 for Windows > Uninstall**.
The uninstaller asks whether to delete your settings, logs and reports as
well:

- **Standard uninstall (No):** removes SSAP3 and restores Windows, but keeps
  your settings and reports.
- **Complete uninstall (Yes):** also deletes `%LocalAppData%\SSAP3` and
  `Documents\SSAP3` for your account.

The uninstaller:

- stops only processes that run from `%ProgramFiles%\SSAP3`;
- reverts every manifest step newest-first: audio effects, registry values,
  key permissions, `DisableProtectedAudioDG`, the Equalizer APO runtime that
  SSAP3 installed, and the driver package matched by original name **and**
  provider;
- removes `%ProgramData%\SSAP3`.

It is safe to run more than once. If a step cannot be reverted, the manifest
is kept so that the next run retries it.

## Verify a clean system

```powershell
# Before installing:
.\installer\Test-Ssap3SystemState.ps1 -Snapshot before.json
# After uninstalling:
.\installer\Test-Ssap3SystemState.ps1 -Snapshot after.json
.\installer\Test-Ssap3SystemState.ps1 -Compare before.json after.json
```

The comparison separates SSAP3 leftovers and changed security settings, which
are failures, from unrelated changes made by you or other software, which are
informational. `-Verify` alone checks that no SSAP3-owned object remains.

## Troubleshooting

| Symptom | What to check |
| --- | --- |
| "Bluetooth is off" | Turn on Bluetooth in Windows Settings. SSAP3 never changes the radio. |
| "Connected (audio only)" | No Microsoft-signed AAP driver is installed. See *About the AAP driver*. |
| Fixed and Off sound the same | The opt-in engine is not installed, the output is not **AirPods (Stereo)**, or the app uses WASAPI exclusive mode or ASIO, which bypass endpoint effects. |
| Call audio sounds like a telephone | Using the AirPods microphone switches Bluetooth to the Hands-Free profile. Use another microphone. |
| Head tracking turns the wrong way | Settings > Head tracking > **Invert yaw**. The AirPods Pro 3 orientation decoder is not hardware-verified yet. |
| Need support data | **Report** creates a redacted local ZIP. Review it before sharing; nothing is uploaded. |
