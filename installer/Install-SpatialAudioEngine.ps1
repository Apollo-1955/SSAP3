#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageRoot
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

$equalizerGuidPre = '{EACD2258-FCAC-4FF4-B36D-419E924A6D79}'
$equalizerGuidPost = '{EC1CC9CE-FAED-4822-828A-82A81A6F018F}'
$defaultProcessingMode = '{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}'
$guidValueNames = @(
    '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},1',
    '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},2',
    '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},5',
    '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},6',
    '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7'
)
$sfxGuidName = $guidValueNames[2]
$mfxGuidName = $guidValueNames[3]
$efxGuidName = $guidValueNames[4]
$sfxModesName = '{d3993a3f-99c2-4402-b5ec-a92a0367664b},5'
$mfxModesName = '{d3993a3f-99c2-4402-b5ec-a92a0367664b},6'
$efxModesName = '{d3993a3f-99c2-4402-b5ec-a92a0367664b},7'
$disableEnhancementsName = '{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5'
$deviceNameValue = '{b3f8fa53-0004-438e-9003-51a46e139bfc},6'
$combinedDeviceValue = '{b3f8fa53-0004-438e-9003-51a46e139bfc},41'

$sourceRoot = Join-Path $PackageRoot 'spatial'
$runtimeSource = Join-Path $sourceRoot 'EqualizerAPO'
$pluginSource = Join-Path $sourceRoot 'SSAP3Spatializer.dll'
$equalizerRoot = Join-Path $env:ProgramFiles 'EqualizerAPO'
$configRoot = Join-Path $equalizerRoot 'config'
$pluginRoot = Join-Path $equalizerRoot 'VSTPlugins'
$programDataRoot = Join-Path $env:ProgramData 'SSAP3'
$statePath = Join-Path $programDataRoot 'head-tracking-state.bin'
$spatialRegistry = 'HKLM:\SOFTWARE\SSAP3\SpatialAudio'
$originalEffectsRoot = Join-Path $spatialRegistry 'OriginalEffects'
$equalizerRegistry = 'HKLM:\SOFTWARE\EqualizerAPO'
$childApoRoot = Join-Path $equalizerRegistry 'Child APOs'
$renderRoot =
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render'

function Write-Step {
    param([string]$Message)
    Write-Output "[Spatial Audio] $Message"
}

function Get-PropertyValue {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }
    return $property.Value
}

function Set-RegistryString {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Value
    )

    New-ItemProperty `
        -LiteralPath $Path `
        -Name $Name `
        -PropertyType String `
        -Value $Value `
        -Force | Out-Null
}

function Ensure-RegistryKey {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -Path $Path -Force | Out-Null
    }
}

function Set-RegistryMultiStringIfMissing {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $properties = Get-ItemProperty -LiteralPath $Path
    if ($null -eq (Get-PropertyValue $properties $Name)) {
        New-ItemProperty `
            -LiteralPath $Path `
            -Name $Name `
            -PropertyType MultiString `
            -Value @($defaultProcessingMode) `
            -Force | Out-Null
    }
}

