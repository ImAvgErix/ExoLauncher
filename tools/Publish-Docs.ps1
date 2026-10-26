# Release docs (github.com/ImAvgErix/ExoLauncher).
#   pwsh -File tools\Publish-Docs.ps1
# 1. Writes docs\RELEASE_<v>.md, the release page: date and download, no notes (What's New carries them).
# 2. Checks: VERSION == README title == top CHANGELOG heading == release file
#    (.github\scripts\Test-DocsVersion.ps1, which the Docs workflow also runs);
#    every top-section entry ends with its verification; every docs\media image
#    is used by README; every relative link in the reader docs resolves.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$inv = [Globalization.CultureInfo]::InvariantCulture
$utf8 = [Text.UTF8Encoding]::new($false)
$version = (Get-Content (Join-Path $repo 'VERSION') -Raw).Trim()
$problems = [Collections.Generic.List[string]]::new()

# The public file set, as published path -> source path.
$publicFiles = [ordered]@{}
foreach ($f in 'README.md', 'CHANGELOG.md', 'PRIVACY.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md',
    'BUILDING.md', 'CONTRIBUTING.md', 'TRADEMARKS.md', 'docs/HELP.md', 'docs/SUPPORT_MATRIX.md', 'docs/VENDORS.md') { $publicFiles[$f] = Join-Path $repo $f }
$githubRoot = $repo
Get-ChildItem -LiteralPath (Join-Path $githubRoot '.github') -Recurse -File -Force | ForEach-Object {
    $publicFiles[[IO.Path]::GetRelativePath($githubRoot, $_.FullName).Replace('\', '/')] = $_.FullName
}

# 1. docs/RELEASE_<v>.md: its date from the top CHANGELOG section.
$changelog = (Get-Content (Join-Path $repo 'CHANGELOG.md') -Raw -Encoding utf8) -replace "`r`n", "`n"
$sections = [regex]::Matches($changelog, '(?ms)^## (\d+\.\d+\.\d+) — ([^\n]+)\n(.*?)(?=^## |\z)')
if (-not $sections.Count) { throw 'CHANGELOG.md has no "## X.Y.Z — Month D, YYYY" section.' }
$top = $sections[0]
$topVersion = $top.Groups[1].Value
$date = $top.Groups[2].Value.Trim()
$body = $top.Groups[3].Value.Trim()
$parsed = [datetime]::MinValue
if (-not [datetime]::TryParseExact($date, 'MMMM d, yyyy', $inv, 'None', [ref]$parsed)) { $problems.Add("Top CHANGELOG date '$date' is not 'Month D, YYYY'.") }
if ($topVersion -ne $version) { throw "VERSION is $version but the top CHANGELOG section is $topVersion. Write the $version entry first." }

foreach ($line in $body -split "`n" | Where-Object { $_ -match '^\s*- ' }) {
    # "Checked in code only; needs a Radeon." names the hardware it still needs.
    if ($line -notmatch '_(Verified: on screen|Verified: end-to-end|Checked in code only(; needs an? [^._]+)?)\._$') {
        $problems.Add("$version entry without its verification: $($line.Substring(0, [Math]::Min(80, $line.Length)))")
    }
}

$base = 'https://github.com/ImAvgErix/ExoLauncher'
# The release page carries no notes: what changed is Exo's own What's New sheet (ui/src/lib/whatsNew.ts).
$release = @(
    "# Exo Launcher $version", '',
    "_$($date)_", '',
    "[Download ExoLauncher-Setup.exe]($base/releases/download/v$version/ExoLauncher-Setup.exe) · [Privacy policy]($base/blob/main/PRIVACY.md)", ''
) -join "`r`n"
$release = $release -replace "(?<!`r)`n", "`r`n"
$releaseName = "docs/RELEASE_$version.md"
[IO.File]::WriteAllText((Join-Path $repo $releaseName), $release, $utf8)
$publicFiles[$releaseName] = Join-Path $repo $releaseName
Write-Host "Wrote $releaseName from the CHANGELOG ($date)."

# docs/media: every image is used by README, and only those are published.
$readme = Get-Content (Join-Path $repo 'README.md') -Raw -Encoding utf8
foreach ($img in Get-ChildItem (Join-Path $repo 'docs/media') -File | Where-Object Name -notlike '.*') {
    if ($readme -match [regex]::Escape("docs/media/$($img.Name)")) { $publicFiles["docs/media/$($img.Name)"] = $img.FullName }
    else { $problems.Add("docs/media/$($img.Name) is not used by README.md.") }
}

# 2. Version sync, on a staged copy of the reader-facing docs.
$stage = Join-Path ([IO.Path]::GetTempPath()) "exo-public-docs-$PID"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
foreach ($entry in $publicFiles.GetEnumerator()) {
    $target = Join-Path $stage $entry.Key
    New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
    Copy-Item -LiteralPath $entry.Value -Destination $target
}
& pwsh -NoProfile -File (Join-Path $stage '.github/scripts/Test-DocsVersion.ps1') -Root $stage -Version $version
if ($LASTEXITCODE) { $problems.Add('Version sync failed (above).') }

# Relative links in the public Markdown resolve inside the public file set.
foreach ($md in $publicFiles.Keys | Where-Object { $_ -like '*.md' }) {
    $text = Get-Content (Join-Path $stage $md) -Raw -Encoding utf8
    $targets = [regex]::Matches($text, '\]\(([^)\s]+)(?:\s+"[^"]*")?\)|(?:href|src)="([^"]+)"') |
        ForEach-Object { if ($_.Groups[1].Success) { $_.Groups[1].Value } else { $_.Groups[2].Value } }
    foreach ($t in $targets) {
        if ($t -match '^(?:[a-z][a-z0-9+.-]*:|#)') { continue }
        $path = [Uri]::UnescapeDataString(($t -split '#')[0])
        $resolved = [IO.Path]::GetFullPath((Join-Path (Split-Path (Join-Path $stage $md)) $path))
        if (-not $resolved.StartsWith($stage) -or -not (Test-Path -LiteralPath $resolved)) { $problems.Add("$md links '$t', which doesn't exist.") }
    }
}

if ($problems.Count) {
    Remove-Item $stage -Recurse -Force
    $problems | ForEach-Object { Write-Host "FAIL $_" -ForegroundColor Red }
    exit 1
}
Write-Host "Public docs check out: $($publicFiles.Count) files for $version."
# Older release notes go only once the new set checks out, so a failed run loses nothing.
Get-ChildItem (Join-Path $repo 'docs') -Filter 'RELEASE_*.md' | Where-Object Name -ne "RELEASE_$version.md" | Remove-Item

Remove-Item $stage -Recurse -Force
