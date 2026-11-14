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
| 11 | CAB signed with the EV certificate | ⬜ |
| 12 | Submitted in Partner Center (test-signing options unchecked); signed package downloaded | ⬜ |
| 13 | Catalog signer verified as "Microsoft Windows Hardware Compatibility Publisher" | ⬜ (checked automatically by Setup) |
| 14 | First load on hardware with a restore point (Driver Verifier optional); `SSAP3-TransportProbe <address> Motion` passes | ⬜ |
| 15 | Driver Verifier and HVCI runtime test on hardware | ⬜ |

Steps 11–15 are described below.

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
`ssap3-driver-attestation-cab-unsigned`. It holds:

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

Use the signing method your CA documents for your token or key store. With
`signtool` (Windows SDK) it looks like this:

```powershell
signtool sign /fd SHA256 /tr <your CA's RFC 3161 timestamp URL> /td SHA256 /sha1 <EV certificate thumbprint> ssap3aap.cab
signtool verify /pa /v ssap3aap.cab
```

Never import the EV certificate into the machine Root store, and never commit
it or its export to this repository (`.gitignore` blocks `*.pfx`).

## 4. Submit in Partner Center

1. Partner Center > Hardware > **Submit new hardware**.
2. Product name: for example `SSAP3 AAP control transport`.
3. Leave **both test-signing options unchecked**.
4. Requested signatures: Windows 11 x64 (and Windows 10 x64 if offered).
5. Submit, wait for signing to finish, then download the signed package.

## 5. Check Microsoft's signature

```powershell
signtool verify /pa /v ssap3aap.cat
```

The catalog signer must be **Microsoft Windows Hardware Compatibility
Publisher**. SSAP3 Setup checks this itself and skips any driver package
that doesn't match (`Install-Ssap3Driver`).

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
   driver.
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
2. Run `SSAP3-TransportProbe <address> Motion` first. It prints, for 20
   seconds:
   - the motion service that streams;
   - the packet sizes;
   - the decoded yaw, which should rise as you turn left;
   - gravity, about 1 g at rest.

   See *Hardware validation procedure* in `HARDWARE_COMPATIBILITY.md` for
   pass criteria.
3. Select **Tracked** in SSAP3. If left and right are reversed, use
   **Invert yaw**.
4. Record what worked in `HARDWARE_COMPATIBILITY.md` and
   `DEVELOPMENT_STATUS.md`. Keep simulated and hardware results separate.
