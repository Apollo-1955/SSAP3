#Requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$NoRelaunch
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

$driverDirectory = Join-Path $PSScriptRoot 'driver'
$appSourceDirectory = Join-Path $PSScriptRoot 'app'
$appSource = Join-Path $appSourceDirectory 'SSAP3.exe'
$spatialToolSourceDirectory =
    Join-Path $appSourceDirectory 'tools\SoundVolumeView'
$spatialEngineInstaller =
    Join-Path $PSScriptRoot 'Install-SpatialAudioEngine.ps1'
$infPath = Join-Path $driverDirectory 'ssap3aap.inf'
$sysPath = Join-Path $driverDirectory 'ssap3aap.sys'
$catPath = Join-Path $driverDirectory 'ssap3aap.cat'
$cerPath = Join-Path $driverDirectory 'ssap3-dev-test-driver.cer'
$programDataDirectory = Join-Path $env:ProgramData 'SSAP3'
$statePath = Join-Path $programDataDirectory 'install-state.json'
$logPath = Join-Path $programDataDirectory 'install.log'
$restartMarkerPath =
    Join-Path $programDataDirectory 'restart-required.flag'
$installDirectory = Join-Path $env:ProgramFiles 'SSAP3'
$legacyProductName = 'Pod' + 'Link'
$legacyProgramDataDirectory =
    Join-Path $env:ProgramData $legacyProductName
$legacyInstallDirectory =
    Join-Path $env:ProgramFiles $legacyProductName
$legacyCatalogFileName =
    ('{0}-airpods4-anc.cat' -f $legacyProductName.ToLowerInvariant())
$targetHardwareIdPatterns = @(
    'BTHENUM\{4715650b-5e9d-4ac2-b898-a4fc0aa5df78}*',
    'BTHENUM\{74ec2172-0bad-4d01-8f77-997b2be0722a}*'
)

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Write-InstallLog {
    param([Parameter(Mandatory = $true)][string]$Message)

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

    Write-InstallLog ('> {0} {1}' -f $FilePath, ($Arguments -join ' '))
    $output = & $FilePath @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    foreach ($line in $output) {
        if (-not [string]::IsNullOrEmpty([string]$line)) {
            Write-InstallLog ([string]$line)
        }
    }

    if ($AllowedExitCodes -notcontains $exitCode) {
        throw '{0} failed with exit code {1}.' -f $FilePath, $exitCode
    }

    return $output
}

function Get-CodeIntegrityOptions {
    if (-not ('SSAP3.NativeCodeIntegrity' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace SSAP3
{
    public static class NativeCodeIntegrity
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_CODEINTEGRITY_INFORMATION
        {
            public UInt32 Length;
            public UInt32 CodeIntegrityOptions;
        }

        [DllImport("ntdll.dll")]
        private static extern Int32 NtQuerySystemInformation(
            Int32 systemInformationClass,
            ref SYSTEM_CODEINTEGRITY_INFORMATION systemInformation,
            Int32 systemInformationLength,
            IntPtr returnLength);

        public static UInt32 QueryOptions()
        {
            SYSTEM_CODEINTEGRITY_INFORMATION information =
                new SYSTEM_CODEINTEGRITY_INFORMATION();
            information.Length =
                (UInt32)Marshal.SizeOf(typeof(SYSTEM_CODEINTEGRITY_INFORMATION));
            Int32 status = NtQuerySystemInformation(
                103,
                ref information,
                (Int32)information.Length,
                IntPtr.Zero);
            if (status < 0)
            {
                throw new InvalidOperationException(
                    String.Format(
                        "NtQuerySystemInformation failed with NTSTATUS 0x{0:X8}.",
                        status));
            }

            return information.CodeIntegrityOptions;
        }
    }
}
'@
    }

    return [SSAP3.NativeCodeIntegrity]::QueryOptions()
}

function Test-TestSigningActive {
    return ((Get-CodeIntegrityOptions) -band 0x2) -ne 0
}

