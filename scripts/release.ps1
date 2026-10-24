<#
.SYNOPSIS
  Signs a V9 Retro Controller release and (with -Publish) creates its GitHub release.
.DESCRIPTION
  The update server (michaeldale.com.au, v9x_update_checker) imports releases from GitHub with
  the updates_publish_github_release MCP tool, and the app installs a release only if its
  SIGNED.TXT verifies under the V9x release key. Run on the developer's PC, after
  scripts\build-windows.ps1 and scripts\publish-github.ps1 -Tag:

    1. Checks that the top CHANGELOG.md section is this version; it becomes the release notes.
    2. Collects dist\v9-retro-controller-<version>-<family>.zip (win-x64 from build-windows.ps1;
       osx-arm64 / osx-x64 copied over from a Mac once Mac builds ship) and the standalone exe.
    3. Writes SHA256SUMS.txt and SIGNED.TXT ([V9RetroRelease] with each family's file, size and
       SHA-256), signed with %USERPROFILE%\.v9x\release-signing.key through v9xsign.exe (from the
       V9x Remote Agent repository's build\tools, or -Signer / V9X_SIGNER). It refuses a key that
       is not the one the app trusts (UpdateClient.ReleasePublicKeyHex).
    4. With -Publish: creates GitHub release v<version> on -Repo with the exe, the packages,
       SIGNED.TXT, SHA256SUMS.txt and the CHANGELOG notes (the tag must already exist).

  Then import it: updates_publish_github_release with app v9-retro-controller, tag v<version>.
  The signing key never leaves this PC.
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\release.ps1 -Publish
#>
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+(\.\d+)?$')] [string]$Version,
    [string]$Repo = 'michaeldale/V9-Retro-Controller',
    [string]$Signer,
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Release-Common.ps1')
$app = 'v9-retro-controller'
$built = Get-V9RetroVersion $root
if (-not $Version) { $Version = $built }
if ($Version -ne $built) { throw "Directory.Build.props says $built; build that version first or pass -Version $built." }
$notes = Get-V9RetroReleaseNotes $root $Version

if (-not $Signer) { $Signer = $env:V9X_SIGNER }
if (-not $Signer) { $Signer = Join-Path (Split-Path -Parent $root) 'v9x-remote-agent\build\tools\v9xsign.exe' }
$keyFile = Join-Path $env:USERPROFILE '.v9x\release-signing.key'
if (-not (Test-Path -LiteralPath $Signer)) { throw "Missing $Signer (build it in the V9x Remote Agent repository with scripts\build-host-tests.ps1, or pass -Signer)." }
if (-not (Test-Path -LiteralPath $keyFile)) { throw "Missing release signing key $keyFile." }

$source = Get-Content -LiteralPath (Join-Path $root 'src\V9Retro.Core\Updates\UpdateClient.cs') -Raw
if ($source -notmatch 'ReleasePublicKeyHex\s*=\s*"([0-9a-f]{64})"') { throw 'ReleasePublicKeyHex not found in UpdateClient.cs.' }
$expectedPublic = $Matches[1]
$public = (& $Signer public "@$keyFile" | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $public -ne $expectedPublic) { throw "The key in $keyFile is not the release key the app trusts." }

$dist = Join-Path $root 'dist'
$zips = @(Get-ChildItem -LiteralPath $dist -Filter "$app-$Version-*.zip" -File -ErrorAction SilentlyContinue | Sort-Object Name)
if ($zips.Count -eq 0) { throw "No $app-$Version-<family>.zip in $dist. Run scripts\build-windows.ps1 first." }
foreach ($family in 'win-x64', 'osx-arm64') {
    if (-not ($zips | Where-Object Name -eq "$app-$Version-$family.zip")) { Write-Warning "No $family package; that platform will be told there is no update." }
}
$exes = @(Get-ChildItem -LiteralPath $dist -Filter "V9RetroController-$Version-*.exe" -File -ErrorAction SilentlyContinue)

$outDir = Join-Path $dist "release\$Version"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$body = "[V9RetroRelease]`r`nSchema=1`r`nApp=$app`r`nVersion=$Version`r`nBuild=v$Version`r`n`r`n"
$sums = ''
foreach ($zip in $zips) {
    $family = $zip.Name.Substring("$app-$Version-".Length, $zip.Name.Length - "$app-$Version-".Length - 4)
    $sha = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip.FullName).Hash.ToLowerInvariant()
    $body += "[Family.$family]`r`nFile=$($zip.Name)`r`nSize=$($zip.Length)`r`nSha256=$sha`r`n`r`n"
    $sums += "$sha  $($zip.Name)`n"
}
foreach ($exe in $exes) {
    $sums += "$((Get-FileHash -Algorithm SHA256 -LiteralPath $exe.FullName).Hash.ToLowerInvariant())  $($exe.Name)`n"
}
$bodyPath = Join-Path $outDir 'SIGNED.BODY'
[IO.File]::WriteAllText($bodyPath, $body, [Text.Encoding]::ASCII)
$signature = (& $Signer sign "@$keyFile" $bodyPath | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $signature -notmatch '^[0-9a-f]{128}$') { throw 'Signing failed.' }
& $Signer verify $expectedPublic $signature $bodyPath | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'The new signature does not verify.' }
$signedPath = Join-Path $outDir 'SIGNED.TXT'
[IO.File]::WriteAllText($signedPath, $body + "[Signature]`r`nEd25519=$signature`r`n", [Text.Encoding]::ASCII)
Remove-Item -LiteralPath $bodyPath -Force -Confirm:$false
$sumsPath = Join-Path $outDir 'SHA256SUMS.txt'
[IO.File]::WriteAllText($sumsPath, $sums, [Text.Encoding]::ASCII)
$notesPath = Join-Path $outDir 'NOTES.md'
# UTF-8 without a BOM, so the release body does not start with one.
[IO.File]::WriteAllText($notesPath, $notes + "`n", (New-Object Text.UTF8Encoding($false)))

if ($Publish) {
    $assets = @($exes.FullName) + @($zips.FullName) + $signedPath + $sumsPath
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & gh release view "v$Version" --repo $Repo *> $null
    $exists = $LASTEXITCODE -eq 0
    $ErrorActionPreference = $previous
    if ($exists) {
        & gh release upload "v$Version" @assets --repo $Repo --clobber
        if ($LASTEXITCODE -eq 0) { & gh release edit "v$Version" --repo $Repo --notes-file $notesPath }
    } else {
        & gh release create "v$Version" @assets --repo $Repo --verify-tag --title "V9 Retro Controller v$Version" --notes-file $notesPath
    }
    if ($LASTEXITCODE -ne 0) { throw 'Creating or uploading the GitHub release failed.' }
}

[pscustomobject]@{
    Version   = $Version
    Packages  = ($zips.Name -join ', ')
    Exe       = ($exes.Name -join ', ')
    Signed    = $signedPath
    Notes     = $notesPath
    Published = [bool]$Publish
    Next      = "updates_publish_github_release app=$app tag=v$Version"
}
