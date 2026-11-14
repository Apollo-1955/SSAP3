# SSAP3 technical report: audio engine, motion acquisition and tracking

Status as of 2026-11-13, branch `development/ssap3`. This report separates
what was **verified on real hardware**, what was **tested in software**, and
what is **blocked**. Simulated results are never reported as real tracking.

## 1. Summary

| Area | State |
| --- | --- |
| Fixed soundstage | **Working on the development PC.** Verified by a listening test with AirPods Pro 3 through the Equalizer APO host. |
| Tracked mode, simulated motion | **Working in software, end to end.** The simulator feeds the real decoder, estimator and tracking-state manager. The UI labels it **SIMULATED**. |
| Tracked mode, AirPods sensors over SSAP3's driver | **Implemented, blocked on Microsoft signing.** The driver package is submission-ready (`driver/SIGNING.md`). The motion decoder follows the published protocol but is unverified on AirPods Pro 3. |
| Tracked mode, companion device | **Implemented on Windows** (authenticated, encrypted, tested over loopback). No real companion app exists yet. One hardware question is open (§3.3). |
| Pitch and roll | **Implemented** with a guided nod calibration. Tested with simulated mountings. |
| HRTF rendering | **Parametric binaural model** (ITD, ILD, head shadow, rear and elevation cues, room). It renders full 3-D head orientation. **Measured HRIR/SOFA convolution is not implemented** (§5). |

## 2. Motion access on Windows (measured on the development PC)

`SSAP3-TransportProbe --investigate` runs these checks read-only. It sends
nothing to the AirPods, and its output is redacted.

| Check | Result |
| --- | --- |
| User-mode L2CAP socket (`AF_BTH`, `BTHPROTO_L2CAP`) | **Refused**: Winsock error 10044, socket type not supported |
| AirPods SDP records (8) | Motion is only on **AAP, L2CAP PSM `0x1001`**. Other services: GATT over BR/EDR (ATT), UARP firmware update, Hands-Free (RFCOMM channel 7), AVRCP, A2DP sink. **No RFCOMM, LE or HID path to motion.** |
| RFCOMM services (uncached) | none returned |
| Bluetooth LE | the AirPods have **no paired LE identity** on Windows |
| HID interfaces from Bluetooth or Apple | none |
| Windows sensor platform (accelerometer, gyrometer, orientation) | **0 sensors** |
| AAP service node | a raw PDO (`RawDeviceOK`) with no driver. Its device interface **cannot be opened** (error 50, `ERROR_NOT_SUPPORTED`) |

**Conclusion.** No documented user-mode interface carries the motion data.
L2CAP PSM `0x1001` needs a kernel profile driver: SSAP3's own driver (needs
Microsoft's signature) or the third-party MagicAAP driver. MagicAAP's own
users report it needs **Test Mode** because it is not signed, so SSAP3 does
not use it.

## 3. Driver-independent architectures

### 3.1 Evaluated

| Option | Outcome |
| --- | --- |
| Windows Bluetooth, BLE/GATT, HID, sensor APIs | Not viable (§2) |
| Existing signed driver | None found that exposes AAP to user mode. MagicAAP requires Test Mode. |
| User-mode Bluetooth stack on a radio rebound to Microsoft's WinUSB (internal Intel AX201, or a dedicated USB dongle) | Technically possible without Test Mode. But Windows Bluetooth is off while it runs, the AirPods must be re-paired, all audio must be routed through SSAP3, and it adds a large attack surface that parses untrusted radio input. **Not implemented; the owner chose not to take this route.** |
| Companion device | **Implemented** (§3.2) |
| Webcam head tracking | Offered and declined by the owner (not AirPods sensors) |

### 3.2 Companion implementation

The protocol is documented in `docs/COMPANION_PROTOCOL.md`, and the code is in
`src/SSAP3.Core/Motion/Companion`.

- SSAP3 connects out, only to local or private addresses.
- Both sides prove the pairing key with HMAC-SHA256. Records are AES-256-GCM
  with an HKDF session key and strictly increasing counters.
- Records are fixed-size and validated, and the sender is rate-limited.
- The pairing key is stored with DPAPI.
- Companion clock offsets are mapped by tracking the fastest packet, so
  network jitter does not reach the estimator.
- The source reconnects with exponential backoff.
- `SSAP3-TransportProbe --companion-sim` is a working simulated companion
  (localhost only, always flagged simulated).

Two companion kinds are defined:

- **Head orientation:** for example Apple's documented `CMHeadphoneMotionManager`
  on iOS or macOS.
