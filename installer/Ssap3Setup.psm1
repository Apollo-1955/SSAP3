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
$script:SidTrustedInstaller = 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'
$script:TrustedSids = @($script:SidSystem, $script:SidAdministrators, $script:SidTrustedInstaller)

# Access bits that let a principal change, replace or re-permission an object.
# Composite names (Modify, FullControl, WriteKey) are NOT used: they include
# read bits, so every read-only ACE would look like write access.
# GENERIC_ALL (0x10000000) and GENERIC_WRITE (0x40000000) are included because
# they can appear unmapped in ACEs written by third-party installers.
#   File:     WriteData 0x2, AppendData 0x4, WriteEA 0x10, DeleteChild 0x40,
#             WriteAttributes 0x100, DELETE 0x10000, WRITE_DAC 0x40000, WRITE_OWNER 0x80000
#   Registry: SetValue 0x2, CreateSubKey 0x4, CreateLink 0x20, DELETE, WRITE_DAC, WRITE_OWNER
$script:FileWriteMask = [int64](0x2 -bor 0x4 -bor 0x10 -bor 0x40 -bor 0x100 -bor 0x10000 -bor 0x40000 -bor 0x80000 -bor 0x10000000 -bor 0x40000000)
$script:RegistryWriteMask = [int64](0x2 -bor 0x4 -bor 0x20 -bor 0x10000 -bor 0x40000 -bor 0x80000 -bor 0x10000000 -bor 0x40000000)

