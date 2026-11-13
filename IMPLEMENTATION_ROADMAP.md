# SSAP3 implementation roadmap

Companion to `ARCHITECTURE_AUDIT.md` and `SECURITY_AUDIT.md`. Live
progress is tracked in `DEVELOPMENT_STATUS.md`.

## Architecture decisions

**ADR-1 — Keep a minimal kernel driver, but only ship it production-signed.**
AACP is reachable only over a classic L2CAP channel, and Windows has no
user-mode API for that. The KMDF profile driver therefore stays, kept as
small as possible:

- no protocol parsing, no generic BRB pass-through, and only five IOCTLs;
- hardened against the problems in S-05, S-06, S-08 and S-09.

The production installer installs the driver only when its catalog
validates with a production signature. Otherwise the app runs in the
driverless tier and says why.

**ADR-2 — Never weaken the platform in production.** The production
installer and app must not:

- enable Test Mode or write to BCD;
- suspend BitLocker;
- import trust anchors;
- disable Memory Integrity or Defender.

The development tier (test-signed driver on a developer's own machine) lives
in `tools/dev/`. It is opt-in, carries clear labels, and the installer never
calls it.

**ADR-3 — System-wide audio: explicit opt-in Equalizer APO host now; research
signed alternatives.** The only way to process every shared-mode stream today
without a signed audio driver is an endpoint APO. Equalizer APO requires
`DisableProtectedAudioDG=1`, which turns off the APO signature check for the
whole system. SSAP3 therefore:

- offers it as an opt-in "System-wide engine" with consent text;
- records the prior value, the endpoint FX values, and the key owner/DACL
  in the install manifest;
- restores them exactly on uninstall.

Two alternatives are research items with unverified feasibility:

- a Microsoft-signed SSAP3 APO delivered through an extension INF for
  Bluetooth A2DP endpoints;
- a driverless process-loopback router (capture all processes except SSAP3,
  render binaural to the AirPods; this needs playback routed to a non-AirPods
  sink, and its latency must be measured).

**ADR-4 — Motion pipeline with explicit uncertainty.** The stages are:

1. A per-model `IOrientationDecoder` turns raw AACP frames into an
   `OrientationSample`: a unit quaternion plus a monotonic timestamp.
2. `HeadPoseEstimator` applies timestamp-aware SLERP smoothing, recenter
   calibration, outlier and rate limiting, and stale detection with
   degraded and recovering states.
3. A v2 shared-state record (quaternion + yaw/pitch/roll + tracking state +
   stage parameters) carries the result.
4. The DSP consumes it.

The inherited LibrePods heuristic is kept as the "legacy opaque" decoder,
and is labelled as unverified until captures from AirPods Pro 3 confirm or
replace it.

**ADR-5 — HRTF data is parsed outside the audio engine.** SOFA (netCDF-4 /
HDF5) files are untrusted. They are parsed in the unprivileged app with a
maintained reader (candidate: PureHDF, MIT), validated, resampled, and
written as a compact, bounded, checksummed SSAP3 HRIR set. The DSP loads
only that format and renders with partitioned convolution, crossfading
between directions. SOFA support does **not** provide Apple's Personalized
Spatial Audio or an ear scan.

**ADR-6 — Conversation detection is local and never records.** Speech
sources, in order of preference:

1. the AirPods' own speaking-level notifications (`0x4B`), when
   conversational awareness reporting is available;
2. a local voice-activity detector on a **non-AirPods** microphone. Opening
   the AirPods mic forces HFP and degrades playback.

A hysteresis state machine (attack, hold, release) requests Transparency and
restores the previous mode. It honours a manual override, and it treats a
mode as changed only after the AirPods echo it. This is SSAP3's own feature,
not Apple's Conversation Awareness.

**ADR-7 — UI stays on WPF/.NET 8 for now.** It adds no new runtime or
packaging dependency, and the existing tray panel works. The Fluent-style
multi-page window is built in WPF. WinUI 3 or .NET 9+ `ThemeMode` will be
re-evaluated after the core is stable.

**ADR-8 — Manifest-driven install engine.** Inno Setup stays as the
packaging and UI shell. Every system change is written to
`%ProgramFiles%\SSAP3\setup\install-manifest.json` (admin-only) *before*
the change is made, so a failed install can be rolled back and uninstall is
exact and idempotent.

## Milestones

| # | Milestone | Contents |
| --- | --- | --- |
| 1 | Audit | Repository audit, the four audit documents, `development/ssap3` branch |
| 2 | Project transformation | SSAP3 identity; removal of the release-blocking install path (S-01, S-02, S-03, S-07); driver and DSP hardening (S-04, S-05, S-06, S-08, S-09); CI expansion |
| 3 | Bluetooth and AAP | Modular protocol layer, strict parsers, acknowledged commands, connection supervisor with backoff, Pro 3 IDs |
| 4 | Motion | Decoders, quaternion estimator, recenter, stale handling, v2 IPC |
| 5 | Spatial engine | Configurable speakers and room, HRIR convolution, SOFA import, latency measurement |
| 6 | Windows audio integration | Opt-in system-wide engine, exclusive-mode detection, endpoint-change handling, safe fallback |
| 7 | AirPods controls | Battery model, listening modes, conversational awareness, local VAD |
| 8 | App and tray | Multi-page Fluent window, tray quick panel, diagnostics page, soundstage visualisation from real pose data |
| 9 | Installer | Manifest engine, rollback, standard and complete uninstall, before/after integrity tool |
| 10 | Security and hardware validation | Threat-model refresh, CodeQL/analysers, fuzzing, Driver Verifier, Pro 3 tests, latency measurement |
| 11 | Release | Attestation-signed driver, Authenticode-signed installer, SBOM, final reports |

## External blockers (need the maintainer)

1. **Microsoft attestation signing for the driver.** This needs:
   - an EV code-signing certificate on a hardware token;
   - a Microsoft Partner Center Hardware Developer Program account;
   - a CAB of the INF and SYS, signed with the EV certificate, then
     submitted for Windows 10/11 attestation.

   Attestation-signed drivers load on Windows 10 1607+ and Windows 11
   client with Secure Boot and Memory Integrity on. They are not
   WHQL-certified. A self-signed certificate can never substitute for this.
2. **Authenticode certificate** (OV or EV) for `SSAP3-Setup.exe` and the app.
3. **GitHub Actions must be enabled on the fork** (Actions tab → enable
   workflows) before CI results exist.
4. **AirPods Pro 3 AACP validation** needs either the signed driver or a
   separate development PC in Test Mode. The maintainer's main PC stays in
   its normal secure configuration.
