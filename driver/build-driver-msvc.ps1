[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$workspaceRoot = Split-Path $repositoryRoot -Parent
$packageRoot = Join-Path $workspaceRoot '.nuget\packages'
$sourceRoot = Join-Path $PSScriptRoot 'Ssap3Aap'
$objectRoot = Join-Path $sourceRoot 'obj-msvc'
$binaryRoot = Join-Path $sourceRoot 'bin-msvc'
$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'

if (-not (Test-Path -LiteralPath $vswhere)) {
    throw 'Visual Studio Installer (vswhere.exe) was not found.'
}

$visualStudioRoot = & $vswhere `
    -latest `
    -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath

if (-not $visualStudioRoot) {
    $visualStudioRoot = @(
        & $vswhere `
            -all `
            -products * `
            -property installationPath
    ) | Select-Object -First 1
}

if (-not $visualStudioRoot) {
    throw 'Microsoft Visual C++ Build Tools were not found.'
}

$msvcRoot = Get-ChildItem -LiteralPath (Join-Path $visualStudioRoot 'VC\Tools\MSVC') -Directory |
    Sort-Object { [version]$_.Name } -Descending |
    Select-Object -First 1 -ExpandProperty FullName

$toolBin = @(
    (Join-Path $msvcRoot 'bin\Hostx64\x64'),
    (Join-Path $msvcRoot 'bin\Hostx86\x64')
) | Where-Object {
    Test-Path -LiteralPath (Join-Path $_ 'cl.exe')
} | Select-Object -First 1

if (-not $toolBin) {
    throw "An x64-targeting MSVC tool directory was not found under $msvcRoot."
}

$compiler = Join-Path $toolBin 'cl.exe'
$linker = Join-Path $toolBin 'link.exe'
$wdk = Join-Path $packageRoot 'microsoft.windows.wdk.x64\10.0.26100.6584\c'
$sdk = Join-Path $packageRoot 'microsoft.windows.sdk.cpp\10.0.26100.1\c'

foreach ($requiredPath in @($compiler, $linker, $wdk, $sdk)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required build dependency was not found: $requiredPath"
    }
}

New-Item -ItemType Directory -Force -Path $objectRoot, $binaryRoot | Out-Null

$compilerArguments = @(
    '/nologo', '/c', '/TC', '/kernel', '/O2', '/Oi', '/Gy', '/W4', '/WX',
    '/GS', '/guard:cf', '/Zp8', '/Zc:wchar_t', '/Zc:inline',
    '/wd4201', '/wd4214', '/wd4324',
    '/D_AMD64_', '/DAMD64', '/D_WIN64', '/DWIN64', '/D_WINDOWS',
    '/DNTDDI_VERSION=0x0A000008',
    '/D_WIN32_WINNT=0x0A00',
    '/DWINVER=0x0A00',
    '/DKMDF_VERSION_MAJOR=1',
    '/DKMDF_VERSION_MINOR=31',
    '/DKMDF_MINIMUM_VERSION_REQUIRED=31',
    '/DUNICODE', '/D_UNICODE', '/DPOOL_NX_OPTIN=1',
    "/I$wdk\Include\10.0.26100.0\km",
    "/I$wdk\Include\10.0.26100.0\km\crt",
    "/I$wdk\Include\10.0.26100.0\shared",
    "/I$wdk\Include\10.0.26100.0\um",
    "/I$wdk\Include\wdf\kmdf\1.31",
    "/I$sdk\Include\10.0.26100.0\ucrt",
    "/I$sdk\Include\10.0.26100.0\shared"
)

foreach ($fileName in @('driver.c', 'queue.c', 'transport_bth_l2cap.c')) {
    $baseName = [IO.Path]::GetFileNameWithoutExtension($fileName)
    & $compiler @compilerArguments `
        "/Fo$objectRoot\$baseName.obj" `
        "$sourceRoot\$fileName"

    if ($LASTEXITCODE -ne 0) {
        throw "cl.exe failed for $fileName with exit code $LASTEXITCODE."
    }
}

$kernelLibraries = Join-Path $wdk 'Lib\10.0.26100.0\km\x64'
$wdfLibraries = Join-Path $wdk 'Lib\wdf\kmdf\x64\1.31'
$driverPath = Join-Path $binaryRoot 'ssap3aap.sys'
$pdbPath = Join-Path $binaryRoot 'ssap3aap.pdb'
$mapPath = Join-Path $binaryRoot 'ssap3aap.map'

& $linker `
    /nologo /driver /kernel /subsystem:native,10.00 /entry:FxDriverEntry `
    /machine:x64 /nodefaultlib /incremental:no /opt:ref /opt:icf `
    /dynamicbase /nxcompat /guard:cf /release `
    /merge:_TEXT=.text /merge:_PAGE=PAGE `
    "/out:$driverPath" `
    "/pdb:$pdbPath" `
    "/map:$mapPath" `
    "$objectRoot\driver.obj" `
    "$objectRoot\queue.obj" `
    "$objectRoot\transport_bth_l2cap.obj" `
    "/libpath:$kernelLibraries" `
    "/libpath:$wdfLibraries" `
    wdfdriverentry.lib wdfldr.lib ntoskrnl.lib hal.lib wmilib.lib `
    BufferOverflowFastFailK.lib ntstrsafe.lib

if ($LASTEXITCODE -ne 0) {
    throw "link.exe failed with exit code $LASTEXITCODE."
}

$infVerifier = Join-Path $wdk 'tools\10.0.26100.0\x64\infverif.exe'
$infPath = Join-Path $sourceRoot 'ssap3aap.inf'
& $infVerifier /w $infPath

if ($LASTEXITCODE -ne 0) {
    throw "InfVerif failed with exit code $LASTEXITCODE."
}

$hash = Get-FileHash -LiteralPath $driverPath -Algorithm SHA256
Write-Output "Compiler: $compiler"
Write-Output "Linker: $linker"
Write-Output "Built: $driverPath"
Write-Output "SHA256: $($hash.Hash)"
