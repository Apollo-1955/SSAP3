[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$audioRoot = $PSScriptRoot
$source = Join-Path $audioRoot 'SoundStageSpatializer\spatializer_smoke.cpp'
$plugin = Join-Path $audioRoot 'bin\SoundStageSpatializer.dll'
$output = Join-Path $audioRoot 'obj\spatializer_smoke.exe'
$vswhere =
    'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'

$visualStudioRoot = & $vswhere `
    -latest `
    -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath
if (-not $visualStudioRoot) {
    throw 'Microsoft Visual C++ Build Tools were not found.'
}

$vcVars = Join-Path $visualStudioRoot 'VC\Auxiliary\Build\vcvars64.bat'
$command = (('"{0}" && cl.exe /nologo /std:c++20 /EHsc /O2 /W4 ' +
    '/I"{1}" /Fe:"{2}" "{3}"') -f `
        $vcVars, `
        (Join-Path $audioRoot 'SoundStageSpatializer'), `
        $output, `
        $source)
& $env:ComSpec /d /s /c $command
if ($LASTEXITCODE -ne 0) {
    throw "Spatializer smoke-test build failed with code $LASTEXITCODE."
}

& $output $plugin
if ($LASTEXITCODE -ne 0) {
    throw "Spatializer smoke test failed with code $LASTEXITCODE."
}
