# Getting the SSAP3 AAP driver signed by Microsoft

The AirPods' motion sensors, listening-mode control and AACP battery all
reach Windows through Apple's AAP control channel. Windows has no user-mode
API for that channel, so SSAP3 uses a small kernel driver, `ssap3aap.sys`. A
PC with Secure Boot on loads it only once **Microsoft** has signed it. SSAP3
never asks you to turn off Secure Boot or enable Test Mode instead.

This guide is for the maintainer who will do the signing. It is based on
Microsoft's documentation, last checked on 2026-11-13:

- [Attestation sign Windows drivers](https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/code-signing-attestation)
- [Driver signing options](https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/driver-signing-offerings)
- [Register for the Windows Hardware Developer Program](https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/hardware-program-register)

## Checklist

**Status key:** ✅ done in this repository · ⬜ needs the maintainer's
organization and credentials. Nothing below may be faked: no borrowed
certificates, no impersonated organizations, and nothing described as
Microsoft-signed until Microsoft has signed it.

| # | Item | Status |
| --- | --- | --- |
| 1 | Registered legal organization (company or registered sole proprietorship, as the CA accepts) with a legal contact who can sign agreements | ⬜ |
| 2 | EV code-signing certificate issued to that organization (hardware token or cloud key store) | ⬜ |
| 3 | Microsoft Entra ID tenant with a global administrator account (free to create during registration) | ⬜ |
| 4 | Windows Hardware Developer Program registration: company details or D-U-N-S, legal-contact email verification, EV certificate uploaded, business questionnaire answered | ⬜ |
| 5 | Driver builds under `/W4 /WX /guard:cf` with a PDB | ✅ CI |
| 6 | INF passes InfVerif `/w` | ✅ CI |
| 7 | HVCI-relevant image checks (`driver/Test-DriverImage.ps1`) | ✅ CI |
| 8 | Microsoft `microsoft/windows-drivers` 1.10.0 CodeQL suites: 0 must-fix, 0 recommended | ✅ CI |
| 9 | Inf2Cat signability test (catalog generated) | ✅ CI |
| 10 | Attestation CAB in the required layout (`ssap3aap\` with `.sys`, `.inf`, `.pdb`, `.cat`) | ✅ CI artifact `ssap3-driver-attestation-cab-unsigned` |
| 10a | Independent code review of the driver (2026-11-14). No critical or high findings; the medium and low findings were fixed (protocol v4, `SECURITY_REPORT.md` S-35) | ✅ |
| 10b | Signature verification and package-integrity tooling, tested on real Microsoft-signed catalogs and in CI (unsigned, tampered and spoofed-publisher packages) | ✅ `installer\Test-Ssap3Driver.ps1`, `driver\Test-SigningPipeline.ps1` |
| 11 | CAB signed with the EV certificate | ⬜ helper ready: `driver\Sign-AttestationCab.ps1` |
| 12 | Submitted in Partner Center (test-signing options unchecked); signed package downloaded | ⬜ |
| 13 | Microsoft signature verified (pinned root, driver-publisher EKU, the `.sys` and `.inf` listed in the catalog, the SSAP3 INF identity) | ⬜ `Test-Ssap3Driver.ps1 -Package`, and again automatically by packaging and Setup |
| 14 | First load on hardware with a restore point (Driver Verifier optional); `Test-Ssap3Driver.ps1 -Require Reachable` and `SSAP3-TransportProbe <address> Motion` pass | ⬜ |
| 15 | Driver Verifier and HVCI runtime test on hardware | ⬜ |

Steps 11–15 are described below. Results so far are in
`docs/DRIVER_TEST_REPORT.md`.

## Tools in this repository

| Tool | Runs where | What it does |
| --- | --- | --- |
| CI job *AAP driver* | GitHub Actions | Builds, runs InfVerif and the HVCI image checks, runs Inf2Cat, and makes the submission CAB. Then runs `driver\Test-SigningPipeline.ps1` against that package. |
| `driver\Sign-AttestationCab.ps1` | Your PC, with the EV token | Checks the CAB against its CI hash, refuses a non-EV certificate, signs with SHA-256 and an RFC 3161 timestamp, and verifies the result. |
| `installer\Test-Ssap3Driver.ps1` | Any PC, no elevation needed | Read-only health check: package signature and integrity, system security, installed driver, device state, control interface and head-tracking freshness. |
| `installer\build-installer.ps1 -SignedDriverFolder` | Your PC | Refuses to package a driver that is not Microsoft-signed for exactly these files. |
| SSAP3 Setup (`Install-Ssap3Driver`) | Elevated, at install | Applies the same rule again, then installs with `pnputil`. Restart notices come from pnputil exit code 3010. |

## How a Microsoft signature is recognised

`Get-Ssap3CatalogSignature` in `installer\Ssap3Setup.psm1` never trusts a
name alone. It accepts a catalog only when all of these hold:

1. **Windows trusts the signature.** `Get-AuthenticodeSignature` reports
   `Valid`, which also covers revocation and the timestamp.
2. **The chain ends at a Microsoft root.** The chain is built with the
   certificates embedded in the catalog, and the root must be one of:

   | Root | Thumbprint |
   | --- | --- |
   | Microsoft Root Certificate Authority 2010 | `3B1EFD3A66EA28B16697394703A72CA340A05BD5` |
   | Microsoft Root Certificate Authority | `CDD4EEAE6000AC7F40C3802C171E30148030C072` |
   | Microsoft Root Authority | `A43489159A520F0D93D032CCAF37E7FE20A8B419` |

3. **The signer is the driver publisher.** It must be *Microsoft Windows
   Hardware Compatibility Publisher* with the Windows Hardware Driver
   Verification EKU `1.3.6.1.4.1.311.10.3.5`. Attestation signatures add
   `1.3.6.1.4.1.311.10.3.5.1`.
4. **The files are the ones that were signed.** The `.sys` (by its
   Authenticode image hash) and the `.inf` must be listed in the catalog.
5. **The INF is SSAP3's own.** Provider SSAP3, the `ssap3aap` service, and
   only the Apple AAP and UARP Bluetooth service hardware IDs.

These rules come from real catalogs, not memory. On the development PC, 216
WHQL and 22 attestation catalogs in the driver store had exactly these
roots and EKUs. The check classifies them correctly and refuses
third-party-signed packages (`docs/DRIVER_TEST_REPORT.md`, Test A).

Possible results:

| Result | Meaning |
| --- | --- |
| `Unsigned` | No signature (the CI build) |
| `NotTrusted` | Signed, but Windows doesn't trust it, for example a self-signed or WDK test certificate, even one named like Microsoft's |
| `NotMicrosoft` | Trusted, but not through a Microsoft root |
| `MicrosoftOther` | A Microsoft signature that isn't a driver-publisher one |
| `MicrosoftWhql`, `MicrosoftAttestation` | Installable |

## What to expect

- **Attestation signing is the realistic route.** Windows 10 and 11 desktop
  trust attestation-signed kernel drivers. Microsoft now describes it as
  being "for testing purposes", and attestation-signed drivers can't be
  published to Windows Update for retail audiences. You install the driver
  yourself through SSAP3 Setup, so neither limit blocks this use.
- **You need an organization.** The Hardware Developer Program asks for:
  - company details or a D-U-N-S number;
  - a legal contact with authority to sign for the company;
  - a Microsoft Entra ID global administrator account (you can create one
    free during registration);
  - an **EV code-signing certificate**, which you upload.

  Certificate authorities issue EV certificates only to verified
  organizations, not to private individuals. Check with the CA which
  business types they accept in your jurisdiction (some accept a registered
  sole proprietorship). Expect a yearly fee and a hardware token or cloud
  key store.
- **Approval takes time.** Microsoft verifies the organization and sends a
  questionnaire before the account can submit.
- **Head tracking is not guaranteed on day one.** The driver has never been
  loaded anywhere yet, and SSAP3's AirPods Pro 3 orientation decoder is
  unverified (`HARDWARE_COMPATIBILITY.md`). The first sessions with the
  signed driver are for capturing motion packets and confirming the decoder.

## 1. Register

1. Register an organization if you don't have one, and buy an EV
   code-signing certificate from a CA.
2. Register for the Hardware Developer Program (link above). Upload the EV
   certificate on **Manage certificates** and complete the questionnaire.
3. Wait for the approval email.

## 2. Get the submission CAB

Every green CI run builds the driver and uploads
`ssap3-driver-attestation-cab-unsigned`. Submit one from run #29 or later:
earlier CABs carry driver protocol v3, which the current app refuses. The
artifact holds:

- `ssap3aap.cab`, with one `ssap3aap\` folder containing `.sys`, `.inf`,
  `.pdb` and an unsigned `.cat`. Microsoft replaces the catalog with its own.
- `ssap3aap.cab.sha256`.

Before it is packaged, CI checks the driver:

- it compiles under `/W4 /WX /guard:cf`;
- InfVerif `/w` passes;
- `driver/Test-DriverImage.ps1` passes: the HVCI-relevant image properties
  (page-aligned sections, no writable+executable section, NX, CFG, ASLR,
  kernel-only imports) and non-executable pool only;
- Microsoft's driver CodeQL **must-fix** queries (`microsoft/windows-drivers`
  1.10.0) report nothing. The **recommended** suite's results are in the
  repository's code scanning alerts.

Download the artifact while signed in to GitHub. Check its zip against the
`digest` that GitHub's public API reports for that artifact ID, as
`tools/dev/Get-SpatialEngineArtifact.ps1` does for the engine. Then check the
CAB against `ssap3aap.cab.sha256`.

## 3. Sign the CAB with the EV certificate

Connect the token (or sign in to the cloud key store), keep
`ssap3aap.cab.sha256` next to the CAB, then run:

```powershell
.\driver\Sign-AttestationCab.ps1 -CabPath "$env:USERPROFILE\Downloads\ssap3aap.cab" -CertificateThumbprint <EV certificate thumbprint> -TimestampUrl <your CA's RFC 3161 URL>
```

The script:

- refuses a CAB that doesn't match its CI hash, or is already signed;
- refuses a certificate that isn't valid now, has no private key on this
  PC, can't sign code, or isn't EV (policy `2.23.140.1.3`);
- signs with SHA-256 and a timestamp, verifies the result, and prints the
  signed CAB's SHA-256.

The token's own software asks for its PIN; the script never does. If your
CA uses a cloud signing service with its own tool, sign with that tool
instead and check the result with `signtool verify /pa /tw`.

Never import the EV certificate into the machine Root store, and never commit
it or its export to this repository (`.gitignore` blocks `*.pfx`).

## 4. Submit in Partner Center

1. Partner Center > Hardware > **Submit new hardware**.
2. Product name: for example `SSAP3 AAP control transport`.
3. Leave **both test-signing options unchecked**.
4. Requested signatures: Windows 11 x64 (and Windows 10 x64 if offered).
5. Submit, wait for signing to finish, then download the signed package.

## 5. Check Microsoft's signature

Unpack the signed package into an empty folder, then run:

```powershell
.\installer\Test-Ssap3Driver.ps1 -Package "$env:USERPROFILE\Downloads\ssap3aap-signed"
```

Every `Package` line must say **PASS**:

| Line | Expected |
| --- | --- |
| Signature | `MicrosoftAttestation` (or `MicrosoftWhql`), root *Microsoft Root Certificate Authority 2010* |
| Catalog lists ssap3aap.sys / ssap3aap.inf | the files are exactly the ones Microsoft signed |
| Architecture, HVCI image flags | x64 kernel image with NX, ASLR and CFG |
| INF | the SSAP3 identity |

Do not install a package with any FAIL line. Packaging and Setup apply the
same rules again, and refuse it anyway.

## 6. Install on your PC

1. Create a System Restore point first: this is a kernel driver that has
   never been loaded.
2. Unpack the signed package into a folder, for example
   `Downloads\ssap3aap-signed`. Then stage and install:

   ```powershell
   .\installer\build-installer.ps1 -StageOnly -SignedDriverFolder "$env:USERPROFILE\Downloads\ssap3aap-signed"
   ```

   Run the same elevated `Install-SSAP3Local.ps1 -EnableSpatialEngine`
   command as before. Setup reverts the previous install and adds the
   driver. If its log ends with **RESTART NEEDED**, restart Windows.
   Your SSAP3 settings are kept.
3. Optional, recommended for the first session: enable Driver Verifier for
   this driver only, from an elevated prompt, then restart:

   ```powershell
   verifier /standard /driver ssap3aap.sys
   ```

   Turn it off afterwards with `verifier /reset` and another restart.
4. If Windows fails to start:
   - boot into Safe Mode (hold Shift while choosing **Restart**, then
     Troubleshoot > Startup Settings);
   - run `pnputil /enum-drivers` to find the `oemNN.inf` whose original name
     is `ssap3aap.inf`, then `pnputil /delete-driver oemNN.inf /uninstall`;
   - or use the restore point.

## 7. First run

1. Connect the AirPods. SSAP3 should report the AAP link instead of
   **Connected (audio only)**.
2. Run `.\installer\Test-Ssap3Driver.ps1 -Require Reachable`. Each level is
   reported separately, so a successful install is never mistaken for a
   working driver:
   - **Installed:** the package is in the driver store and its store copy
     is Microsoft-signed.
   - **Started:** a device uses the driver with no Windows problem code.
     Code 52 means Windows couldn't verify the signature; code 39 or 31
     means the driver failed to load. Code Integrity block events are
     listed too.
   - **Reachable:** the control interface answers with protocol v4.
3. Run `SSAP3-TransportProbe <address> Motion`. It prints, for 20 seconds:
   - the motion service that streams;
   - the packet sizes;
   - the decoded yaw, which should rise as you turn left;
   - gravity, about 1 g at rest.

   See *Hardware validation procedure* in `HARDWARE_COMPATIBILITY.md` for
   pass criteria.
4. Select **Tracked** in SSAP3, with the motion source set to the AirPods
   sensors. If left and right are reversed, use **Invert yaw**.
   `Test-Ssap3Driver.ps1 -Require Tracking` then passes only when:
   - the head-pose record is live and fresh;
   - it comes from the AirPods sensors, not the simulator or a companion.
5. Record what worked in `HARDWARE_COMPATIBILITY.md`,
   `DEVELOPMENT_STATUS.md` and `docs/DRIVER_TEST_REPORT.md`. Keep simulated
   and hardware results separate.

## Removing the driver

`Uninstall-SSAP3Local.ps1` removes only the driver packages Setup added.
They are matched by original name `ssap3aap.inf` **and** provider SSAP3,
and packages that already existed before install are skipped.
`pnputil /delete-driver /uninstall` uninstalls the driver from the devices
using it and deletes the package. If the log ends with **RESTART NEEDED**
(a device was still using the driver), restart to finish. Nothing else is
touched.

Afterwards, `Test-Ssap3Driver.ps1` should report the driver as not in the
driver store, and `Test-Ssap3SystemState.ps1 -Verify` should report no
SSAP3 objects. Neither has been run against a real driver install yet; see
`docs/DRIVER_TEST_REPORT.md`.
