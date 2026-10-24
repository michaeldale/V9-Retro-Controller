<#
.SYNOPSIS
  Builds V9 Retro Controller as one self-contained Windows exe.
.DESCRIPTION
  dist\V9RetroController-<version>-win-x64.exe needs no install and no .NET on the target PC.
  Native libraries (SQLite, Skia) are bundled and unpacked on first run.
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-windows.ps1
#>
param(
    [ValidateSet('win-x64', 'win-arm64')] [string]$Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Release-Common.ps1')
$version = Get-V9RetroVersion $root
# Refuse to build a version the CHANGELOG does not describe yet.
[void](Get-V9RetroReleaseNotes $root $version)
$out = Join-Path $root "artifacts\publish\$Runtime"
$dist = Join-Path $root 'dist'

if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dist | Out-Null

dotnet publish (Join-Path $root 'src\V9Retro.App\V9Retro.App.csproj') `
    -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishReadyToRun=true `
    -p:DebugType=none `
    -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$target = Join-Path $dist "V9RetroController-$version-$Runtime.exe"
Copy-Item -LiteralPath (Join-Path $out 'V9RetroController.exe') -Destination $target -Force
$size = [math]::Round((Get-Item -LiteralPath $target).Length / 1MB, 1)
"Built $target ($size MB)"

# The release package the update server serves: <app>-<version>-<family>.zip holding the exe.
$zip = Join-Path $dist "v9-retro-controller-$version-$Runtime.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force -Confirm:$false }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $out 'V9RetroController.exe'), 'V9RetroController.exe', 'Optimal')
} finally { $archive.Dispose() }
"Packaged $zip"
