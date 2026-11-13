#Requires -Version 5.1
<#
.SYNOPSIS
    Removes an SSAP3 installed with Install-SSAP3Local.ps1.

.DESCRIPTION
    Run from an elevated PowerShell.
    1. Runs %ProgramFiles%\SSAP3\setup\Uninstall-SSAP3.ps1, which reverts every
       recorded system change (audio effects, registry values, Equalizer APO
       permissions and update-checker task, runtime folder, autostart).
    2. Only if that succeeds: removes the Start-menu entry and %ProgramFiles%\SSAP3.
    3. Runs the read-only verifier.
    Equalizer APO itself stays installed (it was installed by you, not by SSAP3);
    remove it from Settings > Apps if you no longer want it.

.EXAMPLE
    .\installer\Uninstall-SSAP3Local.ps1            # keeps your SSAP3 settings
    .\installer\Uninstall-SSAP3Local.ps1 -RemoveUserData
#>
[CmdletBinding()]
param([switch]$RemoveUserData)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this from an elevated PowerShell (Run as administrator).'
}

$installRoot = Join-Path $env:ProgramFiles 'SSAP3'
$uninstall = Join-Path $installRoot 'setup\Uninstall-SSAP3.ps1'
if (Test-Path -LiteralPath $uninstall -PathType Leaf) {
    $arguments = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $uninstall)
    if ($RemoveUserData) { $arguments += '-RemoveUserData' }
    & (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "System cleanup reported exit code $LASTEXITCODE. Files were kept so it can be retried; see $installRoot\setup\install.log."
    }
}

Remove-Item -LiteralPath (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\SSAP3.lnk') -Force -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $installRoot) {
    $owner = (Get-Acl -LiteralPath $installRoot).GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($owner -notin @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')) {
        throw "$installRoot is owned by $owner, not by Administrators; it was left in place."
    }
    Remove-Item -LiteralPath $installRoot -Recurse -Force
}

& (Join-Path $PSScriptRoot 'Test-Ssap3SystemState.ps1') -Verify
exit $LASTEXITCODE
