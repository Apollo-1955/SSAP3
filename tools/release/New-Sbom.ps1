#Requires -Version 5.1
<#
.SYNOPSIS
    Writes a CycloneDX 1.5 JSON SBOM for an SSAP3 build.

.DESCRIPTION
    Components come from:
      * NuGet packages resolved for every shipped project (obj\project.assets.json),
        with test-only projects excluded;
      * every file in the package folder (SHA-256), when -PackageRoot is given;
      * the pinned third-party binaries documented in THIRD_PARTY.md.
#>
[CmdletBinding()]
param(
    [string]$PackageRoot,
    [string]$Version = '0.5.0-dev',
    [Parameter(Mandatory = $true)][string]$Output
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$components = New-Object System.Collections.Generic.List[object]
$seen = @{}

foreach ($project in 'SSAP3.App', 'SSAP3.Core', 'SSAP3.Platform.Windows', 'SSAP3.Diagnostics') {
    $assets = Join-Path $repositoryRoot "src\$project\obj\project.assets.json"
    if (-not (Test-Path -LiteralPath $assets)) { Write-Warning "Restore $project first ($assets missing)."; continue }
    $json = Get-Content -LiteralPath $assets -Raw | ConvertFrom-Json
    # Microsoft runtime/targeting packs (bundled by self-contained publish).
    foreach ($framework in $json.project.frameworks.PSObject.Properties) {
        foreach ($dependency in @($framework.Value.PSObject.Properties['downloadDependencies'].Value)) {
            if ($null -eq $dependency) { continue }
            $depVersion = ([string]$dependency.version).Trim('[', ']', ' ').Split(',')[0]
            $purl = "pkg:nuget/$($dependency.name)@$depVersion"
            if ($seen.ContainsKey($purl)) { continue }
            $seen[$purl] = $true
            $components.Add([ordered]@{ type = 'framework'; name = $dependency.name; version = $depVersion; purl = $purl; supplier = @{ name = 'Microsoft' } })
        }
    }
    foreach ($library in $json.libraries.PSObject.Properties) {
        if ($library.Value.type -ne 'package') { continue }
        $name, $libraryVersion = $library.Name -split '/', 2
        $purl = "pkg:nuget/$name@$libraryVersion"
        if ($seen.ContainsKey($purl)) { continue }
        $seen[$purl] = $true
        $components.Add([ordered]@{
            type = 'library'; name = $name; version = $libraryVersion; purl = $purl
            hashes = @(@{ alg = 'SHA-512'; content = ([BitConverter]::ToString([Convert]::FromBase64String($library.Value.sha512))).Replace('-', '').ToLowerInvariant() })
            scope = 'required'
        })
    }
}

$components.Add([ordered]@{ type = 'application'; name = 'SoundVolumeView'; version = '2.53'; supplier = @{ name = 'NirSoft' }
    hashes = @(@{ alg = 'SHA-256'; content = 'b5af5bd60f7a29af8cb4d8a566382b90f0fe07cac97228d218cb913f3382d647' })
    licenses = @(@{ license = @{ name = 'Freeware (redistribution permitted, see readme.txt)' } }) })
$components.Add([ordered]@{ type = 'library'; name = 'Equalizer APO'; version = '1.4.2'; supplier = @{ name = 'Jonas Thedering' }
    licenses = @(@{ license = @{ id = 'GPL-2.0-or-later' } }); description = 'Optional system-wide engine host (opt-in)' })

if ($PackageRoot -and (Test-Path -LiteralPath $PackageRoot)) {
    foreach ($file in Get-ChildItem -LiteralPath $PackageRoot -File -Recurse) {
        $relative = $file.FullName.Substring($PackageRoot.Length + 1).Replace('\', '/')
        $components.Add([ordered]@{ type = 'file'; name = $relative
            hashes = @(@{ alg = 'SHA-256'; content = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }) })
    }
}

$sbom = [ordered]@{
    bomFormat = 'CycloneDX'; specVersion = '1.5'; version = 1
    serialNumber = 'urn:uuid:' + [guid]::NewGuid().ToString()
    metadata = [ordered]@{
        timestamp = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        component = [ordered]@{ type = 'application'; name = 'SSAP3'; version = $Version; licenses = @(@{ license = @{ id = 'GPL-3.0-or-later' } }) }
        tools = @(@{ name = 'tools/release/New-Sbom.ps1' })
    }
    components = $components
}
New-Item -ItemType Directory -Force -Path (Split-Path $Output -Parent) | Out-Null
$sbom | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Output -Encoding UTF8
Write-Output "SBOM: $Output ($($components.Count) components)"