function Enable-RegistryPrivileges {
    if ('SSAP3.RegistryPrivileges' -as [type]) {
        return
    }

    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace SSAP3
{
    public static class RegistryPrivileges
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public UInt32 LowPart;
            public Int32 HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES
        {
            public UInt32 PrivilegeCount;
            public LUID Luid;
            public UInt32 Attributes;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(
            IntPtr process,
            UInt32 desiredAccess,
            out IntPtr token);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool LookupPrivilegeValue(
            string systemName,
            string name,
            out LUID luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(
            IntPtr token,
            bool disableAll,
            ref TOKEN_PRIVILEGES newState,
            UInt32 bufferLength,
            IntPtr previousState,
            IntPtr returnLength);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        public static void Enable(string name)
        {
            const UInt32 TOKEN_ADJUST_PRIVILEGES = 0x20;
            const UInt32 TOKEN_QUERY = 0x8;
            const UInt32 SE_PRIVILEGE_ENABLED = 0x2;
            IntPtr token;
            if (!OpenProcessToken(
                    GetCurrentProcess(),
                    TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY,
                    out token))
            {
                throw new InvalidOperationException("OpenProcessToken failed.");
            }

            try
            {
                LUID luid;
                if (!LookupPrivilegeValue(null, name, out luid))
                {
                    throw new InvalidOperationException(
                        "LookupPrivilegeValue failed for " + name + ".");
                }
                TOKEN_PRIVILEGES privileges = new TOKEN_PRIVILEGES();
                privileges.PrivilegeCount = 1;
                privileges.Luid = luid;
                privileges.Attributes = SE_PRIVILEGE_ENABLED;
                if (!AdjustTokenPrivileges(
                        token,
                        false,
                        ref privileges,
                        0,
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    throw new InvalidOperationException(
                        "AdjustTokenPrivileges failed for " + name + ".");
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }
    }
}
'@

    [SSAP3.RegistryPrivileges]::Enable('SeTakeOwnershipPrivilege')
    [SSAP3.RegistryPrivileges]::Enable('SeRestorePrivilege')
    [SSAP3.RegistryPrivileges]::Enable('SeBackupPrivilege')
}

function Grant-RegistryAdministratorsControl {
    param([Parameter(Mandatory = $true)][string]$Path)

    Enable-RegistryPrivileges
    $administrators = New-Object `
        Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $prefix = 'HKLM:\'
    if (-not $Path.StartsWith(
            $prefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Only HKLM registry paths are supported: $Path"
    }
    $relativePath = $Path.Substring($prefix.Length)

    $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey(
        $relativePath,
        [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree,
        [Security.AccessControl.RegistryRights]::TakeOwnership)
    if ($null -eq $key) {
        throw "Could not open registry key for ownership: $Path"
    }
    try {
        $acl = $key.GetAccessControl(
            [Security.AccessControl.AccessControlSections]::Owner)
        $acl.SetOwner($administrators)
        $key.SetAccessControl($acl)
    }
    finally {
        $key.Dispose()
    }

    $rights =
        [Security.AccessControl.RegistryRights]::ChangePermissions -bor
        [Security.AccessControl.RegistryRights]::ReadKey
    $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey(
        $relativePath,
        [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree,
        $rights)
    if ($null -eq $key) {
        throw "Could not open registry key permissions: $Path"
    }
    try {
        $acl = $key.GetAccessControl()
        $rule = New-Object Security.AccessControl.RegistryAccessRule(
            $administrators,
            [Security.AccessControl.RegistryRights]::FullControl,
            [Security.AccessControl.InheritanceFlags]::ContainerInherit,
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow)
        $acl.SetAccessRule($rule)
        $key.SetAccessControl($acl)
    }
    finally {
        $key.Dispose()
    }
}

function Save-OriginalEffects {
    param(
        [Parameter(Mandatory = $true)][string]$EndpointGuid,
        [Parameter(Mandatory = $true)][string]$FxPath,
        [Parameter(Mandatory = $true)][bool]$Combined
    )

    $originalPath = Join-Path $originalEffectsRoot $EndpointGuid
    if (-not (Test-Path -LiteralPath $originalPath)) {
        $effects = Get-ItemProperty -LiteralPath $FxPath
        foreach ($name in $guidValueNames) {
            $value = Get-PropertyValue $effects $name
            if ([string]$value -in @($equalizerGuidPre, $equalizerGuidPost)) {
                throw (
                    "Equalizer APO is already attached to $EndpointGuid, " +
                    'but SSAP3 has no independent effects backup. ' +
                    'Restore that endpoint before running setup again.')
            }
        }

        Ensure-RegistryKey -Path $originalPath
        foreach ($name in $guidValueNames) {
            $value = Get-PropertyValue $effects $name
            Set-RegistryString `
                -Path $originalPath `
                -Name $name `
                -Value $(if ($null -eq $value) {
                    '!VALUE'
                } else {
                    [string]$value
                })
        }
        Set-RegistryString -Path $originalPath -Name 'Version' -Value '1'
    }

    $original = Get-ItemProperty -LiteralPath $originalPath
    foreach ($name in $guidValueNames) {
        $value = [string](Get-PropertyValue $original $name)
        if ($value -in @($equalizerGuidPre, $equalizerGuidPost)) {
            throw (
                "SSAP3 effects backup for $EndpointGuid is unsafe. " +
                'It contains an Equalizer APO GUID and will not be used.')
        }
    }

    # Equalizer APO reads this key to chain the endpoint's original effects.
    # Rebuild it from SSAP3's independent backup on every install so an
    # interrupted update can never make Equalizer APO instantiate itself.
    $childPath = Join-Path $childApoRoot $EndpointGuid
    Ensure-RegistryKey -Path $childPath
    foreach ($name in $guidValueNames) {
        Set-RegistryString `
            -Path $childPath `
            -Name $name `
            -Value ([string](Get-PropertyValue $original $name))
    }

    $originalSfxValue =
        [string](Get-PropertyValue $original $sfxGuidName)
    $originalSfx = if ($originalSfxValue -eq '!VALUE') {
        $null
    } else {
        $originalSfxValue
    }
    $originalLfxValue =
        [string](Get-PropertyValue $original $guidValueNames[0])
    $originalLfx = if ($originalLfxValue -eq '!VALUE') {
        $null
    } else {
        $originalLfxValue
    }
    $originalPre = if ($null -ne $originalSfx) {
        [string]$originalSfx
    } elseif ($null -ne $originalLfx) {
        [string]$originalLfx
    } else {
        ''
    }

    $postName = if ($Combined) { $mfxGuidName } else { $efxGuidName }
    $originalPostValue =
        [string](Get-PropertyValue $original $postName)
    $originalPost = if ($originalPostValue -eq '!VALUE') {
        $null
    } else {
        $originalPostValue
    }
    if ($null -eq $originalPost) {
        $originalGfxValue =
            [string](Get-PropertyValue $original $guidValueNames[1])
        if ($originalGfxValue -ne '!VALUE') {
            $originalPost = $originalGfxValue
        }
    }

    Set-RegistryString -Path $childPath -Name 'PreMixChild' -Value $originalPre
    Set-RegistryString `
        -Path $childPath `
        -Name 'PostMixChild' `
        -Value $(if ($null -eq $originalPost) { '' } else { [string]$originalPost })
    Set-RegistryString `
        -Path $childPath `
        -Name 'AllowSilentBufferModification' `
        -Value 'false'
    Set-RegistryString `
        -Path $childPath `
        -Name 'DisableAutomaticAdjustment' `
        -Value 'true'
    Set-RegistryString -Path $childPath -Name 'Version' -Value '2'
}

foreach ($required in @(
        $pluginSource,
        (Join-Path $runtimeSource 'EqualizerAPO.dll'),
        (Join-Path $runtimeSource 'sndfile.dll'),
        (Join-Path $runtimeSource 'fftw3f.dll')
    )) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Spatial Audio package file is missing: $required"
    }
}

New-Item -ItemType Directory -Force `
    -Path $equalizerRoot, $configRoot, $pluginRoot, $programDataRoot |
    Out-Null

$existingSpatialState = Get-ItemProperty `
    -LiteralPath $spatialRegistry `
    -ErrorAction SilentlyContinue
$previousOwnership = if ($null -eq $existingSpatialState) {
    $null
}
else {
    Get-PropertyValue $existingSpatialState 'OwnsEqualizerApo'
}
$runtimeAlreadyPresent = Test-Path -LiteralPath (
    Join-Path $equalizerRoot 'EqualizerAPO.dll')
$ownsEqualizer = if ($null -ne $previousOwnership) {
    [int]$previousOwnership -eq 1
}
else {
    -not $runtimeAlreadyPresent
}

# Persist ownership before copying files so an interrupted first install can be
# retried without mistaking SSAP3's private Equalizer APO runtime for a
# third-party installation.
Ensure-RegistryKey -Path $spatialRegistry
New-ItemProperty `
    -LiteralPath $spatialRegistry `
    -Name 'OwnsEqualizerApo' `
    -PropertyType DWord `
    -Value $(if ($ownsEqualizer) { 1 } else { 0 }) `
    -Force | Out-Null

$runtimeFilesToCopy = @()
if ($ownsEqualizer) {
    $runtimeFilesToCopy = @(
        Get-ChildItem -LiteralPath $runtimeSource -File |
            Where-Object {
                $destination = Join-Path $equalizerRoot $_.Name
                -not (Test-Path -LiteralPath $destination) -or
                (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
            })
}

$pluginDestination =
    Join-Path $pluginRoot 'SSAP3Spatializer.dll'
$pluginNeedsCopy =
    -not (Test-Path -LiteralPath $pluginDestination) -or
    (Get-FileHash -LiteralPath $pluginSource -Algorithm SHA256).Hash -ne
    (Get-FileHash -LiteralPath $pluginDestination -Algorithm SHA256).Hash

if ($runtimeFilesToCopy.Count -gt 0 -or $pluginNeedsCopy) {
    $audioService = Get-Service -Name Audiosrv
    $restartAudioService = [string]$audioService.Status -ne 'Stopped'
    if ($restartAudioService) {
        Write-Step 'Pausing Windows Audio to update the spatial engine.'
        Stop-Service -Name Audiosrv -Force
    }

    try {
        if ($runtimeFilesToCopy.Count -gt 0) {
            Write-Step 'Installing the Equalizer APO 1.4.2 runtime.'
            foreach ($runtimeFile in $runtimeFilesToCopy) {
                Copy-Item -LiteralPath $runtimeFile.FullName `
                    -Destination (Join-Path $equalizerRoot $runtimeFile.Name) `
                    -Force
            }
        }

        if ($pluginNeedsCopy) {
            Copy-Item -LiteralPath $pluginSource `
                -Destination $pluginDestination `
                -Force
        }
    }
    finally {
        if ($restartAudioService) {
            Start-Service -Name Audiosrv
        }
    }
}

Ensure-RegistryKey -Path $equalizerRegistry
Set-RegistryString `
    -Path $equalizerRegistry `
    -Name 'InstallPath' `
    -Value $equalizerRoot
Set-RegistryString `
    -Path $equalizerRegistry `
    -Name 'ConfigPath' `
    -Value $configRoot
Set-RegistryString `
    -Path $equalizerRegistry `
    -Name 'EnableTrace' `
    -Value 'false'
New-ItemProperty `
    -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Audio' `
    -Name 'DisableProtectedAudioDG' `
    -PropertyType DWord `
    -Value 1 `
    -Force | Out-Null

$regsvr32 = Join-Path $env:SystemRoot 'System32\regsvr32.exe'
$registerProcess = Start-Process `
    -FilePath $regsvr32 `
    -ArgumentList (
        '/s "{0}"' -f (Join-Path $equalizerRoot 'EqualizerAPO.dll')) `
    -Wait `
    -PassThru
if ($registerProcess.ExitCode -ne 0) {
    throw "Equalizer APO registration failed with code $($registerProcess.ExitCode)."
}

$soundStageConfig = Join-Path $configRoot 'ssap3.txt'
@(
    '# SSAP3 system-wide binaural renderer'
    'Device: AirPods'
    'Stage: post-mix'
    'VSTPlugin: Library "SSAP3Spatializer.dll"'
) | Set-Content -LiteralPath $soundStageConfig -Encoding UTF8

$mainConfig = Join-Path $configRoot 'config.txt'
if (-not (Test-Path -LiteralPath $mainConfig)) {
    New-Item -ItemType File -Path $mainConfig | Out-Null
}
$mainConfigText = Get-Content -LiteralPath $mainConfig -Raw -ErrorAction SilentlyContinue
if ($null -eq $mainConfigText -or
    $mainConfigText -notmatch '(?im)^\s*Include:\s*ssap3\.txt\s*$') {
    Add-Content `
        -LiteralPath $mainConfig `
        -Value "`r`n# Managed by SSAP3`r`nInclude: ssap3.txt" `
        -Encoding UTF8
}

if (-not (Test-Path -LiteralPath $statePath)) {
    $stream = [IO.File]::Open(
        $statePath,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::ReadWrite)
    try {
        $stream.SetLength(4096)
    }
    finally {
        $stream.Dispose()
    }
}

$icacls = Join-Path $env:SystemRoot 'System32\icacls.exe'
& $icacls $programDataRoot '/grant' '*S-1-5-32-545:(OI)(CI)M' '/T' '/C' |
    Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Could not grant SSAP3 state-file access (icacls $LASTEXITCODE)."
}

