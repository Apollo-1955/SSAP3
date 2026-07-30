[CmdletBinding()]
param(
    [string]$Version = '0.3.0',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$workspaceRoot = Split-Path $repositoryRoot -Parent
$projectRoot = Split-Path $workspaceRoot -Parent
$packageRoot = Join-Path $projectRoot 'outputs\SoundStage-Setup'
$releaseRoot = Join-Path $projectRoot 'outputs'
$publishRoot = Join-Path $workspaceRoot 'soundstage-app-publish'
$certificateStageRoot =
    Join-Path $workspaceRoot 'soundstage-package-stage\driver'
$certificatePath =
    Join-Path $certificateStageRoot 'soundstage-local-test-driver.cer'
$setupPath = Join-Path $releaseRoot 'SoundStage-Setup.exe'
$iscc = @(
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe',
    (Join-Path $env:LocalAppData 'Programs\Inno Setup 6\ISCC.exe')
) |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1

function Remove-BuildDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedParent
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return
    }

    $resolvedPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $resolvedParent = [IO.Path]::GetFullPath($ExpectedParent).TrimEnd('\')
    if (-not $resolvedPath.StartsWith(
            $resolvedParent + '\',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove build directory outside $resolvedParent`: $resolvedPath"
    }

    Remove-Item -LiteralPath $resolvedPath -Recurse -Force
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE."
    }
}

$dotnetCommand = Get-Command dotnet.exe -ErrorAction SilentlyContinue
$dotnet = if ($null -ne $dotnetCommand) {
    $dotnetCommand.Source
}
else {
    Join-Path $workspaceRoot '.dotnet\dotnet.exe'
}

foreach ($requiredPath in @(
        $dotnet,
        (Join-Path $repositoryRoot 'src\SoundStage.App\SoundStage.App.csproj'),
        (Join-Path $PSScriptRoot 'SoundStage.iss')
    )) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required installer build input was not found: $requiredPath"
    }
}
if ([string]::IsNullOrWhiteSpace($iscc)) {
    throw (
        'Inno Setup 6 was not found. Install JRSoftware.InnoSetup with winget.')
}

Remove-BuildDirectory -Path $packageRoot -ExpectedParent $releaseRoot
Remove-BuildDirectory -Path $publishRoot -ExpectedParent $workspaceRoot
New-Item -ItemType Directory -Force `
    -Path $packageRoot, $releaseRoot, $publishRoot, $certificateStageRoot |
    Out-Null

& (Join-Path $repositoryRoot 'assets\build-icons.ps1')
& (Join-Path $repositoryRoot 'audio\build-spatializer.ps1')
& (Join-Path $repositoryRoot 'driver\build-driver-msvc.ps1')

if (-not $SkipTests) {
    Invoke-Checked `
        -FilePath $dotnet `
        -Arguments @(
            'run',
            '--project',
            (Join-Path `
                $repositoryRoot `
                'tests\SoundStage.Core.SmokeTests\SoundStage.Core.SmokeTests.csproj'),
            '-c',
            'Release')
    & (Join-Path $repositoryRoot 'audio\test-spatializer.ps1')
}

$certificate = Get-ChildItem -Path Cert:\CurrentUser\My |
    Where-Object {
        $_.HasPrivateKey -and
        $_.Subject -eq 'CN=SoundStage Local Test Driver' -and
        $_.NotAfter -gt (Get-Date)
    } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1
if ($null -eq $certificate) {
    & (Join-Path $repositoryRoot 'driver\New-TestSigningCertificate.ps1')
    $certificate = Get-ChildItem -Path Cert:\CurrentUser\My |
        Where-Object {
            $_.HasPrivateKey -and
            $_.Subject -eq 'CN=SoundStage Local Test Driver' -and
            $_.NotAfter -gt (Get-Date)
        } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
}
if ($null -eq $certificate) {
    throw 'The SoundStage test-signing certificate could not be created.'
}

Export-Certificate `
    -Cert $certificate `
    -FilePath $certificatePath `
    -Force |
    Out-Null
Import-Certificate `
    -FilePath $certificatePath `
    -CertStoreLocation 'Cert:\CurrentUser\Root' |
    Out-Null
Import-Certificate `
    -FilePath $certificatePath `
    -CertStoreLocation 'Cert:\CurrentUser\TrustedPublisher' |
    Out-Null

Invoke-Checked `
    -FilePath $dotnet `
    -Arguments @(
        'publish',
        (Join-Path `
            $repositoryRoot `
            'src\SoundStage.App\SoundStage.App.csproj'),
        '-c',
        'Release',
        '-r',
        'win-x64',
        '--self-contained',
        'true',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-o',
        $publishRoot)

& (Join-Path $repositoryRoot 'driver\package-driver.ps1') `
    -CertificateThumbprint $certificate.Thumbprint

$packageApp = Join-Path $packageRoot 'app'
New-Item -ItemType Directory -Force -Path $packageApp | Out-Null
Copy-Item -Path (Join-Path $publishRoot '*') `
    -Destination $packageApp `
    -Recurse `
    -Force

foreach ($fileName in @(
        'Install-SoundStage.ps1',
        'Uninstall-SoundStage.ps1',
        'Install-SpatialAudioEngine.ps1',
        'Uninstall-SpatialAudioEngine.ps1',
        'README.md',
        'README-RU.md')) {
    Copy-Item `
        -LiteralPath (Join-Path $PSScriptRoot $fileName) `
        -Destination (Join-Path $packageRoot $fileName) `
        -Force
}

foreach ($fileName in @(
        'LICENSE',
        'THIRD_PARTY.md')) {
    Copy-Item `
        -LiteralPath (Join-Path $repositoryRoot $fileName) `
        -Destination (Join-Path $packageRoot $fileName) `
        -Force
}
Copy-Item `
    -LiteralPath (Join-Path $repositoryRoot 'docs\INSTALL.md') `
    -Destination (Join-Path $packageRoot 'INSTALL.md') `
    -Force

Set-Content `
    -LiteralPath (Join-Path $packageRoot 'VERSION.txt') `
    -Value $Version `
    -Encoding ASCII

$manifestPath = Join-Path $packageRoot 'SHA256SUMS.txt'
$manifestLines = Get-ChildItem -LiteralPath $packageRoot -File -Recurse |
    Where-Object { $_.FullName -ne $manifestPath } |
    Sort-Object FullName |
    ForEach-Object {
        $relativePath =
            $_.FullName.Substring($packageRoot.Length + 1).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        "$hash  $relativePath"
    }
$manifestLines | Set-Content -LiteralPath $manifestPath -Encoding ASCII

if (Test-Path -LiteralPath $setupPath -PathType Leaf) {
    Remove-Item -LiteralPath $setupPath -Force
}

Invoke-Checked `
    -FilePath $iscc `
    -Arguments @(
        "/DSourceRoot=$packageRoot",
        "/DOutputRoot=$releaseRoot",
        "/DAppVersion=$Version",
        (Join-Path $PSScriptRoot 'SoundStage.iss'))

if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
    throw "Inno Setup did not create $setupPath."
}

$setupHash = Get-FileHash -LiteralPath $setupPath -Algorithm SHA256
$setupChecksumPath = "$setupPath.sha256"
Set-Content `
    -LiteralPath $setupChecksumPath `
    -Value ("{0} *SoundStage-Setup.exe" -f $setupHash.Hash) `
    -Encoding ASCII
Write-Output "Built: $setupPath"
Write-Output "SHA256: $($setupHash.Hash)"
