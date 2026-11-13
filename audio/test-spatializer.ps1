[CmdletBinding()]
param()

# Builds and runs the SSAP3 Spatial Engine smoke/robustness tests
# (audio/SSAP3Spatializer/spatializer_smoke.cpp) against the built plug-in.
# On GitHub Actions, failures are also emitted as ::error annotations so the
# message is visible without signing in to read the raw log.

$ErrorActionPreference = 'Stop'
$audioRoot = $PSScriptRoot
$source = Join-Path $audioRoot 'SSAP3Spatializer\spatializer_smoke.cpp'
$plugin = Join-Path $audioRoot 'bin\SSAP3Spatializer.dll'
$objectRoot = Join-Path $audioRoot 'obj'
$output = Join-Path $objectRoot 'spatializer_smoke.exe'
$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'

function Write-Failure([string]$Title, [string[]]$Lines) {
    $text = ($Lines | Where-Object { "$_".Trim() } | Select-Object -Last 40) -join "`n"
    Write-Output $text
    if ($env:GITHUB_ACTIONS -eq 'true') {
        $escaped = $text.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
        Write-Output "::error title=$Title::$escaped"
    }
}

$visualStudioRoot = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $visualStudioRoot) {
    throw 'Microsoft Visual C++ Build Tools were not found.'
}

New-Item -ItemType Directory -Force -Path $objectRoot | Out-Null
$vcVars = Join-Path $visualStudioRoot 'VC\Auxiliary\Build\vcvars64.bat'
$command = ('"{0}" >nul && cl.exe /nologo /std:c++20 /EHsc /O2 /W4 /I"{1}" /Fo"{2}\\" /Fe:"{3}" "{4}"' -f
    $vcVars, (Join-Path $audioRoot 'SSAP3Spatializer'), $objectRoot, $output, $source)
# Windows PowerShell 5.1 turns native stderr into terminating errors under 'Stop';
# capture it as text instead so the failure message can be reported.
$ErrorActionPreference = 'Continue'
$buildOutput = & $env:ComSpec /d /s /c $command 2>&1 | ForEach-Object { "$_" }
$buildExit = $LASTEXITCODE
$buildOutput | ForEach-Object { Write-Output $_ }
if ($buildExit -ne 0) {
    Write-Failure 'Spatializer test build failed' $buildOutput
    Write-Error "Spatializer smoke-test build failed with code $buildExit." -ErrorAction Continue
    exit 1
}

$testOutput = & $output $plugin 2>&1 | ForEach-Object { "$_" }
$testExit = $LASTEXITCODE
$testOutput | ForEach-Object { Write-Output $_ }
if ($testExit -ne 0) {
    Write-Failure 'Spatializer tests failed' $testOutput
    Write-Error "Spatializer smoke test failed with code $testExit." -ErrorAction Continue
    exit 1
}
