[CmdletBinding()]
param([ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')][string]$Version = '1.1.0-rc.1', [switch]$SkipTests, [string]$OutputDirectory = 'artifacts\release')
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskDotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $taskDotnet) { $taskDotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $taskDotnet)) { throw 'Install the .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0 and retry.' }
function Invoke-Dotnet { & $taskDotnet @args; if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" } }
Push-Location $taskRoot
try {
    Invoke-Dotnet restore BLEStudio.sln
    Invoke-Dotnet build BLEStudio.sln -c Release --no-restore "-p:Version=$Version"
    if (-not $SkipTests) { Invoke-Dotnet test BLEStudio.sln -c Release --no-build --logger trx --results-directory artifacts/test-results }
    $taskReleaseRoot = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $taskRoot $OutputDirectory }))
    $taskArtifactsRoot = [IO.Path]::GetFullPath((Join-Path $taskRoot 'artifacts'))
    if (-not $taskReleaseRoot.StartsWith($taskArtifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Release output must be a directory beneath this workspace artifacts folder.' }
    $taskBundleName = 'Windows-BLE-Advertising-Studio-win-x64'
    $taskPublish = Join-Path $taskReleaseRoot $taskBundleName
    # This is a generated output directory. Check its absolute location before cleaning.
    $taskExpected = [IO.Path]::GetFullPath((Join-Path $taskReleaseRoot $taskBundleName))
    if ([IO.Path]::GetFullPath($taskPublish) -ne $taskExpected -or -not $taskExpected.StartsWith($taskArtifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe generated output path.' }
    if (Test-Path -LiteralPath $taskPublish) { Remove-Item -LiteralPath $taskPublish -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $taskPublish | Out-Null
    Invoke-Dotnet publish src/BLEStudio.App/BLEStudio.App.csproj -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=true' '-p:PublishTrimmed=false' "-p:Version=$Version" '-p:DebugType=None' '-p:DebugSymbols=false' -o $taskPublish
    Copy-Item -LiteralPath (Join-Path $taskRoot 'LICENSE'),(Join-Path $taskRoot 'README.md'),(Join-Path $taskRoot 'README.en.md'),(Join-Path $taskRoot 'THIRD-PARTY-NOTICES.md') -Destination $taskPublish
    Copy-Item -LiteralPath (Join-Path $taskRoot 'docs') -Destination $taskPublish -Recurse
    New-Item -ItemType Directory -Force -Path (Join-Path $taskPublish 'deploy') | Out-Null
    Copy-Item -LiteralPath (Join-Path $taskRoot 'deploy/README.md'),(Join-Path $taskRoot 'deploy/README.en.md') -Destination (Join-Path $taskPublish 'deploy')
    Copy-Item -LiteralPath (Join-Path $taskRoot 'profiles') -Destination $taskPublish -Recurse
    Copy-Item -LiteralPath (Join-Path $taskRoot 'assets') -Destination $taskPublish -Recurse
    $taskNugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
    $taskLicenseRoot = Join-Path $taskPublish 'runtime-notices'
    New-Item -ItemType Directory -Force -Path $taskLicenseRoot | Out-Null
    foreach ($taskPackage in @('microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64')) {
        $taskPackageRoot = Join-Path $taskNugetRoot $taskPackage
        $taskPackageVersion = Get-ChildItem -LiteralPath $taskPackageRoot -Directory | Where-Object { $_.Name -match '^10\.0\.\d+$' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
        if (-not $taskPackageVersion) { throw "Runtime license source unavailable: $taskPackageRoot" }
        foreach ($taskNoticeName in @('LICENSE', 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
            $taskNotice = Join-Path $taskPackageVersion.FullName $taskNoticeName
            if (Test-Path -LiteralPath $taskNotice) { Copy-Item -LiteralPath $taskNotice -Destination (Join-Path $taskLicenseRoot "$taskPackage-$taskNoticeName") }
        }
    }
    $taskExe = Get-Item -LiteralPath (Join-Path $taskPublish 'BLEStudio.exe')
    Copy-Item -LiteralPath (Join-Path $taskNugetRoot 'bouncycastle.cryptography/2.7.0/LICENSE.md') -Destination (Join-Path $taskLicenseRoot 'BouncyCastle-LICENSE.md')
    Copy-Item -LiteralPath $taskExe.FullName -Destination (Join-Path $taskReleaseRoot 'BLEStudio.exe') -Force
    $taskHash = (Get-FileHash -LiteralPath $taskExe.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $taskBuildInfo = [ordered]@{ version = $Version; runtime = 'win-x64'; selfContained = $true; singleFile = $true; trimmed = $false; builtAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); exe = 'BLEStudio.exe'; bytes = $taskExe.Length; sha256 = $taskHash; fileVersion = $taskExe.VersionInfo.FileVersion }
    $taskBuildInfo | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskPublish 'build-info.json') -Encoding utf8
    $taskZip = Join-Path $taskReleaseRoot "Windows-BLE-Advertising-Studio-v$Version-win-x64.zip"
    Compress-Archive -LiteralPath $taskPublish -DestinationPath $taskZip -Force
    $taskZipHash = (Get-FileHash -LiteralPath $taskZip -Algorithm SHA256).Hash.ToLowerInvariant()
    @("$taskHash  BLEStudio.exe", "$taskZipHash  $([IO.Path]::GetFileName($taskZip))") | Set-Content -LiteralPath (Join-Path $taskReleaseRoot 'SHA256SUMS.txt') -Encoding ascii
    $taskBuildInfo | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskReleaseRoot 'build-info.json') -Encoding utf8
    Write-Host "EXE: $($taskExe.FullName)"
    Write-Host "Bytes: $($taskExe.Length)"
    Write-Host "SHA256: $taskHash"
    Write-Host "ZIP: $taskZip"
} finally { Pop-Location }
