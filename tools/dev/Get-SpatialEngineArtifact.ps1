#Requires -Version 5.1
<#
.SYNOPSIS
    Verifies a downloaded CI artifact zip of the spatial engine and unpacks
    SSAP3Spatializer.dll to audio\bin, where build-installer.ps1 picks it up.

.DESCRIPTION
    For PCs without the MSVC toolchain. The zip is the "ssap3-spatial-engine-unsigned"
    artifact of a CI run, downloaded while signed in to GitHub (artifact
    downloads need a session; their metadata does not).

    Checks, all of which must pass:
      1. The zip's SHA-256 equals the digest GitHub recorded when CI uploaded it
         (read from the public REST API over HTTPS), or -ExpectedDigest.
      2. The zip holds exactly one entry, SSAP3Spatializer.dll, under 4 MiB.
      3. The DLL is a PE32+ x64 DLL.
    Only that one entry is extracted, to a fixed path (no zip-slip).

    The DLL is unsigned (SECURITY_REPORT.md S-21): its provenance is the CI run
    and commit printed below.

.EXAMPLE
    .\tools\dev\Get-SpatialEngineArtifact.ps1 -ZipPath "$env:USERPROFILE\Downloads\ssap3-spatial-engine-unsigned.zip" -ArtifactId 14082056417
#>
[CmdletBinding(DefaultParameterSetName = 'Api')]
param(
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [Parameter(ParameterSetName = 'Api', Mandatory = $true)][long]$ArtifactId,
    [Parameter(ParameterSetName = 'Api')][string]$Repository = 'Apollo-1955/SSAP3',
    [Parameter(ParameterSetName = 'Digest', Mandatory = $true)][ValidatePattern('^(sha256:)?[0-9a-fA-F]{64}$')][string]$ExpectedDigest
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$target = Join-Path $repositoryRoot 'audio\bin\SSAP3Spatializer.dll'
$ZipPath = (Resolve-Path -LiteralPath $ZipPath).ProviderPath

if ($PSCmdlet.ParameterSetName -eq 'Api') {
    if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw "Invalid repository name: $Repository" }
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $headers = @{ 'User-Agent' = 'ssap3-dev-tools'; 'Accept' = 'application/vnd.github+json' }
    $artifact = Invoke-RestMethod -Headers $headers -Uri "https://api.github.com/repos/$Repository/actions/artifacts/$ArtifactId"
    if ($artifact.name -ne 'ssap3-spatial-engine-unsigned') { throw "Artifact $ArtifactId is '$($artifact.name)', not the spatial engine." }
    if ($artifact.expired) { throw "Artifact $ArtifactId has expired." }
    if (-not $artifact.digest) { throw "GitHub recorded no digest for artifact $ArtifactId; pass -ExpectedDigest instead." }
    $ExpectedDigest = $artifact.digest
    Write-Output ("Artifact {0}: run {1}, commit {2} ({3}), created {4}" -f $ArtifactId, $artifact.workflow_run.id,
        $artifact.workflow_run.head_sha, $artifact.workflow_run.head_branch, $artifact.created_at)
}

$expected = ($ExpectedDigest -replace '^sha256:', '').ToUpperInvariant()
$actual = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash
if ($actual -ne $expected) { throw "Zip SHA-256 $actual does not match the recorded digest $expected. Do not use this file." }
Write-Output "Zip SHA-256 matches the recorded digest: $actual"

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($ZipPath)
try {
    $entries = @($zip.Entries)
    if ($entries.Count -ne 1 -or $entries[0].FullName -ne 'SSAP3Spatializer.dll') {
        throw "Unexpected zip contents: $(($entries | ForEach-Object FullName) -join ', ')"
    }
    if ($entries[0].Length -le 0 -or $entries[0].Length -gt 4MB) { throw "Unexpected DLL size: $($entries[0].Length) bytes." }
    $bytes = New-Object byte[] $entries[0].Length
    $stream = $entries[0].Open()
    try {
        $read = 0
        while ($read -lt $bytes.Length) {
            $n = $stream.Read($bytes, $read, $bytes.Length - $read)
            if ($n -le 0) { throw 'Truncated zip entry.' }
            $read += $n
        }
    }
    finally { $stream.Dispose() }
}
finally { $zip.Dispose() }

# PE checks: MZ header, PE signature, x64 machine, DLL characteristic, PE32+ optional header.
if ($bytes.Length -lt 0x200 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) { throw 'Not a PE file.' }
$pe = [BitConverter]::ToInt32($bytes, 0x3C)
if ($pe -lt 0x40 -or $pe -gt $bytes.Length - 0x40 -or [BitConverter]::ToUInt32($bytes, $pe) -ne 0x00004550) { throw 'Invalid PE header.' }
if ([BitConverter]::ToUInt16($bytes, $pe + 4) -ne 0x8664) { throw 'The DLL is not x64.' }
if (([BitConverter]::ToUInt16($bytes, $pe + 22) -band 0x2000) -eq 0) { throw 'The file is not a DLL.' }
if ([BitConverter]::ToUInt16($bytes, $pe + 24) -ne 0x20B) { throw 'The DLL is not PE32+.' }

New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
[IO.File]::WriteAllBytes($target, $bytes)
Write-Output ("Unpacked {0} ({1} bytes, SHA-256 {2})." -f $target, $bytes.Length, (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash)
Write-Output 'Next: .\installer\build-installer.ps1 -StageOnly'
