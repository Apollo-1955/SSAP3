# SSAP3 hardware compatibility

This report separates **observed** facts (measured on real hardware, with
the date), **documented** facts (from public protocol research, with the
source), and **inferred** or **unverified** items. A feature counts as
working on AirPods Pro 3 only after an observed hardware test.

## Development PC (observed 2026-11-13)

- Windows 11 Home, build 26200, x64.
- Intel Wireless Bluetooth adapter (`LOCALMFG&0002`).
- AirPods Pro 3 paired with Windows.

Observations come from a read-only Plug and Play query. No AACP traffic was
exchanged, because no SSAP3 driver is installed on this PC.

| Item | Observation |
| --- | --- |
| Bluetooth Device ID | Vendor `0x004C` (Apple, source `0x0001`), product **`0x2027`** |
| A2DP | `BTHENUM\{0000110b-…}_VID&0001004c_PID&2027`, service `BthA2dp`, healthy |
| HFP | `BthHFEnum` service PDO present (Hands-Free AG) |
| AVRCP | Two transport PDOs (`0000110c`, `0000110e`) |
| **AAP Server** | PDO for `{74ec2172-0bad-4d01-8f77-997b2be0722a}` present, status OK, **no function driver bound** |
| UARP | PDO for `{4715650b-5e9d-4ac2-b898-a4fc0aa5df78}` present, no driver |
| AAP hardware IDs | `BTHENUM\{74ec2172-…}_VID&0001004c_PID&2027`, `BTHENUM\{74ec2172-…}_LOCALMFG&0002` |
| AAP compatible IDs | `BTHENUM\{74ec2172-…}`, `BTHENUM\{74ec2172-…}_GENERIC` |
| Render endpoints | A2DP Stereo and Hands-Free endpoints exist. Both were not active at query time, because the AirPods were in the case. |

What follows from this:

1. Windows 11 build 26200 exposes the Apple AAP service for AirPods Pro 3 as a
   driverless PDO, which is where an AACP profile driver attaches.
2. The inherited INF had no `PID&2027` line. It would still have bound,
   through its compatible-ID fallback lines. SSAP3 adds an explicit
   `PID&2027` line.
3. The advertisement model ID for Pro 3 is **inferred** to be `0x2720` in
   the parser's byte order. Existing table entries map Bluetooth product IDs
   byte-swapped: AirPods Pro 2 (USB-C) is PID `0x2024` → `0x2420`, and
   AirPods 4 (ANC) is PID `0x201B` → `0x1B20`. This has not yet been seen
   in a captured advertisement.

### Later checks the same day

- **Bluetooth radio was off.** The Windows radio API reported the Bluetooth
  radio as Off (Wi-Fi On). With the radio off, the BLE advertisement watcher
  fails with `0x800710DF`, so no advertisement could be captured, and the
  AirPods Pro 3 advertisement model ID is still **inferred**. SSAP3 now shows
  "Bluetooth is off" instead of crashing, and retries every 15 s. SSAP3
  never turns the radio on itself.
- **App on real hardware.** The SSAP3 Control Center launched on Windows 11
  build 26200. The high-resolution waitable timer and `QueryInterruptTime`
  kernel-timestamp domain were available. The panel showed Bluetooth off,
  listening modes unavailable (no AAP driver), and the spatial engine not
  installed. Those were the true states.
- **Platform security baseline**, from `Test-Ssap3SystemState.ps1 -Verify`:
  Secure Boot on, Memory Integrity (HVCI) on, Defender real-time protection
  on. Any production AAP driver must therefore be HVCI-compatible and
  Microsoft-signed.

To capture the AirPods Pro 3 advertisement, turn Bluetooth on, open the case
near the PC, and run `SSAP3-TransportProbe --ble 30`. The output redacts
addresses.

## Feature matrix for AirPods Pro 3

| Feature | Mechanism | Needs SSAP3 driver | Protocol source | Pro 3 status |
| --- | --- | --- | --- | --- |
| Pairing and A2DP stereo | Windows inbox | No | — | **Observed** (paired, endpoint present) |
| AAP PDO exposure | Windows BTHENUM | — | — | **Observed** |
| Battery, in-ear, lid (BLE advertisement) | WinRT `BluetoothLEAdvertisementWatcher` | No | LibrePods (Windows port parser) | Unverified on Pro 3; model ID inferred |
| AACP control channel (PSM `0x1001`) | KMDF profile driver | **Yes** | LibrePods `docs/AAP Definitions.md` | Blocked: no production-signed driver |
| Battery notifications (`0x04`) | AACP | Yes | LibrePods docs (Pro 2 USB-C) | Unverified |
| Listening mode Off/ANC/Transparency/Adaptive (`0x0D` = 1/2/3/4) | AACP | Yes | LibrePods docs and `linux/airpods_packets.h` | Unverified |
| Listening-mode acknowledgement (echo of `0x0D`) | AACP | Yes | LibrePods docs | Unverified |
| Conversational awareness toggle (`0x28`) | AACP | Yes | LibrePods docs | Unverified |
| Speaking-level notifications (`0x4B`) | AACP | Yes | LibrePods docs | Unverified |
| Head-tracking stream (`0x17`, RTBuddy SensorDataWX, protobuf) | AACP | Yes | See *Motion protocol* below | Unverified on Pro 3. Decoder follows the layout confirmed on AirPods 4, AirPods Pro 2 and AirPods Max 2 by independent projects |
| Fixed binaural soundstage | Equalizer APO + SSAP3 DSP | No (needs `DisableProtectedAudioDG` opt-in) | — | **Working** on this PC (listening test, 2026-11-13) |
| Head-tracked soundstage | DSP + motion | Yes | — | Blocked on the driver signature |

