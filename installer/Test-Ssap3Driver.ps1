#Requires -Version 5.1
<#
.SYNOPSIS
    Read-only health check of the SSAP3 AAP driver: signature, installation,
    loading, device start, control interface and head-tracking data.

.DESCRIPTION
    Each level is reported separately, so that "installed" is never mistaken
    for "working":

      Package    (with -Package) the files, x64/HVCI image flags, INF facts,
                 the catalog's signature class and whether the .sys and .inf
                 are exactly the files that were signed.
      System     Secure Boot, Memory Integrity, Defender, Test Mode, and Code
                 Integrity events that blocked ssap3aap.sys.
      Installed  the driver-store package and its signature, the ssap3aap
                 service, devices using it (with Windows problem codes), the
                 driver's control interface (read-only status query; no AACP
                 session is opened) and how fresh SSAP3's head-pose record is.

    Signature classes (installer\Ssap3Setup.psm1, Get-Ssap3CatalogSignature):
      Unsigned, NotTrusted (for example a self-signed or WDK test
      certificate), NotMicrosoft (trusted, but not through a Microsoft root),
      MicrosoftOther (Microsoft, but not the driver publisher), MicrosoftWhql,
      MicrosoftAttestation. Only the last two are installable on a Secure
      Boot PC, and SSAP3 Setup installs nothing else.

    Nothing is changed. It runs without elevation; a few facts (some Code
    Integrity events) need an elevated prompt and are reported as such.
    Bluetooth addresses and device instance IDs are never printed (S-13).

.PARAMETER Package
    A folder that contains ssap3aap.inf, ssap3aap.sys and ssap3aap.cat (for
    example the unpacked package Microsoft returns). Optional.

.PARAMETER Require
    Fails unless the installed driver reaches this level:
    Installed, Started, Reachable or Tracking.

.EXAMPLE
    .\installer\Test-Ssap3Driver.ps1
.EXAMPLE
    .\installer\Test-Ssap3Driver.ps1 -Package "$env:USERPROFILE\Downloads\ssap3aap-signed" -Require Reachable
