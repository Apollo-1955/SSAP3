#Requires -Version 5.1
[CmdletBinding()]
param()

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

$guidValueNames = @(
    '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},1',
    '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},2',
    '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},5',
    '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},6',
    '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7'
)
$spatialRegistry = 'HKLM:\SOFTWARE\SSAP3\SpatialAudio'
$originalEffectsRoot = Join-Path $spatialRegistry 'OriginalEffects'
$equalizerRegistry = 'HKLM:\SOFTWARE\EqualizerAPO'
$childApoRoot = Join-Path $equalizerRegistry 'Child APOs'
$renderRoot =
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render'
$equalizerRoot = Join-Path $env:ProgramFiles 'EqualizerAPO'

function Get-PropertyValue {
    param($Object, [string]$Name)
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

$spatialState = Get-ItemProperty `
    -LiteralPath $spatialRegistry `
    -ErrorAction SilentlyContinue
if ($null -eq $spatialState) {
    return
}

$audioService = Get-Service -Name Audiosrv
$restartAudioService = [string]$audioService.Status -ne 'Stopped'
if ($restartAudioService) {
    Stop-Service -Name Audiosrv -Force
}

try {
    $endpointGuids = @($spatialState.EndpointGuids)
    foreach ($endpointGuid in $endpointGuids) {
        $fxPath = Join-Path (Join-Path $renderRoot $endpointGuid) 'FxProperties'
        $childPath = Join-Path $childApoRoot $endpointGuid
        $originalPath = Join-Path $originalEffectsRoot $endpointGuid
        $backupPath = if (Test-Path -LiteralPath $originalPath) {
            $originalPath
        }
        else {
            $childPath
        }
        if (-not (Test-Path -LiteralPath $fxPath) -or
            -not (Test-Path -LiteralPath $backupPath)) {
            continue
        }

        $original = Get-ItemProperty -LiteralPath $backupPath
        foreach ($name in $guidValueNames) {
            $value = [string](Get-PropertyValue $original $name)
            if ($value -eq '!VALUE' -or
                [string]::IsNullOrWhiteSpace($value)) {
                Remove-ItemProperty `
                    -LiteralPath $fxPath `
                    -Name $name `
                    -ErrorAction SilentlyContinue
            }
            else {
                New-ItemProperty `
                    -LiteralPath $fxPath `
                    -Name $name `
                    -PropertyType String `
                    -Value $value `
                    -Force | Out-Null
            }
        }
        Remove-Item `
            -LiteralPath $childPath `
            -Recurse `
            -Force `
            -ErrorAction SilentlyContinue
    }

    $configPath = Join-Path $equalizerRoot 'config\config.txt'
    if (Test-Path -LiteralPath $configPath) {
        $lines = Get-Content -LiteralPath $configPath
        @($lines | Where-Object {
            $_ -notmatch '(?i)^\s*Include:\s*ssap3\.txt\s*$' -and
            $_ -notmatch '(?i)^\s*#\s*Managed by SSAP3\s*$'
        }) | Set-Content -LiteralPath $configPath -Encoding UTF8
    }

    Remove-Item `
        -LiteralPath (Join-Path $equalizerRoot 'config\ssap3.txt') `
        -Force `
        -ErrorAction SilentlyContinue
    Remove-Item `
        -LiteralPath (
            Join-Path $equalizerRoot 'VSTPlugins\SSAP3Spatializer.dll') `
        -Force `
        -ErrorAction SilentlyContinue

    if ([int]$spatialState.OwnsEqualizerApo -eq 1) {
        $regsvr32 = Join-Path $env:SystemRoot 'System32\regsvr32.exe'
        $equalizerDll = Join-Path $equalizerRoot 'EqualizerAPO.dll'
        if (Test-Path -LiteralPath $equalizerDll) {
            Start-Process `
                -FilePath $regsvr32 `
                -ArgumentList ('/u /s "{0}"' -f $equalizerDll) `
                -Wait
        }
        if (Test-Path -LiteralPath $equalizerRoot) {
            Remove-Item -LiteralPath $equalizerRoot -Recurse -Force
        }
        Remove-Item -LiteralPath $equalizerRegistry -Recurse -Force
        Remove-ItemProperty `
            -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Audio' `
            -Name 'DisableProtectedAudioDG' `
            -ErrorAction SilentlyContinue
    }

    Remove-Item -LiteralPath $spatialRegistry -Recurse -Force
}
finally {
    if ($restartAudioService) {
        Start-Service -Name Audiosrv
    }
}
