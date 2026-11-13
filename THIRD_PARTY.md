# Third-party references and attribution

## Upstream project

SSAP3 is a fork and continuation of **SoundStage for AirPods**
(`capriqqw/soundstage-for-airpods`, GPL-3.0-or-later). The upstream
project's README credits its design and development to OpenAI Codex.
SSAP3 keeps that project's license (`LICENSE`, GPL-3.0-or-later) and its
history in this repository's git log. Changes made since the fork are
described in `CHANGELOG.md` and `DEVELOPMENT_STATUS.md`.

## Protocol research

SSAP3's AirPods advertisement parser, AACP packet definitions, KMDF bridge
protocol and Bluetooth profile transport are adapted from these GPL-3.0
projects:

- LibrePods by Kavish Devar and the LibrePods contributors (GPL-3.0-or-later)
  https://github.com/librepods-org/librepods
  (formerly https://github.com/kavishdevar/librepods). Packet layouts are
  checked against its `docs/AAP Definitions.md` and `linux/airpods_packets.h`.
- LibrePods Windows by ivLis-Studio (GPL-3.0)
  https://github.com/ivLis-Studio/librepods-windows
- kAirPods by can1357
  https://github.com/can1357/kAirPods

The GPL grants no rights to the LibrePods name or logo, and SSAP3 uses
neither.

Other source-only references:

- apple-wireshark by pabloaul: https://github.com/pabloaul/apple-wireshark
- MagicPods Windows by steam3d: https://github.com/steam3d/MagicPods-Windows
- IntelBluetoothFirmware by OpenIntelWireless:
  https://github.com/OpenIntelWireless/IntelBluetoothFirmware
- BrcmPatchRAM by Acidanthera: https://github.com/acidanthera/BrcmPatchRAM
- Windows Driver Samples by Microsoft:
  https://github.com/microsoft/Windows-driver-samples

## Bundled binary utility

- **SoundVolumeView 2.53** by Nir Sofer (NirSoft). Freeware, redistributed
  unmodified with its original `readme.txt` and help file. SSAP3 uses
  `/SetSpatial` only to switch Windows Sonic off on the AirPods A2DP
  endpoint, so that spatial processing is not applied twice.
  https://www.nirsoft.net/utils/sound_volume_view.html
  - The executable is **not Authenticode-signed**. SSAP3 pins its hash and
    checks it at build time and before launching it:

    | File | SHA-256 |
    | --- | --- |
    | `SoundVolumeView.exe` | `B5AF5BD60F7A29AF8CB4D8A566382B90F0FE07CAC97228D218CB913F3382D647` |
    | `SoundVolumeView.chm` | `E6C46D1C8B88D48F5B9152EC74EE43ECA45CD9E0298D0B62FC7D812A4C2D3167` |
    | `readme.txt` | `8B587F0B2AD6F694197BFAC34CCF558D27851C64661E203AF3EEEC1B00A3C5CE` |
  - Upstream x64 archive SHA-256, as recorded by the upstream project:
    `ee2c45553fb9fb31b71db88b537c18e25c1a387a4a9009d081bdb38265c68e1f`

## Bundled system audio engine (opt-in)

- **Equalizer APO 1.4.2** by Jonas Thedering (GPL-2.0-or-later). SSAP3
  redistributes the unmodified x64 runtime files and includes the complete
  corresponding source archive under `third_party/EqualizerAPO/source`
  (SHA-256
  `BACD5C78BE71F3AAAB010684AB39F0E5C1BBB45FCD75CBB92C103A145A731DC4`).
  SSAP3 uses its VST host to run `SSAP3Spatializer.dll` on AirPods A2DP
  stereo endpoints, and only after the user opts in, because the host
  requires `DisableProtectedAudioDG` (see `SECURITY_AUDIT.md`, S-07).
  https://sourceforge.net/projects/equalizerapo/files/1.4.2/
- Instead of the bundled copy, the engine task can use an Equalizer APO that
  the user installed with the official installer:
  - file `EqualizerAPO-x64-1.4.2.exe`;
  - SHA-256 `7403BE7427BBE1936A40DDED082829B6E217FC4F5990FEE5CBA501F0AE055AFA`;
  - install it with **Check for updates automatically** unchecked.

  SSAP3 then hardens that install (`SECURITY_REPORT.md` S-23 to S-26).

## VST compatibility declarations

The minimal VST 2 ABI declarations used by `SSAP3Spatializer` are compatible
with the GPL-2.0-or-later VeSTige header distributed by LMMS and Equalizer
APO. No proprietary Steinberg SDK is included.

## Trademarks

AirPods, Apple and related marks belong to Apple Inc. SSAP3 is an
independent project, not affiliated with or endorsed by Apple. It refers to
Apple products only to describe compatibility.
