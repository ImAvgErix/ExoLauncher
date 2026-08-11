# Exo Launcher bootstrap installer.
# Downloads the latest release asset from GitHub when available, verifies SHA-256,
# and launches it. Prefer building from source until a release is published.
#
# One-liner: irm https://raw.githubusercontent.com/ImAvgErix/ExoLauncher/main/Install-ExoLauncher.ps1 | iex
param([switch]$Force)

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Windows only.' }
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Repo = 'ImAvgErix/ExoLauncher'
Write-Host ''
Write-Host '  Exo Launcher - checking GitHub releases...' -ForegroundColor Cyan
Write-Host ''

$headers = @{
    'User-Agent' = 'ExoLauncher-Installer/1.0'
    'Accept'     = 'application/vnd.github+json'
}

try {
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers $headers
}
catch {
    Write-Host '  No release published yet (or network error).' -ForegroundColor Yellow
    Write-Host '  Clone and run from source:' -ForegroundColor Yellow
    Write-Host '    git clone https://github.com/ImAvgErix/ExoLauncher.git' -ForegroundColor DarkGray
    Write-Host '    cd ExoLauncher' -ForegroundColor DarkGray
    Write-Host '    pwsh -File Run-ExoLauncher.ps1' -ForegroundColor DarkGray
    Write-Host ''
    return
}

$asset = @($release.assets) |
    Where-Object { $_.name -in @('ExoLauncher.exe', 'ExoLauncher-Setup.exe') } |
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
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $sfx -UseBasicParsing -Headers @{ 'User-Agent' = 'ExoLauncher-Installer/1.0' } -TimeoutSec 300

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
    Remove-Item -LiteralPath $sfx -Force -ErrorAction SilentlyContinue
    throw 'GitHub did not provide a SHA-256 digest. Nothing was installed.'
}

$versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($sfx)
if ($versionInfo.ProductName -ne 'Exo Launcher' -or
    $versionInfo.FileDescription -ne 'Exo Launcher Setup') {
    Remove-Item -LiteralPath $sfx -Force -ErrorAction SilentlyContinue
    throw 'The verified release asset is not an Exo Launcher installer.'
}

# The release asset is an NSIS installer, not the installed application. Let it
# perform its atomic app.incoming -> app swap; never rename it to ExoLauncher.exe.
$installer = Start-Process -FilePath $sfx -ArgumentList '/S' -WorkingDirectory ([IO.Path]::GetDirectoryName($sfx)) -Wait -PassThru -WindowStyle Hidden
if ($installer.ExitCode -ne 0) { throw "Installer failed with exit code $($installer.ExitCode)." }
$target = Join-Path $destDir 'ExoLauncher.exe'
if (-not (Test-Path -LiteralPath $target)) { throw 'Installer completed but ExoLauncher.exe was not found.' }
Remove-Item -LiteralPath $sfx -Force -ErrorAction SilentlyContinue
Write-Host "[+] Installed to $target" -ForegroundColor Green
Start-Process -FilePath $target -WorkingDirectory $destDir

Write-Host ''
