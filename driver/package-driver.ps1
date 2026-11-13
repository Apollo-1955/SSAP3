[CmdletBinding()]
param(
    [string]$CertificateThumbprint
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$workspaceRoot = Split-Path $repositoryRoot -Parent
$projectRoot = Split-Path $workspaceRoot -Parent
$packageRoot = Join-Path $workspaceRoot '.nuget\packages'
$sourceRoot = Join-Path $PSScriptRoot 'Ssap3Aap'
$sourceSys = Join-Path $sourceRoot 'bin-msvc\ssap3aap.sys'
$sourceInf = Join-Path $sourceRoot 'ssap3aap.inf'
$stageRoot = Join-Path $workspaceRoot 'ssap3-package-stage'
$stageDriver = Join-Path $stageRoot 'driver'
$outputRoot = Join-Path $projectRoot 'outputs\SSAP3-Setup'
$outputDriver = Join-Path $outputRoot 'driver'
$outputSpatial = Join-Path $outputRoot 'spatial'
$outputEqualizer = Join-Path $outputSpatial 'EqualizerAPO'
$spatializerSource = Join-Path $repositoryRoot 'audio\bin\SSAP3Spatializer.dll'
$equalizerSource = Join-Path $repositoryRoot 'third_party\EqualizerAPO\bin'
$equalizerSourceArchive = Join-Path `
    $repositoryRoot `
    'third_party\EqualizerAPO\source\EqualizerAPO-src-1.4.2.zip'
$certificatePath = Join-Path $stageDriver 'ssap3-dev-test-driver.cer'
$wdk = Join-Path $packageRoot 'microsoft.windows.wdk.x64\10.0.26100.6584\c'
$sdk = Join-Path $packageRoot 'microsoft.windows.sdk.cpp\10.0.26100.1\c'
$infVerifier = Join-Path $wdk 'tools\10.0.26100.0\x64\infverif.exe'
$inf2Cat = Join-Path $wdk 'bin\10.0.26100.0\x86\Inf2Cat.exe'
$signTool = Join-Path $sdk 'bin\10.0.26100.0\x64\signtool.exe'
$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
$stageSys = Join-Path $stageDriver 'ssap3aap.sys'
$stageInf = Join-Path $stageDriver 'ssap3aap.inf'
$stageCat = Join-Path $stageDriver 'ssap3aap.cat'

New-Item -ItemType Directory -Force `
    -Path $stageDriver, $outputDriver, $outputSpatial, $outputEqualizer |
    Out-Null

foreach ($requiredPath in @(
        $sourceSys,
        $sourceInf,
        $certificatePath,
        $infVerifier,
        $inf2Cat,
        $signTool,
        $vswhere,
        $spatializerSource,
        $equalizerSourceArchive,
        (Join-Path $equalizerSource 'EqualizerAPO.dll'),
        (Join-Path $equalizerSource 'sndfile.dll'),
        (Join-Path $equalizerSource 'fftw3f.dll')
    )) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required package input was not found: $requiredPath"
    }
}

if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    $certificate = Get-ChildItem -Path Cert:\CurrentUser\My |
        Where-Object {
            $_.HasPrivateKey -and
            $_.Subject -eq 'CN=SSAP3 Development Test Driver' -and
            $_.NotAfter -gt (Get-Date)
        } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
    if ($null -eq $certificate) {
        throw (
            'A SSAP3 test-signing certificate with a private key was not found. ' +
            'Run driver\New-TestSigningCertificate.ps1 first.')
    }

    $CertificateThumbprint = $certificate.Thumbprint
}

$visualStudioRoot = & $vswhere `
    -latest `
    -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath

if (-not $visualStudioRoot) {
    throw 'Microsoft Visual C++ Build Tools were not found.'
}

$dumpBin = Get-ChildItem -LiteralPath (Join-Path $visualStudioRoot 'VC\Tools\MSVC') `
    -Recurse `
    -Filter dumpbin.exe |
    Where-Object { $_.FullName -match 'Hostx64\\x64' } |
    Sort-Object FullName -Descending |
    Select-Object -First 1 -ExpandProperty FullName

if (-not $dumpBin) {
    throw 'The x64 MSVC dumpbin.exe was not found.'
}

$peHeaders = (& $dumpBin /headers $sourceSys 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) {
    throw "dumpbin.exe failed with exit code $LASTEXITCODE."
}

if ($peHeaders -notmatch '6 number of sections' -or
    $peHeaders -notmatch '1 subsystem \(Native\)' -or
    $peHeaders -notmatch '4160 DLL characteristics' -or
    ([regex]::Matches($peHeaders, 'Not Paged')).Count -lt 4 -or
    ([regex]::Matches($peHeaders, 'Discardable')).Count -lt 2) {
    throw 'The SYS does not have the expected MSVC kernel-driver PE layout.'
}

$certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint" -ErrorAction Stop
if (-not $certificate.HasPrivateKey) {
    throw "Signing certificate $CertificateThumbprint does not have a private key."
}

& $infVerifier /w $sourceInf
if ($LASTEXITCODE -ne 0) {
    throw "InfVerif failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath $sourceSys -Destination $stageSys -Force
Copy-Item -LiteralPath $sourceInf -Destination $stageInf -Force
if (Test-Path -LiteralPath $stageCat) {
    Remove-Item -LiteralPath $stageCat -Force
}

& $signTool sign `
    /v `
    /s My `
    /sha1 $CertificateThumbprint `
    /fd SHA256 `
    $stageSys
if ($LASTEXITCODE -ne 0) {
    throw "SignTool failed to sign the SYS with exit code $LASTEXITCODE."
}

& $inf2Cat `
    "/driver:$stageDriver" `
    /os:10_VB_X64,10_CO_X64,10_NI_X64,10_GE_X64 `
    /verbose
if ($LASTEXITCODE -ne 0) {
    throw "Inf2Cat failed with exit code $LASTEXITCODE."
}

& $signTool sign `
    /v `
    /s My `
    /sha1 $CertificateThumbprint `
    /fd SHA256 `
    $stageCat
if ($LASTEXITCODE -ne 0) {
    throw "SignTool failed to sign the CAT with exit code $LASTEXITCODE."
}

foreach ($signedPath in @($stageSys, $stageCat)) {
    & $signTool verify /v /pa $signedPath
    if ($LASTEXITCODE -ne 0) {
        throw "SignTool verification failed for $signedPath with exit code $LASTEXITCODE."
    }
}

Copy-Item -LiteralPath $stageSys -Destination (Join-Path $outputDriver 'ssap3aap.sys') -Force
Copy-Item -LiteralPath $stageInf -Destination (Join-Path $outputDriver 'ssap3aap.inf') -Force
Copy-Item -LiteralPath $stageCat -Destination (Join-Path $outputDriver 'ssap3aap.cat') -Force
Copy-Item -LiteralPath $certificatePath -Destination (Join-Path $outputDriver 'ssap3-dev-test-driver.cer') -Force
Copy-Item -LiteralPath $spatializerSource `
    -Destination (Join-Path $outputSpatial 'SSAP3Spatializer.dll') `
    -Force
Get-ChildItem -LiteralPath $equalizerSource -File |
    ForEach-Object {
        Copy-Item -LiteralPath $_.FullName `
            -Destination (Join-Path $outputEqualizer $_.Name) `
            -Force
    }
$sourceOutput = Join-Path $outputSpatial 'source'
New-Item -ItemType Directory -Force -Path $sourceOutput | Out-Null
Copy-Item -LiteralPath $equalizerSourceArchive `
    -Destination (Join-Path $sourceOutput 'EqualizerAPO-src-1.4.2.zip') `
    -Force

$manifestPath = Join-Path $outputRoot 'SHA256SUMS.txt'
$manifestLines = Get-ChildItem -LiteralPath $outputRoot -File -Recurse |
    Where-Object { $_.FullName -ne $manifestPath } |
    Sort-Object FullName |
    ForEach-Object {
        $relativePath = $_.FullName.Substring($outputRoot.Length + 1).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        "$hash  $relativePath"
    }

$manifestLines | Set-Content -LiteralPath $manifestPath -Encoding ASCII

Write-Output "Packaged driver version: $((Select-String -LiteralPath $stageInf -Pattern '^DriverVer=').Line)"
Write-Output "SYS SHA256: $((Get-FileHash -LiteralPath $stageSys -Algorithm SHA256).Hash)"
Write-Output "CAT SHA256: $((Get-FileHash -LiteralPath $stageCat -Algorithm SHA256).Hash)"
Write-Output "Output: $outputDriver"