- **AirPods attitude:** for a Linux bridge using BlueZ's user-mode L2CAP,
  decoded by SSAP3's own decoder.

### 3.3 Open hardware question

AirPods accept one AAP control connection. Windows opens none without SSAP3's
driver, so a companion can hold it. **Unverified:** whether AirPods Pro 3 stay
connected to the companion while Windows streams A2DP audio to them. This
decides whether the companion route can give head tracking during PC
playback.

## 4. Orientation processing

| Component | What it does | Tests |
| --- | --- | --- |
| `SensorDataWx` and `ProtobufReader` | A total, bounded protobuf parser for the AAP sensor channel | byte-exact against 6 published commands, malformed inputs, 200 000 randomized inputs |
| `DeviceMotionReport` | Q15 attitude with the implied `w`, rotation rate, gravity | encoding, rejection of invalid vectors |
| `DeviceMotionOrientationDecoder` | Yaw = twist about gravity (independent of the ear fit). Pitch and roll after the nod calibration, with the axis saved in the bud's frame and rejected if the fit changed. Handles the sign ambiguity and re-anchors on discontinuities. | arbitrary mountings, nodding while turning, sign flips, bud switches, clock resets |
| `HeadPoseEstimator` (existing) | Reference calibration and recentering, timestamped One-Euro SLERP smoothing, outlier rejection, hold → lost → return to centre, resume blending, drift correction | existing tests and 50 000 hostile samples |
| `TrackingStatusEvaluator` | One explicit state: Off / Fixed / Unavailable (with reason) / Starting / Live / Degraded / Lost / Recovering, plus a simulated flag and a real-tracking flag | state tests and a full simulated dropout cycle |
| Audio engine (existing C++) | Validated shared state; block-wise SLERP toward the target; ease to centre when stale; per-sample parameter interpolation; crossfaded mode changes | NaN, Inf, stale and click tests in CI |

No prediction is applied. With 50 Hz input, light One-Euro smoothing and the
engine's short time constant, extrapolating would mainly amplify noise.
Measured input-to-render latency on hardware will decide whether a bounded
predictor is worth adding.

## 5. Spatial audio engine

The renderer rotates the virtual speakers into the head frame using the full
head quaternion (yaw, pitch, roll). It then applies:

- an interaural time difference;
- level differences and head-shadow filtering;
- a rear cue;
- an elevation brightness cue;
- early reflections and a short late room.

It is a **parametric binaural model, not measured-HRTF convolution**.
Remaining work: HRIR convolution with crossfaded filter updates, and a
fuzz-tested SOFA (HDF5) loader or a pre-converted HRIR table. That needs a
measured dataset with a compatible licence, which must be downloaded
separately.

## 6. Security

The details are in `SECURITY_REPORT.md`. Points specific to this work:

- **No system security setting is changed.** There is no Test Mode, the
  driver is not signed by anyone but Microsoft, and the Equalizer APO
  hardening stays as before.
- **Driver protocol v3.** Inbound MTU is 2582 with a bounded queue. Opcode
  `0x17` only accepts sensor start/stop with a bounded interval.
- **New network surface:** the optional, outbound-only, authenticated and
  encrypted companion connection.

## 7. Testing

- **xUnit: 186 tests**, all passing. They cover:
  - protocol parsing and the command builder;
  - the decoder and calibration;
  - the simulator pipeline and dropout and recovery states;
  - the companion handshake and records, including tamper, replay, wrong-key
    and address-policy cases;
  - loopback TCP sessions;
  - the AirPods-sensor provider;
  - randomized robustness.
- **CI** runs these plus the C++ engine tests, the driver build and
  pre-submission checks, CodeQL (C#, C/C++ and Microsoft's driver suites)
  and gitleaks.

**Not yet executed:**

- SSAP3's uninstaller on this PC;
- any real AirPods motion stream (driver unsigned, no companion app);
- audio-engine latency measurement.

## 8. What each remaining step needs

| Needs | Item |
| --- | --- |
| The owner's organization and an EV certificate | Microsoft attestation signing (checklist in `driver/SIGNING.md`) |
| Real AirPods Pro 3 | Decoder verification (`SSAP3-TransportProbe <address> Motion`); yaw sign; nod calibration on a real fit |
| A companion app | iOS/macOS `CMHeadphoneMotionManager` sender, or a Linux BlueZ bridge; then the dual-connection test (§3.3) |
| Engineering time | Measured-HRTF convolution (§5) |
