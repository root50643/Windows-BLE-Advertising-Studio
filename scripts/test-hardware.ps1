[CmdletBinding()]
param([string]$ExePath, [string]$OutputDirectory = 'artifacts\hardware-test')
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $ExePath) { $ExePath = Join-Path $taskRoot 'artifacts\release\Windows-BLE-Advertising-Studio-win-x64\BLEStudio.exe' }
$taskExe = (Resolve-Path -LiteralPath $ExePath).Path
$taskOutput = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $taskRoot $OutputDirectory }
New-Item -ItemType Directory -Force -Path $taskOutput | Out-Null
$taskProcess = Start-Process -FilePath $taskExe -ArgumentList @('--hardware-test', '--output', ('"' + $taskOutput + '"')) -WindowStyle Hidden -PassThru
if (-not $taskProcess.WaitForExit(45000)) { Stop-Process -Id $taskProcess.Id -Force; throw 'Hardware diagnostic timed out.' }
if ($taskProcess.ExitCode -ne 0) { throw "Hardware diagnostic could not complete. Inspect $taskOutput" }
$taskReport = Get-Content -LiteralPath (Join-Path $taskOutput 'hardware-results.json') -Raw | ConvertFrom-Json
Write-Host "Received packets: $($taskReport.receivedPackets)"
if ($taskReport.failures.Count -gt 0) { $taskReport.failures | Write-Warning }
Write-Host "Actual RF equality and network participation remain unverified. Report: $taskOutput"
