# Shared by the build and release scripts: the version and its CHANGELOG section.

function Get-V9RetroVersion([string]$Root) {
    $props = [xml](Get-Content -LiteralPath (Join-Path $Root 'Directory.Build.props'))
    return [string]$props.Project.PropertyGroup.Version
}

# The top "## x.y.z (date)" section of CHANGELOG.md, without its heading. Throws when the top
# section is not $Version, so nothing is built or published with stale release information.
function Get-V9RetroReleaseNotes([string]$Root, [string]$Version) {
    $lines = Get-Content -LiteralPath (Join-Path $Root 'CHANGELOG.md')
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^## ') { $start = $i; break } }
    if ($start -lt 0) { throw 'CHANGELOG.md has no "## <version>" section.' }
    if ($lines[$start] -notmatch ('^## ' + [regex]::Escape($Version) + '(\s|$)')) {
        throw "The top CHANGELOG.md section is '$($lines[$start])', not $Version. Add the release notes first."
    }
    $end = $lines.Count
    for ($i = $start + 1; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^## ') { $end = $i; break } }
    return (($lines[($start + 1)..($end - 1)]) -join "`n").Trim()
}
