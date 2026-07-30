# Third-party references

SoundStage uses independently written diagnostic and policy code. Its AirPods
advertisement parser, AACP packet definitions, KMDF bridge protocol, and
Bluetooth profile transport are adapted from these GPL-3.0 projects:

- LibrePods by Kavish Devar — GPL-3.0
  https://github.com/kavishdevar/librepods
- LibrePods Windows by ivLis-Studio — GPL-3.0
  https://github.com/ivLis-Studio/librepods-windows
- kAirPods by can1357
  https://github.com/can1357/kAirPods

Other source-only references:

- apple-wireshark by pabloaul
  https://github.com/pabloaul/apple-wireshark
- MagicPods Windows by steam3d
  https://github.com/steam3d/MagicPods-Windows
- IntelBluetoothFirmware by OpenIntelWireless
  https://github.com/OpenIntelWireless/IntelBluetoothFirmware
- BrcmPatchRAM by Acidanthera
  https://github.com/acidanthera/BrcmPatchRAM
- Windows Driver Samples by Microsoft
  https://github.com/microsoft/Windows-driver-samples

Bundled binary utility:

- SoundVolumeView 2.53 by Nir Sofer — freeware, redistributed unmodified with
  its original `readme.txt` and help file. SoundStage uses `/SetSpatial` to switch
  Windows Sonic on the AirPods A2DP endpoint.
  https://www.nirsoft.net/utils/sound_volume_view.html
  SHA-256 (x64 archive):
  `ee2c45553fb9fb31b71db88b537c18e25c1a387a4a9009d081bdb38265c68e1f`

Bundled system audio engine:

- Equalizer APO 1.4.2 by Jonas Thedering — GPL-2.0-or-later. SoundStage
  redistributes the unmodified x64 runtime files and includes the complete
  corresponding source archive under `third_party/EqualizerAPO/source`.
  SoundStage uses its VST host to run `SoundStageSpatializer.dll` on AirPods A2DP
  Stereo endpoints.
  https://sourceforge.net/projects/equalizerapo/files/1.4.2/

VST compatibility declarations:

- The minimal VST 2 ABI declarations used by SoundStageSpatializer are compatible
  with the GPL-2.0-or-later VeSTige header distributed by LMMS and Equalizer
  APO. No proprietary Steinberg SDK is included.