function Test-TestSigningConfigured {
    $output = & (Join-Path $env:SystemRoot 'System32\bcdedit.exe') `
        /enum '{current}' 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to read the current Windows boot configuration.'
    }

    $line = $output |
        Where-Object { [string]$_ -match '(?i)^\s*testsigning\s+' } |
        Select-Object -First 1
    if ($null -eq $line) {
        return $false
    }

    $value = (([string]$line).Trim() -split '\s+')[-1]
    return $value -match '(?i)^(yes|on|true|да|вкл\.?|включено)$'
}

function Enable-PersistentTestSigning {
    if (Test-TestSigningConfigured) {
        return
    }

    Write-InstallLog (
        'Test Mode is active in the running kernel but disabled for the next boot. ' +
        'Repairing the persistent BCD setting.')
    Invoke-NativeCommand `
        -FilePath (Join-Path $env:SystemRoot 'System32\bcdedit.exe') `
        -Arguments @('/set', 'testsigning', 'on') | Out-Null
    if (-not (Test-TestSigningConfigured)) {
        throw 'Windows accepted the Test Mode command, but BCD still reports it as disabled.'
    }
}

function Get-PackageCertificate {
    return New-Object `
        -TypeName Security.Cryptography.X509Certificates.X509Certificate2 `
        -ArgumentList $cerPath
}

function Import-PackageCertificate {
    $certificate = Get-PackageCertificate
    Write-InstallLog (
        'Trusting SSAP3 certificate {0} in LocalMachine Root and TrustedPublisher.' `
            -f $certificate.Thumbprint)

    $certUtil = Join-Path $env:SystemRoot 'System32\certutil.exe'
    Invoke-NativeCommand `
        -FilePath $certUtil `
        -Arguments @('-addstore', '-f', 'Root', $cerPath) | Out-Null
    Invoke-NativeCommand `
        -FilePath $certUtil `
        -Arguments @('-addstore', '-f', 'TrustedPublisher', $cerPath) | Out-Null
}

function Assert-Package {
    foreach ($path in @(
            $appSource,
            $infPath,
            $sysPath,
            $catPath,
            $cerPath,
            $spatialEngineInstaller,
            (Join-Path $PSScriptRoot 'spatial\SSAP3Spatializer.dll'),
            (Join-Path $PSScriptRoot 'spatial\EqualizerAPO\EqualizerAPO.dll')
        )) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required package file is missing: $path"
        }
    }

    foreach ($spatialToolFile in @(
        'SoundVolumeView.exe',
        'SoundVolumeView.chm',
        'readme.txt')) {
        $path = Join-Path $spatialToolSourceDirectory $spatialToolFile
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required spatial-audio tool file is missing: $path"
        }
    }

    $operatingSystem = Get-CimInstance Win32_OperatingSystem
    if ($operatingSystem.OSArchitecture -notmatch '64' -or
        [int]$operatingSystem.BuildNumber -lt 19041) {
        throw (
            'SSAP3 supports x64 Windows 10 build 19041 or newer and Windows 11. ' +
            "Detected: $($operatingSystem.Caption) build " +
            "$($operatingSystem.BuildNumber), $($operatingSystem.OSArchitecture).")
    }

    $secureBootState = Get-ItemProperty `
        -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot\State' `
        -ErrorAction SilentlyContinue
    if ($null -ne $secureBootState -and
        [int]$secureBootState.UEFISecureBootEnabled -ne 0) {
        throw (
            'Secure Boot is enabled. This local test-signed driver cannot be loaded. ' +
            'No Secure Boot setting was changed.')
    }

    Import-PackageCertificate
    foreach ($signedFile in @($sysPath, $catPath)) {
        $signature = Get-AuthenticodeSignature -LiteralPath $signedFile
        if ($signature.Status -ne
            [Management.Automation.SignatureStatus]::Valid) {
            throw (
                "Signature verification failed for $signedFile. " +
                "Status: $($signature.Status); $($signature.StatusMessage)")
        }
    }
}

function Get-ExistingState {
    if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) {
        return $null
    }

    return Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 |
        ConvertFrom-Json
}

function Save-State {
    param([Parameter(Mandatory = $true)]$State)

    $State | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath $statePath -Encoding UTF8
}

function Get-StateValue {
    param(
        $State,
        [Parameter(Mandatory = $true)][string]$Name,
        $DefaultValue = $null
    )

    if ($null -eq $State) {
        return $DefaultValue
    }

    $property = $State.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $DefaultValue
    }

    return $property.Value
}

