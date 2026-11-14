#Requires -Version 5.1
<#
.SYNOPSIS
    SSAP3 system configuration step, run elevated by SSAP3-Setup.exe after it copies
    the application into %ProgramFiles%\SSAP3.

.DESCRIPTION
    Production install rules:
      * No Test Mode, BCD, Secure Boot, BitLocker, Defender, Memory Integrity or
        certificate-store changes. Ever.
      * The AAP driver is installed only if its catalog is signed by Microsoft
        (attestation/WHQL). Otherwise it is skipped and the app reports the AAP
        features as unavailable.
      * The system-wide spatial engine is installed only with -EnableSpatialEngine
        (explicit user consent in Setup).
      * Every change is recorded in setup\install-manifest.json before it is made;
        any failure rolls back everything done so far.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [switch]$EnableSpatialEngine,
    [switch]$Autostart
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Ssap3Setup.psm1') -Force -DisableNameChecking

Assert-Ssap3Administrator
$packageRoot = Split-Path $PSScriptRoot -Parent
if ([IO.Path]::GetFullPath($packageRoot).TrimEnd('\') -ne [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')) {
    throw "Install-SSAP3.ps1 must run from $InstallRoot\setup (found $PSScriptRoot)."
}
Assert-Ssap3AdminOnlyPath -Path $PSScriptRoot

$exitCode = 0
try {
    Write-Ssap3Log "SSAP3 $Version system configuration started."

    $os = Get-CimInstance Win32_OperatingSystem
    if ($os.OSArchitecture -notmatch '64' -or [int]$os.BuildNumber -lt 19041) {
        throw "SSAP3 needs x64 Windows 10 build 19041 or Windows 11 (found build $($os.BuildNumber), $($os.OSArchitecture))."
    }

    # Refuse to stack on the upstream test-mode product: its driver, Equalizer APO
    # chaining and root certificate must be removed by its own uninstaller first.
    foreach ($legacy in @((Join-Path $env:ProgramFiles 'SoundStage'), 'HKLM:\SOFTWARE\SoundStage')) {
        if (Test-Path -LiteralPath $legacy) {
            throw "SoundStage for AirPods is installed ($legacy). Uninstall it from Settings > Apps first, then run SSAP3 Setup again."
        }
    }

    $existing = Read-Ssap3Manifest
    if ($null -ne $existing) {
        Write-Ssap3Log "Updating an existing SSAP3 $($existing.version) installation: reverting its system changes first."
        Undo-Ssap3Manifest
    }

    New-Ssap3Manifest -Version $Version
    Install-Ssap3RuntimeFolder
    Set-Ssap3RegistryValue -Path $ProductRegistry -Name 'Version' -Value $Version -Kind String -Reason 'Product registration'
    Set-Ssap3RegistryValue -Path $ProductRegistry -Name 'InstallPath' -Value $InstallRoot -Kind String -Reason 'Product registration'

    $driverInstalled = Install-Ssap3Driver -PackageRoot $packageRoot
    Set-Ssap3RegistryValue -Path $ProductRegistry -Name 'AapDriverInstalled' -Value ([int]$driverInstalled) -Kind DWord -Reason 'Product registration'

    if ($EnableSpatialEngine) {
        Install-Ssap3SpatialEngine -PackageRoot $packageRoot
    }
    else {
        Write-Ssap3Log 'System-wide spatial engine not selected: no audio-engine settings were changed.'
    }

    if ($Autostart) {
        Set-Ssap3RegistryValue -Path $RunKey -Name 'SSAP3' -Value ('"{0}" --tray' -f (Join-Path $InstallRoot 'SSAP3.exe')) -Kind String -Reason 'Start SSAP3 at sign-in (selected in Setup)'
    }

    Write-Ssap3Log 'SSAP3 system configuration completed.'
    if (Test-Ssap3RestartRequired) {
        Write-Ssap3Log 'RESTART NEEDED: restart Windows to finish installing the SSAP3 driver.'
    }
}
catch {
    $exitCode = 1
    Write-Ssap3Log "ERROR: $($_.Exception.Message)"
    Write-Ssap3Log 'Rolling back every recorded change...'
    Undo-Ssap3Manifest
    Remove-Item -LiteralPath $ManifestPath -Force -ErrorAction SilentlyContinue
    Write-Ssap3Log 'Rollback finished. Windows was left as it was before Setup.'
}

exit $exitCode
