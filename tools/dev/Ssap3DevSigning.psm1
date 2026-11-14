#Requires -Version 5.1
<#
    SSAP3 temporary driver signing. DEVELOPMENT PCs ONLY.

    A locally generated, self-signed code-signing certificate signs the SSAP3
    AAP driver package so the driver can be tested under Windows test signing
    on a dedicated development PC. This is NOT a Microsoft signature:
      * a normal Windows PC with Secure Boot on never loads the driver;
      * SSAP3 Setup refuses the package (installer\Ssap3Setup.psm1,
        Test-Ssap3DriverProductionSigned);
      * the production path is unchanged (driver\SIGNING.md).

    Safety rules:
      * Never changes Secure Boot, firmware settings, Memory Integrity,
        Defender or Code Integrity policy, and never works around signature
        enforcement. Test signing is switched only through bcdedit, only while
        Secure Boot is already off, only on an elevated, explicit request on a
        PC the user declares a development machine, and only SSAP3's own change
        is ever reverted.
      * Acts only on certificates it created (fixed subject, the SSAP3 marker
        extension, code signing only, not a CA) and on driver packages that
        are SSAP3's (ssap3aap.inf, provider SSAP3) and signed by such a
        certificate. Anything else is reported and left alone.
      * The private key is created non-exportable in the current user's store
        and is never exported. Machine trust stores receive only the public
        certificate.
      * Elevated steps install from an administrators-only copy, verified
        there, so a standard-user process cannot swap files after the check.
    Not part of SSAP3 Setup and never packaged with it.
#>

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '..\..\installer\Ssap3Setup.psm1') -DisableNameChecking -WarningAction SilentlyContinue

$script:Subject = 'CN=SSAP3 Temporary Driver Signing'
$script:SignerName = 'SSAP3 Temporary Driver Signing'
$script:FriendlyName = 'SSAP3 temporary driver signing (development only, not a Microsoft signature)'
# Marks certificates made by this tool, so removal can never pick up someone
# else's certificate: the standard comment extension (Netscape nsComment,
# IA5String) with this exact text, including a fixed UUID. (A custom 2.25 UUID
# OID was tried first; CryptoAPI cannot encode its 128-bit arc.)
$script:MarkerOid = '2.16.840.1.113730.1.13'
$script:MarkerText = 'SSAP3 development test signing only, not a Microsoft signature; 3387e542-cb6d-4ff3-a9ed-28d629f25ace'
$script:CodeSigningEku = '1.3.6.1.5.5.7.3.3'
$script:Sha256WithRsa = '1.2.840.113549.1.1.11'
$script:KeyLength = 3072
$script:MachineTrustStores = @('Root', 'TrustedPublisher')
$script:DriverFiles = @('ssap3aap.sys', 'ssap3aap.inf', 'ssap3aap.cat')
$script:StateRoot = Join-Path $env:LOCALAPPDATA 'SSAP3\DevSigning'
$script:Verifier = Join-Path $PSScriptRoot '..\..\installer\Test-Ssap3Driver.ps1'
$script:HeadPoseRecord = Join-Path $env:ProgramData 'SSAP3\Runtime\head-pose.bin'
$script:SecureBootBlocker = 'Secure Boot is on. Windows does not allow test signing while Secure Boot is on (bcdedit answers "The value is protected by Secure Boot policy"), so a self-signed driver cannot load on this PC. SSAP3 does not change firmware settings or work around signature enforcement. Use a dedicated development PC where you turn Secure Boot off yourself (tools\dev\README.md), or wait for the Microsoft-signed driver (driver\SIGNING.md).'

Add-Type -AssemblyName System.Security

# ------------------------------------------------------------------ helpers --

function Set-Ssap3DevStateRoot {
    param([Parameter(Mandatory = $true)][string]$Path)
    $script:StateRoot = $Path
}

function Get-Ssap3DevStateRoot { return $script:StateRoot }

function Get-Ssap3DevDefaultOutput { return (Join-Path $script:StateRoot 'package\ssap3aap') }

