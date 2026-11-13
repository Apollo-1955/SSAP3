#Requires -Version 5.1
<#
    Shared engine for SSAP3 Setup and Uninstall.

    Design rules (SECURITY_AUDIT.md S-01/S-02/S-03/S-07/S-15/S-16/S-17):
      * Never touch boot configuration, Secure Boot, BitLocker, Code Integrity,
        Defender, Memory Integrity or certificate stores.
      * Elevated code and state live only under %ProgramFiles%\SSAP3 (admin-only).
        Nothing elevated ever reads a user-writable file.
      * Every system change is recorded in the install manifest BEFORE it is made,
        so failed installs roll back and uninstall restores exactly.
      * Only SSAP3-owned objects are removed; everything else is restored to its
        recorded prior value, never deleted blindly.
#>

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

$script:ProductName = 'SSAP3'
$script:InstallRoot = Join-Path $env:ProgramFiles 'SSAP3'
$script:SetupRoot = Join-Path $script:InstallRoot 'setup'
$script:ManifestPath = Join-Path $script:SetupRoot 'install-manifest.json'
$script:LogPath = Join-Path $script:SetupRoot 'install.log'
$script:ProgramDataRoot = Join-Path $env:ProgramData 'SSAP3'
$script:RuntimeRoot = Join-Path $script:ProgramDataRoot 'Runtime'
$script:StateFile = Join-Path $script:RuntimeRoot 'head-pose.bin'
$script:SpatialRegistry = 'HKLM:\SOFTWARE\SSAP3\SpatialAudio'
$script:ProductRegistry = 'HKLM:\SOFTWARE\SSAP3'
$script:AudioPolicyKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Audio'
$script:RenderRoot = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render'
$script:EqualizerRoot = Join-Path $env:ProgramFiles 'EqualizerAPO'
$script:EqualizerRegistry = 'HKLM:\SOFTWARE\EqualizerAPO'
$script:RunKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
$script:Manifest = $null

# Well-known SIDs.
$script:SidSystem = 'S-1-5-18'
$script:SidAdministrators = 'S-1-5-32-544'
$script:SidInteractive = 'S-1-5-4'
$script:SidLocalService = 'S-1-5-19'
$script:SidUsers = 'S-1-5-32-545'

function Write-Ssap3Log {
    param([Parameter(Mandatory = $true)][string]$Message)
    $line = '{0:yyyy-MM-dd HH:mm:ss}  {1}' -f (Get-Date), $Message
    Write-Output $line
    if (Test-Path -LiteralPath $script:SetupRoot -PathType Container) {
        Add-Content -LiteralPath $script:LogPath -Value $line -Encoding UTF8
    }
}

function Assert-Ssap3Administrator {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'SSAP3 Setup must run elevated (it is started by SSAP3-Setup.exe).'
    }
}

function Invoke-Ssap3Native {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [int[]]$AllowedExitCodes = @(0)
    )
    Write-Ssap3Log ('> {0} {1}' -f (Split-Path $FilePath -Leaf), ($Arguments -join ' '))
    $output = & $FilePath @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    foreach ($line in $output) { if ("$line".Trim()) { Write-Ssap3Log "  $line" } }
    if ($AllowedExitCodes -notcontains $exitCode) {
        throw ('{0} failed with exit code {1}.' -f (Split-Path $FilePath -Leaf), $exitCode)
    }
    return $output
}

# ---------------------------------------------------------------- manifest --

function New-Ssap3Manifest {
    param([Parameter(Mandatory = $true)][string]$Version)
    $script:Manifest = [ordered]@{
        product = $script:ProductName
        version = $Version
        createdAt = (Get-Date).ToString('o')
        steps = New-Object System.Collections.ArrayList
    }
    Save-Ssap3Manifest
}

function Read-Ssap3Manifest {
    if (-not (Test-Path -LiteralPath $script:ManifestPath -PathType Leaf)) {
        return $null
    }
    Assert-Ssap3AdminOnlyPath -Path $script:ManifestPath
    $data = Get-Content -LiteralPath $script:ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $script:Manifest = [ordered]@{
        product = $data.product
        version = $data.version
        createdAt = $data.createdAt
        steps = New-Object System.Collections.ArrayList
    }
    foreach ($step in @($data.steps)) { [void]$script:Manifest.steps.Add($step) }
    return $script:Manifest
}

function Save-Ssap3Manifest {
    New-Item -ItemType Directory -Force -Path $script:SetupRoot | Out-Null
    $script:Manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $script:ManifestPath -Encoding UTF8
}

