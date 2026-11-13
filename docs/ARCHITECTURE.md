# SSAP3 architecture

SSAP3 is split into small boundaries so the experimental kernel transport,
protocol parsing, Windows integration, and real-time audio code can be tested
independently.

## Component map

| Component | Responsibility |
| --- | --- |
| `SSAP3.App` | WPF tray panel, user settings, mode state, animations, lifecycle |
| `SSAP3.Core` | AAP packets, BLE/AAP parsers, crypto helpers, profile policy |
| `SSAP3.Platform.Windows` | BLE watcher, remembered-device discovery, AAP/UARP service repair, audio endpoint control, driver client |
| `Ssap3Aap` | Apple AAP/UARP Bluetooth profile binding and bounded packet transport |
| `SSAP3Spatializer` | Real-time stereo-to-binaural rendering |
| `SSAP3.Diagnostics` | Windows/Bluetooth/audio reports and an explicit profile-repair command |
| `SSAP3.TransportProbe` | Low-level AAP transport diagnostics |
| `installer` | Two-stage install, endpoint-effect chaining, and reversible uninstall |

## Control path

1. The app locates a remembered AirPods Bluetooth address.
2. The user-mode client enumerates the SSAP3 device interface
   `{3E98D13A-B2A5-4C3D-AE1E-8DB4F3D8C1F0}`.
3. It checks the version and capability flags through
   `SSAP3_IOCTL_GET_BRIDGE_STATUS`.
4. It opens an AAP session for the selected address.
5. The KMDF driver submits Bluetooth Request Blocks through
   `GUID_BTHDDI_PROFILE_DRIVER_INTERFACE`.
6. AACP packets are sent and received through bounded 512-byte IOCTL buffers.

If step 2 finds no interface, the app verifies the running Code Integrity/Test
Mode state. With Test Mode active, it asks Windows to refresh the paired
AirPods AAP and UARP services using `BluetoothSetServiceState`, then retries
interface discovery. This addresses stacks that keep A2DP connected but lose
the Apple control PDO after reconnect.

The driver owns sessions per file object. Cleanup closes every session
belonging to a terminated client, so a crashed UI does not intentionally leave
an orphaned AAP channel.

### Driver scope

The INF binds to either of these Apple service PDOs:

`BTHENUM\{74ec2172-0bad-4d01-8f77-997b2be0722a}...`

`BTHENUM\{4715650b-5e9d-4ac2-b898-a4fc0aa5df78}...`

Known Apple product IDs are listed explicitly, followed by profile-level
fallback IDs. The driver does not replace:

- the Bluetooth USB adapter driver;
- Microsoft Bluetooth enumerators;
- A2DP/AVDTP audio;
- Hands-Free/HFP;
- AVRCP media controls;
- unrelated vendor or Apple services.

The UARP binding is a fallback parent for the same fixed AACP L2CAP PSM; the
driver does not implement firmware updates. This narrow service-level binding
is important for safe removal and adapter portability.

## AAP and AACP

`AacpPacketFactory` creates the handshake, feature, notification, noise-control,
connection-ownership, conversation-awareness, and motion-stream packets.

Noise control is encoded as AACP control identifier `0x0D`, with the supported
mode value. Incoming notifications are parsed rather than interpreted in the
kernel. Keeping protocol semantics in managed user mode makes malformed packet
handling easier to test and reduces kernel complexity.

Protocol layouts were adapted from the GPL LibrePods ecosystem; attribution is
recorded in `THIRD_PARTY.md`.

## Battery and status

SSAP3 has two telemetry paths:

1. Apple Bluetooth LE manufacturer advertisements provide model, connection,
   charging, in-ear, and battery values.
2. AACP component notifications provide byte-accurate left/right/case battery
   updates while the control channel is open.

Advertisements can be encrypted on newer firmware. The parser supports an
identity resolving key and AES block decryption when keys are available; it
falls back to unencrypted fields otherwise.

The BLE layer intentionally consumes observations and does not modify Windows
pairing records.

## Motion pipeline

1. SSAP3 requests the AirPods motion stream over AAP.
2. `HeadTrackingFrameParser` validates packet prefixes and extracts orientation
   and acceleration fields.
3. Initial frames establish a parser neutral reference.
4. The app collects a second short center sample set representing the user's
   current straight-ahead position.
5. Yaw uses wrap-safe angle deltas. Pitch uses a centered linear delta.
6. Adaptive smoothing uses a lower coefficient for small jitter and a higher
   coefficient for deliberate movement.
7. The resulting pose is written to
   `C:\ProgramData\SSAP3\head-tracking-state.bin`.

The state file is memory-mapped with shared read/write access. Sequence numbers
provide a small seqlock-style snapshot: odd means a write is in progress, even
means readers can validate a stable frame. The audio process rejects tracked
poses older than 1.2 seconds and falls back to centered rendering.

## Spatial audio pipeline

The installer attaches Equalizer APO to the AirPods A2DP Stereo render
endpoint and loads `SSAP3Spatializer.dll` at post-mix stage.

Processing modes:

- `Off`: copies both float channels unchanged;
- `Fixed`: renders two virtual front loudspeakers;
- `Tracked`: applies the same renderer with head-relative source azimuths.

The renderer is allocation-free in the audio callback and uses preallocated
ring buffers. Its cues include:

- interaural time delay;
- near/far-ear level difference;
- far-ear low-pass shadowing;
- fractional-delay interpolation;
- dense asymmetric early reflections;
- high- and low-limited room energy;
- a four-line damped feedback delay network;
- output headroom and hard safety limiting.

The tuned scene presents a detached stereo system in a compact treated room.
It is intentionally not a clone of Apple's personalized HRTF, which is not
available to Windows applications.

Windows Sonic is disabled only when it is actually enabled and SSAP3 is
selected, preventing double HRTF processing. ASIO/WASAPI exclusive clients may
bypass the endpoint effect.

## Endpoint-effect safety

Audio endpoint effects are registry-based and easy to damage if overwritten
blindly. The spatial installer:

1. discovers AirPods A2DP render endpoints;
2. saves original SFX/MFX/EFX registrations under
   `HKLM\SOFTWARE\SSAP3\SpatialAudio\OriginalEffects`;
3. rejects a recursive Equalizer APO backup;
4. configures Equalizer APO to chain the original effect;
5. attaches the SSAP3 post-mix renderer;
6. removes a cached disable-enhancements property while Windows Audio is
   stopped;
7. restarts Windows Audio.

The uninstaller restores the exact saved registrations.

## Installer and recovery state

`C:\ProgramData\SSAP3\install-state.json` records:

- certificate thumbprint;
- whether Test Mode was already active;
- BCD backup path;
- installed driver INF;
- preparation and installation timestamps.

This prevents uninstall from removing an unrelated certificate or disabling a
Test Mode configuration the user already had.

## Threading and process behavior

- A named mutex enforces one SSAP3 UI instance.
- A named event asks the existing instance to reveal itself.
- Tray events are marshaled onto the WPF dispatcher.
- A four-second timer reconciles the classic paired-device state and retries a
  missing control path with throttling.
- A cancellation token owns the motion read loop.
- A semaphore serializes driver-channel operations.
- The spatializer reads shared state without calling managed code or allocating
  in the render callback.

## Security boundaries

The user-mode device interface is accessible to system, administrators, and
interactive users. Every IOCTL validates structure size, session ownership,
packet length, and maximum buffer size. The protocol does not expose arbitrary
kernel pointers, physical memory, or generic Bluetooth control operations.

This does not make a test-signed kernel driver production-safe. Microsoft
production signing and wider hardware validation remain release requirements.
