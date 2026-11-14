#Requires -Version 5.1
<#
.SYNOPSIS
    Reverts every system change SSAP3 Setup recorded. Run by the SSAP3 uninstaller
    from %ProgramFiles%\SSAP3\setup (admin-only), never from a user-writable folder.

.DESCRIPTION
    Standard uninstall keeps the current user's settings and exported reports.
    -RemoveUserData (complete uninstall, confirmed in the uninstaller UI) also
    deletes %LocalAppData%\SSAP3 and Documents\SSAP3 for the user who runs it.
    The AirPods Bluetooth pairing is never touched. Running this twice is safe.
#>
[CmdletBinding()]
param(
    [switch]$RemoveUserData
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Ssap3Setup.psm1') -Force -DisableNameChecking

Assert-Ssap3Administrator
Assert-Ssap3AdminOnlyPath -Path $PSScriptRoot
$exitCode = 0
try {
    Write-Ssap3Log 'SSAP3 uninstall started.'
    Stop-Ssap3Processes

    $manifest = Read-Ssap3Manifest
    if ($null -ne $manifest) {
        Undo-Ssap3Manifest
    }
    else {
        # No manifest (already uninstalled or damaged): remove only objects that are
        # unambiguously SSAP3-owned by exact name. Nothing else is touched.
        Write-Ssap3Log 'No install manifest found: removing SSAP3-owned objects by exact name only.'
        Remove-Ssap3DriverPackages
        Remove-ItemProperty -LiteralPath $RunKey -Name 'SSAP3' -ErrorAction SilentlyContinue
        foreach ($key in @($SpatialRegistry, $ProductRegistry)) {
            Remove-Item -LiteralPath $key -Recurse -Force -ErrorAction SilentlyContinue
        }
        if ((Test-Path -LiteralPath $ProgramDataRoot) -and (Test-Ssap3TrustedOwner -Path $ProgramDataRoot)) {
            Remove-Item -LiteralPath $ProgramDataRoot -Recurse -Force
        }
    }

    if ($RemoveUserData) {
        foreach ($folder in @(
                (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SSAP3'),
                (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'SSAP3'))) {
            if (Test-Path -LiteralPath $folder) {
                Write-Ssap3Log "Complete uninstall: removing $folder."
                Remove-Item -LiteralPath $folder -Recurse -Force
            }
        }
    }

    $failed = @()
    if ($null -ne $manifest) { $failed = @($manifest.steps | Where-Object { $_.status -eq 'revert-failed' }) }
    if ($failed.Count -gt 0) {
        $exitCode = 2
        Write-Ssap3Log "$($failed.Count) change(s) could not be reverted; the manifest is kept for a retry."
    }
    else {
        Remove-Item -LiteralPath $ManifestPath -Force -ErrorAction SilentlyContinue
        Write-Ssap3Log 'SSAP3 uninstall completed. Run installer\Test-Ssap3SystemState.ps1 -Verify to confirm.'
        if (Test-Ssap3RestartRequired) {
            Write-Ssap3Log 'RESTART NEEDED: restart Windows to finish removing the SSAP3 driver.'
        }
        Remove-Item -LiteralPath (Join-Path $SetupRoot 'install.log') -Force -ErrorAction SilentlyContinue
    }
}
catch {
    $exitCode = 1
    Write-Ssap3Log "ERROR: $($_.Exception.Message)"
}

exit $exitCode
