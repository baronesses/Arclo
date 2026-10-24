param(
    [string]$Version = '0.34.0',
    [string]$Executable = '',
    [string]$TestDirectory = ''
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
if (!$Executable) { $Executable = Join-Path $workspace "release\Arclo-$Version-win-x64-single\Arclo.exe" }
$source = (Resolve-Path -LiteralPath $Executable).Path
if (!$TestDirectory) { $TestDirectory = Join-Path $workspace "artifacts\single-exe\$Version\clean-launch" }
$testRoot = [IO.Path]::GetFullPath($TestDirectory)
if (!$testRoot.StartsWith($workspace + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Use an owned workspace test directory for the isolated executable check.'
}
New-Item -ItemType Directory -Force $testRoot | Out-Null
$testExe = Join-Path $testRoot 'Arclo.exe'
if ($source -eq $testExe) { throw 'Keep the distributable separate from its diagnostic copy.' }
Copy-Item -LiteralPath $source -Destination $testExe -Force
$started = [DateTime]::UtcNow
$start = [Diagnostics.ProcessStartInfo]::new($testExe)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardError = $true
$start.RedirectStandardOutput = $true
$start.WorkingDirectory = $testRoot
$start.ArgumentList.Add('--standalone-smoke')
$start.Environment['PATH'] = Join-Path $env:WINDIR 'System32'
$start.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = Join-Path $workspace "artifacts\single-exe\$Version\native-extract"
$start.Environment['DOTNET_DISABLE_GUI_ERRORS'] = '1'
$process = [Diagnostics.Process]::Start($start)
if (!$process.WaitForExit(60000)) { throw "Standalone diagnostic exceeded 60 seconds; owned PID $($process.Id) was left running." }
$process.StandardError.ReadToEnd() | Set-Content -LiteralPath (Join-Path $testRoot 'stderr.txt') -Encoding utf8
$process.StandardOutput.ReadToEnd() | Set-Content -LiteralPath (Join-Path $testRoot 'stdout.txt') -Encoding utf8
$errorPath = Join-Path $testRoot 'standalone-smoke\error.txt'
if (Test-Path -LiteralPath $errorPath) { throw (Get-Content -LiteralPath $errorPath -Raw) }
if ($process.ExitCode -ne 0) { throw "Standalone diagnostic exited $($process.ExitCode). See stderr.txt." }
$reportFile = Get-Item -LiteralPath (Join-Path $testRoot 'standalone-smoke\passed.json')
if ($reportFile.LastWriteTimeUtc -lt $started) { throw 'Stale standalone diagnostic report.' }
$report = Get-Content -LiteralPath $reportFile.FullName -Raw | ConvertFrom-Json
if (!$report.singleFile -or $report.checks.Count -lt 25) { throw 'Incomplete single-file verification.' }
$sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
if ((Get-FileHash -LiteralPath $testExe -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sourceHash) { throw 'Test copy differs from distributable.' }
$result = [ordered]@{
    ExitCode = $process.ExitCode; Checks = $report.checks.Count; SingleFile = $report.singleFile
    Seconds = ([DateTime]::UtcNow - $started).TotalSeconds; Report = $reportFile.FullName
    ExecutableBytes = (Get-Item -LiteralPath $source).Length; Sha256 = $sourceHash
    Path = 'Windows System32 only'; NativeExtraction = 'Owned workspace'; Bundled = $report.bundled
}
$resultFile = Join-Path $workspace "artifacts\single-exe\$Version\standalone-result.json"
$result | ConvertTo-Json | Set-Content -LiteralPath $resultFile -Encoding utf8
$result | ConvertTo-Json
