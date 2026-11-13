[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$statusDirectory = 'C:\ProgramData\SSAP3'
$statusPath = Join-Path $statusDirectory 'msvc-buildtools-install.json'
$logPath = Join-Path $statusDirectory 'msvc-buildtools-install.log'

New-Item -ItemType Directory -Force -Path $statusDirectory | Out-Null

$startedAt = Get-Date
$result = [ordered]@{
    StartedAt = $startedAt.ToString('o')
    CompletedAt = $null
    ExitCode = $null
    Succeeded = $false
    Error = $null
}

try {
    Start-Transcript -LiteralPath $logPath -Append | Out-Null

    & winget.exe install `
        --id Microsoft.VisualStudio.2022.BuildTools `
        --exact `
        --source winget `
        --silent `
        --accept-source-agreements `
        --accept-package-agreements `
        --disable-interactivity `
        --override '--quiet --wait --norestart --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended'

    $result.ExitCode = $LASTEXITCODE
    if ($LASTEXITCODE -ne 0) {
        throw "winget exited with code $LASTEXITCODE."
    }

    $result.Succeeded = $true
}
catch {
    $result.Error = $_.Exception.Message
}
finally {
    $result.CompletedAt = (Get-Date).ToString('o')
    $result | ConvertTo-Json | Set-Content -LiteralPath $statusPath -Encoding UTF8
    try {
        Stop-Transcript | Out-Null
    }
    catch {
    }
}

if (-not $result.Succeeded) {
    exit 1
}
