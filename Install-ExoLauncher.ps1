# Exo Launcher bootstrap installer.
# Downloads the latest release asset from GitHub when available, verifies SHA-256,
# and launches it. Prefer building from source until a release is published.
#
# One-liner: irm https://raw.githubusercontent.com/ImAvgErix/exo-launcher/main/Install-ExoLauncher.ps1 | iex
param([switch]$Force)

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Windows only.' }
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Repo = 'ImAvgErix/exo-launcher'
Write-Host ''
Write-Host '  Exo Launcher - checking GitHub releases...' -ForegroundColor Cyan
Write-Host ''

$headers = @{
    'User-Agent' = 'ExoLauncher-Installer/0.1'
    'Accept'     = 'application/vnd.github+json'
}

try {
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers $headers
}
catch {
    Write-Host '  No release published yet (or network error).' -ForegroundColor Yellow
    Write-Host '  Clone and run from source:' -ForegroundColor Yellow
    Write-Host '    git clone https://github.com/ImAvgErix/exo-launcher.git' -ForegroundColor DarkGray
    Write-Host '    cd exo-launcher' -ForegroundColor DarkGray
    Write-Host '    pwsh -File Run-ExoLauncher.ps1' -ForegroundColor DarkGray
    Write-Host ''
    return
}

$asset = @($release.assets) |
    Where-Object { $_.name -match 'ExoLauncher.*\.(exe|zip)$' } |
    Select-Object -First 1

if (-not $asset) {
    Write-Host "  Latest release $($release.tag_name) has no ExoLauncher asset yet." -ForegroundColor Yellow
    Write-Host '  Build from source with Run-ExoLauncher.ps1.' -ForegroundColor Yellow
    Write-Host ''
    return
}

$destDir = Join-Path $env:LOCALAPPDATA 'ExoLauncher\app'
New-Item -ItemType Directory -Force -Path $destDir | Out-Null
$sfx = Join-Path $env:TEMP ('ExoLauncher-setup-' + [guid]::NewGuid().ToString('N') + [IO.Path]::GetExtension($asset.name))

Write-Host "[*] $($release.tag_name) -> $sfx" -ForegroundColor DarkGray
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $sfx -UseBasicParsing -Headers @{ 'User-Agent' = 'ExoLauncher-Installer/0.1' } -TimeoutSec 300

$downloaded = Get-Item -LiteralPath $sfx
if ($asset.size -and $downloaded.Length -ne [long]$asset.size) {
    Remove-Item -LiteralPath $sfx -Force -ErrorAction SilentlyContinue
    throw "Downloaded asset has the wrong size ($($downloaded.Length); expected $($asset.size))."
}

$expectedDigest = [string]$asset.digest
if ($expectedDigest -match '^sha256:([0-9a-fA-F]{64})$') {
    $expectedHash = $Matches[1].ToLowerInvariant()
    $actualHash = (Get-FileHash -LiteralPath $sfx -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        Remove-Item -LiteralPath $sfx -Force -ErrorAction SilentlyContinue
        throw 'Downloaded asset failed its SHA-256 integrity check.'
    }
    Write-Host '[+] SHA-256 verified' -ForegroundColor Green
}
else {
    Write-Host '[!] GitHub did not provide a SHA-256 digest; continuing without hash check.' -ForegroundColor Yellow
}

if ($asset.name -like '*.zip') {
    Expand-Archive -LiteralPath $sfx -DestinationPath $destDir -Force
    $exe = Get-ChildItem -Path $destDir -Filter 'ExoLauncher.exe' -Recurse | Select-Object -First 1
    if (-not $exe) { throw 'Zip extracted but ExoLauncher.exe was not found.' }
    Write-Host "[+] Installed to $($exe.FullName)" -ForegroundColor Green
    Start-Process -FilePath $exe.FullName -WorkingDirectory $exe.DirectoryName
}
else {
    $target = Join-Path $destDir 'ExoLauncher.exe'
    Move-Item -LiteralPath $sfx -Destination $target -Force
    Write-Host "[+] Installed to $target" -ForegroundColor Green
    Start-Process -FilePath $target -WorkingDirectory $destDir
}

Write-Host ''
