# SSAP3 AAP driver: signing and verification test report

Status as of 2026-11-14, branch `development/ssap3`. Every row says where it
ran:

- **PC:** the development PC (Windows 11 Home 26200, Secure Boot, Memory
  Integrity and Defender on);
- **CI:** GitHub Actions;
- **not yet:** needs something that does not exist yet.

**The SSAP3 driver is not Microsoft-signed.** No result below claims it is.
The signature can only come from Microsoft's Partner Center, after the
maintainer's organization has an EV code-signing certificate and a
Hardware Developer Program account (`driver/SIGNING.md`, items 1–4 and
11–12). Until then Setup installs SSAP3 without the driver. Head tracking
from the AirPods sensors therefore cannot run on a Secure Boot PC, by
design.

## What is finished locally, and what needs Microsoft

| Step | State |
| --- | --- |
| Driver source, INF, `/W4 /WX /guard:cf` build, PDB | Done (CI) |
| InfVerif, HVCI image checks, Microsoft's driver CodeQL suites, Inf2Cat | Done (CI, every push) |
| Independent code review; findings fixed (protocol v4) | Done (`SECURITY_REPORT.md` S-35) |
| Attestation submission CAB in the required layout | Done (CI artifact `ssap3-driver-attestation-cab-unsigned`) |
| EV signing helper | Done (`driver/Sign-AttestationCab.ps1`; rehearsed in CI with a throwaway certificate) |
| Signature, integrity and identity verification | Done (`installer/Test-Ssap3Driver.ps1` and Setup's rule; tested on real catalogs and in CI) |
| Temporary development signing for a dedicated development PC (`tools/dev/Ssap3DevSigning.ps1`; Test F) | Done (this PC: read-only checks and the certificate round trip; CI: the full lifecycle) |
| Loading the development-signed driver in test mode; real head tracking | **Not yet:** needs a development PC with Secure Boot turned off by its owner. Blocked on this PC by Secure Boot, by design |
| Organization, EV certificate, Partner Center account | **Maintainer** |
| Signing the CAB with the EV certificate; submitting; downloading Microsoft's package | **Maintainer + Microsoft** |
| Loading the signed driver; Driver Verifier; head tracking on AirPods Pro 3 | **Not yet** (needs the signed package) |

## Test A: signature validation

| Check | Where | Result |
| --- | --- | --- |
| Microsoft signature on the SSAP3 driver | not yet | No signed package exists |
| Classifier on real Microsoft-signed catalogs (driver store) | PC | 85 catalogs: 79 `MicrosoftWhql` and 1 `MicrosoftAttestation` accepted; 5 third-party-signed (Adafruit/Arduino-style) classified `NotMicrosoft` and refused. Every accepted package's `.sys` and `.inf` verified against its catalog |
| Root and EKU rules come from data | PC | 216 WHQL and 22 attestation catalogs: signer *Microsoft Windows Hardware Compatibility Publisher*, roots Microsoft Root CA 2010 / Root CA / Root Authority, EKU `…10.3.5` (plus `…10.3.5.1` for attestation) |
| Chains built with the catalog's embedded certificates | PC | An older catalog whose chain looked self-terminated without them resolves to *Microsoft Root Authority* with them (its certificates are expired but timestamped, which Windows accepts) |
| Authenticode image hash of `.sys` files | PC | 67/67 real driver files found in their catalogs; a plain file hash matched 0/67 |
| Package mode on a real attestation-signed driver (`gameflt`, copied under SSAP3's file names) | PC | Signature PASS (`MicrosoftAttestation`, Microsoft Root CA 2010); `.sys`/`.inf` PASS; **INF identity FAIL** (not SSAP3's), so it would not be installed |
| One bit changed in the `.text` section of that copy | PC | "Catalog lists ssap3aap.sys" **FAIL**; signature still valid. A change inside the embedded signature block is excluded by Authenticode by design and not reported |
| SSAP3 INF identity: real INF / one foreign hardware ID / another service | PC | True / False / False |
| Unsigned CI package: classified `Unsigned`, `.sys`/`.inf` listed in the Inf2Cat catalog, refused by Setup | CI #29 | pass |
| Self-signed certificate **named** "Microsoft Windows Hardware Compatibility Publisher" signing the catalog | CI #29 | classified `NotTrusted`; Setup refuses it |
| A genuinely attestation-signed driver on the CI runner (`azihsmvf.inf`, Microsoft's own) | CI #29 | accepted by Setup's rule, with its `.sys` verified |

## Test B: security compatibility

| Check | Where | Result |
| --- | --- | --- |
| Secure Boot | PC | on (`Test-Ssap3Driver.ps1`) |
| Memory Integrity (HVCI) | PC | running |
| Microsoft Defender | PC | real-time protection on |
| Test Mode / no-integrity boot options | PC | off |
| Code Integrity events about `ssap3aap.sys` (30 days) | PC | none |
| HVCI-relevant image properties (no W+X sections, NX/CFG/ASLR, kernel-only imports, NX pool only) | CI | pass (every push) |
| Driver loads with Memory Integrity on, no Code Integrity blocks | not yet | needs the signed driver |

## Test C: installation

| Check | Where | Result |
| --- | --- | --- |
| Fresh install (no driver: driverless tier) | PC | 2026-11-13, succeeded; rollback also exercised by an early failed attempt |
| In-place upgrade (reverts and re-applies all 13 recorded changes) | PC | 2026-11-14, three times: engine 2.6.0; engine 2.7.0 (first try failed safely on a locked `SSAP3.exe`, before any system change; installer fixed; second try succeeded). The installed files matched the staged package byte for byte |
| Driver step without a Microsoft-signed package | PC | Setup logged "skipped … nothing was changed" |
| Driver install, upgrade, uninstall, reinstall | not yet | needs the signed package |
| Uninstall of the current install | not yet | not run on this PC |

## Test D: head tracking

| Check | Where | Result |
| --- | --- | --- |
| Sensor initialization, live orientation, tracking loss and recovery with AirPods sensors | not yet | needs the signed driver |
| The same pipeline with simulated motion (decoder, estimator, loss/recovery states, smoothing) | CI | 202 unit tests pass, including the end-to-end simulator and dropout tests |
| Driver protocol v4 contract: IOCTL codes, structure sizes, exact outbound allowlist | CI | compile-time checks in the driver header; a C# mirror is tested against every packet the app emits, and near-misses are refused |

## Test E: audio integration

| Check | Where | Result |
| --- | --- | --- |
| Fixed mode with the Measured renderer | PC | heard on AirPods Pro 3 on 2026-11-14 and judged good (subjective) |
| Classic renderer unchanged; switching is click-free | CI | spatial-engine tests pass |
| Tracked mode follows real head movement | not yet | needs the signed driver |
| No dropouts or crackling | PC | none reported in listening (subjective); CI click tests pass |

## Test F: temporary development signing

The development signature is a local test signature. **It is not a Microsoft
signature**, and SSAP3 Setup refuses it. It loads only in Windows test mode,
which Windows allows only with Secure Boot off.

| Check | Where | Result |
| --- | --- | --- |
| Readiness (`CheckSystem`) | PC | Secure Boot on, Memory Integrity running, Defender on, test mode off. `EnableTestSigning`, `InstallCertificate` and `InstallDriver` are **blocked by Secure Boot**, with that explanation; nothing was changed. **This PC cannot load a development-signed driver**, by design |
| `InstallDriver -DevelopmentMachine` on this PC | PC | refused (Secure Boot, not elevated, test mode not active); nothing created |
| Certificate profile | PC | `CN=SSAP3 Temporary Driver Signing`, sha256RSA, RSA 3072, key non-exportable (PFX export refused: "Key not valid for use in specified state"), code signing only, not a CA, 30 days, SSAP3 marker |
| Reuse, removal, look-alike | PC | Generating again reused the same certificate. Removal deleted the certificate and its key file: the user store and the key folder matched their earlier state. A certificate with the same subject but no marker was left alone |
| Marker encoding | PC | A custom UUID OID (arc 2.25) was tried first; CryptoAPI cannot encode its 128-bit arc. The marker now uses the standard comment extension |
| `Report`, `WatchTracking` | PC | Driver `NotInstalled`. The watcher read 27 head-pose samples from the installed SSAP3 (Fixed mode) and reported "Not tracking", correctly |
| Signing, trust, driver store, renewal and removal on the real package | CI | see below |
| Driver loads in test mode; Tracked mode follows real head movement | not yet | needs a development PC (`tools/dev/README.md`) |

## CI results (run #29, commit `b6a2acc`, all jobs green)

- **Driver (protocol v4):**
  - builds under `/W4 /WX /guard:cf`;
  - passes InfVerif and the HVCI image checks;
  - passes Microsoft's `windows-drivers` CodeQL suites (0 must-fix);
  - passes Inf2Cat, and the submission CAB is rebuilt.
- **`driver/Test-SigningPipeline.ps1`** on that package, 16/16:
  - the verifier fails the unsigned package;
  - the catalog is classified `Unsigned`;
  - the `.sys` is the catalogued file (Authenticode image hash), and so is
    the `.inf`;
  - x64 kernel image, with NX, ASLR and CFG;
  - the INF identity holds;
  - Setup refuses the unsigned package;
  - a changed `.sys` is detected;
  - the spoof-signed catalog is rejected (`NotTrusted`), and Setup refuses
    it;
  - the signing helper refuses a non-EV certificate and leaves the CAB
    unsigned;
  - the rehearsal signs the CAB with the chosen certificate;
  - a real `MicrosoftAttestation` package on the runner is accepted.
- **Driver health check on a clean runner:** reaches `NotInstalled` with no
  failures.
- **Managed tests:** 202 pass, including the exact outbound-allowlist
  mirror. The spatial engine and CodeQL (C#, C/C++) are also green.

Runs #27 and #28 failed only in the new test harness:

- a parameter default that Windows PowerShell 5.1 leaves empty;
- an expected refusal on stderr that 5.1 turned into a terminating error.

Both were fixed. In both runs the driver itself built and passed analysis.

## Next tests, once Microsoft has signed the package

1. Test A: `Test-Ssap3Driver.ps1 -Package <signed folder>`. Every
   `Package` line must pass with `MicrosoftAttestation`.
2. Tests B and C:
   - install with a restore point (`driver/SIGNING.md`, step 6);
   - `Test-Ssap3Driver.ps1 -Require Reachable`, with no Code Integrity
     events;
   - Driver Verifier session;
   - upgrade, uninstall (`Test-Ssap3SystemState.ps1 -Verify`) and
     reinstall.
3. Test D: `SSAP3-TransportProbe <address> Motion`, then Tracked mode and
   `Test-Ssap3Driver.ps1 -Require Tracking`. Then tracking loss (case in a
   pocket), recovery, an app restart and a driver restart (disable and
   enable the device in Device Manager).
4. Test E: Tracked mode follows head turns with both renderers, and Fixed
   mode is unchanged.
