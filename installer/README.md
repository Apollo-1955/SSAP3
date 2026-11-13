# SSAP3 setup payload

The public release is distributed as one `SSAP3-Setup.exe`. It contains:

- the self-contained x64 SSAP3 tray application;
- the test-signed KMDF Apple AAP/UARP profile driver;
- the SSAP3 binaural spatializer;
- the required Equalizer APO runtime and corresponding source archive;
- reversible install, migration, and uninstall scripts;
- licenses, documentation, and a SHA-256 payload manifest.

## Install

1. Pair and connect the AirPods.
2. Disable Secure Boot in UEFI.
3. Run `SSAP3-Setup.exe` as administrator.
4. Restart Windows when requested.
5. Run the same `SSAP3-Setup.exe` again.

The second run installs the app, control-profile driver, and spatial engine. Existing
endpoint effects are backed up before SSAP3 is attached. Pre-SSAP3
development builds are migrated automatically.

If Windows later loses the Apple control PDO while audio remains connected,
use **Repair** in the tray panel. **Report** creates a local diagnostic ZIP in
`Documents\SSAP3\Reports`.

## Audio profiles

Use `AirPods Stereo` for high-quality playback. Classic Bluetooth cannot carry
high-quality A2DP output and the AirPods Hands-Free microphone simultaneously,
so use a separate microphone for Discord and games.

## Uninstall

Open Windows **Settings → Apps**, select **SSAP3**, and click
**Uninstall**. Original endpoint effects, the driver, certificate, shortcuts,
autostart, and Test Mode state are restored or removed as appropriate.

See `INSTALL.md` or
[the complete installation guide](../docs/INSTALL.md) for security warnings,
verification, and recovery instructions.
