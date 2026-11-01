[CmdletBinding()]
param([string]$Version = '1.1.0-rc.1', [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $taskRoot
try {
    & (Join-Path $PSScriptRoot 'build.ps1') -Version $Version -SkipTests:$SkipTests -OutputDirectory 'artifacts/release-tracker'
    $taskOutput = Join-Path $taskRoot 'artifacts/release-tracker'
    $taskStage = Join-Path $taskOutput 'Windows-BLE-Tracking-SelfHost'
    $taskStage = [IO.Path]::GetFullPath($taskStage)
    $taskArtifacts = [IO.Path]::GetFullPath((Join-Path $taskRoot 'artifacts'))
    if (-not $taskStage.StartsWith($taskArtifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($taskStage) -ne 'Windows-BLE-Tracking-SelfHost') { throw 'Unsafe generated staging path.' }
    if (Test-Path -LiteralPath $taskStage) { Remove-Item -LiteralPath $taskStage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path (Join-Path $taskStage 'deploy'),(Join-Path $taskStage 'services/tracking-service'),(Join-Path $taskStage 'scripts') | Out-Null
    # Explicit source allowlists prevent packaging configured credentials, databases or account exports.
    foreach ($taskName in @('compose.yaml','Dockerfile','Dockerfile.apple','Caddyfile','setup.py','README.md','README.en.md')) {
        Copy-Item -LiteralPath (Join-Path $taskRoot "deploy/$taskName") -Destination (Join-Path $taskStage 'deploy')
    }
    foreach ($taskName in @('app.py','asgi.py','store.py','providers.py','google_adapter.py','requirements.txt','test_service.py','LICENSE-google-adapter')) {
        Copy-Item -LiteralPath (Join-Path $taskRoot "services/tracking-service/$taskName") -Destination (Join-Path $taskStage 'services/tracking-service')
    }
    Copy-Item -LiteralPath (Join-Path $taskRoot 'services/tracking-service/static') -Destination (Join-Path $taskStage 'services/tracking-service') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $taskRoot 'scripts/google-authorize.py') -Destination (Join-Path $taskStage 'scripts')
    Copy-Item -LiteralPath (Join-Path $taskRoot '.dockerignore'),(Join-Path $taskRoot 'LICENSE'),(Join-Path $taskRoot 'THIRD-PARTY-NOTICES.md'),(Join-Path $taskRoot 'README.md'),(Join-Path $taskRoot 'README.en.md') -Destination $taskStage
    Copy-Item -LiteralPath (Join-Path $taskRoot 'docs') -Destination $taskStage -Recurse -Force
    $taskDeployZip = Join-Path $taskOutput "Windows-BLE-Tracking-SelfHost-v$Version.zip"
    Compress-Archive -LiteralPath $taskStage -DestinationPath $taskDeployZip -Force
    $taskManifest = Join-Path $taskOutput 'SHA256SUMS.txt'
    $taskHash = (Get-FileHash -LiteralPath $taskDeployZip -Algorithm SHA256).Hash.ToLowerInvariant()
    Add-Content -LiteralPath $taskManifest -Value "$taskHash  $([IO.Path]::GetFileName($taskDeployZip))" -Encoding ascii
    & (Join-Path $PSScriptRoot 'test-ui.ps1') -ExePath (Join-Path $taskOutput 'BLEStudio.exe') -OutputDirectory 'artifacts/tracker-ui-published'
    Write-Host "Candidate artifacts: $taskOutput"
} finally { Pop-Location }