function Get-LegacyState {
    $legacyStatePath =
        Join-Path $legacyProgramDataDirectory 'install-state.json'
    if (-not (Test-Path -LiteralPath $legacyStatePath -PathType Leaf)) {
        return $null
    }

    return Get-Content -LiteralPath $legacyStatePath -Raw -Encoding UTF8 |
        ConvertFrom-Json
}

function Find-LegacyDriverInfs {
    $windowsInfDirectory = Join-Path $env:SystemRoot 'INF'
    return @(
        Get-ChildItem `
            -LiteralPath $windowsInfDirectory `
            -Filter 'oem*.inf' `
            -File |
        Where-Object {
            Select-String `
                -LiteralPath $_.FullName `
                -Pattern $legacyCatalogFileName `
                -SimpleMatch `
                -Quiet
        } |
        Select-Object -ExpandProperty Name
    )
}

function Remove-CertificateByThumbprint {
    param([string]$Thumbprint)

    if ([string]::IsNullOrWhiteSpace($Thumbprint)) {
        return
    }

    foreach ($storePath in @(
        'Cert:\LocalMachine\Root',
        'Cert:\LocalMachine\TrustedPublisher',
        'Cert:\CurrentUser\Root',
        'Cert:\CurrentUser\TrustedPublisher',
        'Cert:\CurrentUser\My')) {
        $certificatePath = Join-Path $storePath $Thumbprint
        if (Test-Path -LiteralPath $certificatePath) {
            Write-InstallLog "Removing legacy certificate $certificatePath."
            Remove-Item -LiteralPath $certificatePath -Force
        }
    }
}

function Remove-LegacyCertificates {
    $legacyCertificateSubject =
        "CN=$legacyProductName Local Test Driver"
    foreach ($storePath in @(
        'Cert:\LocalMachine\Root',
        'Cert:\LocalMachine\TrustedPublisher',
        'Cert:\CurrentUser\Root',
        'Cert:\CurrentUser\TrustedPublisher',
        'Cert:\CurrentUser\My')) {
        Get-ChildItem -LiteralPath $storePath |
            Where-Object {
                $_.Subject -eq $legacyCertificateSubject
            } |
            ForEach-Object {
                Write-InstallLog (
                    "Removing legacy certificate $($_.PSPath).")
                Remove-Item -LiteralPath $_.PSPath -Force
            }
    }
}

function Invoke-LegacyMigration {
    $legacyState = Get-LegacyState
    $legacyRunKey =
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
    $legacyStartMenuShortcut = Join-Path `
        $env:ProgramData `
        ("Microsoft\Windows\Start Menu\Programs\{0}.lnk" `
            -f $legacyProductName)
    $legacyDesktopShortcut = Join-Path `
        ([Environment]::GetFolderPath('Desktop')) `
        ("{0}.lnk" -f $legacyProductName)
    $legacyRegistryPath =
        "HKLM:\SOFTWARE\$legacyProductName"
    $legacySettingsDirectory = Join-Path `
        ([Environment]::GetFolderPath(
            [Environment+SpecialFolder]::LocalApplicationData)) `
        $legacyProductName
    $legacySettingsPath =
        Join-Path $legacySettingsDirectory 'settings.json'
    $newSettingsDirectory = Join-Path `
        ([Environment]::GetFolderPath(
            [Environment+SpecialFolder]::LocalApplicationData)) `
        'SSAP3'
    $newSettingsPath =
        Join-Path $newSettingsDirectory 'settings.json'
    $legacySpatialUninstaller =
        Join-Path $legacyInstallDirectory 'Uninstall-SpatialAudioEngine.ps1'
    $legacyDriverInfs = @(Find-LegacyDriverInfs)
    $hasLegacyInstall =
        $null -ne $legacyState -or
        (Test-Path -LiteralPath $legacyInstallDirectory) -or
        (Test-Path -LiteralPath $legacyProgramDataDirectory) -or
        (Test-Path -LiteralPath $legacyRegistryPath) -or
        $legacyDriverInfs.Count -gt 0

    Remove-LegacyCertificates
    if (-not $hasLegacyInstall) {
        return $null
    }

    Write-InstallLog 'Migrating a pre-SSAP3 installation.'
    Get-Process -Name $legacyProductName -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue

    if (Test-Path -LiteralPath $legacySpatialUninstaller -PathType Leaf) {
        & $legacySpatialUninstaller |
            ForEach-Object { Write-InstallLog ([string]$_) }
    }
    elseif (Test-Path -LiteralPath $legacyRegistryPath) {
        throw (
            'A legacy spatial-audio installation was found, but its safe ' +
            'rollback script is missing. Reinstall the previous build, ' +
            'uninstall it, and run SSAP3 Setup again.')
    }

    foreach ($driverInf in $legacyDriverInfs) {
        Invoke-NativeCommand `
            -FilePath (Join-Path $env:SystemRoot 'System32\pnputil.exe') `
            -Arguments @('/delete-driver', $driverInf, '/uninstall', '/force') `
            -AllowedExitCodes @(0, 3010) | Out-Null
    }

    if ((Test-Path -LiteralPath $legacySettingsPath -PathType Leaf) -and
        -not (Test-Path -LiteralPath $newSettingsPath -PathType Leaf)) {
        New-Item -ItemType Directory -Force -Path $newSettingsDirectory |
            Out-Null
        Copy-Item -LiteralPath $legacySettingsPath `
            -Destination $newSettingsPath `
            -Force
        Write-InstallLog 'Migrated user settings.'
    }

    $legacyBcdBackup =
        [string](Get-StateValue -State $legacyState -Name 'BcdBackup')
    if (-not [string]::IsNullOrWhiteSpace($legacyBcdBackup) -and
        (Test-Path -LiteralPath $legacyBcdBackup -PathType Leaf)) {
        $backupDirectory = Join-Path $programDataDirectory 'backup'
        New-Item -ItemType Directory -Force -Path $backupDirectory | Out-Null
        $migratedBcdBackup =
            Join-Path $backupDirectory 'bcd-before-ssap3-migrated.bak'
        Copy-Item -LiteralPath $legacyBcdBackup `
            -Destination $migratedBcdBackup `
            -Force
        if ($null -ne $legacyState.PSObject.Properties['BcdBackup']) {
            $legacyState.BcdBackup = $migratedBcdBackup
        }
    }

    Remove-CertificateByThumbprint `
        -Thumbprint ([string](
            Get-StateValue `
                -State $legacyState `
                -Name 'CertificateThumbprint'))

    Remove-ItemProperty `
        -LiteralPath $legacyRunKey `
        -Name $legacyProductName `
        -ErrorAction SilentlyContinue
    foreach ($shortcutPath in @(
        $legacyStartMenuShortcut,
        $legacyDesktopShortcut)) {
        if (Test-Path -LiteralPath $shortcutPath -PathType Leaf) {
            Remove-Item -LiteralPath $shortcutPath -Force
        }
    }

    if (Test-Path -LiteralPath $legacyRegistryPath) {
        Remove-Item -LiteralPath $legacyRegistryPath -Recurse -Force
    }

    foreach ($directory in @(
        $legacyInstallDirectory,
        $legacyProgramDataDirectory,
        $legacySettingsDirectory)) {
        if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
            continue
        }

        $resolvedDirectory = [IO.Path]::GetFullPath($directory).TrimEnd('\')
        if ($resolvedDirectory -notin @(
            [IO.Path]::GetFullPath($legacyInstallDirectory).TrimEnd('\'),
            [IO.Path]::GetFullPath($legacyProgramDataDirectory).TrimEnd('\'),
            [IO.Path]::GetFullPath($legacySettingsDirectory).TrimEnd('\'))) {
            throw "Refusing to remove unexpected path: $resolvedDirectory"
        }

        Remove-Item -LiteralPath $resolvedDirectory -Recurse -Force
    }

    Write-InstallLog 'Legacy migration completed.'
    return $legacyState
}

function Suspend-SystemBitLockerForRestart {
    $getBitLockerVolume = Get-Command Get-BitLockerVolume -ErrorAction SilentlyContinue
    if ($null -eq $getBitLockerVolume) {
        throw (
            'The BitLocker status could not be checked. ' +
            'Installation stopped before changing boot configuration.')
    }

    $volume = Get-BitLockerVolume -MountPoint $env:SystemDrive
    Write-InstallLog (
        'BitLocker: VolumeStatus={0}; ProtectionStatus={1}.' `
            -f $volume.VolumeStatus, $volume.ProtectionStatus)
    if ([string]$volume.ProtectionStatus -eq 'On') {
        Write-InstallLog (
            'Suspending BitLocker protection for one restart to avoid a recovery-key prompt.')
        Suspend-BitLocker `
            -MountPoint $env:SystemDrive `
            -RebootCount 1 | Out-Null
    }
}

