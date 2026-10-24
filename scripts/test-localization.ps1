param(
    [string]$Executable = '',
    [ValidateSet('localization','themes')][string]$Mode = 'localization'
)
$ErrorActionPreference='Stop'
$workspace=Split-Path $PSScriptRoot -Parent
if(!$Executable){$Executable=Join-Path $workspace 'release\Arclo-0.34.0-win-x64\Arclo.exe'}
$source=(Resolve-Path -LiteralPath $Executable).Path
$testRoot=Join-Path $workspace "artifacts\localization\final-$Mode"
New-Item -ItemType Directory -Force $testRoot | Out-Null
$testExe=Join-Path $testRoot 'Arclo.exe'
Copy-Item -LiteralPath $source -Destination $testExe -Force
$started=[DateTime]::UtcNow
$start=[Diagnostics.ProcessStartInfo]::new($testExe)
$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WorkingDirectory=$testRoot
$start.ArgumentList.Add("--$Mode-smoke")
$start.Environment['PATH']=Join-Path $env:WINDIR 'System32'
$start.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR']=Join-Path $workspace "artifacts\localization\extract-$Mode"
$start.Environment['DOTNET_DISABLE_GUI_ERRORS']='1'
$process=[Diagnostics.Process]::Start($start)
if(!$process.WaitForExit(60000)){throw "Owned diagnostic timeout PID $($process.Id)"}
$reportRoot=Join-Path $testRoot "$Mode-smoke"
$errorFile=Join-Path $reportRoot 'error.txt'
if(Test-Path -LiteralPath $errorFile){throw(Get-Content -LiteralPath $errorFile -Raw)}
if($process.ExitCode -ne 0){throw "Diagnostic exited $($process.ExitCode)"}
$report=Get-Item -LiteralPath (Join-Path $reportRoot 'passed.json')
if($report.LastWriteTimeUtc -lt $started){throw 'Stale diagnostic report'}
$data=Get-Content -LiteralPath $report.FullName -Raw | ConvertFrom-Json
$count=if($data.checks -is [array]){$data.checks.Count}else{$data.checks}
$sha=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
if((Get-FileHash -LiteralPath $testExe -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sha){throw 'Diagnostic EXE differs from release'}
$summary=[ordered]@{Mode=$Mode;ExitCode=$process.ExitCode;Checks=$count;Sha256=$sha;Report=$report.FullName;NativeInput=$false;UserDataWritten=$false}
$summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRoot 'summary.json') -Encoding utf8
$summary | ConvertTo-Json
