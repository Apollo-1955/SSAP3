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
| HRTF rendering | **Measured-HRIR convolution** (MIT KEMAR dummy head), interpolated across 710 directions and crossfaded on every update, plus a compact room. It renders full 3-D head orientation. Tested in CI (§5). **Not yet heard on AirPods:** the new engine build must be reinstalled first. SOFA import is not implemented. |

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
| Audio engine (C++) | Validated shared state; block-wise SLERP toward the target; ease to centre when stale; HRIR filters rebuilt per block while the head moves, with an output crossfade; crossfaded mode changes | NaN, Inf, stale, HRIR cue and click tests in CI (§5) |

No prediction is applied. With 50 Hz input, light One-Euro smoothing and the
engine's short time constant, extrapolating would mainly amplify noise.
Measured input-to-render latency on hardware will decide whether a bounded
predictor is worth adding.

## 5. Spatial audio engine

The renderer rotates the two virtual speakers into the head frame with the
full head quaternion (yaw, pitch, roll). It convolves each speaker with
**measured head-related impulse responses** for its head-relative direction,
then adds early reflections and a short late room. This replaces the earlier
parametric model (ITD, level and low-pass approximations).

### 5.1 Dataset and offline conversion

- **Data:** MIT KEMAR, diffuse-field equalized set (Gardner and Martin, MIT
  Media Lab, 1994; citation and terms in `THIRD_PARTY.md`). It has 710
  directions from -40° to +90° elevation, measured on a dummy head at
  44.1 kHz: 368 on the right half plus their mirror images.
- **Conversion** (`tools/hrtf/HrirTableBuilder`) runs at build time on a
  developer PC, never in `audiodg.exe`. It:
  - validates every file;
  - confirms from the data which side is the right ear;
  - aligns each response to its onset and stores the onset separately;
  - truncates to 96 taps (99.02 % of the energy kept at worst);
  - normalises to diffuse-field unit power;
  - writes `ssap3_hrir_data.h`, recording the archive's SHA-256.
- **In the engine:** the table is compiled in. `static_assert`s check its
  layout, so a malformed table fails the build.

### 5.2 Rendering (audio thread: no allocation, locks or I/O)

1. **Lookup.** Bilinear weights over the measured grid: the two nearest
   elevation rings, and the two nearest azimuths on each. That gives up to
   four measurements.
   - The left half is the mirror image with the ears swapped. KEMAR's median
     plane is identical for both ears, so there is no seam at 0° or 180°.
   - The 50° ring is measured every 8°, so its right half stops at 176°.
     Between 176° and 184° the lookup blends the 176° response with its
     mirror image; at 180° each counts half.
   - A seam probe over the whole sphere (every elevation, both ears, a
     0.01° step every 0.25°) changes a filter by at most 0.8 %.
   - Below -40° there are no measurements; those directions use the -40°
     ring.
2. **Blend.** The four onset-aligned responses are blended, and their onsets
   (the ITD) are blended separately. This avoids the comb filtering that
   blending unaligned responses causes.
3. **Resample to the device rate.** A Blackman-windowed sinc with 16 zero
   crossings per side, tabulated at 128 phases and interpolated.
   - The same kernel applies the fractional part of the onset, so sub-sample
     ITD changes carry no interpolation loss. (A cubic interpolator was tried
     first; it lost up to 2.5 dB at 15 kHz, varying with the fraction.)
   - At 44.1 kHz the measurements are reproduced exactly.
   - Up to 235 kHz the whole response fits in 512 taps. Above that it is
     faded out after about 1.3 ms (at 384 kHz), which costs some bass (§5.3).
4. **Update without clicks.** Filters are rebuilt only when a speaker's
   head-relative direction moves more than 0.02° from the direction they
   were built for. The new set goes into a second bank, and that block's
   output crossfades linearly from the old filters to the new ones. Head
   turns, recentring glides and stage changes therefore never step the
   output.
5. **Level.** A fixed trim of 0.40 keeps the replaced renderer's loudness
   and low-frequency headroom. At ±30° it gives a coherent near+far gain of
   0.83 at 500 Hz (previously 0.82) and a white-noise power of 0.36 per ear
   (previously 0.37). Distance gain, room, intensity, the 25 ms mode
   crossfade, input sanitising, the output limiter and the bit-exact Off
   path are unchanged.

Cost at 48 kHz: four 106-tap FIRs per sample (eight during a crossfade),
SSE, plus about 10 000 multiply-adds per filter rebuild.

### 5.3 Measured cues

These are interaural level difference (ILD, right minus left) and time
difference (ITD, onset at 15 % of peak) of the rendered responses, at
48 kHz. The values come from the design check, a C# port of the same
algorithm run on the generated table. CI asserts ranges around them in
`spatializer_smoke.cpp`.

| Head-relative direction | ILD | ITD (right ear leads) |
| --- | --- | --- |
| 0° (ahead) | 0.00 dB | 0.000 ms |
| +30° | +9.5 dB | +0.229 ms |
| +90° | +14.9 dB | +0.688 ms |
| +150° | +6.8 dB | +0.271 ms |
| 180° (behind) | 0.00 dB | 0.000 ms |

- The left side mirrors these exactly.
- The front (0°) is about 4.9 dB stronger than the back (180°).
- At 44.1, 96 and 192 kHz the cues agree with 48 kHz at every direction
  above:
  - ILD within 0.01 dB;
  - ITD within 0.022 ms, which is one sample of the onset measure;
  - energy (corrected for the rate) within 0.2 %.
- At 384 kHz the faded tail costs about 3 dB at 200 Hz.
- At 8 and 16 kHz (hands-free profile rates) the band-limited responses
  keep the ITD, but the ILD at 30° and 90° shrinks to 6.6–9.5 dB because the
  high frequencies are gone.

### 5.4 Limitations

- **One generic head.** KEMAR is a dummy head. Front/back and elevation cues
  vary between listeners, and nothing here is personalised. It has nothing
  to do with Apple's Personalized Spatial Audio.
- **Ambiguous far-ear onsets.** The far-ear onset of a few directions near
  90° is ambiguous in the data: it jumps from 32 to 28 samples between 90°
  and 95°. Blends there are slightly coloured. The transition is continuous:
  a 0.01° step changes the filter by at most 0.5 %.
- **Elevation.** No data below -40°.
- **SOFA import is not implemented.** A different dataset needs the offline
  converter extended. Any SOFA/HDF5 parser belongs there (ADR-5), never in
  the engine.
- **Not yet heard on AirPods.** The installed engine on the development PC is
  still the parametric build until the new DLL is installed.

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
| Reinstalling the new build | Listening test of the HRIR renderer on AirPods Pro 3 (§5.4) |
| Engineering time | SOFA import in the offline converter, for other or personalised datasets (§5.4) |
