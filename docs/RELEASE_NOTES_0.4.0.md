## SoundStage v0.4.0 — Compatibility & Diagnostics

This release focuses on the failures observed after reboot and on Bluetooth
adapters where Windows keeps AirPods audio connected but drops the Apple
control-profile device.

### Fixed

- Setup now verifies both the running kernel Test Mode state and the persistent
  BCD setting. If they disagree, setup repairs BCD before another restart.
- The driver can bind through AAP or the Apple UARP service, giving Windows a
  second compatible PDO for the AACP control channel.
- SoundStage automatically refreshes missing AAP/UARP profile devices and also
  exposes the operation through the **Repair** button.
- BLE scanning restarts cleanly and falls back to passive mode on adapters that
  reject active scanning.
- A BLE advertisement no longer falsely marks AirPods as classic Stereo
  connected or replaces the paired Bluetooth address with a randomized one.

### Diagnostics

The new **Report** button creates a ZIP in `Documents\SoundStage\Reports` with:

- Windows version, Code Integrity, Test Mode, Secure Boot, and BCD state;
- Bluetooth radios, paired devices, enabled service GUIDs, PnP devices,
  driver packages, and problem devices;
- render/capture endpoints and the recommended A2DP/HFP route;
- Bluetooth and Windows Audio service state;
- SoundStage installer and application logs.

Reports stay local and are never uploaded automatically.

### Compatibility

- x64 Windows 10 build 19041 or newer;
- x64 Windows 11;
- AirPods exposing Apple AAP/AACP controls;
- Microsoft-stack Bluetooth adapters, including the tested TP-Link path and
  the BARROT compatibility case used to develop the recovery logic.

### Important security notice

The bundled kernel driver is still locally test-signed. Installation requires
Secure Boot disabled, Windows Test Mode, administrator approval, and a restart.
This is not equivalent to Microsoft production signing. The release remains a
public beta even though GitHub presents it as the current **Latest** release.

The spatial renderer and head-tracking tuning are unchanged from v0.3.0.
