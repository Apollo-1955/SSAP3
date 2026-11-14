#Requires -Version 5.1
<#
.SYNOPSIS
    Signs the SSAP3 attestation submission CAB with your organization's EV
    code-signing certificate, for upload to Microsoft Partner Center.

.DESCRIPTION
    Run this on the PC where your EV certificate's token or key store is
    available. The script:

      1. checks the CAB against its .sha256 file from CI (refuses a mismatch);
      2. finds the certificate by thumbprint in your Personal store and checks
         that it is valid now, has its private key available (on the token),
         allows code signing and is an EV code-signing certificate
         (CA/Browser Forum policy 2.23.140.1.3), which Partner Center requires;
      3. signs with SHA-256 and an RFC 3161 timestamp through signtool;
      4. verifies the result and prints the signed CAB's SHA-256.

    Never exports keys, never asks for a PIN itself (the token's own
    software does), and never changes any certificate store. The signed CAB
    is NOT a driver Windows will load: Microsoft replaces the catalog with
    its own signature after you submit it (driver\SIGNING.md).

.PARAMETER CabPath
    The ssap3aap.cab from the CI artifact ssap3-driver-attestation-cab-unsigned.

.PARAMETER CertificateThumbprint
    SHA-1 thumbprint of the EV certificate (certmgr.msc > Personal >
    Certificates > open it > Details > Thumbprint).

.PARAMETER TimestampUrl
    Your certificate authority's RFC 3161 timestamp URL.

.PARAMETER AllowNonEvCertificate
    Rehearsal only (for example CI with a throwaway certificate). Partner
    Center rejects a CAB signed this way.

.PARAMETER SkipTimestamp
    Rehearsal only; requires -AllowNonEvCertificate.

.EXAMPLE
    .\driver\Sign-AttestationCab.ps1 -CabPath "$env:USERPROFILE\Downloads\ssap3aap.cab" -CertificateThumbprint 0123456789ABCDEF0123456789ABCDEF01234567 -TimestampUrl http://timestamp.example-ca.com
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CabPath,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9A-Fa-f]{40}$')][string]$CertificateThumbprint,
    [ValidatePattern('^https?://\S+$')][string]$TimestampUrl,
    [string]$SignToolPath,
    [switch]$AllowNonEvCertificate,
    [switch]$SkipTimestamp
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
$EvCodeSigningPolicy = '2.23.140.1.3'
$CodeSigningEku = '1.3.6.1.5.5.7.3.3'

if ($SkipTimestamp -and -not $AllowNonEvCertificate) { throw '-SkipTimestamp is for rehearsals only and needs -AllowNonEvCertificate.' }
if (-not $SkipTimestamp -and -not $TimestampUrl) { throw 'Pass -TimestampUrl (your CA documents its RFC 3161 URL). Partner Center needs a timestamped signature.' }

$CabPath = (Resolve-Path -LiteralPath $CabPath).ProviderPath
$hashFile = "$CabPath.sha256"
$before = (Get-FileHash -LiteralPath $CabPath -Algorithm SHA256).Hash
if (Test-Path -LiteralPath $hashFile) {
    $expected = ((Get-Content -LiteralPath $hashFile -Raw).Trim() -split '\s+')[0].ToUpperInvariant()
    if ($expected -ne $before) { throw "The CAB's SHA-256 ($before) does not match $hashFile ($expected). Download the CI artifact again." }
    Write-Output "CAB matches its CI hash: $before"
}
else {
    Write-Warning "No $hashFile next to the CAB: its integrity against the CI build was not checked."
}

$existing = Get-AuthenticodeSignature -LiteralPath $CabPath
if ($existing.Status -ne 'NotSigned') {
    $by = if ($null -ne $existing.SignerCertificate) { $existing.SignerCertificate.Subject } else { 'unknown signer' }
    throw "The CAB is already signed ($($existing.Status), $by). Start from the CI artifact."
}

$certificate = @(Get-ChildItem -LiteralPath Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
    Where-Object { $_.Thumbprint -eq $CertificateThumbprint.ToUpperInvariant() }) | Select-Object -First 1
if (-not $certificate) { throw "No certificate with thumbprint $CertificateThumbprint in your Personal store. Insert the token or sign in to the key store, then try again." }
$now = Get-Date
if ($now -lt $certificate.NotBefore -or $now -gt $certificate.NotAfter) { throw "The certificate is not valid now (valid $($certificate.NotBefore) to $($certificate.NotAfter))." }
if (-not $certificate.HasPrivateKey) { throw 'The certificate has no private key on this PC (is the token connected?).' }
$ekus = @($certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' } |
    ForEach-Object { ([System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$_).EnhancedKeyUsages } | ForEach-Object Value)
if ($CodeSigningEku -notin $ekus) { throw 'The certificate does not allow code signing.' }
$policies = @($certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.32' } | ForEach-Object { $_.Format($false) })
$isEv = [bool]($policies | Where-Object { $_ -match [regex]::Escape($EvCodeSigningPolicy) })
if (-not $isEv -and -not $AllowNonEvCertificate) {
    throw "The certificate is not an EV code-signing certificate (no policy $EvCodeSigningPolicy). Partner Center requires EV for attestation signing."
}
Write-Output "Certificate: $($certificate.Subject) (EV: $isEv, valid until $($certificate.NotAfter.ToString('yyyy-MM-dd')))"

if (-not $SignToolPath) {
    $packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
    $candidates = @(@(
        Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue
        Get-ChildItem -Path (Join-Path $packages 'microsoft.windows.sdk.cpp\*\c\bin\*\x64\signtool.exe') -ErrorAction SilentlyContinue
    ) | Sort-Object FullName -Descending)
    if ($candidates.Count -eq 0) { throw 'signtool.exe was not found. Install the Windows SDK (Signing Tools for Desktop Apps) or pass -SignToolPath.' }
    $SignToolPath = $candidates[0].FullName
}
$signature = Get-AuthenticodeSignature -LiteralPath $SignToolPath
if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
    throw "$SignToolPath is not a Microsoft-signed signtool.exe."
}

$arguments = @('sign', '/fd', 'SHA256', '/sha1', $certificate.Thumbprint)
if (-not $SkipTimestamp) { $arguments += @('/tr', $TimestampUrl, '/td', 'SHA256') }
$arguments += $CabPath
Write-Output "Signing with $SignToolPath ..."
& $SignToolPath @arguments
# signtool: 0 = success, 2 = success with warnings, 1 = failure. The signature
# itself is checked below either way.
if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 2) { throw "signtool sign failed with exit code $LASTEXITCODE." }

$signed = Get-AuthenticodeSignature -LiteralPath $CabPath
if ($null -eq $signed.SignerCertificate -or $signed.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) { throw 'The CAB does not carry the expected signature after signing.' }
if (-not $AllowNonEvCertificate) {
    & $SignToolPath verify /pa /tw $CabPath
    if ($LASTEXITCODE -ne 0) { throw "signtool verify failed with exit code $LASTEXITCODE." }
}
$timestamped = $null -ne $signed.TimeStamperCertificate
Write-Output ("Signed: {0}; timestamped: {1}; signed CAB SHA-256: {2}" -f ($signed.SignerCertificate.Subject -replace ',.*$', ''), $timestamped, (Get-FileHash -LiteralPath $CabPath -Algorithm SHA256).Hash)
Write-Output 'Next: upload this CAB in Partner Center (driver\SIGNING.md, step 4).'
