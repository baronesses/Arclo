param([string]$ToolsDirectory = '')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
if (!$ToolsDirectory) { $ToolsDirectory = Join-Path $workspace 'artifacts\bundled-tools\26.04' }
$tools = [IO.Path]::GetFullPath($ToolsDirectory)
$sevenZip = Join-Path $tools '7zip'
New-Item -ItemType Directory -Force -Path $sevenZip | Out-Null

function Get-VerifiedDownload([string]$Url, [string]$Destination, [string]$Sha256) {
    if ((Test-Path -LiteralPath $Destination) -and
        (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -eq $Sha256) { return }
    $download = "$Destination.download"
    Invoke-WebRequest -Uri $Url -OutFile $download
    if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $Sha256) {
        throw "Upstream download has an unexpected SHA256: $Url"
    }
    Move-Item -LiteralPath $download -Destination $Destination -Force
}

$binaryUrl = 'https://github.com/ip7z/7zip/releases/download/26.04/7zr.exe'
$binaryHash = '256feca8e274e5da655e2a284fabafd9f554365eb164862089dacd4e8276d282'
$extraUrl = 'https://github.com/ip7z/7zip/releases/download/26.04/7z2604-extra.7z'
$extraHash = 'dc4b11d3399db18b063630137145f5585d8f7ac847bf3639bd1185d7d1f7cee0'
$binary = Join-Path $sevenZip '7zr.exe'
$extra = Join-Path $tools '7z2604-extra.7z'
Get-VerifiedDownload $binaryUrl $binary $binaryHash
Get-VerifiedDownload $extraUrl $extra $extraHash

# Extract only upstream notices. No executable or library from Extra is bundled.
$notices = Join-Path $tools 'extra-notices'
New-Item -ItemType Directory -Force -Path $notices | Out-Null
& $binary e $extra readme.txt License.txt "-o$notices" -y | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not extract the official 7-Zip notices.' }
Copy-Item -LiteralPath (Join-Path $notices 'readme.txt') -Destination (Join-Path $sevenZip '7ZIP-EXTRA-README.txt') -Force
Copy-Item -LiteralPath (Join-Path $notices 'License.txt') -Destination (Join-Path $sevenZip '7ZIP-EXTRA-LICENSE.txt') -Force
$utf8 = [Text.UTF8Encoding]::new($false)
$license = @'
Arclo includes the unmodified 7zr.exe 26.04 (x86), Copyright Igor Pavlov.
7zr.exe is public-domain code, as confirmed by the accompanying official Extra README.
The official Extra license also describes other modules; those modules are not included.
Official download: https://www.7-zip.org/download.html
Binary: https://github.com/ip7z/7zip/releases/download/26.04/7zr.exe
Source: https://github.com/ip7z/7zip/tree/26.04
'@
[IO.File]::WriteAllText((Join-Path $sevenZip 'LICENSE.txt'), $license + "`n", $utf8)
$origin = [ordered]@{
    Component = '7zr.exe'; Version = '26.04'; Architecture = 'x86 (WoW64 on Windows x64)'
    License = 'Public domain'; Download = $binaryUrl; Sha256 = $binaryHash
    ReferenceExtra = $extraUrl; ReferenceExtraSha256 = $extraHash
}
[IO.File]::WriteAllText((Join-Path $sevenZip 'ORIGIN.json'), ($origin | ConvertTo-Json) + "`n", $utf8)
Write-Output "Bundled tool ready: $binary"
