[CmdletBinding()]
param(
    # DEVELOPMENT ONLY: thumbprint of a certificate from tools\dev\New-DevTestCertificate.ps1.
    [string]$DevCertificateThumbprint,
    # Produce the CAB layout for Microsoft attestation signing (Partner Center).
    [switch]$AttestationCab
)

<#
    Packages ssap3aap.sys + ssap3aap.inf (+ .cat).

    Production: use -AttestationCab, sign the CAB with the project's EV certificate
    (signtool sign /fd SHA256 /tr <RFC3161 timestamp URL> /td SHA256 /sha1 <EV thumbprint>),
    then submit it in Partner Center. Microsoft returns a package whose catalog is
    signed by "Microsoft Windows Hardware Compatibility Publisher"; pass that folder
    to installer\build-installer.ps1 -SignedDriverFolder.

    This script never imports certificates into a trust store and never changes
    boot configuration.
#>

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$sourceRoot = Join-Path $PSScriptRoot 'Ssap3Aap'
$sys = Join-Path $sourceRoot 'bin-msvc\ssap3aap.sys'
$pdb = Join-Path $sourceRoot 'bin-msvc\ssap3aap.pdb'
$inf = Join-Path $sourceRoot 'ssap3aap.inf'
$packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$wdk = Join-Path $packages 'microsoft.windows.wdk.x64\10.0.26100.6584\c'
$sdk = Join-Path $packages 'microsoft.windows.sdk.cpp\10.0.26100.1\c'
$inf2Cat = Join-Path $wdk 'bin\10.0.26100.0\x86\Inf2Cat.exe'
$signTool = Join-Path $sdk 'bin\10.0.26100.0\x64\signtool.exe'

foreach ($required in @($sys, $inf)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Build the driver first: $required is missing." }
}

if ($AttestationCab) {
    $root = Join-Path $repositoryRoot 'artifacts\attestation'
    $stage = Join-Path $root 'ssap3aap'
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Copy-Item -LiteralPath $sys, $inf -Destination $stage -Force
    if (Test-Path -LiteralPath $pdb) { Copy-Item -LiteralPath $pdb -Destination $stage -Force }
    $ddf = Join-Path $root 'ssap3aap.ddf'
    $lines = @(
        '.OPTION EXPLICIT', '.Set CabinetFileCountThreshold=0', '.Set FolderFileCountThreshold=0',
        '.Set FolderSizeThreshold=0', '.Set MaxCabinetSize=0', '.Set MaxDiskFileCount=0', '.Set MaxDiskSize=0',
        '.Set CompressionType=MSZIP', '.Set Cabinet=on', '.Set Compress=on',
        '.Set CabinetNameTemplate=ssap3aap.cab', ".Set DiskDirectoryTemplate=$root", '.Set DestinationDir=ssap3aap')
    $lines += Get-ChildItem -LiteralPath $stage -File | ForEach-Object { '"{0}"' -f $_.FullName }
    $lines | Set-Content -LiteralPath $ddf -Encoding ASCII
    & (Join-Path $env:SystemRoot 'System32\makecab.exe') /f $ddf | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "makecab failed with exit code $LASTEXITCODE." }
    Write-Output "Attestation CAB: $(Join-Path $root 'ssap3aap.cab'). Sign it with the EV certificate, then submit it in Partner Center."
    return
}

if (-not $DevCertificateThumbprint) {
    throw 'Specify -AttestationCab (production) or -DevCertificateThumbprint (development PC only).'
}

$certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$DevCertificateThumbprint"
if ($certificate.Subject -ne 'CN=SSAP3 Local Development (this PC only)') {
    throw 'Refusing to sign with a certificate that tools\dev\New-DevTestCertificate.ps1 did not create.'
}

$out = Join-Path $repositoryRoot 'artifacts\dev-driver'
New-Item -ItemType Directory -Force -Path $out | Out-Null
Copy-Item -LiteralPath $sys, $inf -Destination $out -Force
& $signTool sign /fd SHA256 /sha1 $DevCertificateThumbprint (Join-Path $out 'ssap3aap.sys')
if ($LASTEXITCODE -ne 0) { throw 'signtool failed for the SYS.' }
& $inf2Cat "/driver:$out" /os:10_VB_X64,10_CO_X64,10_NI_X64,10_GE_X64
if ($LASTEXITCODE -ne 0) { throw 'Inf2Cat failed.' }
& $signTool sign /fd SHA256 /sha1 $DevCertificateThumbprint (Join-Path $out 'ssap3aap.cat')
if ($LASTEXITCODE -ne 0) { throw 'signtool failed for the CAT.' }
Write-Output "DEVELOPMENT package (test-signed, not for distribution): $out"
