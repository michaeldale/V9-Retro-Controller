<#
.SYNOPSIS
  Publishes a scrubbed snapshot of the source to the public GitHub repository.
.DESCRIPTION
  The private repository stays primary. The public one never receives its history: each release
  is one squashed snapshot commit ("Release <version>"), as for the V9x Remote Agent.

    1. Mirrors the working tree into artifacts\github-mirror (a clone of -Remote), leaving out
       .git, .claude, bin, obj, artifacts, dist, TestResults and .vs.
    2. Hard scrub gate: refuses to publish if any mirrored text file contains this machine's
       private workspace folder (the parent of this repository), the user-profile path, or the
       local git identity's name or e-mail domain. Fix the source file and re-run; never bypass it.
       The private strings are worked out at run time, so this script can itself be published.
    3. Commits as "V9 Retro Controller" and pushes main; with -Tag also pushes v<version>.

  Run from a clean, committed tree. Then build and run scripts\release.ps1 -Publish.
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish-github.ps1 -Tag
#>
[CmdletBinding()]
param(
    [string]$Message,
    [string]$Remote = 'https://github.com/michaeldale/V9-Retro-Controller.git',
    [switch]$Tag
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Release-Common.ps1')
$version = Get-V9RetroVersion $root
[void](Get-V9RetroReleaseNotes $root $version)
if (-not $Message) { $Message = "Release $version" }
$mirror = Join-Path $root 'artifacts\github-mirror'

function Invoke-Git {
    # No stderr redirection: Windows PowerShell 5.1 turns redirected native stderr into errors.
    & git @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed ($LASTEXITCODE)." }
}

$dirty = (& git -C $root status --porcelain)
if ($dirty) { throw 'Commit or stash your changes first: the snapshot must match a commit.' }

if (-not (Test-Path -LiteralPath (Join-Path $mirror '.git'))) {
    New-Item -ItemType Directory -Force -Path $mirror | Out-Null
    Invoke-Git -C $mirror init -b main
    Invoke-Git -C $mirror remote add origin $Remote
    & git -C $mirror fetch origin main
    if ($LASTEXITCODE -eq 0) { Invoke-Git -C $mirror reset --hard origin/main }
}
Invoke-Git -C $mirror config user.name 'V9 Retro Controller'
Invoke-Git -C $mirror config user.email 'v9-retro-controller@users.noreply.github.com'

robocopy $root $mirror /MIR /XD .git .claude bin obj artifacts dist TestResults .vs /XF *.user *.suo /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE." }

# Hard scrub gate.
$patterns = New-Object System.Collections.Generic.List[string]
$workspace = Split-Path -Parent $root
$patterns.Add($workspace); $patterns.Add($workspace.Replace('\', '/'))
if ($env:USERPROFILE) { $patterns.Add($env:USERPROFILE); $patterns.Add($env:USERPROFILE.Replace('\', '/')) }
$name = (& git -C $root config user.name | Out-String).Trim()
if ($name.Length -gt 3) { $patterns.Add($name) }
$email = (& git -C $root config user.email | Out-String).Trim()
if ($email -match '@(.+)$') { $patterns.Add($Matches[1]) }
$binary = '.webp', '.png', '.ico', '.zip', '.exe', '.dll', '.pfx', '.icns', '.iso'
$hits = @()
foreach ($file in Get-ChildItem -LiteralPath $mirror -Recurse -File | Where-Object { $_.FullName -notlike "*\.git\*" }) {
    if ($binary -contains $file.Extension.ToLowerInvariant()) { continue }
    $text = [IO.File]::ReadAllText($file.FullName)
    foreach ($pattern in $patterns) {
        if ($text.IndexOf($pattern, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $hits += "$($file.FullName.Substring($mirror.Length + 1)): contains a private string"
        }
    }
}
if ($hits.Count -gt 0) {
    $hits | Sort-Object -Unique | ForEach-Object { Write-Host $_ }
    throw 'Scrub gate: private strings found; nothing was published.'
}

Invoke-Git -C $mirror add -A
& git -C $mirror diff --cached --quiet
if ($LASTEXITCODE -ne 0) { Invoke-Git -C $mirror commit -q -m $Message }
Invoke-Git -C $mirror push origin main
if ($Tag) {
    & git -C $mirror rev-parse -q --verify "refs/tags/v$version" | Out-Null
    if ($LASTEXITCODE -ne 0) { Invoke-Git -C $mirror tag "v$version" }
    Invoke-Git -C $mirror push origin "v$version"
}

[pscustomobject]@{ Version = $version; Message = $Message; Tagged = [bool]$Tag; Mirror = $mirror; Remote = $Remote }
