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
| Head-tracking stream (`0x17`, opaque "orientation 1/2/3" at offsets 43/45/47) | AACP | Yes | LibrePods docs, which give no value encoding | Unverified. Field semantics unknown; **not a documented quaternion** |
| Fixed binaural soundstage | Equalizer APO + SSAP3 DSP | No (needs `DisableProtectedAudioDG` opt-in) | — | Not installed on this PC |
| Head-tracked soundstage | DSP + motion | Yes | — | Blocked on the driver |

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
3. `SSAP3-TransportProbe <address> Motion` streams motion. Pass when you see
   more than 50 head-tracking frames per second while moving your head, with
   orientation fields that change monotonically as you turn left and right.
4. Record the firmware version, adapter, Windows build, and the raw capture
   in a private note. Do not commit Bluetooth addresses.

## Previously reported upstream (not re-verified by SSAP3)

- AirPods 4 (ANC), PID `0x201B`, on Windows 10 22H2 build 19045 with a TP-Link
  Bluetooth 5.4 adapter (upstream README).
- AAP/UARP profile recovery on Windows 11 26H1 with a BARROT Bluetooth 5.4
  adapter (upstream README).
