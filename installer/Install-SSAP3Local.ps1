#Requires -Version 5.1
<#
.SYNOPSIS
    Installs a staged SSAP3 package without Inno Setup (development and test PCs).
    The release path is SSAP3-Setup.exe; this script uses the same layout and the
    same elevated configuration step.

.DESCRIPTION
    Run from an elevated PowerShell after:
        .\installer\build-installer.ps1 -StageOnly

    1. Copies the package into %ProgramFiles%\SSAP3, which is admin-only by
       inheritance, and refuses a pre-existing folder that is not owned by
       SYSTEM or Administrators (S-02).
    2. Checks that every copied setup and engine file is admin-only.
    3. Runs %ProgramFiles%\SSAP3\setup\Install-SSAP3.ps1 from that copy. That
       step records every system change and rolls back on any failure.

    Nothing elevated is executed from the user-writable package folder; it is
    only read and copied. Remove everything with Uninstall-SSAP3Local.ps1.

    A transcript is written to <repository>\artifacts\install-local.log so the
    result can be read after the elevated window closes.

.EXAMPLE
    .\installer\Install-SSAP3Local.ps1 -EnableSpatialEngine
#>
[CmdletBinding()]
param(
    # Defaults to <repository>\artifacts\package. (Resolved below: Windows
    # PowerShell 5.1 leaves $PSScriptRoot empty in parameter defaults.)
    [string]$PackageRoot,
    [string]$Version = '0.5.0',
    [switch]$EnableSpatialEngine,
    [switch]$Autostart
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
if (-not $PackageRoot) { $PackageRoot = Join-Path $repositoryRoot 'artifacts\package' }

# Transcript in the (user-owned) artifacts folder. An elevated process writes
# there, so refuse a junction or symlink that could redirect the write, and
# replace any existing file instead of appending to it.
$logDirectory = Join-Path $repositoryRoot 'artifacts'
$logPath = Join-Path $logDirectory 'install-local.log'
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
foreach ($path in @($repositoryRoot, $logDirectory)) {
    if (((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to write the install log: $path is a link or junction."
    }
}
Remove-Item -LiteralPath $logPath -Force -ErrorAction SilentlyContinue
Start-Transcript -LiteralPath $logPath | Out-Null

$exitCode = 0
try {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this from an elevated PowerShell (Run as administrator).'
    }

    $installRoot = Join-Path $env:ProgramFiles 'SSAP3'
    $PackageRoot = (Resolve-Path -LiteralPath $PackageRoot).ProviderPath
    foreach ($required in 'app\SSAP3.exe', 'setup\Install-SSAP3.ps1', 'setup\Ssap3Setup.psm1', 'setup\Uninstall-SSAP3.ps1') {
        if (-not (Test-Path -LiteralPath (Join-Path $PackageRoot $required) -PathType Leaf)) {
            throw "The package is incomplete ($required is missing). Run .\installer\build-installer.ps1 -StageOnly first."
        }
    }
    if ($EnableSpatialEngine -and -not (Test-Path -LiteralPath (Join-Path $PackageRoot 'spatial\SSAP3Spatializer.dll') -PathType Leaf)) {
        throw 'The package has no spatial\SSAP3Spatializer.dll. Unpack it with tools\dev\Get-SpatialEngineArtifact.ps1 (or build it), then stage again.'
    }

    $trustedOwners = @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    if (Test-Path -LiteralPath $installRoot) {
        $owner = (Get-Acl -LiteralPath $installRoot).GetOwner([Security.Principal.SecurityIdentifier]).Value
        if ($owner -notin $trustedOwners) { throw "$installRoot exists but is owned by $owner. Remove it as an administrator first." }
    }

    # Stop SSAP3 processes started from the install folder (never by name: S-15).
    $prefix = $installRoot.TrimEnd('\') + '\'
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

    Write-Output "Copying the package to $installRoot ..."
    New-Item -ItemType Directory -Force -Path $installRoot | Out-Null
    Copy-Item -Path (Join-Path $PackageRoot 'app\*') -Destination $installRoot -Recurse -Force
    foreach ($folder in 'setup', 'spatial', 'driver', 'docs') {
        $source = Join-Path $PackageRoot $folder
        if (Test-Path -LiteralPath $source -PathType Container) {
            $target = Join-Path $installRoot $folder
            New-Item -ItemType Directory -Force -Path $target | Out-Null
            Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse -Force
        }
    }

    # Everything that runs elevated, or inside audiodg.exe, must be admin-only.
    Import-Module (Join-Path $installRoot 'setup\Ssap3Setup.psm1') -Force -DisableNameChecking
    foreach ($folder in 'setup', 'spatial', 'driver') {
        $path = Join-Path $installRoot $folder
        if (-not (Test-Path -LiteralPath $path)) { continue }
        Assert-Ssap3AdminOnlyPath -Path $path
        Get-ChildItem -LiteralPath $path -Recurse -Force | ForEach-Object { Assert-Ssap3AdminOnlyPath -Path $_.FullName }
    }
    Assert-Ssap3AdminOnlyPath -Path (Join-Path $installRoot 'SSAP3.exe')

    $arguments = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $installRoot 'setup\Install-SSAP3.ps1'), '-Version', $Version)
    if ($EnableSpatialEngine) { $arguments += '-EnableSpatialEngine' }
    if ($Autostart) { $arguments += '-Autostart' }
    # Pipe the child's output so the transcript records it. Windows PowerShell 5.1
    # turns redirected stderr lines into errors, which must not abort this script.
    $ErrorActionPreference = 'Continue'
    & (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') @arguments 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($code -ne 0) {
        Write-Output "FAILED: system configuration failed (exit $code) and was rolled back. Log: $installRoot\setup\install.log"
        Write-Output 'Remove the copied files with .\installer\Uninstall-SSAP3Local.ps1.'
        $exitCode = $code
    }
    else {
        # Start-menu entry, as SSAP3-Setup.exe creates. Removed by Uninstall-SSAP3Local.ps1.
        $shortcutPath = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\SSAP3.lnk'
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = Join-Path $installRoot 'SSAP3.exe'
        $shortcut.WorkingDirectory = $installRoot
        $shortcut.Description = 'SSAP3 Control Center'
        $shortcut.Save()
        Write-Output 'SSAP3 is installed. Open it from the Start menu (SSAP3), not from this elevated window.'
    }
}
catch {
    $exitCode = 1
    Write-Output "FAILED: $($_.Exception.Message)"
    Write-Output $_.InvocationInfo.PositionMessage
}
finally {
    Stop-Transcript | Out-Null
}
exit $exitCode
