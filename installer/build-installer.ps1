[CmdletBinding()]
param(
    [string]$Version = '0.5.0',
    [switch]$SkipTests,
    # Path to a Microsoft-signed driver package folder (ssap3aap.inf/.sys/.cat) returned
    # by attestation signing. Without it, the package ships WITHOUT the AAP driver.
    [string]$SignedDriverFolder,
    # Folder with the unmodified Equalizer APO 1.4.2 x64 runtime files (optional).
    [string]$EqualizerApoFolder
)

<#
    Builds SSAP3-Setup-<version>.exe. Unlike the upstream build, this script never
    creates or trusts certificates and never test-signs anything (SECURITY_AUDIT.md
    S-01/S-03). Output goes to .\artifacts\.
#>

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $repositoryRoot 'artifacts'
$packageRoot = Join-Path $artifacts 'package'
$dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
$iscc = @(
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe',
    (Join-Path $env:LocalAppData 'Programs\Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1

function Invoke-Checked([string]$FilePath, [string[]]$Arguments) {
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$FilePath failed with exit code $LASTEXITCODE." }
}

if (Test-Path -LiteralPath $packageRoot) { Remove-Item -LiteralPath $packageRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $packageRoot, "$packageRoot\setup", "$packageRoot\docs" | Out-Null

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
if (-not $SkipTests) {
    Invoke-Checked $dotnet @('test', (Join-Path $repositoryRoot 'tests\SSAP3.Core.Tests\SSAP3.Core.Tests.csproj'), '-c', 'Release')
}

Invoke-Checked $dotnet @(
    'publish', (Join-Path $repositoryRoot 'src\SSAP3.App\SSAP3.App.csproj'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=None', '-p:DebugSymbols=false', "-p:Version=$Version",
    '-o', (Join-Path $packageRoot 'app'))

# SoundVolumeView must match the pinned, reviewed hash (S-11).
$svv = Join-Path $packageRoot 'app\tools\SoundVolumeView\SoundVolumeView.exe'
if ((Get-FileHash -LiteralPath $svv -Algorithm SHA256).Hash -ne 'B5AF5BD60F7A29AF8CB4D8A566382B90F0FE07CAC97228D218CB913F3382D647') {
    throw 'SoundVolumeView.exe does not match the pinned SHA-256 in THIRD_PARTY.md.'
}

foreach ($file in 'Install-SSAP3.ps1', 'Uninstall-SSAP3.ps1', 'Ssap3Setup.psm1', 'Test-Ssap3SystemState.ps1') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $packageRoot 'setup') -Force
}
foreach ($file in 'LICENSE', 'THIRD_PARTY.md', 'README.md', 'SECURITY.md') {
    Copy-Item -LiteralPath (Join-Path $repositoryRoot $file) -Destination (Join-Path $packageRoot 'docs') -Force
}
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\INSTALL.md') -Destination (Join-Path $packageRoot 'docs') -Force

if ($SignedDriverFolder) {
    $catalog = Join-Path $SignedDriverFolder 'ssap3aap.cat'
    $signature = Get-AuthenticodeSignature -LiteralPath $catalog
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'CN=Microsoft Windows Hardware Compatibility Publisher') {
        throw "The driver catalog is not Microsoft-signed ($($signature.Status)). Production packages only carry attestation/WHQL-signed drivers."
    }
    New-Item -ItemType Directory -Force -Path (Join-Path $packageRoot 'driver') | Out-Null
    Copy-Item -Path (Join-Path $SignedDriverFolder 'ssap3aap.*') -Destination (Join-Path $packageRoot 'driver') -Force
}
else {
    Write-Warning 'No Microsoft-signed driver supplied: this package installs without the AAP driver (driverless tier).'
}

$spatializer = Join-Path $repositoryRoot 'audio\bin\SSAP3Spatializer.dll'
if ($EqualizerApoFolder -and (Test-Path -LiteralPath $spatializer)) {
    New-Item -ItemType Directory -Force -Path (Join-Path $packageRoot 'spatial\EqualizerAPO') | Out-Null
    Copy-Item -LiteralPath $spatializer -Destination (Join-Path $packageRoot 'spatial') -Force
    Copy-Item -Path (Join-Path $EqualizerApoFolder '*') -Destination (Join-Path $packageRoot 'spatial\EqualizerAPO') -Recurse -Force
    New-Item -ItemType Directory -Force -Path (Join-Path $packageRoot 'spatial\source') | Out-Null
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'third_party\EqualizerAPO\source\EqualizerAPO-src-1.4.2.zip') -Destination (Join-Path $packageRoot 'spatial\source') -Force
}
else {
    Write-Warning 'Spatial engine files not supplied: the opt-in engine task will fail cleanly if selected.'
}

& (Join-Path $repositoryRoot 'tools\release\New-Sbom.ps1') -PackageRoot $packageRoot -Version $Version -Output (Join-Path $artifacts "SSAP3-$Version.cdx.json")

$manifest = Get-ChildItem -LiteralPath $packageRoot -File -Recurse | Sort-Object FullName | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, $_.FullName.Substring($packageRoot.Length + 1).Replace('\', '/')
}
$manifest | Set-Content -LiteralPath (Join-Path $artifacts "SSAP3-$Version-package.sha256") -Encoding ASCII

if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) was not found.' }
Invoke-Checked $iscc @("/DSourceRoot=$packageRoot", "/DOutputRoot=$artifacts", "/DAppVersion=$Version", (Join-Path $PSScriptRoot 'SSAP3.iss'))
$setup = Join-Path $artifacts "SSAP3-Setup-$Version.exe"
"{0} *{1}" -f (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash, (Split-Path $setup -Leaf) | Set-Content -LiteralPath "$setup.sha256" -Encoding ASCII
Write-Output "Built $setup (unsigned: Authenticode signing needs the project's code-signing certificate)."
