#Requires -Version 5.1
<#
.SYNOPSIS
    Before/after system-integrity check for SSAP3 (installation lifecycle).

.EXAMPLE
    # 1. Before installing:
    .\Test-Ssap3SystemState.ps1 -Snapshot before.json
    # 2. After uninstalling:
    .\Test-Ssap3SystemState.ps1 -Snapshot after.json
    .\Test-Ssap3SystemState.ps1 -Compare before.json after.json
    # Or just check that no SSAP3-owned object remains:
    .\Test-Ssap3SystemState.ps1 -Verify

.DESCRIPTION
    Read-only. Runs as a standard user; a few checks (boot configuration) need an
    elevated shell and report "requires elevation" otherwise. Snapshots contain
    no Bluetooth addresses or user names.

    Differences are classified as:
      SSAP3-RESIDUE  an SSAP3-owned object that should not exist after uninstall (fail)
      SECURITY       a platform security setting differs (fail; investigate)
      OTHER          a difference not attributable to SSAP3 (informational only;
                     SSAP3 does not claim the system is identical when other
                     software or the user changed things in between)
#>
[CmdletBinding(DefaultParameterSetName = 'Verify')]
param(
    [Parameter(ParameterSetName = 'Snapshot', Mandatory = $true)][string]$Snapshot,
    [Parameter(ParameterSetName = 'Compare', Mandatory = $true, Position = 0)][string]$Compare,
    [Parameter(ParameterSetName = 'Compare', Mandatory = $true, Position = 1)][string]$With,
    [Parameter(ParameterSetName = 'Verify')][switch]$Verify
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

function Get-OptionalValue([string]$Path, [string]$Name) {
    $item = Get-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $item) { return $null }
    return [string]$item.$Name
}

function Get-Ssap3SystemState {
    $state = [ordered]@{ capturedAt = (Get-Date).ToString('o'); ssap3 = [ordered]@{}; security = [ordered]@{}; audio = [ordered]@{} }

    # SSAP3-owned objects (must all be absent after uninstall).
    $state.ssap3.programFiles = Test-Path -LiteralPath (Join-Path $env:ProgramFiles 'SSAP3')
    $state.ssap3.programData = Test-Path -LiteralPath (Join-Path $env:ProgramData 'SSAP3')
    $state.ssap3.registry = Test-Path -LiteralPath 'HKLM:\SOFTWARE\SSAP3'
    $state.ssap3.runValue = $null -ne (Get-OptionalValue 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' 'SSAP3')
    $state.ssap3.services = @(Get-Service -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'ssap3*' } | ForEach-Object Name)
    $state.ssap3.scheduledTasks = @(Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like '*SSAP3*' } | ForEach-Object TaskName)
    $state.ssap3.firewallRules = @(Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like '*SSAP3*' } | ForEach-Object DisplayName)
    $state.ssap3.environment = @([Environment]::GetEnvironmentVariables('Machine').Keys | Where-Object { "$_" -like '*SSAP3*' })
    $drivers = @()
    $current = @{}
    foreach ($line in (& (Join-Path $env:SystemRoot 'System32\pnputil.exe') /enum-drivers 2>$null)) {
        if ($line -match '^\s*Published Name\s*:\s*(\S+)') { $current = @{ published = $Matches[1] } }
        elseif ($line -match '^\s*Original Name\s*:\s*(\S+)') { $current.original = $Matches[1] }
        elseif ($line -match '^\s*Provider Name\s*:\s*(.+)$') {
            if ($current.ContainsKey('original') -and $current.original -eq 'ssap3aap.inf') { $drivers += $current.published }
        }
    }
    $state.ssap3.driverPackages = $drivers
    $state.ssap3.spatializerPlugin = Test-Path -LiteralPath (Join-Path $env:ProgramFiles 'EqualizerAPO\VSTPlugins\SSAP3Spatializer.dll')

    # Platform security settings SSAP3 must never change.
    $secureBoot = Get-OptionalValue 'HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot\State' 'UEFISecureBootEnabled'
    $state.security.secureBoot = if ($null -eq $secureBoot) { 'unknown' } else { $secureBoot }
    $hvci = Get-OptionalValue 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity' 'Enabled'
    $state.security.memoryIntegrity = if ($null -eq $hvci) { 'not configured' } else { $hvci }
    try {
        $defender = Get-MpComputerStatus -ErrorAction Stop
        $state.security.defenderRealTime = [string]$defender.RealTimeProtectionEnabled
    } catch { $state.security.defenderRealTime = 'unknown' }
    $bcd = & (Join-Path $env:SystemRoot 'System32\bcdedit.exe') /enum '{current}' 2>$null
    $state.security.testSigning = if ($LASTEXITCODE -ne 0) { 'requires elevation' } elseif ($bcd -match '(?im)^\s*testsigning\s+yes') { 'on' } else { 'off' }
    $state.security.disableProtectedAudioDG = Get-OptionalValue 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Audio' 'DisableProtectedAudioDG'
    $state.security.rootCertificatesNamedSsap3OrSoundStage = @(Get-ChildItem Cert:\LocalMachine\Root -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -match 'SSAP3|SoundStage' } | ForEach-Object Thumbprint)

    # Audio endpoint effects (hash only: no device names).
    $render = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render'
    foreach ($endpoint in Get-ChildItem -LiteralPath $render -ErrorAction SilentlyContinue) {
        $fx = Get-ItemProperty -LiteralPath (Join-Path $endpoint.PSPath 'FxProperties') -ErrorAction SilentlyContinue
        $text = if ($null -eq $fx) { '' } else { ($fx.PSObject.Properties | Where-Object { $_.Name -like '{*' } | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ';' }
        $hash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($text))).Replace('-', '').Substring(0, 16)
        $state.audio[$endpoint.PSChildName] = $hash
    }
    return $state
}

