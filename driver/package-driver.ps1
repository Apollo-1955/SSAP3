[CmdletBinding()]
param(
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

    Development (test) signing on a dedicated development PC is a separate tool:
    tools\dev\Ssap3DevSigning.ps1 SignDriver -Package artifacts\attestation\ssap3aap

    This script never signs, never imports certificates into a trust store and
    never changes boot configuration.
#>

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$sourceRoot = Join-Path $PSScriptRoot 'Ssap3Aap'
$sys = Join-Path $sourceRoot 'bin-msvc\ssap3aap.sys'
$pdb = Join-Path $sourceRoot 'bin-msvc\ssap3aap.pdb'
$inf = Join-Path $sourceRoot 'ssap3aap.inf'
$packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$wdk = Join-Path $packages 'microsoft.windows.wdk.x64\10.0.26100.6584\c'
$inf2Cat = Join-Path $wdk 'bin\10.0.26100.0\x86\Inf2Cat.exe'

if (-not $AttestationCab) {
    throw 'Specify -AttestationCab. For development (test) signing on a dedicated development PC, use tools\dev\Ssap3DevSigning.ps1 (tools\dev\README.md).'
}

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
    # Read the stored paths from the CAB's CFFILE records ("expand -D" drops the
    # folder, and the folder layout is exactly what Partner Center checks).
    $bytes = [IO.File]::ReadAllBytes($cab)
    if ([Text.Encoding]::ASCII.GetString($bytes, 0, 4) -ne 'MSCF') { throw 'makecab did not produce a cabinet file.' }
    $offset = [int][BitConverter]::ToUInt32($bytes, 16)
    $fileCount = [BitConverter]::ToUInt16($bytes, 28)
    $entries = @()
    for ($i = 0; $i -lt $fileCount; $i++) {
        $nameStart = $offset + 16
        $nameEnd = [Array]::IndexOf($bytes, [byte]0, $nameStart)
        $entries += [Text.Encoding]::ASCII.GetString($bytes, $nameStart, $nameEnd - $nameStart)
        $offset = $nameEnd + 1
    }
    $expected = 'ssap3aap\ssap3aap.cat', 'ssap3aap\ssap3aap.inf', 'ssap3aap\ssap3aap.pdb', 'ssap3aap\ssap3aap.sys'
    $actual = @($entries | Sort-Object)
    if (($actual -join '|') -ne ($expected -join '|')) {
        throw "Unexpected CAB layout: $($actual -join ', ') (expected $($expected -join ', '))."
    }
    Remove-Item -LiteralPath $ddf, (Join-Path $root 'setup.inf'), (Join-Path $root 'setup.rpt') -Force -ErrorAction SilentlyContinue
    $hash = (Get-FileHash -LiteralPath $cab -Algorithm SHA256).Hash
    "$hash *ssap3aap.cab" | Set-Content -LiteralPath "$cab.sha256" -Encoding ASCII
    Write-Output "Attestation CAB (unsigned): $cab"
    Write-Output "Contents: $($expected -join ', ')"
    Write-Output "SHA-256: $hash"
    Write-Output 'Next: sign it with your EV certificate and submit it in Partner Center (driver\SIGNING.md).'
}
