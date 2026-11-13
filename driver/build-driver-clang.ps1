[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

throw @'
This build path is intentionally disabled. lld-link produces a kernel image
whose pageable/discardable section flags are rejected by Windows 10.
Use driver\build-driver-msvc.ps1; package-driver.ps1 accepts only bin-msvc.
'@

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$workspaceRoot = Split-Path $repositoryRoot -Parent
$toolRoot = Join-Path $workspaceRoot '.llvm-toolchain-22.1.8'
$packageRoot = Join-Path $workspaceRoot '.nuget\packages'
$sourceRoot = Join-Path $PSScriptRoot 'Ssap3Aap'
$objectRoot = Join-Path $sourceRoot 'obj-clang'
$binaryRoot = Join-Path $sourceRoot 'bin-clang'

$clang = Get-ChildItem $toolRoot -Recurse -Filter clang-cl.exe |
    Select-Object -First 1 -ExpandProperty FullName
$linker = Get-ChildItem $toolRoot -Recurse -Filter lld-link.exe |
    Select-Object -First 1 -ExpandProperty FullName
$wdk = Join-Path $packageRoot 'microsoft.windows.wdk.x64\10.0.26100.6584\c'
$sdk = Join-Path $packageRoot 'microsoft.windows.sdk.cpp\10.0.26100.1\c'

if (-not $clang -or -not $linker) {
    throw 'Portable LLVM 22.1.8 was not found under work\.llvm-toolchain-22.1.8.'
}

if (-not (Test-Path -LiteralPath $wdk) -or -not (Test-Path -LiteralPath $sdk)) {
    throw 'Microsoft WDK/SDK NuGet packages have not been restored.'
}

New-Item -ItemType Directory -Force -Path $objectRoot, $binaryRoot | Out-Null

$compilerArguments = @(
    '/nologo', '/c', '/TC', '/kernel', '/O2', '/W4', '/WX', '/GS', '/Zp8',
    '-Wno-unknown-pragmas',
    '-Wno-nonportable-include-path',
    '-Wno-ignored-pragma-intrinsic',
    '-Wno-pragma-pack',
    '-Wno-microsoft-enum-forward-reference',
    '-Wno-visibility',
    '-Wno-deprecated-declarations',
    '-Wno-microsoft-anon-tag',
    '-Wno-unused-but-set-variable',
    '-Wno-switch',
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
    & $clang @compilerArguments `
        "/Fo$objectRoot\$baseName.obj" `
        "$sourceRoot\$fileName"
    if ($LASTEXITCODE -ne 0) {
        throw "clang-cl failed for $fileName with exit code $LASTEXITCODE."
    }
}

$kernelLibraries = Join-Path $wdk 'Lib\10.0.26100.0\km\x64'
$wdfLibraries = Join-Path $wdk 'Lib\wdf\kmdf\x64\1.31'
$driverPath = Join-Path $binaryRoot 'ssap3aap.sys'

& $linker `
    /nologo /driver /subsystem:native,10.00 /entry:FxDriverEntry `
    /machine:x64 /nodefaultlib /opt:ref /opt:icf `
    /dynamicbase /nxcompat /guard:cf /release `
    "/out:$driverPath" `
    "$objectRoot\driver.obj" `
    "$objectRoot\queue.obj" `
    "$objectRoot\transport_bth_l2cap.obj" `
    "/libpath:$kernelLibraries" `
    "/libpath:$wdfLibraries" `
    wdfdriverentry.lib wdfldr.lib ntoskrnl.lib hal.lib `
    BufferOverflowK.lib ntstrsafe.lib
if ($LASTEXITCODE -ne 0) {
    throw "lld-link failed with exit code $LASTEXITCODE."
}

$infVerifier = Join-Path $wdk 'tools\10.0.26100.0\x64\infverif.exe'
$infPath = Join-Path $sourceRoot 'ssap3aap.inf'
& $infVerifier /w $infPath
if ($LASTEXITCODE -ne 0) {
    throw "InfVerif failed with exit code $LASTEXITCODE."
}

$hash = Get-FileHash -LiteralPath $driverPath -Algorithm SHA256
Write-Output "Built: $driverPath"
Write-Output "SHA256: $($hash.Hash)"
Write-Warning 'The driver is not production-signed. Do not install this preview.'