function Invoke-PreparePhase {
    Write-InstallLog 'SSAP3 prepare phase started.'
    Assert-Package

    $existingState = Get-ExistingState
    if ($null -eq $existingState) {
        $backupDirectory = Join-Path $programDataDirectory 'backup'
        New-Item -ItemType Directory -Force -Path $backupDirectory | Out-Null
        $bcdBackup = Join-Path $backupDirectory (
            'bcd-before-ssap3-{0:yyyyMMdd-HHmmss}.bak' -f (Get-Date))

        Suspend-SystemBitLockerForRestart
        Invoke-NativeCommand `
            -FilePath (Join-Path $env:SystemRoot 'System32\bcdedit.exe') `
            -Arguments @('/export', $bcdBackup) | Out-Null

        $certificate = Get-PackageCertificate
        $existingState = [ordered]@{
            PreparedAt = (Get-Date).ToString('o')
            InstalledAt = $null
            CertificateThumbprint = $certificate.Thumbprint
            BcdBackup = $bcdBackup
            TestSigningWasActive = $false
            DriverInf = $null
        }
        Save-State -State $existingState
    }

    Invoke-NativeCommand `
        -FilePath (Join-Path $env:SystemRoot 'System32\bcdedit.exe') `
        -Arguments @('/set', 'testsigning', 'on') | Out-Null
    if (-not (Test-TestSigningConfigured)) {
        throw 'Windows did not persist the Test Mode boot setting.'
    }

    Set-Content `
        -LiteralPath $restartMarkerPath `
        -Value 'Restart Windows, then run SSAP3 Setup again.' `
        -Encoding ASCII
    Write-InstallLog 'Prepare phase completed. A Windows restart is required.'
    Write-Host ''
    Write-Host 'RESTART REQUIRED.'
    Write-Host 'After Windows starts, run SSAP3-Setup.exe again.'
}

function Find-InstalledSsap3Inf {
    $device = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
        Where-Object {
            $instanceId = $_.InstanceId
            $null -ne ($targetHardwareIdPatterns |
                Where-Object { $instanceId -like $_ } |
                Select-Object -First 1)
        } |
        Select-Object -First 1

    if ($null -ne $device) {
        $infProperty = Get-PnpDeviceProperty `
            -InstanceId $device.InstanceId `
            -KeyName 'DEVPKEY_Device_DriverInfPath' `
            -ErrorAction SilentlyContinue
        if ($null -ne $infProperty -and
            -not [string]::IsNullOrWhiteSpace([string]$infProperty.Data)) {
            return [string]$infProperty.Data
        }
    }

    $windowsInfDirectory = Join-Path $env:SystemRoot 'INF'
    foreach ($candidate in Get-ChildItem `
        -LiteralPath $windowsInfDirectory `
        -Filter 'oem*.inf' `
        -File) {
        if (Select-String `
            -LiteralPath $candidate.FullName `
            -Pattern 'ssap3aap.cat' `
            -SimpleMatch `
            -Quiet) {
            return $candidate.Name
        }
    }

    return $null
}

function Install-Application {
    Write-InstallLog "Installing application to $installDirectory."
    $runningProcesses = @(
        Get-Process -Name SSAP3 -ErrorAction SilentlyContinue)
    foreach ($runningProcess in $runningProcesses) {
        $runningProcess.Kill()
    }
    foreach ($runningProcess in $runningProcesses) {
        if (-not $runningProcess.WaitForExit(10000)) {
            throw (
                'SSAP3 is still running. Exit it from the tray and run ' +
                'the installer again.')
        }
    }

    New-Item -ItemType Directory -Force -Path $installDirectory | Out-Null
    Copy-Item -LiteralPath $appSource `
        -Destination (Join-Path $installDirectory 'SSAP3.exe') `
        -Force
    $spatialToolInstallDirectory =
        Join-Path $installDirectory 'tools\SoundVolumeView'
    New-Item -ItemType Directory -Force `
        -Path $spatialToolInstallDirectory | Out-Null
    foreach ($spatialToolFile in @(
        'SoundVolumeView.exe',
        'SoundVolumeView.chm',
        'readme.txt')) {
        Copy-Item `
            -LiteralPath (
                Join-Path $spatialToolSourceDirectory $spatialToolFile) `
            -Destination (
                Join-Path $spatialToolInstallDirectory $spatialToolFile) `
            -Force
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall-SSAP3.ps1') `
        -Destination (Join-Path $installDirectory 'Uninstall-SSAP3.ps1') `
        -Force
    Copy-Item `
        -LiteralPath (Join-Path $PSScriptRoot 'Uninstall-SpatialAudioEngine.ps1') `
        -Destination (
            Join-Path $installDirectory 'Uninstall-SpatialAudioEngine.ps1') `
        -Force
    Copy-Item -LiteralPath $cerPath `
        -Destination (Join-Path $installDirectory 'ssap3-dev-test-driver.cer') `
        -Force

    $shell = New-Object -ComObject WScript.Shell
    $startMenuPath = Join-Path `
        $env:ProgramData `
        'Microsoft\Windows\Start Menu\Programs\SSAP3.lnk'
    $shortcut = $shell.CreateShortcut($startMenuPath)
    $shortcut.TargetPath = Join-Path $installDirectory 'SSAP3.exe'
    $shortcut.WorkingDirectory = $installDirectory
    $shortcut.Description = 'SSAP3 for Windows'
    $shortcut.IconLocation =
        ('"{0}",0' -f (Join-Path $installDirectory 'SSAP3.exe'))
    $shortcut.Save()

    $desktopPath = [Environment]::GetFolderPath('Desktop')
    if (-not [string]::IsNullOrWhiteSpace($desktopPath)) {
        $desktopShortcut = $shell.CreateShortcut(
            (Join-Path $desktopPath 'SSAP3.lnk'))
        $desktopShortcut.TargetPath = Join-Path $installDirectory 'SSAP3.exe'
        $desktopShortcut.WorkingDirectory = $installDirectory
        $desktopShortcut.Description = 'SSAP3 for Windows'
        $desktopShortcut.IconLocation =
            ('"{0}",0' -f (Join-Path $installDirectory 'SSAP3.exe'))
        $desktopShortcut.Save()
    }

    $runKey =
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
    New-ItemProperty `
        -LiteralPath $runKey `
        -Name 'SSAP3' `
        -PropertyType String `
        -Value ('"{0}"' -f (Join-Path $installDirectory 'SSAP3.exe')) `
        -Force | Out-Null
}

function Invoke-DriverInstall {
    Write-InstallLog 'Installing the SSAP3 AAP/UARP profile driver.'
    Invoke-NativeCommand `
        -FilePath (Join-Path $env:SystemRoot 'System32\pnputil.exe') `
        -Arguments @('/add-driver', $infPath, '/install') `
        -AllowedExitCodes @(0, 259, 3010) | Out-Null
    Invoke-NativeCommand `
        -FilePath (Join-Path $env:SystemRoot 'System32\pnputil.exe') `
        -Arguments @('/scan-devices') `
        -AllowedExitCodes @(0, 3010) | Out-Null
}

function Write-HardwareStatus {
    $devices = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
        Where-Object {
            $instanceId = $_.InstanceId
            $null -ne ($targetHardwareIdPatterns |
                Where-Object { $instanceId -like $_ } |
                Select-Object -First 1)
        }

    if ($null -eq $devices) {
        Write-InstallLog (
            'AAP Server is not present. Pair/connect AirPods and open their case, ' +
            'then run this installer once more.')
        return $false
    }

    $allHealthy = $true
    foreach ($device in @($devices)) {
        Write-InstallLog (
            'Apple control device: Status={0}; Class={1}; InstanceId={2}' `
                -f $device.Status, $device.Class, $device.InstanceId)
        if ([string]$device.Status -ne 'OK') {
            $allHealthy = $false
            $problem = Get-PnpDeviceProperty `
                -InstanceId $device.InstanceId `
                -KeyName 'DEVPKEY_Device_ProblemCode' `
                -ErrorAction SilentlyContinue
            if ($null -ne $problem) {
                Write-InstallLog "PnP problem code: $($problem.Data)"
            }
        }
    }

    return $allHealthy
}

function Invoke-InstallPhase {
    Write-InstallLog 'SSAP3 post-restart install phase started.'
    Assert-Package

    if (-not (Test-TestSigningActive)) {
        throw (
            'Windows Test Mode is not active yet. Restart Windows and run this installer again.')
    }
    Enable-PersistentTestSigning

    $legacyState = Invoke-LegacyMigration
    $state = Get-ExistingState
    if ($null -eq $state) {
        $certificate = Get-PackageCertificate
        $state = [ordered]@{
            PreparedAt = Get-StateValue `
                -State $legacyState `
                -Name 'PreparedAt'
            InstalledAt = $null
            CertificateThumbprint = $certificate.Thumbprint
            BcdBackup = Get-StateValue `
                -State $legacyState `
                -Name 'BcdBackup'
            TestSigningWasActive = [bool](
                Get-StateValue `
                    -State $legacyState `
                    -Name 'TestSigningWasActive' `
                    -DefaultValue $true)
            DriverInf = $null
        }
    }
    elseif ($null -ne $legacyState) {
        if ([string]::IsNullOrWhiteSpace([string]$state.BcdBackup)) {
            $state.BcdBackup = Get-StateValue `
                -State $legacyState `
                -Name 'BcdBackup'
        }
        $state.TestSigningWasActive = [bool](
            Get-StateValue `
                -State $legacyState `
                -Name 'TestSigningWasActive' `
                -DefaultValue ([bool]$state.TestSigningWasActive))
    }

    Save-State -State $state

    Invoke-DriverInstall
    Install-Application
    & $spatialEngineInstaller -PackageRoot $PSScriptRoot |
        ForEach-Object { Write-InstallLog ([string]$_) }
    $driverInf = Find-InstalledSsap3Inf
    $hardwareHealthy = Write-HardwareStatus

    $state.InstalledAt = (Get-Date).ToString('o')
    $state.DriverInf = $driverInf
    Save-State -State $state
    Remove-Item `
        -LiteralPath $restartMarkerPath `
        -Force `
        -ErrorAction SilentlyContinue

    Write-InstallLog (
        'SSAP3 install completed. DriverInf={0}; HardwareHealthy={1}.' `
            -f $driverInf, $hardwareHealthy)
    Write-Host ''
    if ($hardwareHealthy) {
        Write-Host 'INSTALLATION COMPLETED. Start SSAP3 from the desktop shortcut.'
    }
    else {
        Write-Host (
            'DRIVER PACKAGE INSTALLED, BUT AIRPODS AAP IS NOT READY. ' +
            'Connect/open the AirPods case and run this installer again.')
    }
}

if (-not (Test-IsAdministrator)) {
    if ($NoRelaunch) {
        throw 'Administrator privileges are required.'
    }

    $powerShell = Join-Path $PSHOME 'powershell.exe'
    $arguments =
        '-NoProfile -ExecutionPolicy Bypass -File "{0}" -NoRelaunch' `
            -f $PSCommandPath
    $process = Start-Process `
        -FilePath $powerShell `
        -Verb RunAs `
        -ArgumentList $arguments `
        -Wait `
        -PassThru
    exit $process.ExitCode
}

New-Item -ItemType Directory -Force -Path $programDataDirectory | Out-Null

try {
    $testSigningActive = Test-TestSigningActive
    $testSigningConfigured = Test-TestSigningConfigured
    $codeIntegrityMessage =
        ('Code Integrity options: 0x{0:X8}; TestSigningActive={1}; ' +
         'TestSigningConfigured={2}.') -f `
            (Get-CodeIntegrityOptions), `
            $testSigningActive, `
            $testSigningConfigured
    Write-InstallLog $codeIntegrityMessage

    if ($testSigningActive) {
        Enable-PersistentTestSigning
        Invoke-InstallPhase
    }
    else {
        Invoke-PreparePhase
    }
}
catch {
    Write-InstallLog "ERROR: $($_.Exception.Message)"
    Write-InstallLog "LOCATION: $($_.InvocationInfo.PositionMessage)"
    Write-InstallLog "STACK: $($_.ScriptStackTrace)"
    Write-Host ''
    Write-Host 'INSTALLATION STOPPED. No automatic reboot was performed.'
    exit 1
}
