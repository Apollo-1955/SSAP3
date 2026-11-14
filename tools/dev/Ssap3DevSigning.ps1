#Requires -Version 5.1
<#
.SYNOPSIS
    SSAP3 temporary driver signing, for a DEDICATED DEVELOPMENT PC only.
    A locally generated test signature, never a Microsoft signature.

.DESCRIPTION
    Lets the SSAP3 AAP driver be installed and tested under Windows test
    signing before Microsoft signs it. Read tools\dev\README.md first.

    Read-only commands (any PC, no elevation):
      CheckSystem          Secure Boot, Memory Integrity, Defender, BitLocker,
                           test signing and signtool, and what blocks what.
      CheckCertificate     the SSAP3 development certificates: expiry,
                           algorithm, key properties, machine trust.
      VerifySignature      verifies a signed package (-Package) and reports
                           the four statuses: cryptographically signed,
                           locally trusted, accepted under test signing,
                           Microsoft-signed.
      Report               the full verification report (-ReportPath saves it).
      WatchTracking        samples SSAP3's head-pose record for -Seconds while
                           you turn your head; says whether it is real AirPods
                           tracking, simulated, stale or not moving.

    Commands that change something (each asks for confirmation):
      GenerateCertificate  creates, or reuses, the certificate
                           "CN=SSAP3 Temporary Driver Signing" in your Personal
                           store: RSA 3072, SHA-256, non-exportable key, code
                           signing only, not a CA, -ValidityDays 1-90
                           (default 30).
      SignDriver           signs the UNSIGNED CI package (-Package: a folder
                           or ssap3aap.cab) with Microsoft's signtool.
                           -DiscardKey deletes the private key afterwards.
      RenewCertificate     a new certificate replaces the old one (and its
                           machine trust); the old key is deleted.
      RemoveCertificate    removes SSAP3 development certificates, their keys
                           and their machine trust (-TrustOnly keeps the key).
      Elevated, and only with -DevelopmentMachine:
      InstallCertificate   trusts the PUBLIC certificate in LocalMachine Root
                           and TrustedPublisher. Refused while Secure Boot is on.
      EnableTestSigning    bcdedit /set {current} testsigning on. Refused while
                           Secure Boot is on; Windows itself forbids it then.
      InstallDriver        certificate, trust, signing (-Package), verification
                           from an administrators-only copy, then pnputil.
                           Needs test signing active (after the restart).
      Elevated:
      RemoveDriver         stops SSAP3, removes the development driver, then
                           verifies. Add -RemoveCertificate and
                           -RestoreTestSigning to undo those too.
      DisableTestSigning   reverts SSAP3's own test-signing change only.

    Never changes Secure Boot, firmware, Memory Integrity, Defender or Code
    Integrity policy, and never touches certificates or driver packages that
    are not SSAP3's own development ones.

.EXAMPLE
    .\tools\dev\Ssap3DevSigning.ps1 CheckSystem
.EXAMPLE
    .\tools\dev\Ssap3DevSigning.ps1 SignDriver -Package "$env:USERPROFILE\Downloads\ssap3aap.cab"
.EXAMPLE
    .\tools\dev\Ssap3DevSigning.ps1 InstallDriver -DevelopmentMachine -Package "$env:USERPROFILE\Downloads\ssap3aap.cab"
.EXAMPLE
    .\tools\dev\Ssap3DevSigning.ps1 RemoveDriver -RemoveCertificate -RestoreTestSigning
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('CheckSystem', 'GenerateCertificate', 'CheckCertificate', 'InstallCertificate', 'SignDriver', 'VerifySignature',
        'RenewCertificate', 'RemoveCertificate', 'EnableTestSigning', 'DisableTestSigning', 'InstallDriver', 'RemoveDriver', 'Report', 'WatchTracking')]
    [string]$Command,
    [string]$Package,
    [string]$OutputFolder,
    [ValidateRange(1, 90)][int]$ValidityDays = 30,
    [string]$SignToolPath,
    [switch]$DevelopmentMachine,
    [switch]$RecoveryKeyReady,
    [switch]$DiscardKey,
    [switch]$TrustOnly,
    [switch]$RemoveCertificate,
    [switch]$RestoreTestSigning,
    [ValidateRange(1, 120)][int]$Seconds = 15,
    [string]$ReportPath
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Ssap3DevSigning.psm1') -Force -DisableNameChecking -WarningAction SilentlyContinue

