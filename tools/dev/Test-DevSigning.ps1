#Requires -Version 5.1
<#
.SYNOPSIS
    CI rehearsal of the SSAP3 temporary driver-signing lifecycle on the real
    built driver package.

.DESCRIPTION
    For disposable CI runners only. Do not run it on a PC you use: on an
    elevated runner it trusts a throwaway development certificate in
    LocalMachine\Root and \TrustedPublisher and stages the test-signed driver
    package in the driver store (it is never loaded: the runner is not in test
    mode), then removes all of it and checks that the certificate stores match
    their state before the test.

    Covers: the test-signing gates (Secure Boot, elevation, development-machine
    confirmation, BitLocker, test mode), the certificate profile, signing the
    CI package (folder and CAB), the four signature statuses, tampering and
    look-alike certificates, machine trust, the driver store, renewal, removal
    (including a tampered state file), and the head-tracking verdicts.
#>
[CmdletBinding()]
param(
    [string]$Stage,
    [string]$Cab
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
$repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $Stage) { $Stage = Join-Path $repository 'artifacts\attestation\ssap3aap' }
if (-not $Cab) { $Cab = Join-Path $repository 'artifacts\attestation\ssap3aap.cab' }
Import-Module (Join-Path $repository 'installer\Ssap3Setup.psm1') -Force -DisableNameChecking -WarningAction SilentlyContinue
Import-Module (Join-Path $PSScriptRoot 'Ssap3DevSigning.psm1') -Force -DisableNameChecking -WarningAction SilentlyContinue

