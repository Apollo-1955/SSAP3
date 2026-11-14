[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$sourceRoot = Join-Path $PSScriptRoot 'SSAP3Spatializer'
$binaryRoot = Join-Path $PSScriptRoot 'bin'
$objectRoot = Join-Path $PSScriptRoot 'obj'
$vswhere =
    'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'

if (-not (Test-Path -LiteralPath $vswhere)) {
    throw 'Visual Studio Installer (vswhere.exe) was not found.'
}

$visualStudioRoot = & $vswhere `
    -latest `
    -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath
if (-not $visualStudioRoot) {
    throw 'Microsoft Visual C++ Build Tools were not found.'
}

$msvcRoot = Get-ChildItem `
    -LiteralPath (Join-Path $visualStudioRoot 'VC\Tools\MSVC') `
    -Directory |
    Sort-Object { [version]$_.Name } -Descending |
    Select-Object -First 1 -ExpandProperty FullName
$toolBin = Join-Path $msvcRoot 'bin\Hostx64\x64'
$compiler = Join-Path $toolBin 'cl.exe'
$linker = Join-Path $toolBin 'link.exe'

$sdkRoot = 'C:\Program Files (x86)\Windows Kits\10'
$sdkVersion = Get-ChildItem `
    -LiteralPath (Join-Path $sdkRoot 'Include') `
    -Directory |
    Sort-Object { [version]$_.Name } -Descending |
    Select-Object -First 1 -ExpandProperty Name

foreach ($requiredPath in @($compiler, $linker, $sdkVersion)) {
    if (-not $requiredPath -or
        ($requiredPath -like '*:*' -and
         -not (Test-Path -LiteralPath $requiredPath))) {
        throw "Required spatializer build dependency was not found: $requiredPath"
    }
}

New-Item -ItemType Directory -Force -Path $binaryRoot, $objectRoot |
    Out-Null

$source = Join-Path $sourceRoot 'ssap3_spatializer.cpp'
$object = Join-Path $objectRoot 'ssap3_spatializer.obj'
$output = Join-Path $binaryRoot 'SSAP3Spatializer.dll'
$pdb = Join-Path $binaryRoot 'SSAP3Spatializer.pdb'
$sdkInclude = Join-Path $sdkRoot "Include\$sdkVersion"
$sdkLib = Join-Path $sdkRoot "Lib\$sdkVersion"

# Diagnostics are captured as text (Windows PowerShell 5.1 would turn native
# stderr into terminating errors) and, on GitHub Actions, also published as an
# ::error annotation so a failure is readable without signing in to the log.
$ErrorActionPreference = 'Continue'
$compilerOutput = & $compiler `
    /nologo /c /std:c++20 /O2 /Oi /GL /GS /guard:cf /EHsc /W4 /WX `
    /DUNICODE /D_UNICODE /DWIN32_LEAN_AND_MEAN /DNOMINMAX `
    "/I$sourceRoot" `
    "/I$(Join-Path $msvcRoot 'include')" `
    "/I$(Join-Path $sdkInclude 'ucrt')" `
    "/I$(Join-Path $sdkInclude 'shared')" `
    "/I$(Join-Path $sdkInclude 'um')" `
    "/Fo$object" `
    $source 2>&1 | ForEach-Object { "$_" }
$compilerExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
$compilerOutput | ForEach-Object { Write-Output $_ }
if ($compilerExit -ne 0) {
    if ($env:GITHUB_ACTIONS -eq 'true') {
        $diagnostics = @($compilerOutput | Where-Object { $_ -match '(error|warning) [A-Z]+\d+' })
        if ($diagnostics.Count -eq 0) { $diagnostics = @($compilerOutput | Select-Object -Last 20) }
        $text = (($diagnostics | Select-Object -First 25) | ForEach-Object { $_ -replace '^\s*[A-Za-z]:\\(?:[^\\(]+\\)*', '' }) -join "`n"
        $escaped = $text.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
        Write-Output "::error title=Spatial engine compile failed::$escaped"
    }
    throw "cl.exe failed with exit code $compilerExit."
}

& $linker `
    /nologo /dll /machine:x64 /subsystem:windows,10.00 `
    /dynamicbase /nxcompat /guard:cf /ltcg /opt:ref /opt:icf `
    "/out:$output" `
    "/pdb:$pdb" `
    $object `
    "/libpath:$(Join-Path $msvcRoot 'lib\x64')" `
    "/libpath:$(Join-Path $sdkLib 'ucrt\x64')" `
    "/libpath:$(Join-Path $sdkLib 'um\x64')" `
    kernel32.lib user32.lib advapi32.lib
if ($LASTEXITCODE -ne 0) {
    throw "link.exe failed with exit code $LASTEXITCODE."
}

$hash = Get-FileHash -LiteralPath $output -Algorithm SHA256
Write-Output "Built: $output"
Write-Output "SHA256: $($hash.Hash)"