Ensure-RegistryKey -Path $childApoRoot
Ensure-RegistryKey -Path $originalEffectsRoot
$endpointGuids = New-Object Collections.Generic.List[string]
foreach ($endpoint in Get-ChildItem -LiteralPath $renderRoot) {
    $endpointRegistryPath =
        Join-Path $renderRoot $endpoint.PSChildName
    $propertiesPath = Join-Path $endpoint.PSPath 'Properties'
    $properties = Get-ItemProperty `
        -LiteralPath $propertiesPath `
        -ErrorAction SilentlyContinue
    if ($null -eq $properties) {
        continue
    }

    $deviceName = [string](Get-PropertyValue $properties $deviceNameValue)
    if ($deviceName -notmatch 'AirPods' -or
        $deviceName -match 'Hands-Free|Headset') {
        continue
    }

    $fxPath = Join-Path $endpoint.PSPath 'FxProperties'
    $fxRegistryPath = Join-Path $endpointRegistryPath 'FxProperties'
    if (-not (Test-Path -LiteralPath $fxPath)) {
        Ensure-RegistryKey -Path $fxPath
    }

    try {
        $probeName = 'Ssap3WriteProbe'
        New-ItemProperty `
            -LiteralPath $fxPath `
            -Name $probeName `
            -PropertyType DWord `
            -Value 1 `
            -Force | Out-Null
        Remove-ItemProperty -LiteralPath $fxPath -Name $probeName
    }
    catch {
        Grant-RegistryAdministratorsControl `
            -Path $endpointRegistryPath
        Grant-RegistryAdministratorsControl -Path $fxRegistryPath
    }

    $combined = $null -ne (
        Get-PropertyValue $properties $combinedDeviceValue)
    Save-OriginalEffects `
        -EndpointGuid $endpoint.PSChildName `
        -FxPath $fxPath `
        -Combined $combined

    Remove-ItemProperty `
        -LiteralPath $fxPath `
        -Name $guidValueNames[0], $guidValueNames[1] `
        -ErrorAction SilentlyContinue
    Set-RegistryString `
        -Path $fxPath `
        -Name $sfxGuidName `
        -Value $equalizerGuidPre
    Set-RegistryMultiStringIfMissing -Path $fxPath -Name $sfxModesName

    if ($combined) {
        Set-RegistryString `
            -Path $fxPath `
            -Name $mfxGuidName `
            -Value $equalizerGuidPost
        Set-RegistryMultiStringIfMissing -Path $fxPath -Name $mfxModesName
    }
    else {
        Set-RegistryString `
            -Path $fxPath `
            -Name $efxGuidName `
            -Value $equalizerGuidPost
        Set-RegistryMultiStringIfMissing -Path $fxPath -Name $efxModesName
    }

    Remove-ItemProperty `
        -LiteralPath $fxPath `
        -Name $disableEnhancementsName `
        -ErrorAction SilentlyContinue
    $endpointGuids.Add($endpoint.PSChildName)
    Write-Step "Attached to $deviceName ($($endpoint.PSChildName))."
}

