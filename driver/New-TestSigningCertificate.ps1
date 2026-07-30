[CmdletBinding()]
param(
    [int]$ValidYears = 5
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$workspaceRoot = Split-Path $repositoryRoot -Parent
$stageDriver = Join-Path $workspaceRoot 'soundstage-package-stage\driver'
$certificatePath = Join-Path $stageDriver 'soundstage-local-test-driver.cer'

New-Item -ItemType Directory -Force -Path $stageDriver | Out-Null

$certificate = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject 'CN=SoundStage Local Test Driver' `
    -FriendlyName 'SoundStage local test driver signing' `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -KeyAlgorithm RSA `
    -KeyLength 3072 `
    -HashAlgorithm SHA256 `
    -KeyExportPolicy Exportable `
    -NotAfter (Get-Date).AddYears($ValidYears)

Export-Certificate `
    -Cert $certificate `
    -FilePath $certificatePath `
    -Force | Out-Null

Write-Output "Thumbprint: $($certificate.Thumbprint)"
Write-Output "Public certificate: $certificatePath"
Write-Output 'The private key remains in the current user certificate store.'