$work = Join-Path ([IO.Path]::GetTempPath()) ('ssap3-devsign-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
Set-Ssap3DevStateRoot (Join-Path $work 'state')
$failures = New-Object System.Collections.Generic.List[string]
$decoys = New-Object System.Collections.Generic.List[string]

function Check([bool]$Condition, [string]$Message) {
    if ($Condition) { Write-Output "PASS  $Message" } else { Write-Output "FAIL  $Message"; $failures.Add($Message) }
}

function Get-Snapshot {
    return [pscustomobject]@{
        Root = @(Get-Ssap3StoreThumbprints 'LocalMachine' 'Root' | Sort-Object)
        TrustedPublisher = @(Get-Ssap3StoreThumbprints 'LocalMachine' 'TrustedPublisher' | Sort-Object)
        My = @(Get-Ssap3StoreThumbprints 'CurrentUser' 'My' | Sort-Object)
        Packages = @(Get-Ssap3DevStorePackages | ForEach-Object { $_.Published } | Sort-Object)
    }
}

function Copy-Package([string]$From, [string]$Name) {
    $to = Join-Path $work $Name
    New-Item -ItemType Directory -Force -Path $to | Out-Null
    foreach ($file in 'ssap3aap.sys', 'ssap3aap.inf', 'ssap3aap.cat', 'signing-record.json') {
        if (Test-Path -LiteralPath (Join-Path $From $file)) { Copy-Item -LiteralPath (Join-Path $From $file) -Destination $to }
    }
    return $to
}

function Get-FailedCheck($Result, [string]$Name) {
    return [bool]($Result.Checks | Where-Object { $_.Check -eq $Name -and -not $_.Ok })
}

$elevated = Test-Ssap3DevElevated
$before = Get-Snapshot
try {
    foreach ($name in 'ssap3aap.sys', 'ssap3aap.inf', 'ssap3aap.cat') {
        if (-not (Test-Path -LiteralPath (Join-Path $Stage $name))) { throw "The staged package lacks $name ($Stage). Run driver\package-driver.ps1 -AttestationCab first." }
    }
    $signTool = Find-Ssap3SignTool
    if (-not $signTool) { throw 'signtool.exe not found on the runner.' }

    # ------------------------------------------------------------ 1. gates
    $base = [pscustomobject]@{ Elevated = $true; SecureBoot = 'Off'; TestSigningActive = $false; IntegrityChecksDisabled = $false; TestSigningConfigured = 'Absent'; MemoryIntegrity = 'Running'; Defender = 'On'; BitLocker = 'Off or suspended'; SignTool = $signTool; SignToolProblem = $null }
    function With([hashtable]$Changes) { $copy = $base.PSObject.Copy(); foreach ($key in $Changes.Keys) { $copy.$key = $Changes[$key] }; return $copy }
    $blocked = @(Get-Ssap3DevBlockers -Readiness (With @{ SecureBoot = 'On' }) -Action EnableTestSigning -DevelopmentMachine -RecoveryKeyReady)
    Check ($blocked.Count -eq 1 -and $blocked[0] -match 'Secure Boot is on' -and $blocked[0] -match 'does not change firmware') 'Secure Boot on: test signing refused, with the exact blocker and no workaround'
    Check ([bool](@(Get-Ssap3DevBlockers -Readiness (With @{ SecureBoot = 'On' }) -Action InstallCertificate -DevelopmentMachine) | Where-Object { $_ -match 'Secure Boot is on' })) 'Secure Boot on: machine trust refused'
    Check (@(Get-Ssap3DevBlockers -Readiness (With @{}) -Action EnableTestSigning -DevelopmentMachine -RecoveryKeyReady).Count -eq 0) 'development PC, Secure Boot off, Memory Integrity running: allowed (Memory Integrity stays on)'
    Check ([bool](@(Get-Ssap3DevBlockers -Readiness (With @{}) -Action EnableTestSigning -RecoveryKeyReady) | Where-Object { $_ -match 'DevelopmentMachine' })) 'without -DevelopmentMachine: refused'
    Check ([bool](@(Get-Ssap3DevBlockers -Readiness (With @{ Elevated = $false }) -Action EnableTestSigning -DevelopmentMachine -RecoveryKeyReady) | Where-Object { $_ -match 'elevated' })) 'not elevated: refused'
    Check ([bool](@(Get-Ssap3DevBlockers -Readiness (With @{ BitLocker = 'On' }) -Action EnableTestSigning -DevelopmentMachine) | Where-Object { $_ -match 'recovery key' })) 'BitLocker on without -RecoveryKeyReady: refused'
    Check ([bool](@(Get-Ssap3DevBlockers -Readiness (With @{ IntegrityChecksDisabled = $true }) -Action EnableTestSigning -DevelopmentMachine -RecoveryKeyReady) | Where-Object { $_ -match 'NOINTEGRITYCHECKS' })) 'NOINTEGRITYCHECKS present: refused'
    Check ([bool](@(Get-Ssap3DevBlockers -Readiness (With @{}) -Action InstallDriver -DevelopmentMachine) | Where-Object { $_ -match 'not active' })) 'driver install refused outside test mode'
    Check (@(Get-Ssap3DevBlockers -Readiness (With @{ TestSigningActive = $true }) -Action InstallDriver -DevelopmentMachine).Count -eq 0) 'driver install allowed in test mode on a development PC'

    $ready = Get-Ssap3DevReadiness
    Write-Output "INFO  runner: elevated $($ready.Elevated), Secure Boot $($ready.SecureBoot), test signing active $($ready.TestSigningActive), configured $($ready.TestSigningConfigured), Memory Integrity $($ready.MemoryIntegrity)"
    $refusal = $null
    try { Install-Ssap3DevDriver -Package $Stage -DevelopmentMachine | Out-Null } catch { $refusal = $_.Exception.Message }
    Check ($null -ne $refusal -and $refusal -match 'not active|Secure Boot') 'the real InstallDriver refuses on a PC that is not in test mode'
    Check (@(Find-Ssap3DevCertificates).Count -eq 0) '... before creating any certificate'

    # ------------------------------------------------------ 2. certificate
    $certificate = New-Ssap3DevCertificate -ValidityDays 30
    $facts = Get-Ssap3DevCertificateFacts $certificate
    Check ($facts.Subject -eq 'CN=SSAP3 Temporary Driver Signing' -and $facts.IsSsap3Development) 'certificate CN=SSAP3 Temporary Driver Signing with the SSAP3 marker'
    Check ($certificate.SignatureAlgorithm.Value -eq '1.2.840.113549.1.1.11') 'SHA-256 with RSA'
    Check ($facts.PublicKey -eq 'RSA 3072') 'RSA 3072 key'
    Check ($facts.KeyExportable -eq $false) 'private key marked non-exportable'
    $exported = $true
    try { [void]$certificate.Export('Pfx', 'ssap3-test') } catch { $exported = $false }
    Check (-not $exported) 'PFX export of the private key is refused'
    Check ($facts.Ekus -eq '1.3.6.1.5.5.7.3.3' -and -not $facts.IsCa) 'code signing only, not a CA'
    $days = ($certificate.NotAfter - (Get-Date)).TotalDays
    Check ($days -gt 29 -and $days -le 30.01) "valid for 30 days ($([Math]::Round($days, 2)))"
    Check ((New-Ssap3DevCertificate).Thumbprint -eq $certificate.Thumbprint) 'an existing certificate is reused, not duplicated'
    $tooLong = $false
    try { New-Ssap3DevCertificate -ValidityDays 91 -Force | Out-Null } catch { $tooLong = $true }
    Check $tooLong 'validity over 90 days refused'

    # ---------------------------------------------------------- 3. signing
    $signedDir = Join-Path $work 'signed'
    $signed = Invoke-Ssap3DevSignDriver -Package $Stage -OutputFolder $signedDir
    Check $signed.CryptographicallySigned '1. cryptographically signed (signature math, image hash, catalog members)'
    Check ($signed.SignerThumbprint -eq $certificate.Thumbprint -and $signed.SignerIsSsap3Development) 'signed by the SSAP3 development certificate'
    $embedded = Get-AuthenticodeSignature -LiteralPath (Join-Path $signedDir 'ssap3aap.sys')
    Check ($null -ne $embedded.SignerCertificate -and $embedded.SignerCertificate.Thumbprint -eq $certificate.Thumbprint) '.sys carries an embedded signature (required with Memory Integrity)'
    Check (Test-Ssap3CatalogMember -CatalogPath (Join-Path $signedDir 'ssap3aap.cat') -FilePath (Join-Path $signedDir 'ssap3aap.sys')) 'the catalog still lists the signed .sys (the image hash excludes the signature)'
    Check ($signed.Hashes['ssap3aap.inf'] -eq (Get-FileHash -LiteralPath (Join-Path $Stage 'ssap3aap.inf') -Algorithm SHA256).Hash) 'the INF is unchanged by signing'
    Check ($signed.UnchangedSinceSigning -eq $true) 'signing record matches the files'
    Check (-not $signed.LocallyTrusted) '2. not locally trusted before the certificate is trusted'
    Check (-not $signed.MicrosoftSigned -and $signed.CatalogClass -eq 'NotTrusted') "4. not Microsoft-signed (catalog class $($signed.CatalogClass))"
    Check (-not $signed.SetupAccepts) 'SSAP3 Setup refuses the development package'
    Write-Output "INFO  signed .sys SHA-256 $($signed.Hashes['ssap3aap.sys']); .cat SHA-256 $($signed.Hashes['ssap3aap.cat'])"

    $cabSigned = Invoke-Ssap3DevSignDriver -Package $Cab -OutputFolder (Join-Path $work 'signed-cab')
    Check $cabSigned.CryptographicallySigned 'the CI CAB is signed directly (after its .sha256 check)'
    $badCab = Join-Path $work 'bad.cab'
    Copy-Item -LiteralPath $Cab -Destination $badCab
    ('0' * 64 + ' *ssap3aap.cab') | Set-Content -LiteralPath "$badCab.sha256" -Encoding ASCII
    $mismatch = $null
    try { Invoke-Ssap3DevSignDriver -Package $badCab -OutputFolder (Join-Path $work 'bad') | Out-Null } catch { $mismatch = $_.Exception.Message }
    Check ($null -ne $mismatch -and $mismatch -match 'does not match') 'a CAB that does not match its .sha256 is refused'
    $resign = $null
    try { Invoke-Ssap3DevSignDriver -Package $signedDir -OutputFolder (Join-Path $work 'resign') | Out-Null } catch { $resign = $_.Exception.Message }
    Check ($null -ne $resign -and $resign -match 'already signed') 'an already-signed package is never re-signed'
    $occupied = Join-Path $work 'occupied'
    New-Item -ItemType Directory -Force -Path $occupied | Out-Null
    Set-Content -LiteralPath (Join-Path $occupied 'notes.txt') -Value 'unrelated'
    $refusedOutput = $null
    try { Invoke-Ssap3DevSignDriver -Package $Stage -OutputFolder $occupied | Out-Null } catch { $refusedOutput = $_.Exception.Message }
    Check ($null -ne $refusedOutput -and (Test-Path -LiteralPath (Join-Path $occupied 'notes.txt'))) 'an output folder with unrelated files is refused and left untouched'

    # --------------------------------------------------------- 4. tampering
    $code = Copy-Package $signedDir 'tampered-code'
    $sysPath = Join-Path $code 'ssap3aap.sys'
    $bytes = [IO.File]::ReadAllBytes($sysPath)
    $pe = [BitConverter]::ToInt32($bytes, 0x3C)
    $section = $pe + 24 + [BitConverter]::ToUInt16($bytes, $pe + 20)
    $target = [int]([BitConverter]::ToUInt32($bytes, $section + 20) + [Math]::Floor([BitConverter]::ToUInt32($bytes, $section + 16) / 2))
    $bytes[$target] = $bytes[$target] -bxor 0x01
    [IO.File]::WriteAllBytes($sysPath, $bytes)
    $codeResult = Test-Ssap3DevPackageSignature -Folder $code
    Check (-not $codeResult.CryptographicallySigned -and (Get-FailedCheck $codeResult '.sys embedded signature') -and (Get-FailedCheck $codeResult 'Catalog lists ssap3aap.sys')) 'one changed code byte breaks the .sys signature and the catalog match'
    Check ($codeResult.UnchangedSinceSigning -eq $false) '... and the signing record'
    $infCopy = Copy-Package $signedDir 'tampered-inf'
    Add-Content -LiteralPath (Join-Path $infCopy 'ssap3aap.inf') -Value '; changed'
    $infResult = Test-Ssap3DevPackageSignature -Folder $infCopy
    Check (-not $infResult.CryptographicallySigned -and (Get-FailedCheck $infResult 'Catalog lists ssap3aap.inf')) 'a changed INF is detected'

    $decoy = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=SSAP3 Temporary Driver Signing' -CertStoreLocation 'Cert:\CurrentUser\My' -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddDays(1)
    $decoys.Add($decoy.Thumbprint)
    Check (-not (Test-Ssap3DevCertificate $decoy)) 'a look-alike certificate (same subject, no marker) is not an SSAP3 development certificate'
    $lookalike = Join-Path $work 'lookalike'
    New-Item -ItemType Directory -Force -Path $lookalike | Out-Null
    foreach ($file in 'ssap3aap.sys', 'ssap3aap.inf', 'ssap3aap.cat') { Copy-Item -LiteralPath (Join-Path $Stage $file) -Destination $lookalike }
    foreach ($file in 'ssap3aap.sys', 'ssap3aap.cat') {
        $run = Invoke-Ssap3DevNative $signTool @('sign', '/fd', 'SHA256', '/sha1', $decoy.Thumbprint, (Join-Path $lookalike $file))
        if ($run.Exit -ne 0 -and $run.Exit -ne 2) { throw "look-alike signing failed: $($run.Lines -join ' ')" }
    }
    $lookalikeResult = Test-Ssap3DevPackageSignature -Folder $lookalike
    Check ($lookalikeResult.CryptographicallySigned -and -not $lookalikeResult.SignerIsSsap3Development) 'a package signed by the look-alike is recognised as not SSAP3''s'

    # ------------------------------------------- 5. machine trust and store
    if ($elevated) {
        Add-Ssap3DevTrust -Certificate $certificate
        Check (Test-Ssap3DevTrusted $certificate.Thumbprint) 'public certificate trusted in LocalMachine\Root and \TrustedPublisher'
        $root = Open-Ssap3Store 'LocalMachine' 'Root'
        try { $copy = @($root.Certificates.Find([System.Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint, $certificate.Thumbprint, $false))[0] } finally { $root.Close() }
        Check (-not $copy.HasPrivateKey) 'the machine store holds the public certificate only'
        $trusted = Test-Ssap3DevPackageSignature -Folder $signedDir
        Check $trusted.LocallyTrusted '2. locally trusted once the certificate is trusted'
        Check (-not $trusted.MicrosoftSigned -and $trusted.CatalogClass -eq 'NotMicrosoft') "4. still not Microsoft-signed (catalog class $($trusted.CatalogClass))"
        Check (-not $trusted.SetupAccepts) 'SSAP3 Setup still refuses it'
        $verifier = Invoke-Ssap3DevNative 'powershell.exe' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $repository 'installer\Test-Ssap3Driver.ps1'), '-Package', $signedDir)
        Check ([bool]($verifier.Lines | Where-Object { $_ -match '^FAIL\s+Package\s+Signature\s+NotMicrosoft' })) 'the production health check reports it as NotMicrosoft'

        $stage = New-Ssap3AdminOnlyStage
        try { Check (@(Get-Ssap3UntrustedWriters -Path $stage).Count -eq 0 -and (Test-Ssap3TrustedOwner -Path $stage)) 'the install staging folder is administrators-only' }
        finally { Remove-Item -LiteralPath $stage -Recurse -Force }

        $packagesBefore = @(Get-Ssap3DevStorePackages | ForEach-Object { $_.Published })
        $add = Invoke-Ssap3DevNative (Join-Path $env:SystemRoot 'System32\pnputil.exe') @('/add-driver', (Join-Path $signedDir 'ssap3aap.inf'))
        $staged = @(Get-Ssap3DevStorePackages | Where-Object { $_.Published -notin $packagesBefore })
        Write-Output "INFO  pnputil /add-driver without test mode: exit $($add.Exit); staged: $(if ($staged.Count) { ($staged | ForEach-Object { "$($_.Published) signer '$($_.Signer)'" }) -join ', ' } else { 'nothing' }); $(($add.Lines | Select-Object -Last 2) -join ' ')"
        if ($staged.Count -gt 0) {
            Check ($staged[0].Signer -eq 'SSAP3 Temporary Driver Signing') 'the driver store records the development signer'
            $removed = Remove-Ssap3DevStorePackage -Published $staged[0].Published
            Write-Output "INFO  $removed"
            Check (-not (Get-Ssap3DevStorePackages | Where-Object { $_.Published -eq $staged[0].Published })) 'the development package is removed from the driver store'
        }
        else {
            Write-Output 'SKIP  the runner did not stage the package (store checks need a development PC)'
        }

        $old = $certificate.Thumbprint
        $renewed = Update-Ssap3DevCertificate -ValidityDays 7
        $certificate = $renewed.Certificate
        Check ($certificate.Thumbprint -ne $old -and (Test-Ssap3DevTrusted $certificate.Thumbprint) -and -not (Test-Ssap3DevTrusted $old) -and -not (Test-Path "Cert:\CurrentUser\My\$old")) 'renewal: new certificate trusted; old certificate, key and trust removed'
        Check ([Math]::Round(($certificate.NotAfter - (Get-Date)).TotalDays) -eq 7) 'renewed certificate valid for the requested 7 days'
    }
    else {
        Write-Output 'SKIP  machine trust, driver store and renewal with trust need an elevated runner'
    }

    # ---------------------------------------------------------- 6. removal
    $state = Read-Ssap3DevState
    $state.certificates = @($state.certificates) + [pscustomobject]@{ thumbprint = $decoy.Thumbprint; created = (Get-Date).ToString('o'); notAfter = $decoy.NotAfter.ToString('o') }
    Save-Ssap3DevState $state
    $removal = @(Remove-Ssap3DevCertificate)
    $removal | ForEach-Object { Write-Output "INFO  $_" }
    Check (Test-Path "Cert:\CurrentUser\My\$($decoy.Thumbprint)") 'a look-alike named in a tampered state file is left alone'
    Check (@(Find-Ssap3DevCertificates).Count -eq 0) 'every SSAP3 development certificate is removed (user store and machine trust)'
    Check ([bool]($removal | Where-Object { $_ -match 'key container verified gone' })) 'the private key container is verified gone'

    $ephemeral = New-Ssap3DevCertificate -ValidityDays 1
    $discarded = Invoke-Ssap3DevSignDriver -Package $Stage -OutputFolder (Join-Path $work 'discarded') -DiscardKey
    Check ($discarded.CryptographicallySigned -and $null -eq (Get-Ssap3DevActiveCertificate) -and -not (Test-Path "Cert:\CurrentUser\My\$($ephemeral.Thumbprint)")) 'with -DiscardKey the package is signed and the key is deleted'

    # --------------------------------------------------- 7. tracking verdicts
    $sweep = @(for ($i = 0; $i -lt 40; $i++) { [pscustomobject]@{ Mode = 2; Tracking = 1; Norm = 1.0; Yaw = -40 + 2 * $i; Pitch = 2.0; Roll = -1.0; AgeMs = 20 } })
    Check (Get-Ssap3TrackingSummary -Samples $sweep -MotionSource 'AirPodsSensors').RealTracking 'a live, fresh yaw sweep from the AirPods sensors counts as real tracking'
    $simulated = Get-Ssap3TrackingSummary -Samples $sweep -MotionSource 'Simulator'
    Check (-not $simulated.RealTracking -and $simulated.Verdict -match 'SIMULATED') 'the same movement from the simulator is labelled simulated'
    $fixed = @($sweep | ForEach-Object { $copy = $_.PSObject.Copy(); $copy.Mode = 1; $copy })
    Check (-not (Get-Ssap3TrackingSummary -Samples $fixed -MotionSource 'AirPodsSensors').RealTracking) 'Fixed mode is not reported as tracking'
    $stale = @($sweep | ForEach-Object { $copy = $_.PSObject.Copy(); $copy.AgeMs = 2000; $copy })
    Check (-not (Get-Ssap3TrackingSummary -Samples $stale -MotionSource 'AirPodsSensors').RealTracking) 'stale samples are not reported as tracking'
    $still = @($sweep | ForEach-Object { $copy = $_.PSObject.Copy(); $copy.Yaw = 3.0; $copy })
    Check (-not (Get-Ssap3TrackingSummary -Samples $still -MotionSource 'AirPodsSensors').RealTracking) 'live data without head movement is not reported as following the head'
    $denormal = @($sweep | ForEach-Object { $copy = $_.PSObject.Copy(); $copy.Norm = 1.2; $copy })
    Check (-not (Get-Ssap3TrackingSummary -Samples $denormal -MotionSource 'AirPodsSensors').RealTracking) 'a non-normalised quaternion is rejected'
    $none = Watch-Ssap3HeadTracking -Seconds 1 -RecordPath (Join-Path $work 'no-record.bin')
    Check ($none.Samples -eq 0 -and -not $none.RealTracking) 'no head-pose record: no tracking claimed'
}
finally {
    try { Remove-Ssap3DevCertificate | Out-Null } catch { Write-Output "WARN  cleanup: $($_.Exception.Message)" }
    foreach ($thumbprint in $decoys) { Remove-Item -LiteralPath "Cert:\CurrentUser\My\$thumbprint" -DeleteKey -Force -ErrorAction SilentlyContinue }
    if ($elevated) {
        foreach ($package in @(Get-Ssap3DevStorePackages | Where-Object { $_.Signer -eq 'SSAP3 Temporary Driver Signing' })) {
            try { Remove-Ssap3DevStorePackage -Published $package.Published | Out-Null } catch { Write-Output "WARN  cleanup: $($_.Exception.Message)" }
        }
    }
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

$after = Get-Snapshot
foreach ($name in 'Root', 'TrustedPublisher', 'My', 'Packages') {
    $then = @($before.$name)
    $now = @($after.$name)
    $difference = @($then | Where-Object { $_ -notin $now }) + @($now | Where-Object { $_ -notin $then })
    Check ($difference.Count -eq 0) "after cleanup, $(@{ Root = 'LocalMachine\Root'; TrustedPublisher = 'LocalMachine\TrustedPublisher'; My = 'CurrentUser\My'; Packages = 'the SSAP3 driver-store packages' }[$name]) matches the state before the test"
}

if ($failures.Count -gt 0) {
    Write-Output "$($failures.Count) development-signing check(s) failed."
    exit 1
}
Write-Output 'Development-signing checks passed.'
