#Requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$NoRelaunch,
    [switch]$KeepInstallDirectory
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

$programDataDirectory = Join-Path $env:ProgramData 'SSAP3'
$statePath = Join-Path $programDataDirectory 'install-state.json'
$logPath = Join-Path $programDataDirectory 'uninstall.log'
$installDirectory = Join-Path $env:ProgramFiles 'SSAP3'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Write-UninstallLog {
    param([Parameter(Mandatory = $true)][string]$Message)

    New-Item -ItemType Directory -Force -Path $programDataDirectory | Out-Null
    $line = '{0:yyyy-MM-dd HH:mm:ss}  {1}' -f (Get-Date), $Message
    Write-Host $line
    Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
}

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [int[]]$AllowedExitCodes = @(0)
    )

    Write-UninstallLog ('> {0} {1}' -f $FilePath, ($Arguments -join ' '))
    $output = & $FilePath @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    foreach ($line in $output) {
        if (-not [string]::IsNullOrEmpty([string]$line)) {
            Write-UninstallLog ([string]$line)
        }
    }

    if ($AllowedExitCodes -notcontains $exitCode) {
        throw '{0} failed with exit code {1}.' -f $FilePath, $exitCode
    }
}

function Find-InstalledSsap3Infs {
    $windowsInfDirectory = Join-Path $env:SystemRoot 'INF'
    return @(
        Get-ChildItem `
            -LiteralPath $windowsInfDirectory `
            -Filter 'oem*.inf' `
            -File |
        Where-Object {
            Select-String `
                -LiteralPath $_.FullName `
                -Pattern 'ssap3aap.cat' `
                -SimpleMatch `
                -Quiet
        } |
        Select-Object -ExpandProperty Name
    )
}

if (-not (Test-IsAdministrator)) {
    if ($NoRelaunch) {
        throw 'Administrator privileges are required.'
    }

    $powerShell = Join-Path $PSHOME 'powershell.exe'
    $arguments =
        '-NoProfile -ExecutionPolicy Bypass -File "{0}" -NoRelaunch{1}' `
            -f `
                $PSCommandPath, `
                $(if ($KeepInstallDirectory) {
                    ' -KeepInstallDirectory'
                }
                else {
                    ''
                })
    $process = Start-Process `
        -FilePath $powerShell `
        -Verb RunAs `
        -ArgumentList $arguments `
        -Wait `
        -PassThru
    exit $process.ExitCode
}

try {
    Write-UninstallLog 'SSAP3 uninstall started.'
    Get-Process -Name SSAP3 -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue

    $spatialUninstaller =
        Join-Path $installDirectory 'Uninstall-SpatialAudioEngine.ps1'
    if (Test-Path -LiteralPath $spatialUninstaller) {
        & $spatialUninstaller |
            ForEach-Object { Write-UninstallLog ([string]$_) }
    }

    foreach ($driverInf in Find-InstalledSsap3Infs) {
        Invoke-NativeCommand `
            -FilePath (Join-Path $env:SystemRoot 'System32\pnputil.exe') `
            -Arguments @('/delete-driver', $driverInf, '/uninstall', '/force') `
            -AllowedExitCodes @(0, 3010)
    }

    $state = $null
    if (Test-Path -LiteralPath $statePath -PathType Leaf) {
        $state = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 |
            ConvertFrom-Json
    }

    if ($null -ne $state -and
        -not [string]::IsNullOrWhiteSpace($state.CertificateThumbprint)) {
        $thumbprint = [string]$state.CertificateThumbprint
        foreach ($storePath in @(
            'Cert:\LocalMachine\Root',
            'Cert:\LocalMachine\TrustedPublisher',
            'Cert:\CurrentUser\Root',
            'Cert:\CurrentUser\TrustedPublisher',
            'Cert:\CurrentUser\My')) {
            $certificatePath = Join-Path $storePath $thumbprint
            if (Test-Path -LiteralPath $certificatePath) {
                Write-UninstallLog "Removing certificate $certificatePath."
                Remove-Item -LiteralPath $certificatePath -Force
            }
        }
    }

    $startMenuShortcut = Join-Path `
        $env:ProgramData `
        'Microsoft\Windows\Start Menu\Programs\SSAP3.lnk'
    if (Test-Path -LiteralPath $startMenuShortcut) {
        Remove-Item -LiteralPath $startMenuShortcut -Force
    }

    $desktopPath = [Environment]::GetFolderPath('Desktop')
    $desktopShortcut = Join-Path $desktopPath 'SSAP3.lnk'
    if (Test-Path -LiteralPath $desktopShortcut) {
        Remove-Item -LiteralPath $desktopShortcut -Force
    }

    $runKey =
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
    Remove-ItemProperty `
        -LiteralPath $runKey `
        -Name 'SSAP3' `
        -ErrorAction SilentlyContinue

    if (-not $KeepInstallDirectory -and
        (Test-Path -LiteralPath $installDirectory)) {
        $resolvedInstallDirectory =
            [IO.Path]::GetFullPath($installDirectory).TrimEnd('\')
        $expectedInstallDirectory =
            [IO.Path]::GetFullPath(
                (Join-Path $env:ProgramFiles 'SSAP3')).TrimEnd('\')
        if ($resolvedInstallDirectory -ne $expectedInstallDirectory) {
            throw "Refusing to remove unexpected path: $resolvedInstallDirectory"
        }

        Remove-Item -LiteralPath $resolvedInstallDirectory -Recurse -Force
    }

    if ($null -eq $state -or -not [bool]$state.TestSigningWasActive) {
        Invoke-NativeCommand `
            -FilePath (Join-Path $env:SystemRoot 'System32\bcdedit.exe') `
            -Arguments @('/set', 'testsigning', 'off')
        Write-UninstallLog (
            'Windows Test Mode was disabled. Restart Windows to complete rollback.')
    }

    Write-UninstallLog 'SSAP3 uninstall completed.'
    Write-Host ''
    Write-Host 'UNINSTALL COMPLETED. Restart Windows if Test Mode was disabled.'
}
catch {
    Write-UninstallLog "ERROR: $($_.Exception.Message)"
    Write-Host ''
    Write-Host 'UNINSTALL STOPPED. Review the log in C:\ProgramData\SSAP3.'
    exit 1
}