# Writes to the host, never to the output stream: functions that log and then
# return a value (Install-Ssap3Driver) would otherwise return an array.
function Write-Ssap3Log {
    param([Parameter(Mandatory = $true)][string]$Message)
    $line = '{0:yyyy-MM-dd HH:mm:ss}  {1}' -f (Get-Date), $Message
    Write-Host $line
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

# Get-Acl -LiteralPath fails on registry paths in Windows PowerShell 5.1
# ("Cannot find path HKEY_LOCAL_MACHINE\..."), so keys are read through the key object.
function Get-Ssap3Acl {
    param([Parameter(Mandatory = $true)][string]$Path)
    if ($Path -like 'HKLM:*') { return (Get-Item -LiteralPath $Path).GetAccessControl() }
    return Get-Acl -LiteralPath $Path
}

function Test-Ssap3TrustedOwner {
    param([Parameter(Mandatory = $true)][string]$Path)
    $owner = (Get-Ssap3Acl -Path $Path).GetOwner([Security.Principal.SecurityIdentifier]).Value
    return $owner -in $script:TrustedSids
}

function Get-Ssap3RuleSid($Rule) {
    return $Rule.IdentityReference.Value
}

# ACEs with SIDs, not account names: translating names back to SIDs throws for
# capability SIDs such as ALL APPLICATION PACKAGES, which Program Files carries.
function Get-Ssap3AccessRules($Acl) {
    return @($Acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
}

# SIDs other than SYSTEM, Administrators and TrustedInstaller that can modify the
# object itself. Inherit-only ACEs are skipped: they do not apply to this object
# (children are checked on their own).
function Get-Ssap3UntrustedWriters {
    param([Parameter(Mandatory = $true)][string]$Path)
    $acl = Get-Ssap3Acl -Path $Path
    $isRegistry = $Path -like 'HKLM:*'
    $writers = @()
    foreach ($rule in (Get-Ssap3AccessRules $acl)) {
        if ($rule.AccessControlType -ne 'Allow') { continue }
        if (($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -ne 0) { continue }
        $sid = Get-Ssap3RuleSid $rule
        if ($sid -in $script:TrustedSids) { continue }
        $rights = if ($isRegistry) { [int64]$rule.RegistryRights } else { [int64]$rule.FileSystemRights }
        $mask = if ($isRegistry) { $script:RegistryWriteMask } else { $script:FileWriteMask }
        if (($rights -band $mask) -ne 0) { $writers += $sid }
    }
    return @($writers | Select-Object -Unique)
}

# Refuses to trust any file, folder or HKLM key that a standard user could have
# planted or can modify (S-02, S-23, S-26).
function Assert-Ssap3AdminOnlyPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Ssap3TrustedOwner -Path $Path)) {
        throw "Refusing to use $Path because it is not owned by SYSTEM, Administrators or TrustedInstaller."
    }
    $writers = @(Get-Ssap3UntrustedWriters -Path $Path)
    if ($writers.Count -gt 0) {
        throw "Refusing to use $Path because $($writers -join ', ') can modify it."
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

# Microsoft's driver-publisher signature, as observed on real WHQL and
# attestation-signed catalogs (driver/SIGNING.md, "How a Microsoft signature
# is recognised"): signer "Microsoft Windows Hardware Compatibility Publisher",
# chain to one of these Microsoft roots, and the Windows Hardware Driver
# Verification EKU. Attestation signing adds the "attested" EKU.
$script:MicrosoftDriverRoots = @(
    '3B1EFD3A66EA28B16697394703A72CA340A05BD5', # Microsoft Root Certificate Authority 2010
    'CDD4EEAE6000AC7F40C3802C171E30148030C072', # Microsoft Root Certificate Authority
    'A43489159A520F0D93D032CCAF37E7FE20A8B419'  # Microsoft Root Authority
)
$script:DriverVerificationEku = '1.3.6.1.4.1.311.10.3.5'
$script:AttestedVerificationEku = '1.3.6.1.4.1.311.10.3.5.1'

# Classifies a driver catalog's signature without trusting names alone:
#   Unsigned             - no signature
#   NotTrusted           - signed, but Windows does not trust it (for example a
#                          self-signed or WDK test certificate)
#   NotMicrosoft         - trusted, but not by Microsoft's driver publisher
#                          (a third-party CA, or a test root someone trusted)
#   MicrosoftOther       - a Microsoft signature that is not a driver-publisher one
#   MicrosoftWhql        - Microsoft hardware signature (WHQL/HLK)
#   MicrosoftAttestation - Microsoft attestation signature
# Trust, time validity (with the timestamp) and revocation are Windows' own
# verdict (Get-AuthenticodeSignature); the chain is built here only to find
# its root, using the certificates embedded in the catalog.
function Get-Ssap3CatalogSignature {
    param([Parameter(Mandatory = $true)][string]$CatalogPath)
    $result = [ordered]@{ Kind = 'Unsigned'; Status = 'Missing'; Signer = $null; Root = $null; RootThumbprint = $null; Ekus = @(); Detail = $null }
    if (-not (Test-Path -LiteralPath $CatalogPath -PathType Leaf)) {
        $result.Detail = 'The catalog file is missing.'
        return [pscustomobject]$result
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $CatalogPath
    $result.Status = "$($signature.Status)"
    if ($signature.Status -eq 'NotSigned' -or $null -eq $signature.SignerCertificate) {
        $result.Detail = 'The catalog has no signature.'
        return [pscustomobject]$result
    }

    $signer = $signature.SignerCertificate
    $result.Signer = $signer.Subject
    $result.Ekus = @($signer.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' } |
        ForEach-Object { ([System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$_).EnhancedKeyUsages } |
        ForEach-Object Value)

    Add-Type -AssemblyName System.Security
    $chain = New-Object System.Security.Cryptography.X509Certificates.X509Chain
    try {
        $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
        $chain.ChainPolicy.VerificationFlags = [System.Security.Cryptography.X509Certificates.X509VerificationFlags]::IgnoreNotTimeValid
        try {
            $cms = New-Object System.Security.Cryptography.Pkcs.SignedCms
            $cms.Decode([IO.File]::ReadAllBytes($CatalogPath))
            [void]$chain.ChainPolicy.ExtraStore.AddRange($cms.Certificates)
        }
        catch {
            $result.Detail = "The catalog's embedded certificates could not be read: $($_.Exception.Message)"
        }

        [void]$chain.Build($signer)
        $root = $chain.ChainElements[$chain.ChainElements.Count - 1].Certificate
        $result.Root = $root.Subject
        $result.RootThumbprint = $root.Thumbprint
    }
    finally {
        $chain.Dispose()
    }

    if ($signature.Status -ne 'Valid') {
        $result.Kind = 'NotTrusted'
        $result.Detail = "Windows does not trust this signature ($($signature.Status)): $($signature.StatusMessage)"
        return [pscustomobject]$result
    }

    $microsoftRoot = $result.RootThumbprint -in $script:MicrosoftDriverRoots -and $root.Subject -eq $root.Issuer
    $publisher = $signer.Subject -match '^CN=Microsoft Windows Hardware Compatibility Publisher(,|$)'
    if (-not $microsoftRoot) {
        $result.Kind = 'NotMicrosoft'
        $result.Detail = 'Trusted on this PC, but the chain does not end at a Microsoft root.'
    }
    elseif (-not $publisher -or $script:DriverVerificationEku -notin $result.Ekus) {
        $result.Kind = 'MicrosoftOther'
        $result.Detail = 'Signed by Microsoft, but not as a driver publisher (no Windows Hardware Driver Verification EKU).'
    }
    elseif ($script:AttestedVerificationEku -in $result.Ekus) {
        $result.Kind = 'MicrosoftAttestation'
        $result.Detail = 'Microsoft attestation signature.'
    }
    else {
        $result.Kind = 'MicrosoftWhql'
        $result.Detail = 'Microsoft hardware (WHQL) signature.'
    }

    return [pscustomobject]$result
}

# Authenticode image hash of a PE file: the whole file except the checksum
# field, the certificate-table directory entry and the certificate table.
# Driver catalogs list binaries by this hash (checked against 67 real
# Microsoft-signed driver files; a plain file hash matched none).
function Get-Ssap3PeImageHash {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [ValidateSet('SHA256', 'SHA1')][string]$Algorithm = 'SHA256'
    )
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 0x200 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) { return $null }
    $pe = [BitConverter]::ToInt32($bytes, 0x3C)
    if ($pe -lt 0x40 -or ($pe + 24 + 112 + 40) -gt $bytes.Length -or [BitConverter]::ToUInt32($bytes, $pe) -ne 0x4550) { return $null }
    $optional = $pe + 24
    $magic = [BitConverter]::ToUInt16($bytes, $optional)
    if ($magic -eq 0x20B) { $directories = $optional + 112 } elseif ($magic -eq 0x10B) { $directories = $optional + 96 } else { return $null }
    $checksum = $optional + 64
    $certificateEntry = $directories + 32
    $certificateOffset = [BitConverter]::ToUInt32($bytes, $certificateEntry)
    $certificateSize = [BitConverter]::ToUInt32($bytes, $certificateEntry + 4)
    $end = $bytes.Length
    if ($certificateSize -gt 0 -and $certificateOffset -gt $certificateEntry -and ($certificateOffset + $certificateSize) -le $bytes.Length) {
        $end = [int]$certificateOffset
    }

    $hash = [System.Security.Cryptography.HashAlgorithm]::Create($Algorithm)
    try {
        [void]$hash.TransformBlock($bytes, 0, $checksum, $null, 0)
        [void]$hash.TransformBlock($bytes, $checksum + 4, $certificateEntry - ($checksum + 4), $null, 0)
        [void]$hash.TransformFinalBlock($bytes, $certificateEntry + 8, $end - ($certificateEntry + 8))
        return $hash.Hash
    }
    finally {
        $hash.Dispose()
    }
}

# True when the catalog lists the file: by its Authenticode image hash for PE
# files, by its plain hash otherwise (INF). SHA-256 or SHA-1 (older catalogs).
function Test-Ssap3CatalogMember {
    param(
        [Parameter(Mandatory = $true)][string]$CatalogPath,
        [Parameter(Mandatory = $true)][string]$FilePath
    )
    if (-not (Test-Path -LiteralPath $CatalogPath -PathType Leaf) -or -not (Test-Path -LiteralPath $FilePath -PathType Leaf)) { return $false }
    $catalog = [BitConverter]::ToString([IO.File]::ReadAllBytes($CatalogPath)).Replace('-', '')
    foreach ($algorithm in 'SHA256', 'SHA1') {
        $digest = Get-Ssap3PeImageHash -Path $FilePath -Algorithm $algorithm
        if ($null -eq $digest) {
            $engine = [System.Security.Cryptography.HashAlgorithm]::Create($algorithm)
            try { $digest = $engine.ComputeHash([IO.File]::ReadAllBytes($FilePath)) } finally { $engine.Dispose() }
        }

        $needle = [BitConverter]::ToString($digest).Replace('-', '')
        $index = $catalog.IndexOf($needle, [StringComparison]::Ordinal)
        while ($index -ge 0 -and ($index % 2) -ne 0) { $index = $catalog.IndexOf($needle, $index + 1, [StringComparison]::Ordinal) }
        if ($index -ge 0) { return $true }
    }

    return $false
}

# The INF must be SSAP3's own: provider SSAP3, catalog ssap3aap.cat, service
# ssap3aap, and models that bind only to the Apple AAP and UARP Bluetooth
# service PDOs. Together with the catalog check this stops a different
# (even Microsoft-signed) driver package renamed to ssap3aap.* from being
# installed.
$script:Ssap3HardwareIdPrefixes = @(
    'BTHENUM\{74ec2172-0bad-4d01-8f77-997b2be0722a}', # Apple AAP service
    'BTHENUM\{4715650b-5e9d-4ac2-b898-a4fc0aa5df78}'  # Apple UARP service (fallback parent)
)
function Test-Ssap3DriverInfIdentity {
    param([Parameter(Mandatory = $true)][string]$InfPath)
    if (-not (Test-Path -LiteralPath $InfPath -PathType Leaf)) { return $false }
    $lines = @(Get-Content -LiteralPath $InfPath | ForEach-Object { ($_ -replace ';.*$', '').Trim() } | Where-Object { $_ })
    $provider = [bool]($lines | Where-Object { $_ -match '^ProviderName\s*=\s*"?SSAP3"?$' })
    $catalog = [bool]($lines | Where-Object { $_ -match '^CatalogFile\s*=\s*ssap3aap\.cat$' })
    $services = @($lines | Where-Object { $_ -match '^AddService\s*=' } | ForEach-Object { ($_ -replace '^AddService\s*=\s*', '' -split ',')[0].Trim() })
    $ids = @($lines | Where-Object { $_ -match '^%\w+%\s*=\s*SSAP3_Profile_Install\s*,' } | ForEach-Object { ($_ -split ',', 2)[1].Trim() })
    $foreign = @($ids | Where-Object { $id = $_; -not ($script:Ssap3HardwareIdPrefixes | Where-Object { $id.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) }) })
    $otherModels = @($lines | Where-Object { $_ -match '^%\w+%\s*=\s*\w+\s*,\s*\S' -and $_ -notmatch 'SSAP3_Profile_Install' -and $_ -notmatch '^%ProviderName%\s*=\s*Standard\s*,' })
    return $provider -and $catalog -and ($services.Count -ge 1) -and -not ($services | Where-Object { $_ -ne 'ssap3aap' }) -and
        $ids.Count -ge 1 -and $foreign.Count -eq 0 -and $otherModels.Count -eq 0
}

# Production rule: the catalog must carry Microsoft's driver-publisher signature
# (attestation or WHQL, chain pinned to a Microsoft root) and list the driver
# binary and INF being installed. Anything else is never installed.
function Test-Ssap3DriverProductionSigned {
    param(
        [Parameter(Mandatory = $true)][string]$CatalogPath,
        [string[]]$MemberPaths = @()
    )
    $signature = Get-Ssap3CatalogSignature -CatalogPath $CatalogPath
    if ($signature.Kind -notin 'MicrosoftAttestation', 'MicrosoftWhql') { return $false }
    foreach ($member in $MemberPaths) {
        if (-not (Test-Ssap3CatalogMember -CatalogPath $CatalogPath -FilePath $member)) { return $false }
    }
    return $true
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

# pnputil exits with 3010 when Windows needs a restart to finish adding or
# removing a driver (for example while a device still uses it).
$script:RestartRequired = $false
function Register-Ssap3PnpResult {
    if ($LASTEXITCODE -eq 3010) {
        $script:RestartRequired = $true
        Write-Ssap3Log 'Windows reports that a restart is needed to finish this driver change.'
    }
}

function Test-Ssap3RestartRequired { return $script:RestartRequired }

function Install-Ssap3Driver {
    param([Parameter(Mandatory = $true)][string]$PackageRoot)
    $inf = Join-Path $PackageRoot 'driver\ssap3aap.inf'
    $cat = Join-Path $PackageRoot 'driver\ssap3aap.cat'
    $sys = Join-Path $PackageRoot 'driver\ssap3aap.sys'
    if (-not (Test-Ssap3DriverProductionSigned -CatalogPath $cat -MemberPaths @($sys, $inf))) {
        $signature = Get-Ssap3CatalogSignature -CatalogPath $cat
        Write-Ssap3Log "AAP driver skipped: the package is not Microsoft-signed for this driver (catalog: $($signature.Kind)). Listening modes and head tracking stay unavailable; nothing was changed."
        return $false
    }
    if (-not (Test-Ssap3DriverInfIdentity -InfPath $inf)) {
        Write-Ssap3Log 'AAP driver skipped: the signed INF is not the SSAP3 AAP driver (provider, service or hardware IDs differ). Nothing was changed.'
        return $false
    }
    $before = @(Get-Ssap3DriverPackages | ForEach-Object Published)
    $step = Add-Ssap3Step -Type 'DriverPackage' -Data @{ originalName = 'ssap3aap.inf'; provider = 'SSAP3'; preexisting = $before }
    Invoke-Ssap3Native -FilePath (Join-Path $env:SystemRoot 'System32\pnputil.exe') -Arguments @('/add-driver', $inf, '/install') -AllowedExitCodes @(0, 259, 3010) | Out-Null
    Register-Ssap3PnpResult
    $step.data | Add-Member -NotePropertyName published -NotePropertyValue @(Get-Ssap3DriverPackages | ForEach-Object Published | Where-Object { $_ -notin $before }) -Force
    Complete-Ssap3Step $step
    return $true
}

function Remove-Ssap3DriverPackages {
    foreach ($package in Get-Ssap3DriverPackages) {
        Invoke-Ssap3Native -FilePath (Join-Path $env:SystemRoot 'System32\pnputil.exe') -Arguments @('/delete-driver', $package.Published, '/uninstall') -AllowedExitCodes @(0, 3010) | Out-Null
        Register-Ssap3PnpResult
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

# ---------------------------------------------------------------- Equalizer APO hardening
#
# S-23: Equalizer APO 1.4.2's own installer grants BUILTIN\Users FullAccess on its
# config folder (Setup.nsi: AccessControl::GrantOnFile "$INSTDIR\config"
# "(S-1-5-32-545)" "FullAccess"). config.txt accepts absolute plug-in paths
# ("VSTPlugin: Library C:\any\file.dll", VSTPluginFilterFactory.cpp), and
# audiodg.exe loads them as LOCAL SERVICE, so any standard user or any process
# running as one could run code in the audio engine. SSAP3 removes write access
# for everyone except SYSTEM, Administrators and TrustedInstaller, keeps read
# access, and records every security descriptor first so uninstall restores
# them exactly.
#
# S-24: the optional Equalizer APO update checker runs at every logon, contacts
# equalizerapo.sourceforge.io and opens a server-supplied URL. SSAP3 removes the
# scheduled task if it exists (recorded; restored on uninstall).
#
# S-26: every DLL audiodg.exe loads from the Equalizer APO folder, and the
# HKLM\SOFTWARE\EqualizerAPO key (ConfigPath, Child APOs), must be admin-only.
# Setup refuses to continue otherwise.

$script:UpdateCheckerTask = 'EqualizerAPOUpdateChecker'

function Get-Ssap3EqualizerInstall {
    $installPath = [string](Get-Ssap3RegistryValue -Path $script:EqualizerRegistry -Name 'InstallPath').value
    if (-not $installPath) { return $null }
    $installPath = [IO.Path]::GetFullPath($installPath).TrimEnd('\')
    if (-not (Test-Path -LiteralPath (Join-Path $installPath 'EqualizerAPO.dll') -PathType Leaf)) { return $null }
    $configPath = [string](Get-Ssap3RegistryValue -Path $script:EqualizerRegistry -Name 'ConfigPath').value
    if (-not $configPath) { $configPath = Join-Path $installPath 'config' }
    return [pscustomobject]@{
        InstallPath = $installPath
        ConfigPath = [IO.Path]::GetFullPath($configPath).TrimEnd('\')
    }
}

# Removes write access for untrusted SIDs from one object while keeping their
# read access, and makes Administrators the owner if an untrusted SID owns it.
function Remove-Ssap3UntrustedWrite {
    param([Parameter(Mandatory = $true)][string]$Path)
    $acl = Get-Acl -LiteralPath $Path
    $changed = $false
    foreach ($rule in (Get-Ssap3AccessRules $acl)) {
        if ($rule.IsInherited -or $rule.AccessControlType -ne 'Allow') { continue }
        $sid = Get-Ssap3RuleSid $rule
        if ($sid -in $script:TrustedSids) { continue }
        if (([int64]$rule.FileSystemRights -band $script:FileWriteMask) -eq 0) { continue }
        [void]$acl.RemoveAccessRuleSpecific($rule)
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
            (Get-Ssap3Sid $sid), [Security.AccessControl.FileSystemRights]::ReadAndExecute,
            $rule.InheritanceFlags, $rule.PropagationFlags, [Security.AccessControl.AccessControlType]::Allow)))
        $changed = $true
    }
    if (-not (Test-Ssap3TrustedOwner -Path $Path)) {
        $acl.SetOwner((Get-Ssap3Sid $script:SidAdministrators))
        $changed = $true
    }
    if ($changed) { Set-Acl -LiteralPath $Path -AclObject $acl }
    return $changed
}

function Protect-Ssap3EqualizerConfig {
    param([Parameter(Mandatory = $true)][string]$ConfigPath)
    $items = @(Get-Item -LiteralPath $ConfigPath -Force) + @(Get-ChildItem -LiteralPath $ConfigPath -Recurse -Force)
    foreach ($item in $items) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to change permissions under $ConfigPath because $($item.FullName) is a link or junction."
        }
    }
    $records = @($items | ForEach-Object { [pscustomobject]@{ path = $_.FullName; sddl = (Get-Acl -LiteralPath $_.FullName).Sddl } })
    $step = Add-Ssap3Step -Type 'FileSecurity' -Data @{ items = $records; reason = 'S-23: Equalizer APO config folder writable by standard users' }
    $fixed = 0
    # Parent first: its new inheritable ACEs propagate before the children are checked.
    foreach ($item in $items) { if (Remove-Ssap3UntrustedWrite -Path $item.FullName) { $fixed++ } }
    Complete-Ssap3Step $step
    foreach ($item in $items) { Assert-Ssap3AdminOnlyPath -Path $item.FullName }
    Write-Ssap3Log "Equalizer APO config is now admin-only for writing ($fixed object(s) changed, $($items.Count) checked). Run the Equalizer APO Configuration Editor as administrator to edit it."
}

function Assert-Ssap3EqualizerLoadPathsTrusted {
    param([Parameter(Mandatory = $true)]$Install)
    Assert-Ssap3AdminOnlyPath -Path $Install.InstallPath
    foreach ($dll in Get-ChildItem -LiteralPath $Install.InstallPath -Filter '*.dll' -File -Force) {
        Assert-Ssap3AdminOnlyPath -Path $dll.FullName
    }
    $plugins = Join-Path $Install.InstallPath 'VSTPlugins'
    if (Test-Path -LiteralPath $plugins) { Assert-Ssap3AdminOnlyPath -Path $plugins }
    foreach ($key in @($script:EqualizerRegistry, "$($script:EqualizerRegistry)\Child APOs")) {
        if (Test-Path -LiteralPath $key) { Assert-Ssap3AdminOnlyPath -Path $key }
    }
    $installRoot = $Install.InstallPath + '\'
    if (-not ($Install.ConfigPath + '\').StartsWith($installRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Equalizer APO's ConfigPath ($($Install.ConfigPath)) is outside its install folder. SSAP3 only supports the default layout; reinstall Equalizer APO with its defaults."
    }
}

function Disable-Ssap3EqualizerUpdateChecker {
    $tasks = @(Get-ScheduledTask -TaskName $script:UpdateCheckerTask -ErrorAction SilentlyContinue)
    if ($tasks.Count -eq 0) {
        Write-Ssap3Log 'Equalizer APO update checker: off (no scheduled task).'
        return
    }
    foreach ($task in $tasks) {
        $xml = Export-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath
        $step = Add-Ssap3Step -Type 'ScheduledTask' -Data @{ name = $task.TaskName; taskPath = $task.TaskPath; xml = [string]$xml; reason = 'S-24: update checker opens a server-supplied URL' }
        Unregister-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath -Confirm:$false
        Complete-Ssap3Step $step
        Write-Ssap3Log "Equalizer APO update checker task removed ($($task.TaskPath)$($task.TaskName)); it is restored on uninstall."
    }
}

function Test-Ssap3EndpointUsesEqualizer {
    param([Parameter(Mandatory = $true)]$Endpoint)
    foreach ($name in $script:FxNames[0..4]) {
        if ([string](Get-Ssap3RegistryValue -Path $Endpoint.FxPath -Name $name).value -in @($script:EqualizerPre, $script:EqualizerPost)) { return $true }
    }
    return $false
}

function Install-Ssap3SpatialEngine {
    param([Parameter(Mandatory = $true)][string]$PackageRoot)

    $runtimeSource = Join-Path $PackageRoot 'spatial\EqualizerAPO'
    $pluginSource = Join-Path $PackageRoot 'spatial\SSAP3Spatializer.dll'
    if (-not (Test-Path -LiteralPath $pluginSource -PathType Leaf)) { throw "Spatial engine package file is missing: $pluginSource" }
    Assert-Ssap3AdminOnlyPath -Path $pluginSource

    $existing = Get-Ssap3EqualizerInstall
    $ownsEqualizer = $null -eq $existing
    if ($ownsEqualizer -and -not (Test-Path -LiteralPath (Join-Path $runtimeSource 'EqualizerAPO.dll') -PathType Leaf)) {
        throw 'Equalizer APO is not installed and this package does not bundle it. Install Equalizer APO 1.4.2 x64 (official installer, SHA-256 in THIRD_PARTY.md), then run Setup again.'
    }
    $equalizerRoot = if ($ownsEqualizer) { $script:EqualizerRoot } else { $existing.InstallPath }
    $configRoot = if ($ownsEqualizer) { Join-Path $script:EqualizerRoot 'config' } else { $existing.ConfigPath }
    if (-not $ownsEqualizer) {
        Assert-Ssap3EqualizerLoadPathsTrusted -Install $existing
        Write-Ssap3Log "Using the Equalizer APO installed at $equalizerRoot (not owned by SSAP3; it is left installed on uninstall)."
    }

    $endpoints = @(Get-Ssap3AirPodsStereoEndpoints)
    if ($endpoints.Count -eq 0) {
        throw 'No AirPods A2DP stereo endpoint exists yet. Connect the AirPods once, then run Setup again to add the spatial engine.'
    }

    $audio = Get-Service -Name Audiosrv
    $step = Add-Ssap3Step -Type 'AudioServiceRestart' -Data @{ wasRunning = ($audio.Status -eq 'Running') }
    Stop-Service -Name Audiosrv -Force
    try {
        # Explicit, consented security setting change (recorded with its prior value).
        # An existing Equalizer APO install has already set it; it is then left as is.
        if ([string](Get-Ssap3RegistryValue -Path $script:AudioPolicyKey -Name 'DisableProtectedAudioDG').value -ne '1') {
            Set-Ssap3RegistryValue -Path $script:AudioPolicyKey -Name 'DisableProtectedAudioDG' -Value 1 -Kind DWord -Reason 'Equalizer APO host for the opt-in spatial engine'
        }

        if (-not $ownsEqualizer) {
            Protect-Ssap3EqualizerConfig -ConfigPath $configRoot
            Disable-Ssap3EqualizerUpdateChecker
        }

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

        $pluginFolder = Join-Path $equalizerRoot 'VSTPlugins'
        if (-not (Test-Path -LiteralPath $pluginFolder)) { New-Item -ItemType Directory -Path $pluginFolder | Out-Null }
        Assert-Ssap3AdminOnlyPath -Path $pluginFolder
        $pluginTarget = Join-Path $pluginFolder 'SSAP3Spatializer.dll'
        $plugin = Add-Ssap3Step -Type 'OwnedFile' -Data @{ path = $pluginTarget }
        Copy-Item -LiteralPath $pluginSource -Destination $pluginTarget -Force
        Complete-Ssap3Step $plugin
        Assert-Ssap3AdminOnlyPath -Path $pluginTarget
        $pluginHash = (Get-FileHash -LiteralPath $pluginTarget -Algorithm SHA256).Hash
        if ($pluginHash -ne (Get-FileHash -LiteralPath $pluginSource -Algorithm SHA256).Hash) { throw 'The installed spatial engine does not match the package copy.' }
        Write-Ssap3Log "Spatial engine installed: $pluginTarget (SHA-256 $pluginHash)."

        # Scope the plug-in to the AirPods stereo endpoints by GUID. A name pattern
        # ("AirPods") would also match any other AirPods ever paired with this PC.
        $deviceFilter = ($endpoints | ForEach-Object { $_.Guid }) -join '; '
        $includeFile = Join-Path $configRoot 'ssap3.txt'
        $include = Add-Ssap3Step -Type 'OwnedFile' -Data @{ path = $includeFile }
        @('# SSAP3 Spatial Engine (managed by SSAP3 Setup; edits are overwritten)', "Device: $deviceFilter", 'Stage: post-mix', 'VSTPlugin: Library "SSAP3Spatializer.dll"') |
            Set-Content -LiteralPath $includeFile -Encoding UTF8
        Complete-Ssap3Step $include
        Assert-Ssap3AdminOnlyPath -Path $includeFile

        $mainConfig = Join-Path $configRoot 'config.txt'
        $prior = if (Test-Path -LiteralPath $mainConfig) { Get-Content -LiteralPath $mainConfig -Raw } else { $null }
        $configStep = Add-Ssap3Step -Type 'ConfigInclude' -Data @{ path = $mainConfig; existed = ($null -ne $prior) }
        if ($null -eq $prior -or $prior -notmatch '(?im)^\s*Include:\s*ssap3\.txt\s*$') {
            Add-Content -LiteralPath $mainConfig -Value "`r`n# Managed by SSAP3`r`nInclude: ssap3.txt" -Encoding UTF8
        }
        Complete-Ssap3Step $configStep

        foreach ($endpoint in $endpoints) {
            if (Test-Ssap3EndpointUsesEqualizer -Endpoint $endpoint) {
                if ($ownsEqualizer) {
                    throw "Endpoint $($endpoint.Guid) already references Equalizer APO but no Equalizer APO runtime is installed. Repair it first."
                }
                # Attached with Equalizer APO's Device Selector, which already saved the
                # endpoint's original effects under Child APOs. Rewriting them here would
                # overwrite that record with Equalizer APO's own CLSIDs, so leave it.
                Write-Ssap3Log "Endpoint $($endpoint.Guid) already uses Equalizer APO (Device Selector); its effects were not changed."
                continue
            }
            Grant-Ssap3EndpointWrite -KeyPath $endpoint.KeyPath -FxPath $endpoint.FxPath
            $priorValues = @{}
            foreach ($name in $script:FxNames) { $priorValues[$name] = Get-Ssap3RegistryValue -Path $endpoint.FxPath -Name $name }
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
        'FileSecurity' {
            # Parent first, so that each child's own recorded descriptor is applied last.
            foreach ($item in @($d.items)) {
                if ($item.sddl -and (Test-Path -LiteralPath $item.path)) {
                    $acl = Get-Acl -LiteralPath $item.path
                    $acl.SetSecurityDescriptorSddlForm($item.sddl, [Security.AccessControl.AccessControlSections]'Owner, Group, Access')
                    Set-Acl -LiteralPath $item.path -AclObject $acl
                }
            }
        }
        'ScheduledTask' {
            if (-not (Get-ScheduledTask -TaskName $d.name -TaskPath $d.taskPath -ErrorAction SilentlyContinue)) {
                Register-ScheduledTask -TaskName $d.name -TaskPath $d.taskPath -Xml $d.xml | Out-Null
            }
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
                    Register-Ssap3PnpResult
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
