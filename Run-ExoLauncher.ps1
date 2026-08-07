#Requires -Version 5.1
<#
.SYNOPSIS
  Build (if needed) and launch Exo Launcher.

.EXAMPLE
  pwsh -File .\Run-ExoLauncher.ps1
  .\Run-ExoLauncher.ps1 -NoBuild
#>
param(
    [switch]$NoBuild,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$Project = Join-Path $Root 'ExoLauncher\ExoLauncher.csproj'
$UiDir = Join-Path $Root 'ui'
$WwwIndex = Join-Path $Root 'ExoLauncher\wwwroot\index.html'

function Get-ExoLauncherExe {
    $tfms = @(
        'net10.0-windows10.0.26100.0',
        'net10.0-windows10.0.19041.0'
    )
    $candidates = foreach ($tfm in $tfms) {
        Join-Path $Root "ExoLauncher\bin\x64\$Configuration\$tfm\win-x64\ExoLauncher.exe"
        Join-Path $Root "ExoLauncher\bin\x64\$Configuration\$tfm\ExoLauncher.exe"
        Join-Path $Root "ExoLauncher\bin\$Configuration\$tfm\win-x64\ExoLauncher.exe"
        Join-Path $Root "ExoLauncher\bin\$Configuration\$tfm\ExoLauncher.exe"
    }
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c) { return $c }
    }
    $hit = Get-ChildItem -Path (Join-Path $Root 'ExoLauncher\bin') -Filter 'ExoLauncher.exe' -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($hit) { return $hit.FullName }
    return $null
}

Write-Host ''
Write-Host "  Exo Launcher  -  PowerShell $($PSVersionTable.PSVersion)" -ForegroundColor Cyan
Write-Host ''

if (-not $NoBuild) {
    if (-not (Test-Path -LiteralPath $WwwIndex) -or (Test-Path (Join-Path $UiDir 'package.json'))) {
        if (Test-Path (Join-Path $UiDir 'package.json')) {
            Write-Host '[*] Building UI...' -ForegroundColor DarkGray
            Push-Location $UiDir
            try {
                if (-not (Test-Path 'node_modules')) {
                    npm ci
                    if ($LASTEXITCODE -ne 0) { npm install }
                }
                npm run build
                if ($LASTEXITCODE -ne 0) { throw "UI build failed (exit $LASTEXITCODE)" }
            }
            finally { Pop-Location }
        }
    }

    Write-Host '[*] Building ExoLauncher (x64 / win-x64)...' -ForegroundColor DarkGray
    & dotnet build $Project -c $Configuration -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)" }
}

$exe = Get-ExoLauncherExe
if (-not $exe) {
    throw 'Could not locate ExoLauncher.exe after build.'
}

Write-Host "[+] Starting $exe" -ForegroundColor Green
Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe -Parent)