# Records a change BEFORE it is made; Complete-Ssap3Step marks it applied.
function Add-Ssap3Step {
    param(
        [Parameter(Mandatory = $true)][string]$Type,
        [Parameter(Mandatory = $true)][hashtable]$Data
    )
    $step = [pscustomobject]@{ id = $script:Manifest.steps.Count + 1; type = $Type; status = 'pending'; data = [pscustomobject]$Data }
    [void]$script:Manifest.steps.Add($step)
    Save-Ssap3Manifest
    return $step
}

function Complete-Ssap3Step {
    param([Parameter(Mandatory = $true)]$Step)
    $Step.status = 'applied'
    Save-Ssap3Manifest
}

# ---------------------------------------------------------------- ACL helpers --

function Get-Ssap3Sid([string]$Sid) { New-Object Security.Principal.SecurityIdentifier($Sid) }

function Test-Ssap3TrustedOwner {
    param([Parameter(Mandatory = $true)][string]$Path)
    $owner = (Get-Acl -LiteralPath $Path).GetOwner([Security.Principal.SecurityIdentifier]).Value
    return $owner -in @($script:SidSystem, $script:SidAdministrators, 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
}

# Refuses to trust any file or folder that a standard user could have planted (S-02).
function Assert-Ssap3AdminOnlyPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Ssap3TrustedOwner -Path $Path)) {
        throw "Refusing to use $Path because it is not owned by SYSTEM or Administrators."
    }
    $acl = Get-Acl -LiteralPath $Path
    foreach ($rule in $acl.Access) {
        $sid = $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
        $writeRights = [Security.AccessControl.FileSystemRights]'Write, Modify, FullControl, ChangePermissions, TakeOwnership, Delete'
        if ($rule.AccessControlType -eq 'Allow' -and
            $sid -notin @($script:SidSystem, $script:SidAdministrators, 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464') -and
            ($rule.FileSystemRights -band $writeRights) -ne 0) {
            throw "Refusing to use $Path because $sid can modify it."
        }
    }
}

