# SSAP3 developer tools

> [!CAUTION]
> The driver-signing tools are **for a dedicated development PC only**.
> They are not part of SSAP3 Setup, Setup never loads them, and production
> builds never use them (CI checks both).

## Temporary development signing (`Ssap3DevSigning.ps1`)

### What it is, and what it is not

The AAP features (head tracking, listening modes, AACP battery) need the
`ssap3aap.sys` profile driver. Until Microsoft signs it (`driver/SIGNING.md`),
the only way to run it is **Windows test signing** on a development PC. This
tool does that with a certificate generated on that PC:

- **Subject:** `CN=SSAP3 Temporary Driver Signing`, with an SSAP3 marker
  extension.
- **Key:** RSA 3072 with SHA-256, created non-exportable in your Personal
  store. It is never exported.
- **Use:** code signing only, not a CA.
- **Validity:** 30 days by default; 1–90 days with `-ValidityDays`.

It is **not a Microsoft signature**. Three things follow from that:

- A normal PC with Secure Boot on never loads a driver signed this way.
- SSAP3 Setup refuses such a package, and so does `installer\Test-Ssap3Driver.ps1`.
- The development signature never replaces the production path. Once
  Microsoft has signed the driver, Setup installs Microsoft's package and
  nothing here is needed.

The app, the head-tracking pipeline and the spatial engine are the same for
both: the app talks to whichever SSAP3 driver is loaded.

### What blocks it, and why

Windows lets test-signed drivers load only in **test mode**
(`bcdedit /set testsigning on`). Windows refuses that setting while
**Secure Boot** is on: bcdedit answers that the value is protected by Secure
Boot policy. The tool:

- never changes firmware settings, Secure Boot, Memory Integrity, Defender
  or Code Integrity policy;
- never works around signature enforcement;
- stops with that explanation while Secure Boot is on (`CheckSystem` shows it).

**Memory Integrity can stay on.** Microsoft documents that test-signed
drivers load with Memory Integrity in test mode, provided the binary itself
is signed. `SignDriver` embeds a signature in `ssap3aap.sys` as well as
signing the catalog.

> [!WARNING]
> Test mode lets **any** test-signed kernel driver load, not only SSAP3's.
> That is why test mode, and the Secure Boot change it needs, belong on a
> development PC and never on your everyday PC.

### Procedure on a development PC

You make every security-relevant decision yourself. Each step the tool takes
asks for confirmation (`-Confirm:$false` skips that).

1. **Choose the PC.** Use a spare Windows 11 x64 PC with Bluetooth that can
   pair the AirPods Pro 3.
   - Not your everyday PC.
   - A virtual machine with a USB Bluetooth adapter passed through might work.
     This is not tested.