function Get-Ssap3Residue($state) {
    $residue = @()
    foreach ($name in @('programFiles', 'programData', 'registry', 'runValue', 'spatializerPlugin')) {
        if ($state.ssap3.$name -eq $true) { $residue += "SSAP3-RESIDUE  $name still present" }
    }
    foreach ($name in @('services', 'scheduledTasks', 'firewallRules', 'environment', 'driverPackages')) {
        if (@($state.ssap3.$name).Count -gt 0) { $residue += "SSAP3-RESIDUE  ${name}: $(@($state.ssap3.$name) -join ', ')" }
    }
    return $residue
}

switch ($PSCmdlet.ParameterSetName) {
    'Snapshot' {
        Get-Ssap3SystemState | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Snapshot -Encoding UTF8
        Write-Output "Snapshot written to $Snapshot"
    }
    'Verify' {
        $state = Get-Ssap3SystemState
        $residue = @(Get-Ssap3Residue $state)
        $residue | ForEach-Object { Write-Output $_ }
        Write-Output ('Security: Secure Boot={0}; Memory Integrity={1}; Defender real-time={2}; Test Mode={3}; DisableProtectedAudioDG={4}' -f
            $state.security.secureBoot, $state.security.memoryIntegrity, $state.security.defenderRealTime, $state.security.testSigning,
            $(if ($null -eq $state.security.disableProtectedAudioDG) { 'absent' } else { $state.security.disableProtectedAudioDG }))
        if ($residue.Count -eq 0) { Write-Output 'PASS: no SSAP3-owned objects found.' ; exit 0 } else { Write-Output 'FAIL: SSAP3-owned objects remain.'; exit 1 }
    }
    'Compare' {
        $before = Get-Content -LiteralPath $Compare -Raw | ConvertFrom-Json
        $after = Get-Content -LiteralPath $With -Raw | ConvertFrom-Json
        $findings = @(Get-Ssap3Residue $after)
        foreach ($property in $before.security.PSObject.Properties) {
            $afterValue = $after.security.PSObject.Properties[$property.Name].Value
            if ((ConvertTo-Json $property.Value -Compress) -ne (ConvertTo-Json $afterValue -Compress)) {
                $findings += "SECURITY       $($property.Name): '$($property.Value)' -> '$afterValue'"
            }
        }
        foreach ($property in $before.audio.PSObject.Properties) {
            $afterValue = $after.audio.PSObject.Properties[$property.Name]
            if ($null -eq $afterValue) { $findings += "OTHER          endpoint $($property.Name) disappeared (device removed?)" }
            elseif ($afterValue.Value -ne $property.Value) { $findings += "SSAP3-RESIDUE  endpoint $($property.Name) effects differ from the pre-install snapshot" }
        }
        foreach ($property in $after.audio.PSObject.Properties) {
            if ($null -eq $before.audio.PSObject.Properties[$property.Name]) { $findings += "OTHER          new endpoint $($property.Name) (new device)" }
        }
        $findings | ForEach-Object { Write-Output $_ }
        $failures = @($findings | Where-Object { $_ -notlike 'OTHER*' })
        if ($failures.Count -eq 0) { Write-Output 'PASS: no unintended SSAP3 changes remain.'; exit 0 } else { exit 1 }
    }
}
