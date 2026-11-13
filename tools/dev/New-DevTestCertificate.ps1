#Requires -Version 5.1
<#
.SYNOPSIS
    DEVELOPMENT ONLY. Creates a code-signing certificate for test-signing the SSAP3
    driver on THIS development PC. Never use it for releases and never distribute it.

.DESCRIPTION
    Compared with the upstream script (SECURITY_AUDIT.md S-01), this certificate:
      * has a NON-EXPORTABLE private key that stays in this user's store;
      * is marked as not a CA (basicConstraints CA:false) with the code-signing EKU only;
      * expires after one year.
    Loading a test-signed driver still requires Test Mode, which needs Secure Boot off.
    That is a deliberate, manual decision for a dedicated development machine and is
    described in tools/dev/README.md. Nothing here changes boot configuration.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param()

$ErrorActionPreference = 'Stop'
if ($PSCmdlet.ShouldProcess('Cert:\CurrentUser\My', 'Create SSAP3 development code-signing certificate')) {
    $certificate = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject 'CN=SSAP3 Local Development (this PC only)' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
        -KeyExportPolicy NonExportable `
        -TextExtension @('2.5.29.19={critical}{text}ca=false') `
        -NotAfter (Get-Date).AddYears(1)
    Write-Output "Created development certificate $($certificate.Thumbprint) (non-exportable, CA:false, code signing only)."
    Write-Output 'Sign a local build with: tools\dev\Sign-DevDriver.ps1 -Thumbprint <thumbprint>'
}
