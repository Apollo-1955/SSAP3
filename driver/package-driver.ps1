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
    # Layout required by Partner Center attestation signing (learn.microsoft.com,
    # "Attestation sign Windows drivers"): one driver per subfolder, no files at the
    # CAB root, folder name under 40 characters, and the SYS, INF, PDB (needed for
    # Microsoft's crash analysis) and a CAT. Microsoft replaces the CAT with its own
    # signed catalog; ours is unsigned and only satisfies the package format.
    if (-not (Test-Path -LiteralPath $pdb)) { throw "Partner Center needs the symbol file: $pdb is missing." }
    if (-not (Test-Path -LiteralPath $inf2Cat)) {
        $found = Get-ChildItem -LiteralPath $wdk -Recurse -Filter 'Inf2Cat.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $found) { throw "Inf2Cat.exe was not found in the WDK package ($wdk). Run: dotnet restore driver\WdkPackages.proj" }
        $inf2Cat = $found.FullName
    }
    $root = Join-Path $repositoryRoot 'artifacts\attestation'
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
    $stage = Join-Path $root 'ssap3aap'
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Copy-Item -LiteralPath $sys, $inf, $pdb -Destination $stage -Force
    & $inf2Cat "/driver:$stage" /os:10_VB_X64,10_CO_X64,10_NI_X64,10_GE_X64
    if ($LASTEXITCODE -ne 0) { throw "Inf2Cat failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path -LiteralPath (Join-Path $stage 'ssap3aap.cat'))) { throw 'Inf2Cat did not produce ssap3aap.cat.' }

    $ddf = Join-Path $root 'ssap3aap.ddf'
    $lines = @(
        '.OPTION EXPLICIT', '.Set CabinetFileCountThreshold=0', '.Set FolderFileCountThreshold=0',
        '.Set FolderSizeThreshold=0', '.Set MaxCabinetSize=0', '.Set MaxDiskFileCount=0', '.Set MaxDiskSize=0',
        '.Set CompressionType=MSZIP', '.Set Cabinet=on', '.Set Compress=on',
        '.Set CabinetNameTemplate=ssap3aap.cab', ".Set DiskDirectoryTemplate=$root", '.Set DestinationDir=ssap3aap')
    $lines += Get-ChildItem -LiteralPath $stage -File | Sort-Object Name | ForEach-Object { '"{0}"' -f $_.FullName }
    $lines | Set-Content -LiteralPath $ddf -Encoding ASCII
    Push-Location $root
    try { & (Join-Path $env:SystemRoot 'System32\makecab.exe') /f $ddf | Out-Null } finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw "makecab failed with exit code $LASTEXITCODE." }

    $cab = Join-Path $root 'ssap3aap.cab'
    $listing = & (Join-Path $env:SystemRoot 'System32\expand.exe') -D $cab
    $expected = 'ssap3aap\ssap3aap.cat', 'ssap3aap\ssap3aap.inf', 'ssap3aap\ssap3aap.pdb', 'ssap3aap\ssap3aap.sys'
    foreach ($entry in $expected) {
        if (-not ($listing | Where-Object { $_ -match [regex]::Escape($entry) })) { throw "The CAB is missing $entry." }
    }
    Remove-Item -LiteralPath $ddf, (Join-Path $root 'setup.inf'), (Join-Path $root 'setup.rpt') -Force -ErrorAction SilentlyContinue
    $hash = (Get-FileHash -LiteralPath $cab -Algorithm SHA256).Hash
    "$hash *ssap3aap.cab" | Set-Content -LiteralPath "$cab.sha256" -Encoding ASCII
    Write-Output "Attestation CAB (unsigned): $cab"
    Write-Output "Contents: $($expected -join ', ')"
    Write-Output "SHA-256: $hash"
    Write-Output 'Next: sign it with your EV certificate and submit it in Partner Center (driver\SIGNING.md).'
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
