param(
    [string]$Version = '',
    [string]$RuntimeVersion = '9.0.18',
    [string]$ToolsDirectory = '',
    [string]$OutputDirectory = '',
    [switch]$Offline
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$project = Join-Path $workspace 'src\Orbit.App\Orbit.App.csproj'
if (!$Version) { $Version = ([xml][IO.File]::ReadAllText($project)).Project.PropertyGroup.Version }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'The application version must have three numeric components.' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $workspace "release\Arclo-$Version-win-x64-single" }
$outputFullPath = [System.IO.Path]::GetFullPath($OutputDirectory)
if (!(Test-Path -LiteralPath $outputFullPath)) { New-Item -ItemType Directory -Path $outputFullPath | Out-Null }
if (@(Get-ChildItem -LiteralPath $outputFullPath -Force).Count -gt 0) {
    throw "Use an empty output directory to avoid loose-file contamination: $outputFullPath"
}
$env:DOTNET_CLI_HOME = Join-Path $workspace '.dotnet'
if (!$env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $env:USERPROFILE '.nuget\packages' }
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
if (!$ToolsDirectory) { $ToolsDirectory = Join-Path $workspace 'artifacts\bundled-tools\26.04' }
$ToolsDirectory = [IO.Path]::GetFullPath($ToolsDirectory)
if (!$Offline) { & (Join-Path $PSScriptRoot 'fetch-bundled-tools.ps1') -ToolsDirectory $ToolsDirectory }
$payload = Join-Path $workspace "artifacts\single-exe\$Version\Arclo.BundledTools.zip"
try { [xml][System.IO.File]::ReadAllText((Join-Path $workspace 'src\Orbit.App\app.manifest'), [System.Text.Encoding]::UTF8) | Out-Null }
catch { throw 'The Windows application manifest is not valid XML. Repair its encoding before publishing.' }
if (!(Select-String -LiteralPath $project -Pattern 'ArcloToolsPayload' -Quiet)) {
    throw 'The app project must embed $(ArcloToolsPayload) as logical resource Arclo.BundledTools.zip.'
}
$hostArgs = @()
$hostDescription = "NuGet $RuntimeVersion"
if ($Offline) {
    $dotnetDirectory = Split-Path (Get-Command dotnet).Source
    $hostPack = Get-ChildItem (Join-Path $dotnetDirectory 'packs\Microsoft.NETCore.App.Host.win-x64') -Directory |
        Where-Object { $_.Name -like '9.0.*' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    if (!$hostPack) { throw 'Offline publishing requires a local .NET 9 Windows x64 apphost pack.' }
    $singleFileHost = Join-Path $hostPack.FullName 'runtimes\win-x64\native\singlefilehost.exe'
    if (!(Test-Path -LiteralPath $singleFileHost)) { throw 'The local host pack does not contain singlefilehost.exe.' }
    $hostArgs = @('-p:EnableAppHostPackDownload=false',
        "-p:AppHostSourcePath=$($hostPack.FullName)\runtimes\win-x64\native\apphost.exe",
        "-p:SingleFileHostSourcePath=$singleFileHost")
    $hostDescription = $hostPack.Name
}
$buildArtifacts = Join-Path $workspace "artifacts\single-exe\$Version\build"
$properties = @("-p:Version=$Version", "-p:RuntimeFrameworkVersion=$RuntimeVersion",
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true', '-p:PublishTrimmed=false',
    '-p:DebugSymbols=false', '-p:DebugType=None') + $hostArgs
Push-Location $workspace
try {
    $restoreArgs = @('restore', $project, '-r', 'win-x64', '--artifacts-path', $buildArtifacts,
        '-p:SelfContained=true') + $properties
    if (!$Offline) { $restoreArgs += @('--source', 'https://api.nuget.org/v3/index.json') }
    & dotnet @restoreArgs
    if ($LASTEXITCODE -ne 0) { throw 'Runtime restore failed.' }
    & python (Join-Path $PSScriptRoot 'prepare-single-exe.py') --version $Version --runtime-version $RuntimeVersion --tools-directory $ToolsDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Embedded payload preparation failed.' }
}
finally { Pop-Location }
function Get-ArcloBuildInputs {
    $inputs = [ordered]@{}
    Get-ChildItem -LiteralPath (Join-Path $workspace 'src') -Recurse -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Extension -in '.cs','.xaml','.csproj','.manifest','.ico','.png','.resx' } |
        Sort-Object FullName | ForEach-Object { $inputs[$_.FullName] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    $inputs[$payload] = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash
    return ($inputs | ConvertTo-Json -Compress)
}
$inputSnapshot = Get-ArcloBuildInputs
Push-Location $workspace
try {
    & dotnet publish $project -c Release -r win-x64 --self-contained true --nologo --no-restore `
        --artifacts-path $buildArtifacts @properties "-p:ArcloToolsPayload=$payload" -o $outputFullPath
    if ($LASTEXITCODE -ne 0) { throw 'Single EXE publish failed.' }
    if ((Get-ArcloBuildInputs) -ne $inputSnapshot) { throw 'Application inputs changed during publishing; rebuild from frozen source.' }
    $files = @(Get-ChildItem -LiteralPath $outputFullPath -Recurse -File)
    if ($files.Count -ne 1 -or $files[0].Name -ne 'Arclo.exe') {
        throw "Expected precisely one distributable file Arclo.exe; found $($files.Count)."
    }
    $executable = $files[0]
    $hash = (Get-FileHash -LiteralPath $executable.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $result = [ordered]@{ Version=$Version;Executable=$executable.FullName;Bytes=$executable.Length;Sha256=$hash;SingleFile=$true;Runtime=$RuntimeVersion;HostPack=$hostDescription;InputsStable=$true;Payload=$payload }
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $workspace "artifacts\single-exe\$Version\build-result.json") -Encoding utf8
    Write-Output "Ready: $($executable.FullName)"
    Write-Output "SHA256: $hash"
}
finally { Pop-Location }