function Test-Ssap3DevElevated {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-Ssap3DevElevated([string]$What) {
    if (-not (Test-Ssap3DevElevated)) { throw "$What needs an elevated PowerShell (Run as administrator), under your own account." }
}

# Runs a native tool and returns its exit code and output as text. Windows
# PowerShell 5.1 turns native stderr into terminating errors under 'Stop'.
function Invoke-Ssap3DevNative {
    param([Parameter(Mandatory = $true)][string]$FilePath, [string[]]$Arguments = @())
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $FilePath @Arguments 2>&1 | ForEach-Object { "$_" }
        return [pscustomobject]@{ Exit = $LASTEXITCODE; Lines = @($output | Where-Object { $_.Trim() }) }
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

function Get-Ssap3DevFileHash([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

# ---------------------------------------------------------------- state file --
# %LOCALAPPDATA%\SSAP3\DevSigning\state.json records what this tool created or
# changed, so removal undoes exactly that. It holds no secrets. Elevated steps
# never act on it alone: every certificate and package it names is checked
# again before anything is removed.

function Get-Ssap3DevStatePath { return (Join-Path $script:StateRoot 'state.json') }

function Read-Ssap3DevState {
    $path = Get-Ssap3DevStatePath
    $state = [pscustomobject]@{
        schema = 1; certificates = @(); trust = @(); testSigning = $null; drivers = @(); lastSigned = $null; snapshot = $null
    }
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        try { $json = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
        catch { throw "The development-signing state file is unreadable ($path): $($_.Exception.Message)" }
        foreach ($property in $json.PSObject.Properties) {
            if ($state.PSObject.Properties[$property.Name]) { $state.($property.Name) = $property.Value }
        }
    }
    foreach ($list in 'certificates', 'trust', 'drivers') { $state.$list = @($state.$list | Where-Object { $null -ne $_ }) }
    return $state
}

function Save-Ssap3DevState($State) {
    New-Item -ItemType Directory -Force -Path $script:StateRoot | Out-Null
    ($State | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath (Get-Ssap3DevStatePath) -Encoding UTF8
}

# --------------------------------------------------------------- certificates --

function ConvertTo-Ssap3Ia5Der([string]$Text) {
    $bytes = [Text.Encoding]::ASCII.GetBytes($Text)
    if ($bytes.Length -gt 127) { throw 'Marker text too long for a short-form DER length.' }
    return [byte[]](@(0x16, $bytes.Length) + $bytes)
}

function Get-Ssap3CertificateEkus($Certificate) {
    return @($Certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' } |
        ForEach-Object { ([System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$_).EnhancedKeyUsages } |
        ForEach-Object Value)
}

# True only for certificates this tool creates: the fixed subject, self-issued,
# the SSAP3 marker extension with its exact text, code signing as the only
# EKU, and not a CA.
function Test-Ssap3DevCertificate {
    param($Certificate)
    if ($null -eq $Certificate -or $Certificate.Subject -ne $script:Subject -or $Certificate.Issuer -ne $script:Subject) { return $false }
    $marker = @($Certificate.Extensions | Where-Object { $_.Oid.Value -eq $script:MarkerOid })
    if ($marker.Count -ne 1) { return $false }
    $raw = $marker[0].RawData
    if ($raw.Length -lt 2 -or $raw[0] -ne 0x16 -or $raw[1] -ne ($raw.Length - 2)) { return $false }
    if ([Text.Encoding]::ASCII.GetString($raw, 2, $raw.Length - 2) -ne $script:MarkerText) { return $false }
    $ekus = @(Get-Ssap3CertificateEkus $Certificate)
    if ($ekus.Count -ne 1 -or $ekus[0] -ne $script:CodeSigningEku) { return $false }
    $basic = @($Certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.19' })
    if ($basic.Count -ne 1 -or ([System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]$basic[0]).CertificateAuthority) { return $false }
    return $true
}

function Open-Ssap3Store([string]$Location, [string]$Name, [switch]$Write) {
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($Name, [System.Security.Cryptography.X509Certificates.StoreLocation]$Location)
    $flags = if ($Write) { [System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite } else { [System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly }
    $store.Open($flags)
    return $store
}

function Get-Ssap3StoreThumbprints([string]$Location, [string]$Name) {
    $store = Open-Ssap3Store $Location $Name
    try { return @($store.Certificates | ForEach-Object { $_.Thumbprint }) } finally { $store.Close() }
}

# SSAP3 development certificates in one store.
function Get-Ssap3DevCertificates([string]$Location = 'CurrentUser', [string]$Name = 'My') {
    $store = Open-Ssap3Store $Location $Name
    try { return @($store.Certificates | Where-Object { Test-Ssap3DevCertificate $_ }) } finally { $store.Close() }
}

# Every SSAP3 development certificate anywhere this tool could have put one
# (or a copy of one), with where it was found.
# The current user's Root and TrustedPublisher views also show the machine's
# certificates, so an entry there counts only when the machine store lacks it.
function Find-Ssap3DevCertificates {
    $found = @()
    foreach ($place in @('LocalMachine\My', 'LocalMachine\Root', 'LocalMachine\TrustedPublisher', 'CurrentUser\My', 'CurrentUser\Root', 'CurrentUser\TrustedPublisher')) {
        $location, $name = $place -split '\\'
        foreach ($certificate in Get-Ssap3DevCertificates $location $name) {
            if ($location -eq 'CurrentUser' -and $name -ne 'My' -and ($found | Where-Object { $_.Store -eq "LocalMachine\$name" -and $_.Thumbprint -eq $certificate.Thumbprint })) { continue }
            $found += [pscustomobject]@{ Store = $place; Thumbprint = $certificate.Thumbprint; NotAfter = $certificate.NotAfter }
        }
    }
    return $found
}

function Get-Ssap3KeyFacts($Certificate) {
    $facts = [ordered]@{ HasPrivateKey = $Certificate.HasPrivateKey; Exportable = $null; Provider = $null; KeyName = $null }
    if (-not $Certificate.HasPrivateKey) { return [pscustomobject]$facts }
    $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
    try {
        if ($rsa -is [System.Security.Cryptography.RSACng]) {
            $allow = [int][System.Security.Cryptography.CngExportPolicies]::AllowExport -bor [int][System.Security.Cryptography.CngExportPolicies]::AllowPlaintextExport
            $facts.Exportable = ([int]$rsa.Key.ExportPolicy -band $allow) -ne 0
            $facts.Provider = $rsa.Key.Provider.Provider
            $facts.KeyName = $rsa.Key.KeyName
        }
        elseif ($rsa -is [System.Security.Cryptography.RSACryptoServiceProvider]) {
            $facts.Exportable = $rsa.CspKeyContainerInfo.Exportable
            $facts.Provider = $rsa.CspKeyContainerInfo.ProviderName
            $facts.KeyName = $rsa.CspKeyContainerInfo.KeyContainerName
        }
    }
    finally {
        if ($null -ne $rsa) { $rsa.Dispose() }
    }
    return [pscustomobject]$facts
}

function Get-Ssap3DevCertificateFacts($Certificate) {
    $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($Certificate)
    try { $size = if ($null -ne $rsa) { $rsa.KeySize } else { $null } } finally { if ($null -ne $rsa) { $rsa.Dispose() } }
    $key = Get-Ssap3KeyFacts $Certificate
    $basic = @($Certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.19' })
    return [pscustomobject][ordered]@{
        Subject            = $Certificate.Subject
        Thumbprint         = $Certificate.Thumbprint
        NotBefore          = $Certificate.NotBefore
        NotAfter           = $Certificate.NotAfter
        DaysLeft           = [Math]::Floor(($Certificate.NotAfter - (Get-Date)).TotalDays)
        SignatureAlgorithm = "$($Certificate.SignatureAlgorithm.FriendlyName) ($($Certificate.SignatureAlgorithm.Value))"
        PublicKey          = "RSA $size"
        Ekus               = (Get-Ssap3CertificateEkus $Certificate) -join ', '
        IsCa               = ($basic.Count -eq 1 -and ([System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]$basic[0]).CertificateAuthority)
        HasPrivateKey      = $key.HasPrivateKey
        KeyExportable      = $key.Exportable
        KeyProvider        = $key.Provider
        IsSsap3Development = (Test-Ssap3DevCertificate $Certificate)
    }
}

# The newest SSAP3 development certificate that is valid now and has its
# private key on this PC.
function Get-Ssap3DevActiveCertificate {
    $now = Get-Date
    return @(Get-Ssap3DevCertificates 'CurrentUser' 'My' | Where-Object { $_.HasPrivateKey -and $_.NotBefore -le $now -and $_.NotAfter -gt $now } |
        Sort-Object NotAfter -Descending) | Select-Object -First 1
}

function Get-Ssap3DevStorePackages {
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
            if ($null -eq $current) { $current = @{ Published = $null; Original = $null; Provider = $null; Version = $null; Signer = $null } }
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

function New-Ssap3DevSnapshot {
    return [pscustomobject]@{
        taken                   = (Get-Date).ToString('o')
        machineRoot             = @(Get-Ssap3StoreThumbprints 'LocalMachine' 'Root')
        machineTrustedPublisher = @(Get-Ssap3StoreThumbprints 'LocalMachine' 'TrustedPublisher')
        userMy                  = @(Get-Ssap3StoreThumbprints 'CurrentUser' 'My')
        testSigningActive       = (Get-Ssap3DevReadiness).TestSigningActive
        ssap3DriverPackages     = @(Get-Ssap3DevStorePackages | ForEach-Object { $_.Published })
    }
}

# GenerateCertificate. Reuses a valid SSAP3 development certificate unless
# -Force (used by renewal).
function New-Ssap3DevCertificate {
    param([ValidateRange(1, 90)][int]$ValidityDays = 30, [switch]$Force)
    $state = Read-Ssap3DevState
    if ($null -eq $state.snapshot) { $state.snapshot = New-Ssap3DevSnapshot; Save-Ssap3DevState $state }
    if (-not $Force) {
        $active = Get-Ssap3DevActiveCertificate
        if ($null -ne $active) { return $active }
    }

    $marker = New-Object System.Security.Cryptography.X509Certificates.X509Extension($script:MarkerOid, (ConvertTo-Ssap3Ia5Der $script:MarkerText), $false)
    $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject $script:Subject -FriendlyName $script:FriendlyName `
        -CertStoreLocation 'Cert:\CurrentUser\My' -KeyAlgorithm RSA -KeyLength $script:KeyLength -HashAlgorithm SHA256 `
        -KeyExportPolicy NonExportable -Provider 'Microsoft Software Key Storage Provider' `
        -TextExtension @('2.5.29.19={critical}{text}ca=0') -Extension @($marker) `
        -NotBefore (Get-Date).AddMinutes(-10) -NotAfter (Get-Date).AddDays($ValidityDays)

    if (-not (Test-Ssap3DevCertificate $certificate)) {
        Remove-Ssap3DevUserCertificate -Thumbprint $certificate.Thumbprint -SkipIdentityCheck | Out-Null
        throw 'The new certificate does not have the expected SSAP3 development profile; it was deleted again.'
    }
    $facts = Get-Ssap3DevCertificateFacts $certificate
    if ($facts.KeyExportable -ne $false) {
        Remove-Ssap3DevUserCertificate -Thumbprint $certificate.Thumbprint | Out-Null
        throw 'The new private key is exportable; it was deleted again.'
    }

    $state = Read-Ssap3DevState
    $state.certificates = @($state.certificates) + [pscustomobject]@{
        thumbprint = $certificate.Thumbprint; created = (Get-Date).ToString('o'); notAfter = $certificate.NotAfter.ToString('o')
    }
    Save-Ssap3DevState $state
    return $certificate
}

# Adds the PUBLIC certificate to LocalMachine\Root and \TrustedPublisher so
# Windows (PnP) accepts the test-signed package without prompts. Callers
# check the development gate first (Install-Ssap3DevTrust).
function Add-Ssap3DevTrust {
    param([Parameter(Mandatory = $true)]$Certificate)
    Assert-Ssap3DevElevated 'Trusting the development certificate'
    if (-not (Test-Ssap3DevCertificate $Certificate)) { throw 'Refusing to trust a certificate that this tool did not create.' }
    $public = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(, [byte[]]$Certificate.RawData)
    $state = Read-Ssap3DevState
    foreach ($name in $script:MachineTrustStores) {
        $store = Open-Ssap3Store 'LocalMachine' $name -Write
        try { $store.Add($public) } finally { $store.Close() }
        if (-not ($state.trust | Where-Object { $_.store -eq $name -and $_.thumbprint -eq $Certificate.Thumbprint })) {
            $state.trust = @($state.trust) + [pscustomobject]@{ store = $name; thumbprint = $Certificate.Thumbprint; added = (Get-Date).ToString('o') }
        }
    }
    Save-Ssap3DevState $state
}

function Test-Ssap3DevTrusted([string]$Thumbprint) {
    foreach ($name in $script:MachineTrustStores) {
        if (-not (@(Get-Ssap3DevCertificates 'LocalMachine' $name) | Where-Object { $_.Thumbprint -eq $Thumbprint })) { return $false }
    }
    return $true
}

function Remove-Ssap3DevTrust {
    param([Parameter(Mandatory = $true)][string]$Thumbprint)
    Assert-Ssap3DevElevated 'Removing machine trust'
    $messages = @()
    foreach ($name in $script:MachineTrustStores) {
        $store = Open-Ssap3Store 'LocalMachine' $name -Write
        try {
            foreach ($certificate in @($store.Certificates.Find([System.Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint, $Thumbprint, $false))) {
                if (Test-Ssap3DevCertificate $certificate) { $store.Remove($certificate); $messages += "removed from LocalMachine\$name" }
                else { $messages += "LocalMachine\$name has $Thumbprint but it is not an SSAP3 development certificate: left alone" }
            }
        }
        finally { $store.Close() }
    }
    $state = Read-Ssap3DevState
    $state.trust = @($state.trust | Where-Object { $_.thumbprint -ne $Thumbprint })
    Save-Ssap3DevState $state
    return $messages
}

# Deletes the certificate and its private key from CurrentUser\My, and checks
# that the key container is gone.
function Remove-Ssap3DevUserCertificate {
    param([Parameter(Mandatory = $true)][string]$Thumbprint, [switch]$SkipIdentityCheck)
    $messages = @()
    $store = Open-Ssap3Store 'CurrentUser' 'My' -Write
    try {
        foreach ($certificate in @($store.Certificates.Find([System.Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint, $Thumbprint, $false))) {
            if (-not $SkipIdentityCheck -and -not (Test-Ssap3DevCertificate $certificate)) {
                $messages += "CurrentUser\My has $Thumbprint but it is not an SSAP3 development certificate: left alone"
                continue
            }
            $key = Get-Ssap3KeyFacts $certificate
            if ($certificate.HasPrivateKey) {
                $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
                if ($rsa -is [System.Security.Cryptography.RSACng]) { $rsa.Key.Delete() }
                elseif ($rsa -is [System.Security.Cryptography.RSACryptoServiceProvider]) { $rsa.PersistKeyInCsp = $false; $rsa.Clear() }
                try { $rsa.Dispose() } catch { }
            }
            $store.Remove($certificate)
            $gone = $true
            if ($key.KeyName -and $key.Provider) {
                $gone = -not [System.Security.Cryptography.CngKey]::Exists($key.KeyName, (New-Object System.Security.Cryptography.CngProvider($key.Provider)))
            }
            $messages += $(if ($gone) { 'removed from CurrentUser\My with its private key (key container verified gone)' } else { 'removed from CurrentUser\My, but its private key container still exists' })
        }
    }
    finally { $store.Close() }
    return $messages
}

# RemoveCertificate. Default: every SSAP3 development certificate this tool
# recorded or can identify by its marker, from the user store (with the key)
# and from machine trust. -TrustOnly keeps the user certificate and key.
function Remove-Ssap3DevCertificate {
    param([string[]]$Thumbprint, [switch]$TrustOnly)
    $state = Read-Ssap3DevState
    # @(...) around the whole if: a single thumbprint would otherwise unroll to a string.
    $targets = @(if ($Thumbprint) { $Thumbprint | ForEach-Object { $_.ToUpperInvariant() } } else {
            @($state.certificates | ForEach-Object { $_.thumbprint }) + @($state.trust | ForEach-Object { $_.thumbprint }) +
            @(Find-Ssap3DevCertificates | ForEach-Object { $_.Thumbprint }) | Select-Object -Unique
        })
    $report = @()
    foreach ($target in $targets) {
        $inMachine = $false
        foreach ($name in $script:MachineTrustStores) {
            $store = Open-Ssap3Store 'LocalMachine' $name
            try { if (@($store.Certificates.Find([System.Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint, $target, $false)).Count -gt 0) { $inMachine = $true } } finally { $store.Close() }
        }
        if ($inMachine) {
            if (Test-Ssap3DevElevated) { $report += Remove-Ssap3DevTrust -Thumbprint $target | ForEach-Object { "${target}: $_" } }
            else { $report += "${target}: still trusted machine-wide; run RemoveCertificate elevated to remove that trust"; continue }
        }
        if (-not $TrustOnly) {
            $report += Remove-Ssap3DevUserCertificate -Thumbprint $target | ForEach-Object { "${target}: $_" }
            $state = Read-Ssap3DevState
            $state.certificates = @($state.certificates | Where-Object { $_.thumbprint -ne $target })
            Save-Ssap3DevState $state
        }
    }
    if ($targets.Count -eq 0) { $report += 'No SSAP3 development certificate was found.' }
    # Drop trust records whose certificate is no longer in the machine store.
    $state = Read-Ssap3DevState
    $state.trust = @($state.trust | Where-Object { $entry = $_; @(Get-Ssap3StoreThumbprints 'LocalMachine' $entry.store) -contains $entry.thumbprint })
    Save-Ssap3DevState $state
    return $report
}

# RenewCertificate: a new certificate (new key) replaces the old ones. Machine
# trust moves to the new certificate if the old one was trusted; the old
# certificates, their keys and their trust entries are then removed.
function Update-Ssap3DevCertificate {
    param([ValidateRange(1, 90)][int]$ValidityDays = 30)
    $old = @(Get-Ssap3DevCertificates 'CurrentUser' 'My' | ForEach-Object { $_.Thumbprint })
    $trusted = @($old | Where-Object { Test-Ssap3DevTrusted $_ })
    if ($trusted.Count -gt 0 -and -not (Test-Ssap3DevElevated)) {
        throw 'The current certificate is trusted machine-wide. Run RenewCertificate elevated so that the trust moves to the new certificate.'
    }
    $new = New-Ssap3DevCertificate -ValidityDays $ValidityDays -Force
    if ($trusted.Count -gt 0) { Add-Ssap3DevTrust -Certificate $new }
    $report = @()
    foreach ($thumbprint in $old) { $report += Remove-Ssap3DevCertificate -Thumbprint $thumbprint }
    return [pscustomobject]@{ Certificate = $new; Removed = $report; Trusted = ($trusted.Count -gt 0) }
}

# ------------------------------------------------------------------- system --

function Find-Ssap3SignTool {
    param([string]$Path)
    if (-not $Path) {
        $packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
        $candidates = @(@(
            Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue
            Get-ChildItem -Path (Join-Path $packages 'microsoft.windows.sdk.cpp\*\c\bin\*\x64\signtool.exe') -ErrorAction SilentlyContinue
        ) | Sort-Object FullName -Descending)
        if ($candidates.Count -eq 0) { return $null }
        $Path = $candidates[0].FullName
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        throw "$Path is not a Microsoft-signed signtool.exe."
    }
    return $Path
}

function Get-Ssap3DevReadiness {
    $elevated = Test-Ssap3DevElevated
    $secureBoot = 'Unknown'
    try {
        $value = (Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot\State' -Name UEFISecureBootEnabled -ErrorAction Stop).UEFISecureBootEnabled
        $secureBoot = if ($value -eq 1) { 'On' } else { 'Off' }
    }
    catch { $secureBoot = 'Unknown' }

    $options = ''
    try { $options = "$((Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control' -Name SystemStartOptions -ErrorAction Stop).SystemStartOptions)" } catch { }

    # bcdedit needs elevation. Element names are not localized; values may be.
    $configured = 'Unknown (needs elevation)'
    if ($elevated) {
        $run = Invoke-Ssap3DevNative (Join-Path $env:SystemRoot 'System32\bcdedit.exe') @('/enum', '{current}')
        if ($run.Exit -eq 0) {
            $line = @($run.Lines | Where-Object { $_ -match '^\s*testsigning\s+(\S+)' }) | Select-Object -First 1
            $configured = if ($line -and $line -match '^\s*testsigning\s+(\S+)') { $Matches[1] } else { 'Absent' }
        }
        else { $configured = "Unknown (bcdedit exit $($run.Exit))" }
    }

    $hvci = 'Unknown'
    try {
        $guard = Get-CimInstance -Namespace 'root\Microsoft\Windows\DeviceGuard' -ClassName Win32_DeviceGuard -ErrorAction Stop
        $hvci = if (@($guard.SecurityServicesRunning) -contains 2) { 'Running' } else { 'Not running' }
    }
    catch { }

    $defender = 'Unknown'
    try { $defender = if ((Get-MpComputerStatus -ErrorAction Stop).RealTimeProtectionEnabled) { 'On' } else { 'Off' } } catch { }

    $bitLocker = 'Unknown (needs elevation)'
    if ($elevated) {
        try {
            $volume = Get-CimInstance -Namespace 'root\CIMV2\Security\MicrosoftVolumeEncryption' -ClassName Win32_EncryptableVolume -Filter "DriveLetter='$env:SystemDrive'" -ErrorAction Stop
            $bitLocker = if ($null -eq $volume) { 'Not encrypted' } elseif ($volume.ProtectionStatus -eq 1) { 'On' } else { 'Off or suspended' }
        }
        catch { $bitLocker = 'Unknown' }
    }

    $signTool = $null
    $signToolProblem = $null
    try { $signTool = Find-Ssap3SignTool } catch { $signToolProblem = $_.Exception.Message }

    return [pscustomobject][ordered]@{
        Elevated                = $elevated
        SecureBoot              = $secureBoot
        TestSigningActive       = ($options -match 'TESTSIGNING')
        IntegrityChecksDisabled = ($options -match 'NOINTEGRITYCHECKS')
        TestSigningConfigured   = $configured
        MemoryIntegrity         = $hvci
        Defender                = $defender
        BitLocker               = $bitLocker
        SignTool                = $signTool
        SignToolProblem         = $signToolProblem
    }
}

# Why an action may not run, given the facts. Empty means it may.
#   EnableTestSigning   bcdedit testsigning on (then a restart)
#   InstallCertificate  public certificate into machine Root/TrustedPublisher
#   InstallDriver       the test-signed package into the driver store
function Get-Ssap3DevBlockers {
    param(
        [Parameter(Mandatory = $true)]$Readiness,
        [Parameter(Mandatory = $true)][ValidateSet('EnableTestSigning', 'InstallCertificate', 'InstallDriver')][string]$Action,
        [switch]$DevelopmentMachine,
        [switch]$RecoveryKeyReady
    )
    $blockers = @()
    if ($Readiness.SecureBoot -eq 'On' -and -not $Readiness.TestSigningActive) { $blockers += $script:SecureBootBlocker }
    if (-not $Readiness.Elevated) { $blockers += "$Action needs an elevated PowerShell (Run as administrator), under your own account." }
    if (-not $DevelopmentMachine) { $blockers += 'Pass -DevelopmentMachine to confirm that this PC is a dedicated development machine. Test signing lets any test-signed kernel driver load, so it never belongs on an everyday PC.' }
    if ($Readiness.IntegrityChecksDisabled) { $blockers += 'The boot options contain NOINTEGRITYCHECKS. SSAP3 never uses it; remove it first (bcdedit /deletevalue nointegritychecks) and restart.' }
    if ($Action -eq 'EnableTestSigning' -and $Readiness.BitLocker -eq 'On' -and -not $RecoveryKeyReady) {
        $blockers += 'BitLocker (Device Encryption) protects the system drive. Changing boot settings can make Windows ask for the recovery key at the next start. Make sure you have the key (for a Microsoft account: aka.ms/myrecoverykey), then pass -RecoveryKeyReady.'
    }
    if ($Action -eq 'InstallDriver' -and -not $Readiness.TestSigningActive) {
        $blockers += 'Test signing is not active in this boot. Run EnableTestSigning, restart, then try again.'
    }
    return @($blockers | Select-Object -Unique)
}

function Install-Ssap3DevTrust {
    param([switch]$DevelopmentMachine)
    $blockers = @(Get-Ssap3DevBlockers -Readiness (Get-Ssap3DevReadiness) -Action InstallCertificate -DevelopmentMachine:$DevelopmentMachine)
    if ($blockers.Count -gt 0) { throw ($blockers -join ' ') }
    $certificate = Get-Ssap3DevActiveCertificate
    if ($null -eq $certificate) { throw 'No usable SSAP3 development certificate. Run GenerateCertificate first.' }
    Add-Ssap3DevTrust -Certificate $certificate
    return $certificate
}

function Enable-Ssap3TestSigning {
    param([switch]$DevelopmentMachine, [switch]$RecoveryKeyReady)
    $readiness = Get-Ssap3DevReadiness
    $blockers = @(Get-Ssap3DevBlockers -Readiness $readiness -Action EnableTestSigning -DevelopmentMachine:$DevelopmentMachine -RecoveryKeyReady:$RecoveryKeyReady)
    if ($blockers.Count -gt 0) { throw ($blockers -join ' ') }
    if ($readiness.TestSigningConfigured -match '^(?i)(Yes|On|True)$') {
        return 'Test signing is already configured (not changed by SSAP3, so SSAP3 will not turn it off either).'
    }
    $state = Read-Ssap3DevState
    if ($null -eq $state.snapshot) { $state.snapshot = New-Ssap3DevSnapshot }
    # Recorded before the change, so a failed or interrupted run can be undone.
    $state.testSigning = [pscustomobject]@{ changedByTool = $true; previous = $readiness.TestSigningConfigured; changedAt = (Get-Date).ToString('o') }
    Save-Ssap3DevState $state
    $run = Invoke-Ssap3DevNative (Join-Path $env:SystemRoot 'System32\bcdedit.exe') @('/set', '{current}', 'testsigning', 'on')
    if ($run.Exit -ne 0) {
        $state.testSigning = $null
        Save-Ssap3DevState $state
        throw "bcdedit refused (exit $($run.Exit)): $($run.Lines -join ' ')"
    }
    return 'Test signing is configured. Restart to enter test mode (Windows shows "Test Mode" on the desktop). Secure Boot, Memory Integrity and Defender were not changed.'
}

function Disable-Ssap3TestSigning {
    Assert-Ssap3DevElevated 'DisableTestSigning'
    $state = Read-Ssap3DevState
    if ($null -eq $state.testSigning -or -not $state.testSigning.changedByTool) {
        return 'SSAP3 did not turn test signing on, so it leaves the setting as it is.'
    }
    $messages = @()
    if (@(Get-Ssap3DevStorePackages | Where-Object { $_.Signer -eq $script:SignerName }).Count -gt 0) {
        $messages += 'The development driver is still installed and will not load after the restart. Run RemoveDriver to remove it.'
    }
    $arguments = if ("$($state.testSigning.previous)" -match '^(?i)(No|Off|False)$') { @('/set', '{current}', 'testsigning', 'off') } else { @('/deletevalue', '{current}', 'testsigning') }
    $run = Invoke-Ssap3DevNative (Join-Path $env:SystemRoot 'System32\bcdedit.exe') $arguments
    if ($run.Exit -ne 0) { throw "bcdedit failed (exit $($run.Exit)): $($run.Lines -join ' ')" }
    $state.testSigning = $null
    Save-Ssap3DevState $state
    $messages += "Test signing restored to its previous setting ($(($arguments[0..1]) -join ' ') testsigning). Restart to leave test mode; if you turned Secure Boot off for development, turn it back on in UEFI after that."
    return $messages
}

# ------------------------------------------------------------------ signing --

function Get-Ssap3EmbeddedSignedCms([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 0x200 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) { return $null }
    $pe = [BitConverter]::ToInt32($bytes, 0x3C)
    if ($pe -lt 0x40 -or ($pe + 24 + 112 + 40) -gt $bytes.Length) { return $null }
    $optional = $pe + 24
    $magic = [BitConverter]::ToUInt16($bytes, $optional)
    $directories = if ($magic -eq 0x20B) { $optional + 112 } elseif ($magic -eq 0x10B) { $optional + 96 } else { return $null }
    $offset = [BitConverter]::ToUInt32($bytes, $directories + 32)
    $size = [BitConverter]::ToUInt32($bytes, $directories + 36)
    if ($size -lt 8 -or ($offset + $size) -gt $bytes.Length) { return $null }
    $length = [BitConverter]::ToUInt32($bytes, [int]$offset)
    $type = [BitConverter]::ToUInt16($bytes, [int]$offset + 6)
    if ($type -ne 2 -or $length -lt 8 -or $length -gt $size) { return $null }
    $pkcs = New-Object byte[] ($length - 8)
    [Array]::Copy($bytes, [int]$offset + 8, $pkcs, 0, $pkcs.Length)
    $cms = New-Object System.Security.Cryptography.Pkcs.SignedCms
    $cms.Decode($pkcs)
    return $cms
}

function Test-Ssap3BytesContain([byte[]]$Haystack, [byte[]]$Needle) {
    $hay = [BitConverter]::ToString($Haystack).Replace('-', '')
    $pin = [BitConverter]::ToString($Needle).Replace('-', '')
    $index = $hay.IndexOf($pin, [StringComparison]::Ordinal)
    while ($index -ge 0 -and ($index % 2) -ne 0) { $index = $hay.IndexOf($pin, $index + 1, [StringComparison]::Ordinal) }
    return $index -ge 0
}

# Signature math only (no chain or trust): the signer's signature over the
# signed content is valid, and for the .sys the signed content carries the
# file's own Authenticode image hash.
function Test-Ssap3SignedCms($Cms, [byte[]]$ExpectedDigest) {
    if ($null -eq $Cms -or $Cms.SignerInfos.Count -lt 1) { return $false }
    try { $Cms.CheckSignature($true) } catch { return $false }
    if ($null -ne $ExpectedDigest -and -not (Test-Ssap3BytesContain $Cms.ContentInfo.Content $ExpectedDigest)) { return $false }
    return $true
}

# VerifySignature. Separates the four statuses:
#   1. CryptographicallySigned  signatures are mathematically valid, and the
#                               .sys and .inf are exactly the signed files
#   2. LocallyTrusted           Windows trusts the chain on THIS PC
#   3. (accepted under the test-signing policy: only observable once the
#      kernel has loaded the driver; see Get-Ssap3DevReport)
#   4. MicrosoftSigned          Microsoft's driver-publisher signature; never
#                               true for a development package
function Test-Ssap3DevPackageSignature {
    param([Parameter(Mandatory = $true)][string]$Folder)
    $checks = New-Object System.Collections.Generic.List[object]
    $add = { param($Name, $Ok, $Detail) $checks.Add([pscustomobject]@{ Check = $Name; Ok = [bool]$Ok; Detail = $Detail }) }
    $files = @{}
    foreach ($name in $script:DriverFiles) {
        $path = Join-Path $Folder $name
        if (Test-Path -LiteralPath $path -PathType Leaf) { $files[$name] = $path }
        & $add "$name present" ($files.ContainsKey($name)) $(if ($files.ContainsKey($name)) { 'yes' } else { 'missing' })
    }
    $result = [ordered]@{
        Folder = $Folder; Checks = $checks; CryptographicallySigned = $false; LocallyTrusted = $false; MicrosoftSigned = $false
        SignerThumbprint = $null; SignerSubject = $null; SignerNotAfter = $null; SignerIsSsap3Development = $false
        UnchangedSinceSigning = $null; Hashes = [ordered]@{}; SetupAccepts = $false; CatalogClass = $null
    }
    if ($files.Count -ne 3) { return [pscustomobject]$result }

    foreach ($name in $script:DriverFiles) { $result.Hashes[$name] = Get-Ssap3DevFileHash $files[$name] }
    & $add 'INF is SSAP3''s driver' (Test-Ssap3DriverInfIdentity -InfPath $files['ssap3aap.inf']) 'provider SSAP3, ssap3aap service, Apple AAP/UARP hardware IDs only'

    $sysCms = $null
    try { $sysCms = Get-Ssap3EmbeddedSignedCms $files['ssap3aap.sys'] } catch { $sysCms = $null }
    $imageHash = Get-Ssap3PeImageHash -Path $files['ssap3aap.sys'] -Algorithm SHA256
    $sysMath = Test-Ssap3SignedCms $sysCms $imageHash
    $sysWindows = Get-AuthenticodeSignature -LiteralPath $files['ssap3aap.sys']
    & $add '.sys embedded signature' ($sysMath -and $sysWindows.Status -ne 'HashMismatch') "signature valid and matches the file's image hash: $sysMath; Windows: $($sysWindows.Status)"

    $catCms = $null
    try { $catCms = New-Object System.Security.Cryptography.Pkcs.SignedCms; $catCms.Decode([IO.File]::ReadAllBytes($files['ssap3aap.cat'])) } catch { $catCms = $null }
    $catMath = Test-Ssap3SignedCms $catCms $null
    $catWindows = Get-AuthenticodeSignature -LiteralPath $files['ssap3aap.cat']
    & $add 'Catalog signature' ($catMath -and $catWindows.Status -ne 'HashMismatch' -and $catWindows.Status -ne 'NotSigned') "signature valid: $catMath; Windows: $($catWindows.Status)"

    $sysListed = Test-Ssap3CatalogMember -CatalogPath $files['ssap3aap.cat'] -FilePath $files['ssap3aap.sys']
    $infListed = Test-Ssap3CatalogMember -CatalogPath $files['ssap3aap.cat'] -FilePath $files['ssap3aap.inf']
    & $add 'Catalog lists ssap3aap.sys' $sysListed 'by Authenticode image hash'
    & $add 'Catalog lists ssap3aap.inf' $infListed 'by file hash'

    $sysSigner = if ($sysMath) { $sysCms.SignerInfos[0].Certificate } else { $null }
    $catSigner = if ($catMath) { $catCms.SignerInfos[0].Certificate } else { $null }
    $sameSigner = $null -ne $sysSigner -and $null -ne $catSigner -and $sysSigner.Thumbprint -eq $catSigner.Thumbprint
    & $add 'Same signer on .sys and .cat' $sameSigner $(if ($sameSigner) { $catSigner.Thumbprint } else { 'differs or missing' })
    if ($null -ne $catSigner) {
        $result.SignerThumbprint = $catSigner.Thumbprint
        $result.SignerSubject = $catSigner.Subject
        $result.SignerNotAfter = $catSigner.NotAfter
        $result.SignerIsSsap3Development = (Test-Ssap3DevCertificate $catSigner) -and $sameSigner
    }

    $record = Join-Path $Folder 'signing-record.json'
    if (Test-Path -LiteralPath $record -PathType Leaf) {
        $recorded = Get-Content -LiteralPath $record -Raw | ConvertFrom-Json
        $same = $true
        foreach ($name in $script:DriverFiles) { if ("$($recorded.files.$name)" -ne $result.Hashes[$name]) { $same = $false } }
        $result.UnchangedSinceSigning = $same
        & $add 'Unchanged since signing' $same 'SHA-256 of all three files against signing-record.json'
    }

    $result.CryptographicallySigned = $sysMath -and $catMath -and $sysListed -and $infListed -and $sameSigner -and $sysWindows.Status -ne 'HashMismatch' -and $catWindows.Status -ne 'HashMismatch'
    $result.LocallyTrusted = $result.CryptographicallySigned -and $sysWindows.Status -eq 'Valid' -and $catWindows.Status -eq 'Valid'
    $classification = Get-Ssap3CatalogSignature -CatalogPath $files['ssap3aap.cat']
    $result.CatalogClass = $classification.Kind
    $result.MicrosoftSigned = $classification.Kind -in 'MicrosoftAttestation', 'MicrosoftWhql'
    $result.SetupAccepts = Test-Ssap3DriverProductionSigned -CatalogPath $files['ssap3aap.cat'] -MemberPaths @($files['ssap3aap.sys'], $files['ssap3aap.inf'])
    return [pscustomobject]$result
}

# Accepts a folder with ssap3aap.sys/.inf/.cat, or the CI submission CAB
# (checked against its .sha256 file when present), and returns the files.
function Resolve-Ssap3DriverPackage {
    param([Parameter(Mandatory = $true)][string]$Package)
    $item = Get-Item -LiteralPath $Package
    $temp = $null
    $sourceHash = $null
    if ($item.PSIsContainer) { $root = $item.FullName }
    elseif ($item.Extension -eq '.cab') {
        $sourceHash = Get-Ssap3DevFileHash $item.FullName
        $hashFile = "$($item.FullName).sha256"
        if (Test-Path -LiteralPath $hashFile) {
            $expected = ((Get-Content -LiteralPath $hashFile -Raw).Trim() -split '\s+')[0].ToUpperInvariant()
            if ($expected -ne $sourceHash) { throw "The CAB's SHA-256 ($sourceHash) does not match $hashFile ($expected). Download the CI artifact again." }
        }
        $temp = Join-Path ([IO.Path]::GetTempPath()) ('ssap3-dev-cab-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $temp | Out-Null
        $run = Invoke-Ssap3DevNative (Join-Path $env:SystemRoot 'System32\expand.exe') @($item.FullName, '-F:*', $temp)
        if ($run.Exit -ne 0) { Remove-Item -LiteralPath $temp -Recurse -Force; throw "expand.exe failed (exit $($run.Exit))." }
        $root = $temp
    }
    else { throw "Pass a folder with ssap3aap.sys/.inf/.cat or the CI submission CAB, not $($item.Name)." }

    $files = @{}
    foreach ($name in $script:DriverFiles) {
        $found = @(Get-ChildItem -LiteralPath $root -Recurse -File -Filter $name -ErrorAction SilentlyContinue)
        if ($found.Count -ne 1) {
            if ($temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
            throw "Expected exactly one $name in $Package (found $($found.Count))."
        }
        $files[$name] = $found[0].FullName
    }
    return [pscustomobject]@{ Files = $files; Temp = $temp; Source = $item.FullName; SourceHash = $sourceHash }
}

# SignDriver: embeds a signature in ssap3aap.sys, then signs the catalog, with
# Microsoft's signtool and the SSAP3 development certificate. The input must
# be the UNSIGNED package from CI (or driver\package-driver.ps1
# -AttestationCab): SSAP3 never re-signs a Microsoft or third-party signature.
# The catalog lists the .sys by its Authenticode image hash, which leaves the
# embedded signature out, so the catalog stays valid after embedding.
function Invoke-Ssap3DevSignDriver {
    param(
        [Parameter(Mandatory = $true)][string]$Package,
        [string]$OutputFolder,
        [string]$SignToolPath,
        [switch]$DiscardKey
    )
    if (-not $OutputFolder) { $OutputFolder = Get-Ssap3DevDefaultOutput }
    $signTool = Find-Ssap3SignTool -Path $SignToolPath
    if (-not $signTool) { throw 'signtool.exe was not found. Install the Windows SDK signing tools, or run: dotnet restore driver\WdkPackages.proj (it brings Microsoft''s signtool as a NuGet package).' }
    $certificate = Get-Ssap3DevActiveCertificate
    if ($null -eq $certificate) { throw 'No usable SSAP3 development certificate (valid now, with its private key). Run GenerateCertificate first.' }

    $source = Resolve-Ssap3DriverPackage -Package $Package
    try {
        $in = $source.Files
        if (-not (Test-Ssap3DriverInfIdentity -InfPath $in['ssap3aap.inf'])) { throw 'The INF is not the SSAP3 AAP driver (provider, service or hardware IDs differ).' }
        if ((Get-AuthenticodeSignature -LiteralPath $in['ssap3aap.sys']).Status -ne 'NotSigned') { throw 'ssap3aap.sys is already signed. Start from the unsigned CI package (artifact ssap3-driver-attestation-cab-unsigned).' }
        $kind = (Get-Ssap3CatalogSignature -CatalogPath $in['ssap3aap.cat']).Kind
        if ($kind -ne 'Unsigned') { throw "The catalog is already signed ($kind). Start from the unsigned CI package; SSAP3 never re-signs an existing signature." }
        foreach ($member in 'ssap3aap.sys', 'ssap3aap.inf') {
            if (-not (Test-Ssap3CatalogMember -CatalogPath $in['ssap3aap.cat'] -FilePath $in[$member])) { throw "The catalog does not list this $member; the package is inconsistent." }
        }

        if (Test-Path -LiteralPath $OutputFolder) {
            $foreign = @(Get-ChildItem -LiteralPath $OutputFolder -Force | Where-Object { $_.Name -notin ($script:DriverFiles + 'signing-record.json') })
            if ($foreign.Count -gt 0) { throw "The output folder $OutputFolder contains other files ($($foreign.Name -join ', ')); choose an empty folder." }
            Get-ChildItem -LiteralPath $OutputFolder -Force | Remove-Item -Force
        }
        else { New-Item -ItemType Directory -Force -Path $OutputFolder | Out-Null }
        foreach ($name in $script:DriverFiles) { Copy-Item -LiteralPath $in[$name] -Destination (Join-Path $OutputFolder $name) }
        $sys = Join-Path $OutputFolder 'ssap3aap.sys'
        $cat = Join-Path $OutputFolder 'ssap3aap.cat'

        foreach ($target in $sys, $cat) {
            $run = Invoke-Ssap3DevNative $signTool @('sign', '/fd', 'SHA256', '/sha1', $certificate.Thumbprint, $target)
            # 0 = success, 2 = success with warnings (the result is verified below either way).
            if ($run.Exit -ne 0 -and $run.Exit -ne 2) { throw "signtool sign failed for $(Split-Path $target -Leaf) (exit $($run.Exit)): $($run.Lines -join ' ')" }
            if ($target -eq $sys -and -not (Test-Ssap3CatalogMember -CatalogPath $cat -FilePath $sys)) {
                throw 'After embedding the signature the catalog no longer lists ssap3aap.sys (its image hash changed).'
            }
        }

        $verified = Test-Ssap3DevPackageSignature -Folder $OutputFolder
        if (-not $verified.CryptographicallySigned -or $verified.SignerThumbprint -ne $certificate.Thumbprint) {
            $failed = @($verified.Checks | Where-Object { -not $_.Ok } | ForEach-Object { "$($_.Check): $($_.Detail)" })
            throw "The signed package did not verify: $($failed -join '; ')"
        }
        $record = [pscustomobject]@{
            note        = 'SSAP3 development (test) signature. Not a Microsoft signature; loads only under Windows test signing.'
            signedAt    = (Get-Date).ToString('o')
            thumbprint  = $certificate.Thumbprint
            subject     = $certificate.Subject
            notAfter    = $certificate.NotAfter.ToString('o')
            source      = $source.Source
            sourceSha256 = $source.SourceHash
            files       = [pscustomobject]@{ 'ssap3aap.sys' = $verified.Hashes['ssap3aap.sys']; 'ssap3aap.inf' = $verified.Hashes['ssap3aap.inf']; 'ssap3aap.cat' = $verified.Hashes['ssap3aap.cat'] }
        }
        ($record | ConvertTo-Json -Depth 4) | Set-Content -LiteralPath (Join-Path $OutputFolder 'signing-record.json') -Encoding UTF8
        $state = Read-Ssap3DevState
        $state.lastSigned = [pscustomobject]@{ folder = (Resolve-Path -LiteralPath $OutputFolder).ProviderPath; thumbprint = $certificate.Thumbprint; signedAt = $record.signedAt; files = $record.files }
        Save-Ssap3DevState $state
        if ($DiscardKey) { Remove-Ssap3DevUserCertificate -Thumbprint $certificate.Thumbprint | Out-Null }
        return (Test-Ssap3DevPackageSignature -Folder $OutputFolder)
    }
    finally {
        if ($source.Temp) { Remove-Item -LiteralPath $source.Temp -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

# ------------------------------------------------------------------- driver --

# An administrators-only folder with a random name, checked empty after its
# ACL is set: files are copied here and verified again before installing.
function New-Ssap3AdminOnlyStage {
    $path = Join-Path $env:ProgramData ('SSAP3-DevDriverStage-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $path | Out-Null
    Set-Ssap3ProtectedAcl -Path $path -Directory -Rules @(
        [pscustomobject]@{ Sid = 'S-1-5-18'; Rights = 'FullControl'; Inherit = $true },
        [pscustomobject]@{ Sid = 'S-1-5-32-544'; Rights = 'FullControl'; Inherit = $true })
    Assert-Ssap3AdminOnlyPath -Path $path
    if (@(Get-ChildItem -LiteralPath $path -Force).Count -ne 0) { Remove-Item -LiteralPath $path -Recurse -Force; throw 'The staging folder was not empty after it was locked down.' }
    return $path
}

function Stop-Ssap3DevAppProcesses {
    # S-15: only processes whose image lives in the SSAP3 install folder.
    $root = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'SSAP3')).TrimEnd('\') + '\'
    $stopped = @()
    foreach ($process in @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) })) {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
        $stopped += $process
    }
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline -and @($stopped | Where-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }).Count -gt 0) { Start-Sleep -Milliseconds 250 }
    return @($stopped | ForEach-Object { $_.Name })
}

function Remove-Ssap3DevStorePackage {
    param([Parameter(Mandatory = $true)][string]$Published)
    $package = @(Get-Ssap3DevStorePackages | Where-Object { $_.Published -eq $Published }) | Select-Object -First 1
    if ($null -eq $package) { return "${Published}: not in the driver store" }
    if ($package.Signer -ne $script:SignerName) { return "${Published}: signed by '$($package.Signer)', not by the SSAP3 development certificate: left alone" }
    $run = Invoke-Ssap3DevNative (Join-Path $env:SystemRoot 'System32\pnputil.exe') @('/delete-driver', $Published, '/uninstall')
    if ($run.Exit -ne 0 -and $run.Exit -ne 3010) { throw "pnputil /delete-driver $Published failed (exit $($run.Exit)): $($run.Lines -join ' ')" }
    $state = Read-Ssap3DevState
    $state.drivers = @($state.drivers | Where-Object { $_.published -ne $Published })
    Save-Ssap3DevState $state
    return "${Published}: removed$(if ($run.Exit -eq 3010) { ' (Windows needs a restart to finish)' })"
}

# The ssap3aap service entry, if Windows left it behind once no SSAP3 package
# is installed and it points at SSAP3's own driver file.
function Remove-Ssap3DevServiceLeftover {
    if (@(Get-Ssap3DevStorePackages).Count -gt 0) { return 'Service ssap3aap: kept (an SSAP3 driver package is still installed)' }
    $key = 'HKLM:\SYSTEM\CurrentControlSet\Services\ssap3aap'
    if (-not (Test-Path -LiteralPath $key)) { return 'Service ssap3aap: not registered' }
    $image = "$((Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue).ImagePath)"
    if ($image -notmatch '(?i)\\(DriverStore\\FileRepository\\ssap3aap\.inf_amd64_[0-9a-f]+|drivers)\\ssap3aap\.sys$') { return "Service ssap3aap: kept (ImagePath '$image' is not SSAP3's driver)" }
    $service = Get-CimInstance Win32_SystemDriver -Filter "Name='ssap3aap'" -ErrorAction SilentlyContinue
    if ($null -ne $service -and $service.State -ne 'Stopped') { return 'Service ssap3aap: still running; restart Windows, then run RemoveDriver again' }
    $run = Invoke-Ssap3DevNative (Join-Path $env:SystemRoot 'System32\sc.exe') @('delete', 'ssap3aap')
    return $(if ($run.Exit -eq 0) { 'Service ssap3aap: leftover entry removed' } else { "Service ssap3aap: sc delete failed (exit $($run.Exit))" })
}

# InstallDriver: certificate (reuse or generate), machine trust, signing (if
# -Package is given), verification from an admin-only copy, then pnputil.
function Install-Ssap3DevDriver {
    param(
        [string]$Package,
        [switch]$DevelopmentMachine,
        [ValidateRange(1, 90)][int]$ValidityDays = 30,
        [string]$SignToolPath
    )
    $log = New-Object System.Collections.Generic.List[string]
    $blockers = @(Get-Ssap3DevBlockers -Readiness (Get-Ssap3DevReadiness) -Action InstallDriver -DevelopmentMachine:$DevelopmentMachine)
    if ($blockers.Count -gt 0) { throw ($blockers -join ' ') }

    $existing = @(Get-Ssap3DevStorePackages)
    $foreign = @($existing | Where-Object { $_.Signer -ne $script:SignerName })
    if ($foreign.Count -gt 0) { throw "An SSAP3 driver signed by '$($foreign[0].Signer)' is already installed ($($foreign[0].Published)). The development tool never replaces it; remove it with SSAP3's uninstaller first." }

    if ($Package) {
        $certificate = New-Ssap3DevCertificate -ValidityDays $ValidityDays
        $log.Add("Certificate: $($certificate.Thumbprint), valid until $($certificate.NotAfter.ToString('yyyy-MM-dd'))")
        if (-not (Test-Ssap3DevTrusted $certificate.Thumbprint)) { Add-Ssap3DevTrust -Certificate $certificate; $log.Add('Trusted the public certificate in LocalMachine\Root and LocalMachine\TrustedPublisher') }
        $signed = Invoke-Ssap3DevSignDriver -Package $Package -SignToolPath $SignToolPath
        $folder = $signed.Folder
        $expected = $certificate.Thumbprint
        $log.Add("Signed $Package into $folder")
    }
    else {
        # The last signed package, possibly signed with -DiscardKey: its
        # certificate must already be trusted, or still be in the user store.
        $state = Read-Ssap3DevState
        if ($null -eq $state.lastSigned) { throw 'Nothing is signed yet. Pass -Package with the unsigned CI package (folder or ssap3aap.cab).' }
        $folder = $state.lastSigned.folder
        $expected = "$($state.lastSigned.thumbprint)"
        if (-not (Test-Ssap3DevTrusted $expected)) {
            $owner = @(Get-Ssap3DevCertificates 'CurrentUser' 'My' | Where-Object { $_.Thumbprint -eq $expected }) | Select-Object -First 1
            if ($null -eq $owner) { throw "The certificate that signed $folder ($expected) is neither trusted on this PC nor in your store any more. Sign again with -Package." }
            Add-Ssap3DevTrust -Certificate $owner
            $log.Add("Trusted the public certificate $expected in LocalMachine\Root and LocalMachine\TrustedPublisher")
        }
    }

    $stage = New-Ssap3AdminOnlyStage
    try {
        foreach ($name in $script:DriverFiles + 'signing-record.json') {
            $from = Join-Path $folder $name
            if (Test-Path -LiteralPath $from) { Copy-Item -LiteralPath $from -Destination (Join-Path $stage $name) }
        }
        $check = Test-Ssap3DevPackageSignature -Folder $stage
        $problems = @()
        if (-not $check.CryptographicallySigned) { $problems += 'not cryptographically signed' }
        if ($check.SignerThumbprint -ne $expected -or -not $check.SignerIsSsap3Development) { $problems += "signed by $($check.SignerThumbprint), not by the trusted SSAP3 development certificate $expected" }
        if (-not $check.LocallyTrusted) { $problems += 'Windows does not trust the signature on this PC' }
        if ($check.UnchangedSinceSigning -eq $false) { $problems += 'files changed after signing' }
        if (-not ($check.Checks | Where-Object { $_.Check -eq 'INF is SSAP3''s driver' -and $_.Ok })) { $problems += 'the INF is not SSAP3''s driver' }
        if ($problems.Count -gt 0) { throw "Refusing to install: $($problems -join '; ')." }
        $log.Add("Verified the admin-only copy: cryptographically signed, locally trusted, signer $($check.SignerThumbprint)")

        foreach ($old in @($existing | ForEach-Object { $_.Published })) { $log.Add((Remove-Ssap3DevStorePackage -Published $old)) }
        $before = @(Get-Ssap3DevStorePackages | ForEach-Object { $_.Published })
        $run = Invoke-Ssap3DevNative (Join-Path $env:SystemRoot 'System32\pnputil.exe') @('/add-driver', (Join-Path $stage 'ssap3aap.inf'), '/install')
        # 0 installed; 259 added to the store, no matching device present; 3010 restart needed.
        if ($run.Exit -notin 0, 259, 3010) { throw "pnputil /add-driver failed (exit $($run.Exit)): $($run.Lines -join ' ')" }
        $added = @(Get-Ssap3DevStorePackages | Where-Object { $_.Published -notin $before })
        $state = Read-Ssap3DevState
        # (Not $package: PowerShell names are case-insensitive, and [string]$Package
        # would turn each entry into a string.)
        foreach ($entry in $added) {
            $state.drivers = @($state.drivers) + [pscustomobject]@{ published = $entry.Published; signer = $entry.Signer; thumbprint = $expected; installed = (Get-Date).ToString('o') }
            $log.Add("Driver store: $($entry.Published) (version $($entry.Version), signer '$($entry.Signer)')")
        }
        Save-Ssap3DevState $state
        $log.Add($(switch ($run.Exit) { 0 { 'pnputil: installed on the matching device(s)' } 259 { 'pnputil: added to the driver store; no AirPods AAP device is present yet (connect the AirPods)' } 3010 { 'pnputil: installed; Windows needs a restart to finish' } }))
    }
    finally {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
    return $log
}

# RemoveDriver: stops SSAP3 (it holds the driver interface), removes the
# development driver packages, the leftover service entry if provably SSAP3's,
# and optionally the certificates and the test-signing change. Then verifies.
function Uninstall-Ssap3DevDriver {
    param([switch]$RemoveCertificate, [switch]$RestoreTestSigning)
    Assert-Ssap3DevElevated 'RemoveDriver'
    $log = New-Object System.Collections.Generic.List[string]
    $stopped = @(Stop-Ssap3DevAppProcesses)
    if ($stopped.Count -gt 0) { $log.Add("Stopped SSAP3 processes: $($stopped -join ', ') (they start again at the next sign-in)") }
    foreach ($package in @(Get-Ssap3DevStorePackages)) { $log.Add((Remove-Ssap3DevStorePackage -Published $package.Published)) }
    $log.Add((Remove-Ssap3DevServiceLeftover))
    if ($RemoveCertificate) { foreach ($line in @(Remove-Ssap3DevCertificate)) { $log.Add("Certificate $line") } }
    if ($RestoreTestSigning) { foreach ($line in @(Disable-Ssap3TestSigning)) { $log.Add($line) } }
    foreach ($line in @(Get-Ssap3DevRemovalCheck)) { $log.Add($line) }
    return $log
}

# What is verifiably gone or left, compared with the snapshot taken before
# this tool's first change. It never claims more than it checked.
function Get-Ssap3DevRemovalCheck {
    $lines = @()
    $packages = @(Get-Ssap3DevStorePackages)
    $dev = @($packages | Where-Object { $_.Signer -eq $script:SignerName })
    $lines += $(if ($dev.Count -eq 0) { 'Verified: no development-signed SSAP3 driver package in the driver store' } else { "NOT removed: $($dev.Published -join ', ')" })
    $other = @($packages | Where-Object { $_.Signer -ne $script:SignerName })
    if ($other.Count -gt 0) { $lines += "Left alone: SSAP3 driver packages signed by '$($other[0].Signer)' ($($other.Published -join ', '))" }
    $certificates = @(Find-Ssap3DevCertificates)
    $lines += $(if ($certificates.Count -eq 0) { 'Verified: no SSAP3 development certificate in CurrentUser or LocalMachine My/Root/TrustedPublisher' } else { "Still present: SSAP3 development certificate(s) in $((@($certificates | ForEach-Object { $_.Store }) | Select-Object -Unique) -join ', ')" })
    $state = Read-Ssap3DevState
    $readiness = Get-Ssap3DevReadiness
    $lines += "Test signing: configured '$($readiness.TestSigningConfigured)', active in this boot: $($readiness.TestSigningActive)$(if ($null -ne $state.testSigning) { ' (SSAP3 changed it; DisableTestSigning restores it)' })"
    if ($null -ne $state.snapshot) {
        foreach ($pair in @(@('LocalMachine', 'Root', 'machineRoot'), @('LocalMachine', 'TrustedPublisher', 'machineTrustedPublisher'))) {
            $now = @(Get-Ssap3StoreThumbprints $pair[0] $pair[1])
            $then = @($state.snapshot.($pair[2]))
            $ours = @(Get-Ssap3DevCertificates $pair[0] $pair[1] | ForEach-Object { $_.Thumbprint })
            $added = @($now | Where-Object { $_ -notin $then -and $_ -notin $ours })
            $missing = @($then | Where-Object { $_ -notin $now })
            $store = "$($pair[0])\$($pair[1])"
            if ($added.Count -eq 0 -and $missing.Count -eq 0) {
                $lines += "Verified: $store matches the snapshot of $($state.snapshot.taken)$(if ($ours.Count) { ', apart from the SSAP3 development certificate listed above' })"
            }
            else {
                $lines += "$store differs from the snapshot of $($state.snapshot.taken) by other software (+$($added.Count) / -$($missing.Count)); SSAP3 left those certificates alone"
            }
        }
    }
    $lines += 'Not verified by this tool: Secure Boot in UEFI (turn it back on yourself if you turned it off) and the state after the next restart (run CheckSystem again then).'
    return $lines
}

# ----------------------------------------------------------- head tracking --

$script:TickSource = @'
using System.Runtime.InteropServices;
public static class Ssap3DevTicks { [DllImport("kernel32.dll")] public static extern ulong GetTickCount64(); }
'@

function Read-Ssap3HeadPoseSample([string]$Path) {
    if (-not ('Ssap3DevTicks' -as [type])) { Add-Type -TypeDefinition $script:TickSource }
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        $stream = [IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite')
        try { $bytes = New-Object byte[] 96; [void]$stream.Read($bytes, 0, 96) } finally { $stream.Dispose() }
        $sequence = [BitConverter]::ToUInt32($bytes, 8)
        $stream = [IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite')
        try { $check = New-Object byte[] 12; [void]$stream.Read($check, 0, 12) } finally { $stream.Dispose() }
        if ([BitConverter]::ToUInt32($check, 8) -ne $sequence -or ($sequence % 2) -ne 0) { continue }
        if ([BitConverter]::ToUInt32($bytes, 0) -ne 0x50483353 -or [BitConverter]::ToUInt32($bytes, 4) -ne 2) { return $null }
        $now = [Ssap3DevTicks]::GetTickCount64()
        $written = [BitConverter]::ToUInt64($bytes, 56)
        $w = [BitConverter]::ToSingle($bytes, 24); $x = [BitConverter]::ToSingle($bytes, 28); $y = [BitConverter]::ToSingle($bytes, 32); $z = [BitConverter]::ToSingle($bytes, 36)
        return [pscustomobject]@{
            Mode = [BitConverter]::ToInt32($bytes, 12); Tracking = [BitConverter]::ToInt32($bytes, 16)
            Norm = [Math]::Sqrt($w * $w + $x * $x + $y * $y + $z * $z)
            Yaw = [BitConverter]::ToSingle($bytes, 40); Pitch = [BitConverter]::ToSingle($bytes, 44); Roll = [BitConverter]::ToSingle($bytes, 48)
            AgeMs = $(if ($written -gt 0 -and $written -le $now) { [int64]($now - $written) } else { -1 })
        }
    }
    return $null
}

# Summarises samples of the head-pose record. Real head tracking is reported
# only when the samples are live, fresh, in Tracked mode, come from the
# AirPods sensors (the motion source setting) and actually move.
function Get-Ssap3TrackingSummary {
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Samples, [string]$MotionSource)
    $valid = @($Samples | Where-Object { $null -ne $_ })
    $source = if ($MotionSource) { $MotionSource } else { 'AirPodsSensors' }
    $summary = [ordered]@{ Samples = $valid.Count; MotionSource = $source; LiveShare = 0.0; TrackedShare = 0.0; YawRange = 0.0; PitchRange = 0.0; RollRange = 0.0; NormMin = $null; NormMax = $null; MaxAgeMs = $null; States = ''; Verdict = $null; RealTracking = $false }
    if ($valid.Count -eq 0) { $summary.Verdict = 'No head-pose samples: SSAP3 is not running or has not written its state record.'; return [pscustomobject]$summary }
    $states = @('None', 'Live', 'Holding', 'Lost')
    $summary.States = (@($valid | Group-Object Tracking | ForEach-Object { $name = if ([int]$_.Name -ge 0 -and [int]$_.Name -lt 4) { $states[[int]$_.Name] } else { "invalid($($_.Name))" }; "$name x$($_.Count)" }) -join ', ')
    $summary.LiveShare = [Math]::Round(@($valid | Where-Object { $_.Tracking -eq 1 }).Count / $valid.Count, 3)
    $summary.TrackedShare = [Math]::Round(@($valid | Where-Object { $_.Mode -eq 2 }).Count / $valid.Count, 3)
    foreach ($axis in 'Yaw', 'Pitch', 'Roll') {
        $measure = $valid | Measure-Object -Property $axis -Minimum -Maximum
        $summary["${axis}Range"] = [Math]::Round($measure.Maximum - $measure.Minimum, 1)
    }
    $norm = $valid | Measure-Object -Property Norm -Minimum -Maximum
    $summary.NormMin = [Math]::Round($norm.Minimum, 4); $summary.NormMax = [Math]::Round($norm.Maximum, 4)
    $summary.MaxAgeMs = ($valid | Measure-Object -Property AgeMs -Maximum).Maximum
    $fresh = @($valid | Where-Object { $_.AgeMs -ge 0 -and $_.AgeMs -lt 750 }).Count -eq $valid.Count
    $moving = $summary.YawRange -ge 20
    $live = $summary.LiveShare -ge 0.8 -and $summary.TrackedShare -ge 0.99
    $normalised = $summary.NormMin -gt 0.99 -and $summary.NormMax -lt 1.01
    if ($source -ne 'AirPodsSensors') {
        $summary.Verdict = "SIMULATED or non-AirPods motion (motion source '$source'): not real AirPods head tracking."
    }
    elseif (-not $live) { $summary.Verdict = 'Not tracking: Tracked mode with live AirPods data was not seen in most samples.' }
    elseif (-not $fresh) { $summary.Verdict = 'Stale: some samples were older than the engine''s 750 ms limit.' }
    elseif (-not $normalised) { $summary.Verdict = 'Invalid: an orientation quaternion was not normalised.' }
    elseif (-not $moving) { $summary.Verdict = 'Live AirPods data, but the yaw moved less than 20 degrees: turn your head left and right while this runs.' }
    else { $summary.Verdict = 'Real head tracking: live AirPods sensor data in Tracked mode followed the head movement.'; $summary.RealTracking = $true }
    return [pscustomobject]$summary
}

function Watch-Ssap3HeadTracking {
    param([ValidateRange(1, 120)][int]$Seconds = 15, [string]$RecordPath = $script:HeadPoseRecord)
    $source = $null
    $settings = Join-Path $env:LOCALAPPDATA 'SSAP3\settings.json'
    if (Test-Path -LiteralPath $settings) { try { $source = (Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json).MotionSource } catch { } }
    $samples = @()
    if (Test-Path -LiteralPath $RecordPath) {
        $deadline = (Get-Date).AddSeconds($Seconds)
        while ((Get-Date) -lt $deadline) { $samples += Read-Ssap3HeadPoseSample $RecordPath; Start-Sleep -Milliseconds 50 }
    }
    return Get-Ssap3TrackingSummary -Samples $samples -MotionSource $source
}

# ------------------------------------------------------------------- report --

function Get-Ssap3DevReport {
    param([string]$Folder)
    $state = Read-Ssap3DevState
    if (-not $Folder -and $null -ne $state.lastSigned) { $Folder = $state.lastSigned.folder }
    $readiness = Get-Ssap3DevReadiness
    $certificate = @(Get-Ssap3DevCertificates 'CurrentUser' 'My' | Sort-Object NotAfter -Descending) | Select-Object -First 1
    $package = if ($Folder -and (Test-Path -LiteralPath $Folder)) { Test-Ssap3DevPackageSignature -Folder $Folder } else { $null }
    $store = @(Get-Ssap3DevStorePackages)
    $service = Get-CimInstance Win32_SystemDriver -Filter "Name='ssap3aap'" -ErrorAction SilentlyContinue
    $devices = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.Service -eq 'ssap3aap' })
    $verifier = Invoke-Ssap3DevNative 'powershell.exe' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Resolve-Path -LiteralPath $script:Verifier).ProviderPath)
    $levelLine = @($verifier.Lines | Where-Object { $_ -match '^Installed driver reached: (\w+)' }) | Select-Object -First 1
    $level = if ($levelLine -and $levelLine -match '^Installed driver reached: (\w+)') { $Matches[1] } else { 'Unknown' }

    $devStore = @($store | Where-Object { $_.Signer -eq $script:SignerName })
    $started = @($devices | Where-Object { $_.Status -eq 'OK' -and [int]$_.Problem -eq 0 })
    $refused = @($devices | Where-Object { [int]$_.Problem -eq 52 })
    $accepted = if ($readiness.TestSigningActive -and $devStore.Count -gt 0 -and $started.Count -gt 0) { 'Yes: test signing is active and the kernel loaded the development-signed driver' }
        elseif ($refused.Count -gt 0) { 'No: Windows refused the signature (device problem code 52)' }
        else { 'Not observed: no device has loaded the development-signed driver' }

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('SSAP3 development driver report (temporary development signature; NOT a Microsoft signature)')
    $lines.Add("Generated: $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))")
    $lines.Add('')
    $lines.Add('Certificate')
    if ($null -ne $certificate) {
        $facts = Get-Ssap3DevCertificateFacts $certificate
        $lines.Add("  Subject:             $($facts.Subject)")
        $lines.Add("  Thumbprint:          $($facts.Thumbprint)")
        $lines.Add("  Expires:             $($facts.NotAfter.ToString('yyyy-MM-dd HH:mm')) ($($facts.DaysLeft) days left)")
        $lines.Add("  Signature algorithm: $($facts.SignatureAlgorithm); key $($facts.PublicKey), exportable: $($facts.KeyExportable), provider: $($facts.KeyProvider)")
        $lines.Add("  Machine trust:       $(if (Test-Ssap3DevTrusted $facts.Thumbprint) { 'LocalMachine Root + TrustedPublisher' } else { 'not trusted machine-wide' })")
    }
    else { $lines.Add('  none in CurrentUser\My') }
    $lines.Add('')
    $lines.Add("Package$(if ($Folder) { " ($Folder)" })")
    if ($null -ne $package) {
        foreach ($name in $script:DriverFiles) { $lines.Add(('  {0,-20} SHA-256 {1}' -f "${name}:", $package.Hashes[$name])) }
        foreach ($check in $package.Checks) { $lines.Add(('  {0} {1}: {2}' -f $(if ($check.Ok) { 'PASS' } else { 'FAIL' }), $check.Check, $check.Detail)) }
    }
    else { $lines.Add('  no signed development package found') }
    $lines.Add('')
    $lines.Add('Signature status')
    $lines.Add("  1. Cryptographically signed:          $(if ($null -ne $package -and $package.CryptographicallySigned) { 'Yes' } else { 'No' })")
    $lines.Add("  2. Locally trusted on this PC:        $(if ($null -ne $package -and $package.LocallyTrusted) { 'Yes' } else { 'No' })")
    $lines.Add("  3. Accepted under test-signing policy: $accepted")
    $lines.Add("  4. Officially Microsoft-signed:       $(if ($null -ne $package -and $package.MicrosoftSigned) { 'Yes' } else { 'No (a temporary SSAP3 development signature is not a Microsoft signature)' })")
    $lines.Add('')
    $lines.Add('Windows')
    $lines.Add("  Test signing:     active in this boot: $($readiness.TestSigningActive); configured: $($readiness.TestSigningConfigured)")
    $lines.Add("  Secure Boot:      $($readiness.SecureBoot)")
    $lines.Add("  Memory Integrity: $($readiness.MemoryIntegrity)")
    $lines.Add("  Defender:         real-time protection $($readiness.Defender)")
    $lines.Add('')
    $lines.Add('Driver')
    $lines.Add("  Installation:     $(if ($store.Count -eq 0) { 'not in the driver store' } else { ($store | ForEach-Object { "$($_.Published) v$($_.Version), signer '$($_.Signer)'" }) -join '; ' })")
    $lines.Add("  Service:          $(if ($null -eq $service) { 'not registered' } else { "$($service.State) (start $($service.StartMode))" })")
    $lines.Add("  Devices:          $(if ($devices.Count -eq 0) { 'no present device uses the driver' } else { ($devices | ForEach-Object { "status $($_.Status), problem code $([int]$_.Problem)" }) -join '; ' })")
    $lines.Add("  Health check:     reached '$level' (NotInstalled > Installed > Started > Reachable > Tracking; installer\Test-Ssap3Driver.ps1)")
    $lines.Add("  Head tracking:    $(if ($level -eq 'Tracking') { 'live AirPods data reaches the engine (run WatchTracking to measure movement)' } elseif ($level -eq 'Reachable') { 'driver reachable; no live AirPods head-tracking data yet' } else { 'no communication with the AirPods sensors' })")
    return $lines
}

Export-ModuleMember -Function *-Ssap3* -Variable SecureBootBlocker