2. **BitLocker.** If BitLocker or Device Encryption is on, save the recovery
   key first: [aka.ms/myrecoverykey](https://aka.ms/myrecoverykey) for a
   Microsoft account.
3. **Install SSAP3** on that PC as usual (SSAP3 Setup).
4. **Get the unsigned driver package.** Download the CI artifact
   `ssap3-driver-attestation-cab-unsigned` from a green run while signed in
   to GitHub. It contains `ssap3aap.cab` and its `.sha256`.
5. **Get Microsoft's signtool.** Install the Windows SDK's "Signing Tools for
   Desktop Apps", or run `dotnet restore driver\WdkPackages.proj`.
6. **Check the PC:** `.\tools\dev\Ssap3DevSigning.ps1 CheckSystem`
7. **Turn Secure Boot off yourself** in the PC's UEFI settings. Leave Memory
   Integrity and Defender on.
8. **Turn on test signing,** in an elevated PowerShell under your own account:

   ```powershell
   .\tools\dev\Ssap3DevSigning.ps1 EnableTestSigning -DevelopmentMachine -RecoveryKeyReady
   ```

   Then restart. Windows shows "Test Mode" on the desktop.
9. **Install the driver,** elevated. This creates or reuses the certificate,
   trusts its public part machine-wide, signs, verifies and installs:

   ```powershell
   .\tools\dev\Ssap3DevSigning.ps1 InstallDriver -DevelopmentMachine -Package "$env:USERPROFILE\Downloads\ssap3aap.cab"
   ```

10. **Turn on head tracking.** Connect the AirPods and start SSAP3 normally
    (not elevated). Choose Settings > Head tracking > "AirPods motion
    sensors", then "Head tracked" mode in the main window.
11. **Verify:**

    ```powershell
    .\tools\dev\Ssap3DevSigning.ps1 Report -ReportPath "$env:USERPROFILE\Desktop\ssap3-dev-report.txt"
    .\tools\dev\Ssap3DevSigning.ps1 WatchTracking -Seconds 15
    ```

    Turn your head while `WatchTracking` runs. It reports real tracking only
    when all of these hold:

    - live data comes from the AirPods sensors in Tracked mode;
    - the data stays fresh and the quaternions are normalized;
    - the yaw follows your head through at least 20°.

    Simulated motion is labelled as simulated. Then run the hardware tests in
    `docs/DRIVER_TEST_REPORT.md`.

### Undo everything

1. Run this elevated, then restart:

   ```powershell
   .\tools\dev\Ssap3DevSigning.ps1 RemoveDriver -RemoveCertificate -RestoreTestSigning
   ```

   It does the following:
   - stops SSAP3;
   - removes the development driver package, plus the `ssap3aap` service entry
     if it is provably SSAP3's;
   - deletes the certificates and their private keys;
   - removes their machine trust;
   - reverts only the test-signing change that SSAP3 made;
   - prints what it verified against the snapshot taken before its first
     change.
2. Turn Secure Boot back on in UEFI.
3. Run `CheckSystem` again after the restart.

The tool never claims more than it checked. It cannot see your UEFI setting,
or the boot state before the restart.

### Commands

| Command | Changes | Needs |
| --- | --- | --- |
| `CheckSystem` | nothing | — |
| `GenerateCertificate` | your Personal store (new certificate and key) | — |
| `CheckCertificate` | nothing | — |
| `InstallCertificate` | adds the **public** certificate to LocalMachine Root and TrustedPublisher | elevated, `-DevelopmentMachine`, Secure Boot off |
| `SignDriver -Package <cab or folder>` | writes the signed copy to `%LOCALAPPDATA%\SSAP3\DevSigning\package\ssap3aap` | signtool; an **unsigned** package |
| `VerifySignature [-Package <folder>]` | nothing | — |
| `RenewCertificate` | new certificate and key; moves machine trust; deletes the old certificate and key | elevated if the old one is trusted |
| `RemoveCertificate [-TrustOnly]` | removes SSAP3 development certificates, keys and trust | elevated for machine trust |
| `EnableTestSigning` | `bcdedit /set {current} testsigning on` | elevated, `-DevelopmentMachine`, Secure Boot off, `-RecoveryKeyReady` if BitLocker is on |
| `DisableTestSigning` | reverts SSAP3's own change only | elevated |
| `InstallDriver [-Package ...]` | certificate, trust, signing, `pnputil /add-driver /install` | elevated, `-DevelopmentMachine`, test mode active |
| `RemoveDriver [-RemoveCertificate] [-RestoreTestSigning]` | removes the development driver (and, with the switches, the certificates and SSAP3's test-signing change) | elevated |
| `Report [-ReportPath <file>]` | nothing (writes the report file if asked) | — |
| `WatchTracking [-Seconds n]` | nothing | SSAP3 running |

`Report` prints the following:

- the certificate: subject, thumbprint, expiry and signature algorithm;
- the SHA-256 of the driver, INF and catalog;
- Windows settings: test signing, Secure Boot, Memory Integrity and Defender;
- the driver: driver-store package, service, device state, and the health-check
  level (up to `Tracking`).

It also keeps four statuses apart:

1. **Cryptographically signed:** the signatures are valid, and the `.sys`
   and `.inf` are exactly the signed files.
2. **Locally trusted:** this PC trusts the certificate.
3. **Accepted under the test-signing policy:** shown as "Yes" only once the
   kernel has actually loaded the driver in test mode.
4. **Officially Microsoft-signed:** always "No" for a development package.

### What the tool only ever touches

**Certificates.** Only those it created: the fixed subject, the SSAP3 marker
text, code signing only and not a CA. A certificate with the same name but
no marker is reported and left alone, even when a modified state file names
it.

**Driver packages.** Only those that are SSAP3's (`ssap3aap.inf`, provider
SSAP3) and signed by "SSAP3 Temporary Driver Signing". A Microsoft-signed
SSAP3 driver installed by Setup is never replaced or removed.

**Test signing.** Only its own change, recorded before it is made.

**Elevated installs** run from an administrators-only copy of the package,
checked again there. A standard-user process therefore cannot swap files
between the check and `pnputil`.

**The state file** is `%LOCALAPPDATA%\SSAP3\DevSigning\state.json`. It lists
what was created, plus a snapshot of the machine trust stores taken before
the first change. It holds no secrets.

### Residual risks on the development PC

- **The private key is usable by anything that runs as you** while the key
  exists. Non-exportable stops it from being copied, not from being used. To
  limit this:
  - keep the validity short;
  - use `SignDriver -DiscardKey` to delete the key right after signing;
  - remove the certificate when you are done.
- **Test mode** lets any test-signed driver load. Leave it as soon as you are
  done testing.

## Spatial engine from CI (any PC, no test mode)

`Get-SpatialEngineArtifact.ps1` is unrelated to the driver tools and changes
nothing on Windows. Use it when you have no MSVC toolchain:

1. Download the `ssap3-spatial-engine-unsigned` artifact of a green CI run
   while signed in to GitHub.
2. Run `Get-SpatialEngineArtifact.ps1 -ZipPath <zip> -ArtifactId <id>`.

The script unpacks `SSAP3Spatializer.dll` to `audio\bin` only if all of
these hold:

- the zip matches the SHA-256 digest that GitHub recorded at upload;
- the zip holds exactly that one DLL;
- the DLL is a PE32+ x64 DLL.
