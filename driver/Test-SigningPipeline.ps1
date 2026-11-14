#Requires -Version 5.1
<#
.SYNOPSIS
    CI test of the driver signing pipeline against the real built package.

.DESCRIPTION
    For CI runners only (ephemeral machines): it creates throwaway
    certificates in the CURRENT USER's Personal store and removes them
    afterwards. It never touches a trusted-root store. Do not run it on a
    production PC.

    Checks:
      1. The verifier classifies the unsigned submission package as Unsigned,
         with the .sys and .inf proven to be the catalogued files.
      2. A copy whose .sys has one changed code byte fails the catalog check.
      3. A catalog signed by a self-signed certificate NAMED "Microsoft
         Windows Hardware Compatibility Publisher" is rejected (NotTrusted),
         so a spoofed name is never accepted.
      4. Sign-AttestationCab.ps1 rehearsal: a throwaway (non-EV) certificate
         signs a copy of the CAB; without -AllowNonEvCertificate it refuses.
      5. If the runner has any genuinely Microsoft-signed driver package,
         Setup's production rule accepts it (otherwise reported as skipped;
         the local test report covers this on real hardware).
#>
[CmdletBinding()]
param(
    # Default to artifacts\attestation (resolved below: Windows PowerShell 5.1
    # leaves $PSScriptRoot empty in parameter defaults).
    [string]$Stage,
    [string]$Cab
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
if (-not $Stage) { $Stage = Join-Path $repository 'artifacts\attestation\ssap3aap' }
if (-not $Cab) { $Cab = Join-Path $repository 'artifacts\attestation\ssap3aap.cab' }
Import-Module (Join-Path $repository 'installer\Ssap3Setup.psm1') -Force -DisableNameChecking -WarningAction SilentlyContinue
$verifier = Join-Path $repository 'installer\Test-Ssap3Driver.ps1'
$work = Join-Path ([IO.Path]::GetTempPath()) ("ssap3-signing-test-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$failures = New-Object System.Collections.Generic.List[string]
$created = New-Object System.Collections.Generic.List[string]

function Check([bool]$Condition, [string]$Message) {
    if ($Condition) { Write-Output "PASS  $Message" } else { Write-Output "FAIL  $Message"; $failures.Add($Message) }
}

# Runs a native command and captures stdout and stderr as text. Windows
# PowerShell 5.1 turns native stderr into terminating errors under 'Stop',
# and the refusals tested here are written to stderr on purpose.
function Invoke-Native([string]$FilePath, [string[]]$Arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $FilePath @Arguments 2>&1 | ForEach-Object { "$_" }
        return [pscustomobject]@{ Exit = $LASTEXITCODE; Lines = @($output) }
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

function Invoke-Verifier([string]$PackageFolder) {
    return Invoke-Native 'powershell.exe' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $verifier, '-Package', $PackageFolder)
}

function Find-Line($Run, [string]$Pattern) { return [bool]($Run.Lines | Where-Object { $_ -match $Pattern }) }

function Find-SignTool {
    $packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
    $found = @(@(
        Get-ChildItem -Path (Join-Path $packages 'microsoft.windows.sdk.cpp\*\c\bin\*\x64\signtool.exe') -ErrorAction SilentlyContinue
        Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue
    ) | Sort-Object FullName -Descending)
    if ($found.Count -eq 0) { throw 'signtool.exe not found.' }
    return $found[0].FullName
}

function New-ThrowawayCertificate([string]$Subject) {
    $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject -CertStoreLocation Cert:\CurrentUser\My `
        -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddDays(1)
    $created.Add($certificate.Thumbprint)
    return $certificate
}

try {
    foreach ($name in 'ssap3aap.sys', 'ssap3aap.inf', 'ssap3aap.cat') {
        if (-not (Test-Path -LiteralPath (Join-Path $Stage $name))) { throw "The staged package lacks $name ($Stage). Run driver\package-driver.ps1 -AttestationCab first." }
    }
    $signTool = Find-SignTool

    # 1. The real, unsigned submission package.
    $unsigned = Invoke-Verifier $Stage
    Check ($unsigned.Exit -eq 1) 'verifier fails an unsigned package'
    Check (Find-Line $unsigned '^FAIL\s+Package\s+Signature\s+Unsigned') 'unsigned catalog classified Unsigned'
    Check (Find-Line $unsigned '^PASS\s+Package\s+Catalog lists ssap3aap\.sys') '.sys is the catalogued file (Authenticode image hash)'
    Check (Find-Line $unsigned '^PASS\s+Package\s+Catalog lists ssap3aap\.inf') '.inf is the catalogued file'
    Check (Find-Line $unsigned '^PASS\s+Package\s+Architecture') 'x64 kernel image'
    Check (Find-Line $unsigned '^PASS\s+Package\s+HVCI image flags') 'NX, ASLR and CFG set'
    Check (Find-Line $unsigned '^PASS\s+Package\s+INF ') 'INF facts (provider, catalog, NTamd64)'
    Check (-not (Test-Ssap3DriverProductionSigned -CatalogPath (Join-Path $Stage 'ssap3aap.cat') -MemberPaths @((Join-Path $Stage 'ssap3aap.sys')))) 'Setup refuses the unsigned package'

    # 2. One changed byte in the code section.
    $tampered = Join-Path $work 'tampered'
    New-Item -ItemType Directory -Force -Path $tampered | Out-Null
    Copy-Item -Path (Join-Path $Stage 'ssap3aap.*') -Destination $tampered
    $sysPath = Join-Path $tampered 'ssap3aap.sys'
    $bytes = [IO.File]::ReadAllBytes($sysPath)
    $pe = [BitConverter]::ToInt32($bytes, 0x3C)
    $section = $pe + 24 + [BitConverter]::ToUInt16($bytes, $pe + 20)
    $target = [int]([BitConverter]::ToUInt32($bytes, $section + 20) + [Math]::Floor([BitConverter]::ToUInt32($bytes, $section + 16) / 2))
    $bytes[$target] = $bytes[$target] -bxor 0x01
    [IO.File]::WriteAllBytes($sysPath, $bytes)
    $changed = Invoke-Verifier $tampered
    Check (Find-Line $changed '^FAIL\s+Package\s+Catalog lists ssap3aap\.sys') 'a changed .sys is detected'

    # 3. Spoofed publisher name on a self-signed certificate.
    $spoof = New-ThrowawayCertificate 'CN=Microsoft Windows Hardware Compatibility Publisher'
    $spoofed = Join-Path $work 'spoofed'
    New-Item -ItemType Directory -Force -Path $spoofed | Out-Null
    Copy-Item -Path (Join-Path $Stage 'ssap3aap.*') -Destination $spoofed
    $spoofSign = Invoke-Native $signTool @('sign', '/fd', 'SHA256', '/sha1', $spoof.Thumbprint, (Join-Path $spoofed 'ssap3aap.cat'))
    Check ($spoofSign.Exit -eq 0 -or $spoofSign.Exit -eq 2) 'spoof catalog signed for the test'
    $spoofSignature = Get-Ssap3CatalogSignature -CatalogPath (Join-Path $spoofed 'ssap3aap.cat')
    Check ($spoofSignature.Kind -eq 'NotTrusted') "spoofed publisher name rejected (classified $($spoofSignature.Kind))"
    Check (-not (Test-Ssap3DriverProductionSigned -CatalogPath (Join-Path $spoofed 'ssap3aap.cat') -MemberPaths @((Join-Path $spoofed 'ssap3aap.sys')))) 'Setup refuses the spoofed package'

    # 4. EV signing helper rehearsal.
    $rehearsal = New-ThrowawayCertificate 'CN=SSAP3 CI signing rehearsal (not trusted)'
    $cabCopy = Join-Path $work 'ssap3aap.cab'
    Copy-Item -LiteralPath $Cab -Destination $cabCopy
    Copy-Item -LiteralPath "$Cab.sha256" -Destination "$cabCopy.sha256"
    $helper = Join-Path $PSScriptRoot 'Sign-AttestationCab.ps1'
    $refused = Invoke-Native 'powershell.exe' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $helper, '-CabPath', $cabCopy,
        '-CertificateThumbprint', $rehearsal.Thumbprint, '-TimestampUrl', 'http://timestamp.invalid', '-SignToolPath', $signTool)
    Check ($refused.Exit -ne 0 -and ($refused.Lines -join ' ') -match 'not an EV code-signing certificate') 'signing helper refuses a non-EV certificate'
    Check ((Get-AuthenticodeSignature -LiteralPath $cabCopy).Status -eq 'NotSigned') 'refused run left the CAB unsigned'
    $signedRun = Invoke-Native 'powershell.exe' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $helper, '-CabPath', $cabCopy,
        '-CertificateThumbprint', $rehearsal.Thumbprint, '-SignToolPath', $signTool, '-AllowNonEvCertificate', '-SkipTimestamp')
    $signedRun.Lines | ForEach-Object { Write-Output "      $_" }
    $cabSignature = Get-AuthenticodeSignature -LiteralPath $cabCopy
    Check ($signedRun.Exit -eq 0 -and $null -ne $cabSignature.SignerCertificate -and $cabSignature.SignerCertificate.Thumbprint -eq $rehearsal.Thumbprint) 'signing helper signs the CAB with the chosen certificate'

    # 5. A genuinely Microsoft-signed package on this runner, if any.
    $production = $null
    foreach ($folder in Get-ChildItem "$env:SystemRoot\System32\DriverStore\FileRepository" -Directory -ErrorAction SilentlyContinue | Select-Object -First 300) {
        $cat = Get-ChildItem -LiteralPath $folder.FullName -Filter *.cat -ErrorAction SilentlyContinue | Select-Object -First 1
        $sys = Get-ChildItem -LiteralPath $folder.FullName -Filter *.sys -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $cat -or -not $sys) { continue }
        $kind = (Get-Ssap3CatalogSignature -CatalogPath $cat.FullName).Kind
        if ($kind -in 'MicrosoftAttestation', 'MicrosoftWhql') { $production = [pscustomobject]@{ Cat = $cat.FullName; Sys = $sys.FullName; Kind = $kind; Name = $folder.Name }; break }
    }
    if ($production) {
        Check (Test-Ssap3DriverProductionSigned -CatalogPath $production.Cat -MemberPaths @($production.Sys)) "a real $($production.Kind) package on this runner is accepted ($($production.Name))"
    }
    else {
        Write-Output 'SKIP  no Microsoft-signed third-party driver package on this runner (covered by the local test report)'
    }
}
finally {
    foreach ($thumbprint in $created) {
        Remove-Item -LiteralPath "Cert:\CurrentUser\My\$thumbprint" -DeleteKey -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    Write-Output "$($failures.Count) signing-pipeline check(s) failed."
    exit 1
}
Write-Output 'Signing pipeline checks passed.'
