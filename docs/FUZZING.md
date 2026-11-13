# Fuzzing and randomized-input testing

## What exists today

`tests/SSAP3.Core.Tests/RandomizedRobustnessTests.cs` runs **seeded
randomized-input tests** as part of every `dotnet test`:

| Target | Inputs per run | Checked invariants |
| --- | --- | --- |
| `AacpFrame`, `ControlNotification`, `ConversationAwarenessLevel`, battery parser, head-tracking parser, legacy orientation decoder | 200 000 (random buffers of 0–600 bytes, plus 1–5 byte mutations of valid packets) | Never throws; accepts only bounded lengths and documented framing; percentages stay 0–100; decoded quaternions are finite |
| BLE advertisement parser (with and without a random decryption key) | 200 000 | Never throws; battery values stay bounded |
| Shared-state reader | 200 000 | Only defined enum values are accepted; sanitised stage values stay in range |
| `HeadPoseEstimator` | 50 000 hostile samples (NaN, ±Inf, huge values, time going backwards, gaps, random recenters) | Output stays a finite unit quaternion with finite Euler angles |

The spatial engine's C++ test feeds NaN, Inf, non-unit, stale and
out-of-range state records, and non-finite audio samples.

This is **randomized mutation testing, not coverage-guided fuzzing**. It finds
crashes and invariant violations, but it does not explore code paths
systematically.

## Planned

1. **SharpFuzz + libFuzzer** harnesses for the same managed parsers, with a
   seed corpus taken from the unit-test vectors, run nightly in CI.
2. **libFuzzer** (MSVC `/fsanitize=fuzzer`) on a native harness around
   `Ssap3ValidateState` and the DSP `Process()` path, together with
   AddressSanitizer (`/fsanitize=address`).
3. **Driver**:
   - Driver Verifier (standard settings, plus I/O verification and the
     DDI-compliance checks) on a development PC;
   - a user-mode IOCTL fuzzer that drives the five IOCTLs with malformed
     sizes, session IDs and concurrent close/read/send.
4. **SOFA loader**, once implemented: libFuzzer on the HDF5/netCDF reader
   boundary, with size and allocation limits enforced before parsing.