#>
[CmdletBinding()]
param(
    [string]$Package,
    [ValidateSet('None', 'Installed', 'Started', 'Reachable', 'Tracking')][string]$Require = 'None',
    [switch]$PassThru
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Ssap3Setup.psm1') -Force -DisableNameChecking -WarningAction SilentlyContinue

$script:Results = New-Object System.Collections.Generic.List[object]
function Add-Result([string]$Area, [string]$Check, [ValidateSet('Pass', 'Fail', 'Warn', 'Info')][string]$State, [string]$Detail) {
    $script:Results.Add([pscustomobject]@{ Area = $Area; Check = $Check; State = $State; Detail = $Detail })
}

function Get-RegistryValue([string]$Path, [string]$Name) {
    try { return (Get-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction Stop).$Name } catch { return $null }
}

# ------------------------------------------------------------------ package
function Test-PackageFolder([string]$Folder) {
    $area = 'Package'
    if (-not (Test-Path -LiteralPath $Folder -PathType Container)) {
        Add-Result $area 'Folder' 'Fail' "Not a folder: $Folder"
        return
    }

    $files = @{}
    foreach ($name in 'ssap3aap.inf', 'ssap3aap.sys', 'ssap3aap.cat') {
        $found = @(Get-ChildItem -LiteralPath $Folder -Filter $name -File -Recurse -ErrorAction SilentlyContinue)
        if ($found.Count -eq 1) { $files[$name] = $found[0].FullName }
        Add-Result $area $name $(if ($found.Count -eq 1) { 'Pass' } else { 'Fail' }) $(if ($found.Count -eq 1) { 'present' } elseif ($found.Count -eq 0) { 'missing' } else { "found $($found.Count) copies" })
    }

    if ($files.ContainsKey('ssap3aap.sys')) {
        $bytes = [IO.File]::ReadAllBytes($files['ssap3aap.sys'])
        $pe = if ($bytes.Length -gt 0x40) { [BitConverter]::ToInt32($bytes, 0x3C) } else { -1 }
        if ($pe -gt 0 -and ($pe + 24 + 96) -lt $bytes.Length -and [BitConverter]::ToUInt32($bytes, $pe) -eq 0x4550) {
            $machine = [BitConverter]::ToUInt16($bytes, $pe + 4)
            $magic = [BitConverter]::ToUInt16($bytes, $pe + 24)
            $subsystem = [BitConverter]::ToUInt16($bytes, $pe + 24 + 68)
            $flags = [BitConverter]::ToUInt16($bytes, $pe + 24 + 70)
            $x64 = $machine -eq 0x8664 -and $magic -eq 0x20B -and $subsystem -eq 1
            Add-Result $area 'Architecture' $(if ($x64) { 'Pass' } else { 'Fail' }) $(if ($x64) { 'x64 (PE32+) native kernel image' } else { ('machine 0x{0:X4}, magic 0x{1:X3}, subsystem {2}' -f $machine, $magic, $subsystem) })
            $missing = @()
            if (($flags -band 0x0100) -eq 0) { $missing += 'NX' }
            if (($flags -band 0x0040) -eq 0) { $missing += 'ASLR' }
            if (($flags -band 0x4000) -eq 0) { $missing += 'CFG' }
            Add-Result $area 'HVCI image flags' $(if ($missing.Count -eq 0) { 'Pass' } else { 'Fail' }) $(if ($missing.Count -eq 0) { 'NX, ASLR and CFG set (full section checks: driver\Test-DriverImage.ps1)' } else { "missing: $($missing -join ', ')" })
        }
        else {
            Add-Result $area 'Architecture' 'Fail' 'ssap3aap.sys is not a valid PE image.'
        }
    }

    if ($files.ContainsKey('ssap3aap.inf')) {
        $inf = Get-Content -LiteralPath $files['ssap3aap.inf']
        $version = ($inf | Where-Object { $_ -match '^\s*DriverVer\s*=' } | Select-Object -First 1) -replace '^\s*DriverVer\s*=\s*', ''
        $provider = ($inf | Where-Object { $_ -match '^\s*ProviderName\s*=' } | Select-Object -First 1) -replace '^\s*ProviderName\s*=\s*"?([^"]*)"?.*$', '$1'
        $amd64 = [bool]($inf | Where-Object { $_ -match '^\s*%ProviderName%\s*=\s*\w+\s*,\s*NTamd64' })
        $identity = Test-Ssap3DriverInfIdentity -InfPath $files['ssap3aap.inf']
        $ok = $identity -and $amd64
        Add-Result $area 'INF' $(if ($ok) { 'Pass' } else { 'Fail' }) "provider '$provider', DriverVer $version, NTamd64 models: $amd64, SSAP3 identity (catalog, ssap3aap service, Apple AAP/UARP IDs only): $identity"
    }

    if ($files.ContainsKey('ssap3aap.cat')) {
        $signature = Get-Ssap3CatalogSignature -CatalogPath $files['ssap3aap.cat']
        $production = $signature.Kind -in 'MicrosoftAttestation', 'MicrosoftWhql'
        $signer = if ($signature.Signer) { ($signature.Signer -replace ',.*$', '') } else { 'none' }
        $root = if ($signature.Root) { ($signature.Root -replace ',.*$', '') } else { 'none' }
        Add-Result $area 'Signature' $(if ($production) { 'Pass' } else { 'Fail' }) "$($signature.Kind): $($signature.Detail) Signer: $signer; root: $root"
        foreach ($member in 'ssap3aap.sys', 'ssap3aap.inf') {
            if ($files.ContainsKey($member)) {
                $listed = Test-Ssap3CatalogMember -CatalogPath $files['ssap3aap.cat'] -FilePath $files[$member]
                Add-Result $area "Catalog lists $member" $(if ($listed) { 'Pass' } else { 'Fail' }) $(if ($listed) { 'the file is exactly the one in the catalog' } else { 'NOT in the catalog: the file was changed after cataloguing, or belongs to another package' })
            }
        }
    }
}

# ------------------------------------------------------------------- system
function Test-SystemSecurity {
    $area = 'System'
    $secureBoot = Get-RegistryValue 'HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot\State' 'UEFISecureBootEnabled'
    Add-Result $area 'Secure Boot' $(if ($secureBoot -eq 1) { 'Pass' } else { 'Info' }) $(if ($secureBoot -eq 1) { 'on' } elseif ($null -eq $secureBoot) { 'unknown (legacy BIOS or not reported)' } else { 'off (SSAP3 never needs it off)' })

    $hvci = $null
    try {
        $guard = Get-CimInstance -Namespace 'root\Microsoft\Windows\DeviceGuard' -ClassName Win32_DeviceGuard -ErrorAction Stop
        $hvci = @($guard.SecurityServicesRunning) -contains 2
    }
    catch {
        $enabled = Get-RegistryValue 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity' 'Enabled'
        if ($null -ne $enabled) { $hvci = $enabled -eq 1 }
    }
    Add-Result $area 'Memory Integrity (HVCI)' $(if ($hvci -eq $true) { 'Pass' } else { 'Info' }) $(if ($hvci -eq $true) { 'running' } elseif ($null -eq $hvci) { 'unknown' } else { 'not running (SSAP3 never needs it off)' })

    try {
        $defender = Get-MpComputerStatus -ErrorAction Stop
        Add-Result $area 'Microsoft Defender' $(if ($defender.RealTimeProtectionEnabled) { 'Pass' } else { 'Info' }) $(if ($defender.RealTimeProtectionEnabled) { 'real-time protection on' } else { 'real-time protection off' })
    }
    catch {
        Add-Result $area 'Microsoft Defender' 'Info' 'status unavailable (another antivirus may be active)'
    }

    $options = "$(Get-RegistryValue 'HKLM:\SYSTEM\CurrentControlSet\Control' 'SystemStartOptions')"
    $testMode = $options -match 'TESTSIGNING'
    $noIntegrity = $options -match 'NOINTEGRITYCHECKS'
    $detail = if ($testMode -or $noIntegrity) { "boot options contain $(@(if ($testMode) { 'TESTSIGNING' }; if ($noIntegrity) { 'NOINTEGRITYCHECKS' }) -join ' and '). SSAP3 never needs either; turn them off." } else { 'off (normal signature enforcement)' }
    Add-Result $area 'Test Mode / integrity checks' $(if ($testMode -or $noIntegrity) { 'Warn' } else { 'Pass' }) $detail

    try {
        $since = (Get-Date).AddDays(-30)
        $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-CodeIntegrity/Operational'; StartTime = $since } -ErrorAction Stop |
            Where-Object { $_.Message -match 'ssap3aap' })
        if ($events.Count -eq 0) {
            Add-Result $area 'Code Integrity events' 'Pass' 'no events about ssap3aap.sys in the last 30 days'
        }
        else {
            $ids = ($events | Group-Object Id | ForEach-Object { "$($_.Name) x$($_.Count)" }) -join ', '
            Add-Result $area 'Code Integrity events' 'Fail' "Windows logged $($events.Count) event(s) about ssap3aap.sys (IDs $ids). The newest: $(($events[0].Message -split "`n")[0])"
        }
    }
    catch {
        $message = $_.Exception.Message
        if ($message -match 'No events were found') {
            Add-Result $area 'Code Integrity events' 'Pass' 'no Code Integrity events in the last 30 days'
        }
        else {
            Add-Result $area 'Code Integrity events' 'Info' "log not readable here ($message). Run elevated to check."
        }
    }
}

# ---------------------------------------------------------------- installed
function Get-DriverStorePackages {
    $output = & (Join-Path $env:SystemRoot 'System32\pnputil.exe') /enum-drivers 2>$null
    $packages = @()
    $current = $null
    foreach ($line in @($output) + '') {
        if ($line -match '^\s*$') {
            if ($null -ne $current -and $current['Original'] -eq 'ssap3aap.inf' -and $current['Provider'] -eq 'SSAP3') { $packages += [pscustomobject]$current }
            $current = $null
            continue
        }
        if ($line -match '^\s*([^:]+?)\s*:\s*(.*)$') {
            if ($null -eq $current) { $current = @{} }
            $key = $Matches[1]
            $value = $Matches[2].Trim()
            switch ($key) {
                'Published Name' { $current['Published'] = $value }
                'Original Name' { $current['Original'] = $value }
                'Provider Name' { $current['Provider'] = $value }
                'Driver Version' { $current['Version'] = $value }
                'Signer Name' { $current['Signer'] = $value }
            }
        }
    }
    return $packages
}

$script:InterfaceProbe = @'
using System;
using System.Runtime.InteropServices;
public static class Ssap3InterfaceProbe
{
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_List_SizeW(out uint size, ref Guid guid, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_ListW(ref Guid guid, string deviceId, char[] buffer, uint length, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr device, uint code, IntPtr input, uint inputSize, byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")]
    public static extern ulong GetTickCount64();

    public static string[] Interfaces(Guid guid)
    {
        uint size;
        if (CM_Get_Device_Interface_List_SizeW(out size, ref guid, null, 0) != 0 || size <= 1) return new string[0];
        var buffer = new char[size];
        if (CM_Get_Device_Interface_ListW(ref guid, null, buffer, size, 0) != 0) return new string[0];
        return new string(buffer).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
    }

    // IOCTL_SSAP3_GET_BRIDGE_STATUS (read-only). Returns the 40-byte status or
    // null; error receives the Win32 error.
    public static byte[] Status(string path, out int error)
    {
        error = 0;
        // GENERIC_READ only: the status query needs nothing more (least privilege).
        var handle = CreateFileW(path, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle == new IntPtr(-1)) { error = Marshal.GetLastWin32Error(); return null; }
        try
        {
            var output = new byte[40];
            uint returned;
            // IOCTL_SSAP3_GET_BRIDGE_STATUS, protocol v4 (FILE_READ_ACCESS).
            if (!DeviceIoControl(handle, 0x00226000, IntPtr.Zero, 0, output, (uint)output.Length, out returned, IntPtr.Zero)) { error = Marshal.GetLastWin32Error(); return null; }
            return returned >= 16 ? output : null;
        }
        finally { CloseHandle(handle); }
    }
}
'@

function Test-InstalledDriver {
    $area = 'Installed'
    $level = 'NotInstalled'
    $packages = @(Get-DriverStorePackages)
    if ($packages.Count -eq 0) {
        Add-Result $area 'Driver package' 'Info' 'not in the driver store (expected until a Microsoft-signed package is installed by SSAP3 Setup)'
        return $level
    }

    foreach ($package in $packages) {
        Add-Result $area 'Driver package' 'Pass' "$($package.Published): version $($package.Version), signer '$($package.Signer)'"
    }
    $level = 'Installed'

    $store = @(Get-ChildItem -LiteralPath (Join-Path $env:SystemRoot 'System32\DriverStore\FileRepository') -Directory -Filter 'ssap3aap.inf_amd64_*' -ErrorAction SilentlyContinue)
    foreach ($folder in $store) {
        $cat = Join-Path $folder.FullName 'ssap3aap.cat'
        $signature = Get-Ssap3CatalogSignature -CatalogPath $cat
        $production = $signature.Kind -in 'MicrosoftAttestation', 'MicrosoftWhql'
        $members = (Test-Ssap3CatalogMember -CatalogPath $cat -FilePath (Join-Path $folder.FullName 'ssap3aap.sys')) -and
            (Test-Ssap3CatalogMember -CatalogPath $cat -FilePath (Join-Path $folder.FullName 'ssap3aap.inf'))
        Add-Result $area 'Store copy signature' $(if ($production -and $members) { 'Pass' } else { 'Fail' }) "$($signature.Kind); catalog lists the stored .sys and .inf: $members"
    }

    $service = Get-CimInstance Win32_SystemDriver -Filter "Name='ssap3aap'" -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        Add-Result $area 'Service ssap3aap' 'Info' 'not registered yet (Windows registers it when AirPods are present to bind to)'
    }
    else {
        Add-Result $area 'Service ssap3aap' $(if ($service.State -eq 'Running') { 'Pass' } else { 'Info' }) "state $($service.State), start $($service.StartMode)"
    }

    $problems = @{ 10 = 'cannot start'; 28 = 'driver not installed'; 31 = 'driver failed to load'; 39 = 'driver missing or corrupt'; 48 = 'blocked by Windows'; 52 = 'Windows cannot verify the signature' }
    $devices = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.Service -eq 'ssap3aap' })
    if ($devices.Count -eq 0) {
        Add-Result $area 'Devices' 'Info' 'no present device uses the driver (are the AirPods paired and connected?)'
        return $level
    }

    $started = 0
    foreach ($device in $devices) {
        $code = [int]$device.Problem
        if ($device.Status -eq 'OK' -and $code -eq 0) {
            $started++
            Add-Result $area 'Device' 'Pass' "$($device.Name): started"
        }
        else {
            $meaning = if ($problems.ContainsKey($code)) { $problems[$code] } else { 'see Device Manager' }
            Add-Result $area 'Device' 'Fail' "$($device.Name): status $($device.Status), problem code $code ($meaning)"
        }
    }
    if ($started -eq 0) { return $level }
    $level = 'Started'

    if (-not ('Ssap3InterfaceProbe' -as [type])) { Add-Type -TypeDefinition $script:InterfaceProbe }
    $paths = @([Ssap3InterfaceProbe]::Interfaces([Guid]'1C3A8128-3668-4428-95A4-B1CAD51B7249'))
    if ($paths.Count -eq 0) {
        Add-Result $area 'Control interface' 'Fail' 'the device started but exposes no SSAP3 interface'
        return $level
    }

    $reachable = 0
    foreach ($path in $paths) {
        $win32Error = 0
        $status = [Ssap3InterfaceProbe]::Status($path, [ref]$win32Error)
        if ($null -eq $status) {
            Add-Result $area 'Control interface' 'Fail' "status query failed (Win32 error $win32Error)"
            continue
        }
        $protocol = [BitConverter]::ToUInt32($status, 4)
        $capabilities = [BitConverter]::ToUInt32($status, 8)
        $maximum = [BitConverter]::ToUInt32($status, 12)
        $bound = $status.Length -ge 40 -and [BitConverter]::ToUInt64($status, 32) -ne 0
        $aap = ($capabilities -band 4) -ne 0
        $ok = $protocol -eq 4
        if ($ok) { $reachable++ }
        Add-Result $area 'Control interface' $(if ($ok) { 'Pass' } else { 'Fail' }) "protocol v$protocol (app expects v4), AAP channel usable: $aap, inbound MTU $maximum, bound to a Bluetooth device: $bound"
    }
    if ($reachable -eq 0) { return $level }
    $level = 'Reachable'

    $record = Join-Path $env:ProgramData 'SSAP3\Runtime\head-pose.bin'
    $source = $null
    $settings = Join-Path $env:LOCALAPPDATA 'SSAP3\settings.json'
    if (Test-Path -LiteralPath $settings) {
        try { $source = (Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json).MotionSource } catch { $source = $null }
    }
    if (-not (Test-Path -LiteralPath $record)) {
        Add-Result $area 'Head tracking' 'Info' 'no head-pose record (is SSAP3 running?)'
        return $level
    }

    $stream = [IO.File]::Open($record, 'Open', 'Read', 'ReadWrite')
    try { $bytes = New-Object byte[] 96; [void]$stream.Read($bytes, 0, 96) } finally { $stream.Dispose() }
    $mode = [BitConverter]::ToInt32($bytes, 12)
    $tracking = [BitConverter]::ToInt32($bytes, 16)
    $now = [Ssap3InterfaceProbe]::GetTickCount64()
    $written = [BitConverter]::ToUInt64($bytes, 56)
    $age = if ($written -gt 0 -and $written -le $now) { [int64]($now - $written) } else { -1 }
    $modes = @('Off', 'Fixed', 'Tracked')
    $states = @('None', 'Live', 'Holding', 'Lost')
    $modeName = if ($mode -ge 0 -and $mode -lt 3) { $modes[$mode] } else { "invalid ($mode)" }
    $stateName = if ($tracking -ge 0 -and $tracking -lt 4) { $states[$tracking] } else { "invalid ($tracking)" }
    $live = $mode -eq 2 -and $tracking -eq 1 -and $age -ge 0 -and $age -lt 750
    $real = $source -eq 'AirPodsSensors'
    Add-Result $area 'Head tracking' $(if ($live -and $real) { 'Pass' } else { 'Info' }) "mode $modeName, tracking $stateName, record $age ms old, motion source setting: $(if ($source) { $source } else { 'default (AirPodsSensors)' })$(if ($live -and -not $real) { ' - live, but NOT from the AirPods sensors' })"
    if ($live -and ($real -or -not $source)) { $level = 'Tracking' }
    return $level
}

# --------------------------------------------------------------------- main
if ($Package) { Test-PackageFolder -Folder $Package }
Test-SystemSecurity
$reached = Test-InstalledDriver

$order = @('NotInstalled', 'Installed', 'Started', 'Reachable', 'Tracking')
if ($Require -ne 'None' -and $order.IndexOf($reached) -lt $order.IndexOf($Require)) {
    Add-Result 'Verdict' "Required level $Require" 'Fail' "the driver only reached '$reached'"
}

$symbols = @{ Pass = 'PASS'; Fail = 'FAIL'; Warn = 'WARN'; Info = 'INFO' }
foreach ($result in $script:Results) {
    Write-Output ('{0}  {1,-10} {2,-28} {3}' -f $symbols[$result.State], $result.Area, $result.Check, $result.Detail)
}
$failures = @($script:Results | Where-Object State -eq 'Fail').Count
Write-Output ''
Write-Output "Installed driver reached: $reached (levels: $($order -join ' > '))."
Write-Output $(if ($failures -eq 0) { 'Result: no failures.' } else { "Result: $failures failure(s)." })
if ($PassThru) { $script:Results }
exit $(if ($failures -eq 0) { 0 } else { 1 })
