## SoundStage for AirPods — experimental Windows prerelease

This is the first public SoundStage build for x64 Windows 10 (build 19041 or
newer) and Windows 11.

### Included

- Off, ANC, Transparency, and Adaptive noise-control commands over Apple AAP.
- Left, right, and case battery telemetry when advertised by the AirPods.
- Fixed binaural room rendering for the Windows A2DP Stereo endpoint.
- AirPods IMU head tracking with neutral calibration and adaptive smoothing.
- Animated English tray panel, autostart, shortcuts, and single-instance
  behavior.
- One `SoundStage-Setup.exe` containing the app, driver, spatial engine,
  recovery scripts, documentation, licenses, and payload checksums.
- Automatic migration from earlier private development builds.

### Important security note

The bundled kernel driver is locally test-signed. Installation requires Secure
Boot to be disabled and Windows Test Mode to be enabled. This reduces platform
security and is not equivalent to a Microsoft production-signed driver. Read
the
[installation guide](https://github.com/capriqqw/soundstage-for-airpods/blob/main/docs/INSTALL.md)
before installing.

### Recommended audio setup

Use **AirPods Stereo** for output and a separate microphone for Discord or
games. Selecting the AirPods Hands-Free microphone forces the low-bandwidth
Bluetooth headset profile.

SoundStage is independent software and is not affiliated with or endorsed by
Apple Inc.