## Motion protocol (as implemented, 2026-11-13)

Community reverse engineering now documents the stream well enough to decode
it properly. SSAP3 uses the facts only; no third-party code is included.
Sources:

- LibrePods `docs/AAP Definitions.md` and PR #887 (layout of the 58-byte
  block);
- pabloaul/apple-wireshark (RTBuddy protobuf grammar);
- FIocker/aap-head-tracker (service IDs; quaternion validated against gravity
  on AirPods Max 2);
- ShiratsuYudachi/airpods-head-track (protobuf location; tested on AirPods 4);
- starman-tech/mousepods (units; MTU findings);
- freaktopus/auricle (AirPods Pro 2 on Linux).

Packet framing:

- **Header:** `04 00 04 00 17 00`, then the SensorDataWX descriptor
  `00 00 10 00`, then a u16 body length.
- **Body:** protobuf. Field 1 is a request or sequence id (varint). Field 7
  is `{1: service id, 3: report}`. Field 5 is `{1: service id, 2: descriptor}`,
  which announces a service.
- **Packet length:** the sequence varint is 1 or 2 bytes, so motion packets
  are **80 or 81 bytes**. LibrePods' fixed offset 43 is right only for the
  81-byte form. SSAP3 used to read fixed offsets; it now parses the protobuf.

Report layout:

| Report bytes | Field |
| --- | --- |
| `[0]` | `01` |
| `[1:9]` | u64 timestamp (ns) |
| `[20:26]` | sensor-to-world attitude quaternion x, y, z in Q15. `w = sqrt(1 − |v|²)`; world Z is vertical |
| `[26:32]` | rotation rate, about 0.122 °/s per count |
| `[38:44]` | user acceleration, about 1000 per g |
| `[44:50]` | gravity, about 1000 per g |

Decoding and stream control:

- **Yaw** is the twist of `q · conj(q_start)` about world Z. It does not
  depend on how the bud sits in the ear. Pitch and roll are not decoded yet.
- **Start/stop command:**
  `08 <id> 42 0B 08 <service> 10 02 1A 05 01 <u32 interval µs>`, with
  interval 0 to stop. SSAP3 asks for 50 Hz.
- **Services tried, in order:**
  1. a service announced as `devmotion6` (AirPods Max 2 firmware uses
     dynamic id 6);
  2. 16 (DEVMOTION6);
  3. 14 (LibrePods' original packet).
- **MTU:** AirPods announce only the sensor descriptors that fit the
  receiver's L2CAP MTU. iOS uses 2582, and AirPods 3 hide `devmotion6` at 672.
  SSAP3's driver now offers 2582 (it was 512).
- **Not supported:** AirPods 3 (non-Pro) use a different channel (RemoteHID,
  descriptor `0x800`).

## Why the AAP features are blocked on this PC

Windows has no supported user-mode API for classic L2CAP channels, so AACP
needs a kernel profile driver. On a normal Windows 11 PC with Secure Boot
on, a kernel driver loads only if Microsoft has signed it, either through
attestation signing or WHQL. A locally generated certificate is not enough.
SSAP3 will not ask users to turn on Test Mode or turn off Secure Boot, so
until the driver is production-signed, the AAP features show as
**Unavailable — driver not installed** in the app.

To validate the AAP features on real hardware before production signing, use
one of these:

- a **separate development PC** where the developer chooses to enable test
  signing, using `tools/dev/` (see `docs/DEVELOPMENT.md`); or
- a production-signed preview driver, once attestation signing is set up
  (see `IMPLEMENTATION_ROADMAP.md`, Milestone 11).

## Hardware validation procedure (for when a signed or dev driver is available)

1. `SSAP3-TransportProbe <address>` checks that the driver interface opens.
2. `SSAP3-TransportProbe <address> Transparency` sends a mode change and
   prints the echo. Pass when the response contains
   `0400040009000D03000000`.
3. `SSAP3-TransportProbe <address> Motion` runs the same start sequence as
   the app for 20 seconds. It prints any service announcements, the service
   that streams, and every tenth decoded report: yaw, rotation rate, gravity
   magnitude, raw Q15 vector and re-anchor count. Pass when all of these hold:
   - about 50 reports per second arrive from one service;
   - packets are 80 or 81 bytes;
   - `|g|` is close to 1.00 at rest;
   - yaw rises as you turn **left** and falls as you turn **right**. If it is
     reversed, record that and use Settings > Head tracking > **Invert yaw**;
   - re-anchors stay at 0 while you just move your head.
4. If no reports arrive, note the announcements printed and the largest
   packet size. A missing `devmotion6` announcement with small packets points
   to the MTU.
5. Record the firmware version, adapter, Windows build, and the probe output
   in a private note. Do not commit Bluetooth addresses.

## Previously reported upstream (not re-verified by SSAP3)

- AirPods 4 (ANC), PID `0x201B`, on Windows 10 22H2 build 19045 with a TP-Link
  Bluetooth 5.4 adapter (upstream README).
- AAP/UARP profile recovery on Windows 11 26H1 with a BARROT Bluetooth 5.4
  adapter (upstream README).