function Write-Facts($Object) {
    foreach ($property in $Object.PSObject.Properties) { Write-Output ('  {0,-22} {1}' -f "$($property.Name):", $property.Value) }
}

function Stop-Blocked([string[]]$Blockers) {
    Write-Output 'Not done. Blocked by:'
    foreach ($blocker in $Blockers) { Write-Output "  - $blocker" }
    exit 1
}

try {
    switch ($Command) {
        'CheckSystem' {
            $readiness = Get-Ssap3DevReadiness
            Write-Output 'This PC (read-only check):'
            Write-Facts $readiness
            Write-Output ''
            $byBlocker = [ordered]@{}
            foreach ($action in 'EnableTestSigning', 'InstallCertificate', 'InstallDriver') {
                $blockers = @(Get-Ssap3DevBlockers -Readiness $readiness -Action $action -DevelopmentMachine:$DevelopmentMachine -RecoveryKeyReady:$RecoveryKeyReady)
                Write-Output $(if ($blockers.Count -eq 0) { "${action}: allowed" } else { "${action}: blocked" })
                foreach ($blocker in $blockers) { if (-not $byBlocker.Contains($blocker)) { $byBlocker[$blocker] = @() }; $byBlocker[$blocker] += $action }
            }
            if ($byBlocker.Count -gt 0) { Write-Output ''; Write-Output 'Blockers:' }
            foreach ($blocker in $byBlocker.Keys) { Write-Output "  - [$($byBlocker[$blocker] -join ', ')] $blocker" }
            if (-not $readiness.SignTool) { Write-Output "SignDriver: $(if ($readiness.SignToolProblem) { $readiness.SignToolProblem } else { 'signtool.exe not found (Windows SDK signing tools, or dotnet restore driver\WdkPackages.proj)' })" }
        }
        'GenerateCertificate' {
            $active = Get-Ssap3DevActiveCertificate
            if ($null -ne $active) { Write-Output 'Reusing the existing SSAP3 development certificate:'; Write-Facts (Get-Ssap3DevCertificateFacts $active); break }
            if ($PSCmdlet.ShouldProcess('Cert:\CurrentUser\My', "Create 'CN=SSAP3 Temporary Driver Signing' (non-exportable RSA 3072 key, valid $ValidityDays days)")) {
                $certificate = New-Ssap3DevCertificate -ValidityDays $ValidityDays
                Write-Output 'Created the SSAP3 development certificate (not trusted anywhere yet):'
                Write-Facts (Get-Ssap3DevCertificateFacts $certificate)
            }
        }
        'CheckCertificate' {
            $all = @(Find-Ssap3DevCertificates)
            if ($all.Count -eq 0) { Write-Output 'No SSAP3 development certificate on this PC.'; break }
            foreach ($certificate in Get-Ssap3DevCertificates 'CurrentUser' 'My') {
                Write-Output "Certificate $($certificate.Thumbprint):"
                Write-Facts (Get-Ssap3DevCertificateFacts $certificate)
                Write-Output ('  {0,-22} {1}' -f 'MachineTrust:', $(if (Test-Ssap3DevTrusted $certificate.Thumbprint) { 'LocalMachine Root + TrustedPublisher' } else { 'none' }))
                if ($certificate.NotAfter -lt (Get-Date).AddDays(7)) { Write-Output '  WARNING: expires within 7 days (or has expired). Run RenewCertificate.' }
            }
            Write-Output 'Found in stores:'
            $all | ForEach-Object { Write-Output "  $($_.Store): $($_.Thumbprint), expires $($_.NotAfter.ToString('yyyy-MM-dd'))" }
        }
        'InstallCertificate' {
            $blockers = @(Get-Ssap3DevBlockers -Readiness (Get-Ssap3DevReadiness) -Action InstallCertificate -DevelopmentMachine:$DevelopmentMachine)
            if ($blockers.Count -gt 0) { Stop-Blocked $blockers }
            if ($PSCmdlet.ShouldProcess('LocalMachine\Root and LocalMachine\TrustedPublisher', 'Trust the public SSAP3 development certificate on this development PC')) {
                $certificate = Install-Ssap3DevTrust -DevelopmentMachine:$DevelopmentMachine
                Write-Output "Trusted $($certificate.Thumbprint) in LocalMachine\Root and LocalMachine\TrustedPublisher (public certificate only)."
            }
        }
        'SignDriver' {
            if (-not $Package) { throw 'Pass -Package with the unsigned CI package (a folder with ssap3aap.sys/.inf/.cat, or ssap3aap.cab).' }
            if ($PSCmdlet.ShouldProcess($Package, 'Sign a copy with the SSAP3 development certificate')) {
                $result = Invoke-Ssap3DevSignDriver -Package $Package -OutputFolder $OutputFolder -SignToolPath $SignToolPath -DiscardKey:$DiscardKey
                Write-Output "Signed package: $($result.Folder)"
                foreach ($name in $result.Hashes.Keys) { Write-Output ('  {0,-14} SHA-256 {1}' -f $name, $result.Hashes[$name]) }
                Write-Output "  Cryptographically signed: $($result.CryptographicallySigned); locally trusted: $($result.LocallyTrusted); Microsoft-signed: $($result.MicrosoftSigned); SSAP3 Setup would install it: $($result.SetupAccepts)"
                if ($DiscardKey) { Write-Output '  The private key was deleted: this certificate cannot sign anything else.' }
            }
        }
        'VerifySignature' {
            $folder = if ($Package) { $Package } else { $state = Read-Ssap3DevState; if ($null -ne $state.lastSigned) { $state.lastSigned.folder } else { $null } }
            if (-not $folder) { throw 'Pass -Package with a signed package folder.' }
            $result = Test-Ssap3DevPackageSignature -Folder $folder
            foreach ($check in $result.Checks) { Write-Output ('{0}  {1}: {2}' -f $(if ($check.Ok) { 'PASS' } else { 'FAIL' }), $check.Check, $check.Detail) }
            Write-Output ''
            Write-Output "Signer: $($result.SignerSubject) ($($result.SignerThumbprint)); SSAP3 development certificate: $($result.SignerIsSsap3Development)"
            Write-Output "1. Cryptographically signed:          $($result.CryptographicallySigned)"
            Write-Output "2. Locally trusted on this PC:        $($result.LocallyTrusted)"
            Write-Output '3. Accepted under test-signing policy: shown by Report once the driver has loaded'
            Write-Output "4. Officially Microsoft-signed:       $($result.MicrosoftSigned) (catalog class $($result.CatalogClass); SSAP3 Setup would install it: $($result.SetupAccepts))"
            if (-not $result.CryptographicallySigned) { exit 1 }
        }
        'RenewCertificate' {
            if ($PSCmdlet.ShouldProcess('SSAP3 development certificate', "Replace with a new certificate valid $ValidityDays days; delete the old one and its key")) {
                $renewed = Update-Ssap3DevCertificate -ValidityDays $ValidityDays
                Write-Output "New certificate: $($renewed.Certificate.Thumbprint), valid until $($renewed.Certificate.NotAfter.ToString('yyyy-MM-dd'))$(if ($renewed.Trusted) { '; machine trust moved to it' })"
                $renewed.Removed | ForEach-Object { Write-Output "  $_" }
                Write-Output 'Sign the driver again (SignDriver) and reinstall it (InstallDriver): the installed driver still carries the old signature.'
            }
        }
        'RemoveCertificate' {
            if ($PSCmdlet.ShouldProcess('SSAP3 development certificates', $(if ($TrustOnly) { 'Remove machine trust only' } else { 'Remove certificates, private keys and machine trust' }))) {
                Remove-Ssap3DevCertificate -TrustOnly:$TrustOnly | ForEach-Object { Write-Output $_ }
            }
        }
        'EnableTestSigning' {
            $blockers = @(Get-Ssap3DevBlockers -Readiness (Get-Ssap3DevReadiness) -Action EnableTestSigning -DevelopmentMachine:$DevelopmentMachine -RecoveryKeyReady:$RecoveryKeyReady)
            if ($blockers.Count -gt 0) { Stop-Blocked $blockers }
            if ($PSCmdlet.ShouldProcess('Boot configuration {current}', 'bcdedit /set testsigning on (any test-signed driver can then load on this PC)')) {
                Write-Output (Enable-Ssap3TestSigning -DevelopmentMachine:$DevelopmentMachine -RecoveryKeyReady:$RecoveryKeyReady)
            }
        }
        'DisableTestSigning' {
            if ($PSCmdlet.ShouldProcess('Boot configuration {current}', 'Revert SSAP3''s test-signing change')) {
                Disable-Ssap3TestSigning | ForEach-Object { Write-Output $_ }
            }
        }
        'InstallDriver' {
            $blockers = @(Get-Ssap3DevBlockers -Readiness (Get-Ssap3DevReadiness) -Action InstallDriver -DevelopmentMachine:$DevelopmentMachine)
            if ($blockers.Count -gt 0) { Stop-Blocked $blockers }
            if ($PSCmdlet.ShouldProcess('This development PC', 'Trust the SSAP3 development certificate, sign and install the development driver')) {
                Install-Ssap3DevDriver -Package $Package -DevelopmentMachine:$DevelopmentMachine -ValidityDays $ValidityDays -SignToolPath $SignToolPath | ForEach-Object { Write-Output $_ }
                Write-Output ''
                Write-Output 'Next: connect the AirPods, start SSAP3 normally (not elevated), choose Settings > Head tracking > "AirPods motion sensors", then "Head tracked" mode,'
                Write-Output 'then run: .\tools\dev\Ssap3DevSigning.ps1 Report   and   .\tools\dev\Ssap3DevSigning.ps1 WatchTracking'
            }
        }
        'RemoveDriver' {
            if ($PSCmdlet.ShouldProcess('This development PC', "Remove the SSAP3 development driver$(if ($RemoveCertificate) { ', certificates and keys' })$(if ($RestoreTestSigning) { ' and SSAP3''s test-signing change' })")) {
                Uninstall-Ssap3DevDriver -RemoveCertificate:$RemoveCertificate -RestoreTestSigning:$RestoreTestSigning | ForEach-Object { Write-Output $_ }
            }
        }
        'Report' {
            $lines = Get-Ssap3DevReport -Folder $Package
            $lines | ForEach-Object { Write-Output $_ }
            if ($ReportPath) { $lines | Set-Content -LiteralPath $ReportPath -Encoding UTF8; Write-Output "Saved: $ReportPath" }
        }
        'WatchTracking' {
            Write-Output "Sampling SSAP3's head-pose record for $Seconds s. Turn your head left and right now."
            $summary = Watch-Ssap3HeadTracking -Seconds $Seconds
            Write-Facts $summary
            if (-not $summary.RealTracking) { exit 1 }
        }
    }
}
catch {
    Write-Output "Error: $($_.Exception.Message)"
    exit 1
}
