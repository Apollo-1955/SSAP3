# Installing SoundStage

This guide applies to the `v0.4.0` Windows public beta.

## Read this first

The current SoundStage kernel driver is locally test-signed. Installation
therefore requires:

- an administrator account;
- x64 Windows 10 build 19041 or newer, or x64 Windows 11;
- Windows Test Mode;
- Secure Boot disabled;
- one Windows restart between the two setup phases.

Disabling Secure Boot and enabling Test Mode reduce Windows platform security.
This is a development signing path, not a Microsoft production-signed release:

- [Loading test-signed code](https://learn.microsoft.com/windows-hardware/drivers/install/the-testsigning-boot-configuration-option)
- [Introduction to test signing](https://learn.microsoft.com/windows-hardware/drivers/install/introduction-to-test-signing)

If BitLocker or device encryption is enabled, make sure its recovery key is
available before changing UEFI settings. SoundStage suspends BitLocker
protection for one restart before changing the Test Mode boot option, but it
cannot change Secure Boot for you.

## Download and verify

1. Download `SoundStage-Setup.exe` from the latest GitHub release.
2. Optionally download `SoundStage-Setup.exe.sha256` from the same release.
3. Verify the executable in PowerShell:

   ```powershell
   Get-FileHash .\SoundStage-Setup.exe -Algorithm SHA256
   ```

4. Compare the result with the release checksum.

The setup executable is not yet signed by a publicly trusted commercial
certificate, so Windows may show **Unknown publisher**. The bundled driver SYS
and CAT are signed with the exact test certificate included in the package and
are verified before the boot configuration is changed.

## Disable Secure Boot

The exact UEFI screen differs by PC vendor:

1. Save your work and open Windows advanced startup.
2. Choose **Troubleshoot → Advanced options → UEFI Firmware Settings**.
3. Set **Secure Boot** to **Disabled**.
4. Save the UEFI settings and return to Windows.

Setup checks the Windows Secure Boot state and stops without changing it when
Secure Boot is still enabled.

## Install

### Phase 1: prepare Windows

1. Pair the AirPods in Windows Bluetooth settings.
2. Connect them or open the case near the computer.
3. Run `SoundStage-Setup.exe`.
4. Approve the administrator prompt and accept the license.

The first run:

- validates the Windows version, architecture, and package;
- verifies the driver and catalog signatures;
- imports the exact SoundStage test certificate into Local Machine
  `Root` and `TrustedPublisher`;
- records reversible state under `C:\ProgramData\SoundStage`;
- exports a BCD backup under `C:\ProgramData\SoundStage\backup`;
- suspends BitLocker for one restart when protection is active;
- enables the `TESTSIGNING` boot option.

Choose **Restart now** on the final setup page. A **Test Mode** watermark after
startup is expected.

### Phase 2: install SoundStage

1. Reconnect the AirPods after Windows starts.
2. Run the same `SoundStage-Setup.exe` again.
3. Approve the administrator prompt.

The second run:

- installs the KMDF Apple AAP/UARP control-profile driver;
- verifies that the matching Bluetooth device is healthy;
- installs SoundStage into `C:\Program Files\SoundStage`;
- installs the spatial engine only on detected AirPods A2DP Stereo endpoints;
- backs up and chains existing endpoint effects;
- creates Start menu and desktop shortcuts;
- registers SoundStage in Windows Apps & Features;
- enables tray autostart.

Pre-SoundStage development builds are migrated automatically. Setup restores
their audio-effect backup, removes their obsolete driver packages and
certificates, preserves the BCD rollback state, and carries the user settings
into SoundStage.

## First use

1. Select **AirPods Stereo** as Windows output, not
   **Hands-Free AG Audio**.
2. Click the SoundStage tray icon.
3. Test **Noise Control → ANC** and **Transparency**.
4. Play stereo audio and compare **Spatial Audio → Off** with **Fixed**.
5. Select **Head Tracked**, face forward during neutral calibration, and then
   turn your head.

For Discord and games, keep AirPods Stereo as output and use a separate
microphone. Selecting the AirPods microphone activates the low-bandwidth
Hands-Free Bluetooth profile.

## Update or repair

Run a newer `SoundStage-Setup.exe` again. Setup is idempotent:

- it only performs the preparation phase when Test Mode is not active;
- it verifies that Test Mode remains enabled in BCD for the next restart and
  repairs the setting when the running kernel and BCD disagree;
- it updates the app, driver, and spatial engine after Test Mode is active;
- it retains the independent backup of the original endpoint effects.

If the package is installed but the controls are not ready, open SoundStage and
click **Repair**. It refreshes only the paired Apple AAP/UARP services through
the Windows Bluetooth API and waits for the driver interface. Reconnect the
AirPods or open their case first. Rerun setup if the app reports that Test Mode
is off or the driver package is missing.

Click **Report** to create a support bundle under
`Documents\SoundStage\Reports`. The ZIP includes the BCD/Test Mode state,
Bluetooth devices and drivers, AAP/UARP presence, audio endpoints, Windows
services, and SoundStage install/app logs. It is never uploaded automatically;
review Bluetooth addresses before attaching it to a public issue.

## Uninstall

1. Open **Windows Settings → Apps → Apps & features**.
2. Select **SoundStage** and click **Uninstall**.
3. Approve the administrator prompt.
4. Restart Windows when requested.

The uninstaller:

- restores the original AirPods endpoint effects;
- removes the SoundStage driver package;
- removes the exact test certificate;
- removes the app, package payload, autostart entry, and shortcuts;
- disables Test Mode only when SoundStage originally enabled it.

The uninstaller does not re-enable Secure Boot because that setting belongs to
UEFI firmware. Re-enable Secure Boot manually after SoundStage is removed if no
other test-signed driver requires it.

If the Apps & Features entry is damaged, run this fallback from an elevated
PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File C:\ProgramData\SoundStage\package\Uninstall-SoundStage.ps1
```

## Troubleshooting

### Buttons change visually but AirPods do not respond

- Open the AirPods case and reconnect.
- Click **Repair** in SoundStage.
- If the app reports that Test Mode is off, run `SoundStage-Setup.exe`, restart
  when requested, and run setup once more.
- In Device Manager → Bluetooth, confirm
  **SoundStage Apple Control Profile** has no warning icon.
- Review `C:\ProgramData\SoundStage\install.log`.
- Click **Report** and attach the resulting ZIP to a private support exchange
  or a GitHub issue after reviewing its device addresses.

### Fixed sounds the same as Off

- Confirm output is **AirPods Stereo**.
- Confirm SoundStage reports the spatial engine as active.
- Avoid ASIO and WASAPI exclusive mode, which can bypass endpoint effects.
- Rerun setup to reattach the effect to the current AirPods endpoint.

### Audio becomes telephone quality in Discord

Do not select the AirPods microphone. Use AirPods Stereo for output and a
different microphone for input.

### Case battery is unavailable

The case does not advertise continuously. Put at least one earbud in the case
or open the lid near the computer and wait for a fresh Bluetooth LE packet.

### Bluetooth or audio fails after installation

1. Disconnect the USB Bluetooth adapter if one is used.
2. Boot Windows in Safe Mode if normal startup is unstable.
3. Use Apps & Features or the fallback uninstaller above.
4. Restart Windows and reconnect the adapter.

Do not delete arbitrary Bluetooth packages from Driver Store.
