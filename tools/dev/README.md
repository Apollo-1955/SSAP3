# SSAP3 developer driver tools

> [!CAUTION]
> These tools are **for a dedicated development PC only**. They are not part
> of SSAP3 Setup, and production builds never use them.

## Why they exist

The AAP features (listening modes, AACP battery, head tracking) need the
`ssap3aap.sys` profile driver. A normal Windows 11 PC with Secure Boot on
loads it only after Microsoft signs it through attestation signing. Until that
happens, the only way to test the driver on real AirPods is test signing on a
machine where the developer deliberately accepts weaker boot security.

## Before you start

- Use a spare PC or a dedicated development install. **Do not do this on your
  everyday PC.**
- Turning on Test Mode means turning off Secure Boot. That lets **any**
  test-signed kernel driver load on the PC, not only SSAP3's.
- If BitLocker is on, have the recovery key ready before you change UEFI
  settings.

## Steps

1. Build the driver (`driver\build-driver-msvc.ps1`, needs the MSVC toolchain
   and the WDK NuGet packages).
2. Run `tools\dev\New-DevTestCertificate.ps1`. It creates a non-exportable,
   `CA:false`, code-signing-only certificate that never leaves this PC.
3. Run `driver\package-driver.ps1 -DevCertificateThumbprint <thumbprint>` to
   produce a test-signed package in `artifacts\dev-driver`.
4. Configure the development PC yourself, in an elevated shell:
   - Turn off Secure Boot in UEFI.
   - Run `bcdedit /set testsigning on`, then restart.
   - Trust the development certificate on this PC only: export its **public**
     part to a `.cer` file and import that into `LocalMachine\TrustedPublisher`
     and `LocalMachine\Root`.
   - Run `pnputil /add-driver artifacts\dev-driver\ssap3aap.inf /install`.
5. Run the hardware checks in `HARDWARE_COMPATIBILITY.md`.

## Undo everything

1. `pnputil /delete-driver <oemNN.inf> /uninstall`, using the published name
   that `pnputil /enum-drivers` shows for `ssap3aap.inf`.
2. Remove the development certificate from `TrustedPublisher`, `Root` and
   `CurrentUser\My`.
3. Run `bcdedit /set testsigning off`, restart, then turn Secure Boot back on
   in UEFI.
4. Confirm with `installer\Test-Ssap3SystemState.ps1 -Verify`.
