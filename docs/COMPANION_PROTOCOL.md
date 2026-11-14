# SSAP3 companion motion protocol (version 1)

A **companion** is a device that can read AirPods motion and forward it to
SSAP3. It exists because Windows gives normal apps no way to reach the AirPods'
motion channel without a Microsoft-signed kernel driver (see
`TECHNICAL_REPORT.md`, *Motion access on Windows*). The reference
implementation is `src/SSAP3.Core/Motion/Companion/CompanionProtocol.cs`. Run a
working simulated companion with:

```powershell
SSAP3-TransportProbe --companion-sim [port] [seconds]
```

## Security properties

| Property | How |
| --- | --- |
| No inbound port on Windows | SSAP3 is always the **client**. The companion listens; SSAP3 connects out, so Windows needs no firewall rule. |
| Local only | SSAP3 refuses any address that is not loopback, RFC 1918, link-local, 100.64.0.0/10 (Tailscale), or IPv6 unique-local or link-local. |
| Mutual authentication | Each side proves knowledge of the 256-bit pairing key with HMAC-SHA256 over both fresh 32-byte nonces. Comparisons run in constant time. |
| Confidentiality and integrity | Every record is AES-256-GCM encrypted. The session key is HKDF-SHA256 over the pairing key and both nonces. |
| No replay | Fresh nonces in every session; record counters must strictly increase. A violation closes the connection. |
| Bounded input | Fixed 62-byte records; payload ranges are validated; at most 400 records per second. |
| Honest labelling | A companion **must** set the Simulated flag on generated data. SSAP3 then shows tracking as simulated and never as real. |
| Key storage | Windows DPAPI (current user, SSAP3-specific entropy), in `%LocalAppData%\SSAP3\companion-pairing.bin`. |

## Pairing

In SSAP3, go to Settings > Head tracking > Companion > **Create pairing code**.
The code is the 32-byte key in RFC 4648 Base32 without padding: 52 characters
in groups of four, separated by `-`. Parsing ignores case, spaces and `-`. A new
code replaces the old pairing.

## Messages

Everything is little-endian. `K` is the pairing key.

| Step | Size | Content |
| --- | --- | --- |
| Client hello (SSAP3 → companion) | 40 B | `"S3MC"`, version `1`, 3 zero bytes, client nonce (32 B) |
| Server hello (companion → SSAP3) | 72 B | `"S3MC"`, version `1`, 3 zero bytes, server nonce (32 B), proof (32 B) |
| Client proof (SSAP3 → companion) | 32 B | proof (32 B) |
| Records (companion → SSAP3) | 62 B each | sent until either side closes |

The proofs and keys are computed as follows:

- **Server proof** = `HMAC-SHA256(K, "SSAP3 companion server proof v1" ‖ client nonce ‖ server nonce)`.
- **Client proof** = `HMAC-SHA256(K, "SSAP3 companion client proof v1" ‖ server nonce ‖ client nonce)`.
  The companion must check it and close the connection if it does not match.
- **Session key** = `HKDF-SHA256(ikm = K, salt = client nonce ‖ server nonce,
  info = "SSAP3 companion motion key v1", length = 32)`.

Each record is laid out like this:

| Bytes | Field |
| --- | --- |
| 0–1 | length = 60 |
| 2–9 | counter (u64, starts at 1, strictly increasing) |
| 10–45 | AES-256-GCM ciphertext of the 36-byte payload |
| 46–61 | GCM tag |

The GCM nonce is `"S3M1" ‖ counter` (12 bytes). The associated data is
bytes 0–9 of the record.

Each payload (36 bytes) is laid out like this:

| Bytes | Field |
| --- | --- |
| 0 | kind: `1` = head orientation, `2` = AirPods attitude |
| 1 | flags: bit 0 = includes pitch/roll, bit 1 = **simulated** |
| 2–3 | zero |
| 4–11 | timestamp, microseconds, on the companion's own monotonic clock |
| 12–27 | quaternion x, y, z, w (float32, unit length ±2 %) |
| 28–31 | rotation-rate magnitude, degrees per second (0–10 000) |
| 32–35 | zero |

## Which kind to send

- **Kind 1, head orientation**: the head's rotation in SSAP3's frame
  (+X right, +Y forward, +Z up). Yaw is positive turning **left**, pitch is
  positive looking **up**, roll is positive with the **right** ear down. The
  reference can be arbitrary, because SSAP3 recenters. Use this when the
  companion's API already gives head orientation.
  - **iOS 14+ / macOS 14+:** `CMHeadphoneMotionManager` (Apple's documented
    API) provides the AirPods' head attitude. Convert its quaternion to
    SSAP3's axes and set the pitch/roll flag. **Not built or verified yet;**
    confirm the axis signs with SSAP3's live yaw readout.
- **Kind 2, AirPods attitude**: the raw sensor-to-world attitude from the AAP
  SensorDataWX device-motion report (world Z vertical). SSAP3 decodes it with
  the same decoder as its own driver path, including the nod calibration. Use
  this for a bridge that reads the AAP channel directly. Linux with BlueZ
  allows user-mode L2CAP sockets (PSM `0x1001`), as freaktopus/auricle and
  starman-tech/mousepods do.

## The hardware question to answer first

AirPods accept **one** AAP control connection at a time. Windows opens none
without SSAP3's driver, so a companion can hold it. **Unverified:** whether
AirPods Pro 3 keep a connection to the companion while Windows plays audio to
them over A2DP. Test with the companion connected and audio playing from
Windows before building further.
