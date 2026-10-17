# Checks that the docs agree on one version:
#   the README title (and its release tag links), the top CHANGELOG heading,
#   and docs/RELEASE_<version>.md (the only release file, same version and date).
# Also checks every CHANGELOG release heading reads "## X.Y.Z — Month D, YYYY".
#   pwsh -File .github/scripts/Test-DocsVersion.ps1 [-Root <repo>] [-Version <X.Y.Z>]
# Exit code 1 lists every mismatch.
[CmdletBinding()]
param(
    [string]$Root = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string]$Version
)
$ErrorActionPreference = 'Stop'
$inv = [Globalization.CultureInfo]::InvariantCulture
$problems = [Collections.Generic.List[string]]::new()

$changelog = Get-Content -LiteralPath (Join-Path $Root 'CHANGELOG.md') -Encoding utf8
$headings = @($changelog | Where-Object { $_ -match '^## ' })
foreach ($h in $headings) {
    if ($h -match '^## \d+\.\d+\.\d+$') { continue }  # an old entry with no recorded date
    $ok = $h -match '^## (\d+\.\d+\.\d+) — (\w+ \d{1,2}, \d{4})$'
    if ($ok) {
        $parsed = [datetime]::MinValue
        $ok = [datetime]::TryParseExact($Matches[2], 'MMMM d, yyyy', $inv, 'None', [ref]$parsed)
    }
    if (-not $ok) { $problems.Add("CHANGELOG heading is not '## X.Y.Z — Month D, YYYY': $h") }
}
if (-not $headings -or $headings[0] -notmatch '^## (\d+\.\d+\.\d+) — (.+)$') {
    throw 'CHANGELOG.md has no dated release heading at the top.'
}
$top = $Matches[1]
$date = $Matches[2]

if ($Version -and $Version -ne $top) { $problems.Add("VERSION is $Version but the top CHANGELOG heading is $top.") }

$readme = Get-Content -LiteralPath (Join-Path $Root 'README.md') -Raw -Encoding utf8
if ($readme -match '(?m)(?:<h1[^>]*>|^# )Exo Launcher (\d+\.\d+(?:\.\d+)?)') {
    $title = $Matches[1]
    $expected = if ($title.Split('.').Count -eq 2) { ($top.Split('.')[0..1] -join '.') } else { $top }
    if ($title -ne $expected) { $problems.Add("README title says $title; the CHANGELOG says $top.") }
} else {
    $problems.Add('README.md has no "Exo Launcher X.Y" title.')
}
foreach ($m in [regex]::Matches($readme, 'releases/(?:tag|download)/v(\d+\.\d+\.\d+)')) {
    if ($m.Groups[1].Value -ne $top) { $problems.Add("README links release v$($m.Groups[1].Value); the CHANGELOG says $top.") }
}

$releases = @(Get-ChildItem -LiteralPath (Join-Path $Root 'docs') -Filter 'RELEASE_*.md')
$expectedName = "RELEASE_$top.md"
foreach ($r in $releases | Where-Object Name -ne $expectedName) { $problems.Add("Stale release file docs/$($r.Name).") }
$release = $releases | Where-Object Name -eq $expectedName
if (-not $release) {
    $problems.Add("docs/$expectedName is missing.")
} else {
    $lines = Get-Content -LiteralPath $release.FullName -Encoding utf8
    if ($lines[0] -ne "# Exo Launcher $top") { $problems.Add("docs/$expectedName title is '$($lines[0])'.") }
    if (-not ($lines -contains "_$($date)_")) { $problems.Add("docs/$expectedName does not carry the CHANGELOG date $date.") }
    $text = $lines -join "`n"
    foreach ($m in [regex]::Matches($text, 'releases/(?:tag|download)/v(\d+\.\d+\.\d+)')) {
        if ($m.Groups[1].Value -ne $top) { $problems.Add("docs/$expectedName links release v$($m.Groups[1].Value).") }
    }
}

if ($problems.Count) {
    $problems | ForEach-Object { Write-Host "::error::$_" }
    exit 1
}
Write-Host "Docs agree on $top ($date)."