function Set-Ssap3ProtectedAcl {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object[]]$Rules,
        [switch]$Directory
    )
    $acl = if ($Directory) { New-Object Security.AccessControl.DirectorySecurity } else { New-Object Security.AccessControl.FileSecurity }
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner((Get-Ssap3Sid $script:SidAdministrators))
    foreach ($rule in $Rules) {
        $inheritance = if ($Directory -and $rule.Inherit) { [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit' } else { [Security.AccessControl.InheritanceFlags]::None }
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
            (Get-Ssap3Sid $rule.Sid), [Security.AccessControl.FileSystemRights]$rule.Rights, $inheritance,
            [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

# ---------------------------------------------------------------- runtime folder --

<#
  %ProgramData%\SSAP3               admins/SYSTEM full, Users read (inherited)
  %ProgramData%\SSAP3\Runtime        admins/SYSTEM full, INTERACTIVE + LOCAL SERVICE list/read
  ...\Runtime\head-pose.bin         admins/SYSTEM full, INTERACTIVE read/write (no delete),
                                    LOCAL SERVICE (audiodg.exe) read
  Standard users can update the one state file the unprivileged app writes,
  but cannot create, delete or replace files anywhere in the tree.
#>
function Install-Ssap3RuntimeFolder {
    if (Test-Path -LiteralPath $script:ProgramDataRoot) {
        if (-not (Test-Ssap3TrustedOwner -Path $script:ProgramDataRoot)) {
            throw "$($script:ProgramDataRoot) already exists and is not owned by Administrators or SYSTEM. It may have been planted by another user; remove it as an administrator and run Setup again."
        }
    }
    $step = Add-Ssap3Step -Type 'ProgramDataFolder' -Data @{ path = $script:ProgramDataRoot; existed = (Test-Path -LiteralPath $script:ProgramDataRoot) }
    New-Item -ItemType Directory -Force -Path $script:ProgramDataRoot, $script:RuntimeRoot | Out-Null
    Set-Ssap3ProtectedAcl -Path $script:ProgramDataRoot -Directory -Rules @(
        @{ Sid = $script:SidSystem; Rights = 'FullControl'; Inherit = $true },
        @{ Sid = $script:SidAdministrators; Rights = 'FullControl'; Inherit = $true },
        @{ Sid = $script:SidUsers; Rights = 'ReadAndExecute'; Inherit = $true })
    Set-Ssap3ProtectedAcl -Path $script:RuntimeRoot -Directory -Rules @(
        @{ Sid = $script:SidSystem; Rights = 'FullControl'; Inherit = $true },
        @{ Sid = $script:SidAdministrators; Rights = 'FullControl'; Inherit = $true },
        @{ Sid = $script:SidInteractive; Rights = 'ReadAndExecute'; Inherit = $false },
        @{ Sid = $script:SidLocalService; Rights = 'ReadAndExecute'; Inherit = $false })
    if (-not (Test-Path -LiteralPath $script:StateFile)) {
        $stream = [IO.File]::Open($script:StateFile, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        try { $stream.SetLength(4096) } finally { $stream.Dispose() }
    }
    Set-Ssap3ProtectedAcl -Path $script:StateFile -Rules @(
        @{ Sid = $script:SidSystem; Rights = 'FullControl' },
        @{ Sid = $script:SidAdministrators; Rights = 'FullControl' },
        @{ Sid = $script:SidInteractive; Rights = 'Read, Write' },
        @{ Sid = $script:SidLocalService; Rights = 'Read' })
    Complete-Ssap3Step $step
    Write-Ssap3Log "Runtime folder ready with protected ACLs: $($script:RuntimeRoot)"
}

# ---------------------------------------------------------------- registry helpers --

function Get-Ssap3RegistryValue {
    param([string]$Path, [string]$Name)
    $item = Get-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $item) { return [pscustomobject]@{ exists = $false; value = $null; kind = $null } }
    $key = Get-Item -LiteralPath $Path
    return [pscustomobject]@{ exists = $true; value = $item.$Name; kind = $key.GetValueKind($Name).ToString() }
}

function Restore-Ssap3RegistryValue {
    param([string]$Path, [string]$Name, $Prior)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    if ($Prior.exists) {
        New-ItemProperty -LiteralPath $Path -Name $Name -Value $Prior.value -PropertyType $Prior.kind -Force | Out-Null
    } else {
        Remove-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction SilentlyContinue
    }
}

function Set-Ssap3RegistryValue {
    param([string]$Path, [string]$Name, $Value, [string]$Kind, [string]$Reason)
    $prior = Get-Ssap3RegistryValue -Path $Path -Name $Name
    $step = Add-Ssap3Step -Type 'RegistryValue' -Data @{ path = $Path; name = $Name; prior = $prior; reason = $Reason }
    if (-not (Test-Path -LiteralPath $Path)) { New-Item -Path $Path -Force | Out-Null }
    New-ItemProperty -LiteralPath $Path -Name $Name -Value $Value -PropertyType $Kind -Force | Out-Null
    Complete-Ssap3Step $step
}

# ---------------------------------------------------------------- processes --

# S-15: only processes whose image lives inside the SSAP3 install folder.
function Stop-Ssap3Processes {
    $root = [IO.Path]::GetFullPath($script:InstallRoot).TrimEnd('\') + '\'
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object {
            Write-Ssap3Log "Stopping $($_.Name) (pid $($_.ProcessId))."
            Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
        }
}

# ---------------------------------------------------------------- driver --

# Production rule: the catalog must chain to Microsoft's hardware publisher
# (attestation or WHQL). Locally signed or unsigned packages are never installed.
function Test-Ssap3DriverProductionSigned {
    param([Parameter(Mandatory = $true)][string]$CatalogPath)
    if (-not (Test-Path -LiteralPath $CatalogPath -PathType Leaf)) { return $false }
    $signature = Get-AuthenticodeSignature -LiteralPath $CatalogPath
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate) { return $false }
    return $signature.SignerCertificate.Subject -match 'CN=Microsoft Windows Hardware Compatibility Publisher'
}

function Get-Ssap3DriverPackages {
    $packages = @()
    $output = & (Join-Path $env:SystemRoot 'System32\pnputil.exe') /enum-drivers 2>$null
    $current = @{}
    foreach ($line in $output) {
        if ($line -match '^\s*Published Name\s*:\s*(\S+)') { $current = @{ Published = $Matches[1] } }
        elseif ($line -match '^\s*Original Name\s*:\s*(\S+)') { $current.Original = $Matches[1] }
        elseif ($line -match '^\s*Provider Name\s*:\s*(.+)$') {
            $current.Provider = $Matches[1].Trim()
            if ($current.Original -eq 'ssap3aap.inf' -and $current.Provider -eq 'SSAP3') {
                $packages += [pscustomobject]$current
            }
        }
    }
    return $packages
}

function Install-Ssap3Driver {
    param([Parameter(Mandatory = $true)][string]$PackageRoot)
    $inf = Join-Path $PackageRoot 'driver\ssap3aap.inf'
    $cat = Join-Path $PackageRoot 'driver\ssap3aap.cat'
    if (-not (Test-Ssap3DriverProductionSigned -CatalogPath $cat)) {
        Write-Ssap3Log 'AAP driver skipped: the package is not signed by Microsoft (attestation/WHQL). Listening modes and head tracking stay unavailable; nothing was changed.'
        return $false
    }
    $before = @(Get-Ssap3DriverPackages | ForEach-Object Published)
    $step = Add-Ssap3Step -Type 'DriverPackage' -Data @{ originalName = 'ssap3aap.inf'; provider = 'SSAP3'; preexisting = $before }
    Invoke-Ssap3Native -FilePath (Join-Path $env:SystemRoot 'System32\pnputil.exe') -Arguments @('/add-driver', $inf, '/install') -AllowedExitCodes @(0, 259, 3010) | Out-Null
    $step.data | Add-Member -NotePropertyName published -NotePropertyValue @(Get-Ssap3DriverPackages | ForEach-Object Published | Where-Object { $_ -notin $before }) -Force
    Complete-Ssap3Step $step
    return $true
}

function Remove-Ssap3DriverPackages {
    foreach ($package in Get-Ssap3DriverPackages) {
        Invoke-Ssap3Native -FilePath (Join-Path $env:SystemRoot 'System32\pnputil.exe') -Arguments @('/delete-driver', $package.Published, '/uninstall') -AllowedExitCodes @(0, 3010) | Out-Null
    }
}

# ================================================================ opt-in spatial engine
# Security note (SECURITY_AUDIT.md S-07): Equalizer APO requires DisableProtectedAudioDG=1,
# which turns off the APO signature check system-wide. Setup does this only for the explicit
# 'system-wide spatial engine' task and records the prior value for exact restore.

$script:FxNames = @(
    '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},1',
    '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},2',
    '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},5',
    '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},6',
    '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7',
    '{d3993a3f-99c2-4402-b5ec-a92a0367664b},5',
    '{d3993a3f-99c2-4402-b5ec-a92a0367664b},6',
    '{d3993a3f-99c2-4402-b5ec-a92a0367664b},7',
    '{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5'
)
$script:EqualizerPre = '{EACD2258-FCAC-4FF4-B36D-419E924A6D79}'
$script:EqualizerPost = '{EC1CC9CE-FAED-4822-828A-82A81A6F018F}'
$script:DefaultProcessingMode = '{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}'
$script:DeviceInstanceProperty = '{b3f8fa53-0004-438e-9003-51a46e139bfc},2'
$script:CombinedDeviceProperty = '{b3f8fa53-0004-438e-9003-51a46e139bfc},41'

function Get-Ssap3AirPodsStereoEndpoints {
    foreach ($endpoint in Get-ChildItem -LiteralPath $script:RenderRoot -ErrorAction SilentlyContinue) {
        $properties = Get-ItemProperty -LiteralPath (Join-Path $endpoint.PSPath 'Properties') -ErrorAction SilentlyContinue
        if ($null -eq $properties) { continue }
        $instance = [string]$properties.PSObject.Properties[$script:DeviceInstanceProperty].Value
        # Bluetooth A2DP sink ({0000110B-...}) of an Apple device (VID 004C) only.
        if ($instance -match '\{0000110B-0000-1000-8000-00805F9B34FB\}' -and $instance -match 'VID&0001004C') {
            [pscustomobject]@{
                Guid = $endpoint.PSChildName
                FxPath = Join-Path $endpoint.PSPath 'FxProperties'
                KeyPath = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\$($endpoint.PSChildName)"
                Combined = $null -ne $properties.PSObject.Properties[$script:CombinedDeviceProperty]
            }
        }
    }
}

function Get-Ssap3KeySddl([string]$Path) {
    $relative = $Path.Substring('HKLM:\'.Length)
    $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($relative, [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadSubTree, [Security.AccessControl.RegistryRights]::ReadPermissions)
    if ($null -eq $key) { return $null }
    try { return $key.GetAccessControl([Security.AccessControl.AccessControlSections]'Owner, Access').GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]'Owner, Access') }
    finally { $key.Dispose() }
}

function Set-Ssap3KeySddl([string]$Path, [string]$Sddl) {
    $relative = $Path.Substring('HKLM:\'.Length)
    $rights = [Security.AccessControl.RegistryRights]'ChangePermissions, TakeOwnership, ReadPermissions'
    $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($relative, [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree, $rights)
    if ($null -eq $key) { return }
    try {
        $security = New-Object Security.AccessControl.RegistrySecurity
        $security.SetSecurityDescriptorSddlForm($Sddl, [Security.AccessControl.AccessControlSections]'Owner, Access')
        $key.SetAccessControl($security)
    }
    finally { $key.Dispose() }
}

function Test-Ssap3KeyWritable([string]$Path) {
    try {
        New-ItemProperty -LiteralPath $Path -Name 'SSAP3WriteProbe' -PropertyType DWord -Value 1 -Force | Out-Null
        Remove-ItemProperty -LiteralPath $Path -Name 'SSAP3WriteProbe'
        return $true
    }
    catch [System.UnauthorizedAccessException], [System.Security.SecurityException] { return $false }
}

# Takes ownership of one endpoint key only when Windows blocks writes, after
# recording its exact owner and DACL so uninstall can put them back.
function Grant-Ssap3EndpointWrite([string]$KeyPath, [string]$FxPath) {
    if (Test-Ssap3KeyWritable $FxPath) { return }
    foreach ($path in @($KeyPath, $FxPath)) {
        $step = Add-Ssap3Step -Type 'RegistryKeySecurity' -Data @{ path = $path; sddl = (Get-Ssap3KeySddl $path) }
        $privilegeHelper = @'
using System; using System.Runtime.InteropServices;
public static class Ssap3Privilege {
  [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Low; public int High; }
  [StructLayout(LayoutKind.Sequential)] struct TP { public uint Count; public LUID Luid; public uint Attr; }
  [DllImport("advapi32.dll", SetLastError=true)] static extern bool OpenProcessToken(IntPtr p, uint a, out IntPtr t);
  [DllImport("advapi32.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern bool LookupPrivilegeValue(string s, string n, out LUID l);
  [DllImport("advapi32.dll", SetLastError=true)] static extern bool AdjustTokenPrivileges(IntPtr t, bool d, ref TP n, uint l, IntPtr p, IntPtr r);
  [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  public static void Enable(string name) {
    IntPtr token; if (!OpenProcessToken(GetCurrentProcess(), 0x28, out token)) throw new InvalidOperationException("OpenProcessToken");
    try { LUID luid; if (!LookupPrivilegeValue(null, name, out luid)) throw new InvalidOperationException(name);
      TP tp = new TP { Count = 1, Luid = luid, Attr = 2 }; if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) throw new InvalidOperationException(name);
    } finally { CloseHandle(token); }
  }
}
'@
        if (-not ('Ssap3Privilege' -as [type])) { Add-Type -TypeDefinition $privilegeHelper }
        [Ssap3Privilege]::Enable('SeTakeOwnershipPrivilege')
        [Ssap3Privilege]::Enable('SeRestorePrivilege')
        $relative = $path.Substring('HKLM:\'.Length)
        $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($relative, [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree, [Security.AccessControl.RegistryRights]::TakeOwnership)
        try {
            $acl = $key.GetAccessControl([Security.AccessControl.AccessControlSections]::Owner)
            $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
            $key.SetAccessControl($acl)
        } finally { $key.Dispose() }
        $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($relative, [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree, [Security.AccessControl.RegistryRights]'ChangePermissions, ReadKey')
        try {
            $acl = $key.GetAccessControl()
            $acl.SetAccessRule((New-Object Security.AccessControl.RegistryAccessRule(
                (New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')), 'FullControl', 'ContainerInherit', 'None', 'Allow')))
            $key.SetAccessControl($acl)
        } finally { $key.Dispose() }
        Complete-Ssap3Step $step
    }
}

function Install-Ssap3SpatialEngine {
    param([Parameter(Mandatory = $true)][string]$PackageRoot)

    $runtimeSource = Join-Path $PackageRoot 'spatial\EqualizerAPO'
    $pluginSource = Join-Path $PackageRoot 'spatial\SSAP3Spatializer.dll'
    foreach ($required in @($pluginSource, (Join-Path $runtimeSource 'EqualizerAPO.dll'))) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Spatial engine package file is missing: $required" }
    }

    $endpoints = @(Get-Ssap3AirPodsStereoEndpoints)
    if ($endpoints.Count -eq 0) {
        throw 'No AirPods A2DP stereo endpoint exists yet. Connect the AirPods once, then run Setup again to add the spatial engine.'
    }

    $ownsEqualizer = -not (Test-Path -LiteralPath (Join-Path $script:EqualizerRoot 'EqualizerAPO.dll'))
    $audio = Get-Service -Name Audiosrv
    $step = Add-Ssap3Step -Type 'AudioServiceRestart' -Data @{ wasRunning = ($audio.Status -eq 'Running') }
    Stop-Service -Name Audiosrv -Force
    try {
        # Explicit, consented security setting change (recorded with its prior value).
        Set-Ssap3RegistryValue -Path $script:AudioPolicyKey -Name 'DisableProtectedAudioDG' -Value 1 -Kind DWord -Reason 'Equalizer APO host for the opt-in spatial engine'

        if ($ownsEqualizer) {
            $files = @(Get-ChildItem -LiteralPath $runtimeSource -File | ForEach-Object Name)
            $copy = Add-Ssap3Step -Type 'EqualizerRuntime' -Data @{ root = $script:EqualizerRoot; files = $files }
            New-Item -ItemType Directory -Force -Path $script:EqualizerRoot, (Join-Path $script:EqualizerRoot 'config'), (Join-Path $script:EqualizerRoot 'VSTPlugins') | Out-Null
            foreach ($file in $files) { Copy-Item -LiteralPath (Join-Path $runtimeSource $file) -Destination (Join-Path $script:EqualizerRoot $file) -Force }
            Complete-Ssap3Step $copy
            Set-Ssap3RegistryValue -Path $script:EqualizerRegistry -Name 'InstallPath' -Value $script:EqualizerRoot -Kind String -Reason 'Equalizer APO runtime'
            Set-Ssap3RegistryValue -Path $script:EqualizerRegistry -Name 'ConfigPath' -Value (Join-Path $script:EqualizerRoot 'config') -Kind String -Reason 'Equalizer APO runtime'
            $register = Add-Ssap3Step -Type 'ComRegistration' -Data @{ dll = (Join-Path $script:EqualizerRoot 'EqualizerAPO.dll') }
            Invoke-Ssap3Native -FilePath (Join-Path $env:SystemRoot 'System32\regsvr32.exe') -Arguments @('/s', (Join-Path $script:EqualizerRoot 'EqualizerAPO.dll')) | Out-Null
            Complete-Ssap3Step $register
        }

        $pluginTarget = Join-Path $script:EqualizerRoot 'VSTPlugins\SSAP3Spatializer.dll'
        $plugin = Add-Ssap3Step -Type 'OwnedFile' -Data @{ path = $pluginTarget }
        Copy-Item -LiteralPath $pluginSource -Destination $pluginTarget -Force
        Complete-Ssap3Step $plugin

        $configRoot = Join-Path $script:EqualizerRoot 'config'
        $includeFile = Join-Path $configRoot 'ssap3.txt'
        $include = Add-Ssap3Step -Type 'OwnedFile' -Data @{ path = $includeFile }
        @('# SSAP3 Spatial Engine (managed by SSAP3 Setup)', 'Device: AirPods', 'Stage: post-mix', 'VSTPlugin: Library "SSAP3Spatializer.dll"') |
            Set-Content -LiteralPath $includeFile -Encoding UTF8
        Complete-Ssap3Step $include

        $mainConfig = Join-Path $configRoot 'config.txt'
        $prior = if (Test-Path -LiteralPath $mainConfig) { Get-Content -LiteralPath $mainConfig -Raw } else { $null }
        $configStep = Add-Ssap3Step -Type 'ConfigInclude' -Data @{ path = $mainConfig; existed = ($null -ne $prior) }
        if ($null -eq $prior -or $prior -notmatch '(?im)^\s*Include:\s*ssap3\.txt\s*$') {
            Add-Content -LiteralPath $mainConfig -Value "`r`n# Managed by SSAP3`r`nInclude: ssap3.txt" -Encoding UTF8
        }
        Complete-Ssap3Step $configStep

        foreach ($endpoint in $endpoints) {
            Grant-Ssap3EndpointWrite -KeyPath $endpoint.KeyPath -FxPath $endpoint.FxPath
            $priorValues = @{}
            foreach ($name in $script:FxNames) { $priorValues[$name] = Get-Ssap3RegistryValue -Path $endpoint.FxPath -Name $name }
            foreach ($name in $script:FxNames[0..4]) {
                if ([string]$priorValues[$name].value -in @($script:EqualizerPre, $script:EqualizerPost) -and $ownsEqualizer) {
                    throw "Endpoint $($endpoint.Guid) already references Equalizer APO but no Equalizer APO runtime is installed. Repair it first."
                }
            }
            $fx = Add-Ssap3Step -Type 'EndpointEffects' -Data @{ fxPath = $endpoint.FxPath; endpoint = $endpoint.Guid; prior = [pscustomobject]$priorValues }

            $childPath = Join-Path "$script:EqualizerRegistry\Child APOs" $endpoint.Guid
            New-Item -Path $childPath -Force | Out-Null
            $originalSfx = [string]$priorValues[$script:FxNames[2]].value
            $originalPost = if ($endpoint.Combined) { [string]$priorValues[$script:FxNames[3]].value } else { [string]$priorValues[$script:FxNames[4]].value }
            if ($originalSfx -in @($script:EqualizerPre, $script:EqualizerPost)) { $originalSfx = '' }
            if ($originalPost -in @($script:EqualizerPre, $script:EqualizerPost)) { $originalPost = '' }
            New-ItemProperty -LiteralPath $childPath -Name 'PreMixChild' -Value $originalSfx -PropertyType String -Force | Out-Null
            New-ItemProperty -LiteralPath $childPath -Name 'PostMixChild' -Value $originalPost -PropertyType String -Force | Out-Null
            New-ItemProperty -LiteralPath $childPath -Name 'AllowSilentBufferModification' -Value 'false' -PropertyType String -Force | Out-Null
            New-ItemProperty -LiteralPath $childPath -Name 'Version' -Value '2' -PropertyType String -Force | Out-Null

            Remove-ItemProperty -LiteralPath $endpoint.FxPath -Name $script:FxNames[0], $script:FxNames[1] -ErrorAction SilentlyContinue
            New-ItemProperty -LiteralPath $endpoint.FxPath -Name $script:FxNames[2] -Value $script:EqualizerPre -PropertyType String -Force | Out-Null
            $postName = if ($endpoint.Combined) { $script:FxNames[3] } else { $script:FxNames[4] }
            New-ItemProperty -LiteralPath $endpoint.FxPath -Name $postName -Value $script:EqualizerPost -PropertyType String -Force | Out-Null
            foreach ($modes in $script:FxNames[5..7]) {
                if (-not $priorValues[$modes].exists) {
                    New-ItemProperty -LiteralPath $endpoint.FxPath -Name $modes -Value @($script:DefaultProcessingMode) -PropertyType MultiString -Force | Out-Null
                }
            }
            Remove-ItemProperty -LiteralPath $endpoint.FxPath -Name $script:FxNames[8] -ErrorAction SilentlyContinue
            Complete-Ssap3Step $fx
            Write-Ssap3Log "Spatial engine attached to endpoint $($endpoint.Guid)."
        }

        Set-Ssap3RegistryValue -Path $script:SpatialRegistry -Name 'Enabled' -Value 1 -Kind DWord -Reason 'SSAP3 spatial engine registration'
        Set-Ssap3RegistryValue -Path $script:SpatialRegistry -Name 'PluginPath' -Value $pluginTarget -Kind String -Reason 'SSAP3 spatial engine registration'
        Set-Ssap3RegistryValue -Path $script:SpatialRegistry -Name 'OwnsEqualizerApo' -Value ([int]$ownsEqualizer) -Kind DWord -Reason 'SSAP3 spatial engine registration'
    }
    finally {
        Start-Service -Name Audiosrv
        Complete-Ssap3Step $step
    }
}

# ================================================================ undo engine
# Reverses applied (and pending, best effort) steps newest-first. Every undo is
# idempotent: it checks the current state and only touches SSAP3-owned objects.

function Undo-Ssap3Step {
    param([Parameter(Mandatory = $true)]$Step)
    $d = $Step.data
    switch ($Step.type) {
        'RegistryValue' {
            Restore-Ssap3RegistryValue -Path $d.path -Name $d.name -Prior $d.prior
            if ($d.path -like "$($script:ProductRegistry)*" -and (Test-Path -LiteralPath $d.path)) {
                $key = Get-Item -LiteralPath $d.path
                if ($key.ValueCount -eq 0 -and $key.SubKeyCount -eq 0) { Remove-Item -LiteralPath $d.path -Force }
            }
        }
        'RegistryKeySecurity' {
            if ($d.sddl) { Set-Ssap3KeySddl -Path $d.path -Sddl $d.sddl }
        }
        'EndpointEffects' {
            if (Test-Path -LiteralPath $d.fxPath) {
                foreach ($name in $script:FxNames) {
                    $prior = $d.prior.PSObject.Properties[$name].Value
                    if ($null -ne $prior) { Restore-Ssap3RegistryValue -Path $d.fxPath -Name $name -Prior $prior }
                }
            }
            Remove-Item -LiteralPath (Join-Path "$($script:EqualizerRegistry)\Child APOs" $d.endpoint) -Recurse -Force -ErrorAction SilentlyContinue
        }
        'ConfigInclude' {
            if (Test-Path -LiteralPath $d.path) {
                $lines = @(Get-Content -LiteralPath $d.path | Where-Object {
                    $_ -notmatch '(?i)^\s*Include:\s*ssap3\.txt\s*$' -and $_ -notmatch '(?i)^\s*#\s*Managed by SSAP3\s*$' })
                if (-not $d.existed -and -not ($lines | Where-Object { "$_".Trim() })) { Remove-Item -LiteralPath $d.path -Force }
                else { $lines | Set-Content -LiteralPath $d.path -Encoding UTF8 }
            }
        }
        'OwnedFile' {
            Remove-Item -LiteralPath $d.path -Force -ErrorAction SilentlyContinue
        }
        'ComRegistration' {
            if (Test-Path -LiteralPath $d.dll) {
                Invoke-Ssap3Native -FilePath (Join-Path $env:SystemRoot 'System32\regsvr32.exe') -Arguments @('/u', '/s', $d.dll) | Out-Null
            }
        }
        'EqualizerRuntime' {
            foreach ($file in @($d.files)) { Remove-Item -LiteralPath (Join-Path $d.root $file) -Force -ErrorAction SilentlyContinue }
            foreach ($folder in @('VSTPlugins', 'config', '')) {
                $path = if ($folder) { Join-Path $d.root $folder } else { $d.root }
                if ((Test-Path -LiteralPath $path) -and -not (Get-ChildItem -LiteralPath $path -Force)) { Remove-Item -LiteralPath $path -Force }
            }
        }
        'DriverPackage' {
            foreach ($package in Get-Ssap3DriverPackages) {
                if ($package.Published -notin @($d.preexisting)) {
                    Invoke-Ssap3Native -FilePath (Join-Path $env:SystemRoot 'System32\pnputil.exe') -Arguments @('/delete-driver', $package.Published, '/uninstall') -AllowedExitCodes @(0, 3010) | Out-Null
                }
            }
        }
        'ProgramDataFolder' {
            $expected = [IO.Path]::GetFullPath($script:ProgramDataRoot).TrimEnd('\')
            if ([IO.Path]::GetFullPath($d.path).TrimEnd('\') -eq $expected -and (Test-Path -LiteralPath $expected)) {
                Remove-Item -LiteralPath $expected -Recurse -Force
            }
        }
        'AudioServiceRestart' { }
        default { Write-Ssap3Log "Unknown manifest step type '$($Step.type)' left untouched." }
    }
}

function Undo-Ssap3Manifest {
    if ($null -eq $script:Manifest) { return }
    $steps = @($script:Manifest.steps)
    $touchesAudio = @($steps | Where-Object { $_.type -in @('EndpointEffects', 'EqualizerRuntime', 'ComRegistration', 'ConfigInclude', 'OwnedFile') }).Count -gt 0
    if ($touchesAudio) { Stop-Service -Name Audiosrv -Force -ErrorAction SilentlyContinue }
    try {
        [array]::Reverse($steps)
        foreach ($step in $steps) {
            try {
                Undo-Ssap3Step -Step $step
                $step.status = 'reverted'
                Write-Ssap3Log "Reverted step $($step.id) ($($step.type))."
            }
            catch {
                $step.status = 'revert-failed'
                Write-Ssap3Log "Could not revert step $($step.id) ($($step.type)): $($_.Exception.Message)"
            }
            Save-Ssap3Manifest
        }
    }
    finally {
        if ($touchesAudio) { Start-Service -Name Audiosrv -ErrorAction SilentlyContinue }
    }
}

Export-ModuleMember -Function * -Variable ProductName, InstallRoot, SetupRoot, ManifestPath, LogPath, ProgramDataRoot, RuntimeRoot, StateFile, SpatialRegistry, ProductRegistry, AudioPolicyKey, RenderRoot, EqualizerRoot, EqualizerRegistry, RunKey
