[CmdletBinding()]
param([string]$ExePath, [string]$OutputDirectory = 'artifacts\ui-smoke')
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $ExePath) { $ExePath = Join-Path $taskRoot 'artifacts\release\Windows-BLE-Advertising-Studio-win-x64\BLEStudio.exe' }
$taskExe = (Resolve-Path -LiteralPath $ExePath).Path
$taskOutput = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $taskRoot $OutputDirectory }
New-Item -ItemType Directory -Force -Path $taskOutput | Out-Null
foreach ($taskName in @('failure.txt', 'smoke-results.json')) { $taskOld = Join-Path $taskOutput $taskName; if (Test-Path -LiteralPath $taskOld) { Remove-Item -LiteralPath $taskOld } }
$taskProcess = Start-Process -FilePath $taskExe -ArgumentList @('--smoke-test', '--output', ('"' + $taskOutput + '"')) -WindowStyle Hidden -PassThru
if (-not $taskProcess.WaitForExit(45000)) { Stop-Process -Id $taskProcess.Id -Force; throw 'GUI smoke test timed out.' }
if ($taskProcess.ExitCode -ne 0 -or (Test-Path -LiteralPath (Join-Path $taskOutput 'failure.txt'))) { throw "GUI smoke test failed. Inspect $taskOutput" }
$taskResult = Get-Content -LiteralPath (Join-Path $taskOutput 'smoke-results.json') -Raw | ConvertFrom-Json
if ($taskResult.passed -lt 19) { throw 'GUI smoke checks incomplete.' }
Write-Host "GUI smoke checks passed: $($taskResult.passed). Page renders: $taskOutput"