if ($endpointGuids.Count -eq 0) {
    throw (
        'No AirPods A2DP Stereo render endpoint was found. ' +
        'Connect AirPods in Stereo mode and run setup again.')
}

New-ItemProperty `
    -LiteralPath $spatialRegistry `
    -Name 'Enabled' `
    -PropertyType DWord `
    -Value 1 `
    -Force | Out-Null
New-ItemProperty `
    -LiteralPath $spatialRegistry `
    -Name 'EndpointCount' `
    -PropertyType DWord `
    -Value $endpointGuids.Count `
    -Force | Out-Null
New-ItemProperty `
    -LiteralPath $spatialRegistry `
    -Name 'EndpointGuids' `
    -PropertyType MultiString `
    -Value @($endpointGuids) `
    -Force | Out-Null
Write-Step 'Restarting the Windows Audio service.'
Stop-Service -Name Audiosrv -Force
try {
    # AudioSrv may write its cached "enhancements disabled" flag while
    # stopping. Remove it after the service is down so the newly attached
    # system effect is active on the next audio graph.
    foreach ($endpointGuid in $endpointGuids) {
        $fxPath = Join-Path (
            Join-Path $renderRoot $endpointGuid) 'FxProperties'
        Remove-ItemProperty `
            -LiteralPath $fxPath `
            -Name $disableEnhancementsName `
            -ErrorAction SilentlyContinue
    }
}
finally {
    Start-Service -Name Audiosrv
}
Write-Step 'SSAP3 spatial audio engine installed.'
