param([string]$Repository = (Join-Path $PSScriptRoot '..'))
$ErrorActionPreference = 'Stop'
$version = '8.30.1'
$expectedSha = 'd29144deff3a68aa93ced33dddf84b7fdc26070add4aa0f4513094c8332afc4e'
$toolRoot = Join-Path ([IO.Path]::GetTempPath()) ('exo-gitleaks-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $toolRoot | Out-Null
$archive = Join-Path $toolRoot 'gitleaks.zip'
Invoke-WebRequest -Uri "https://github.com/gitleaks/gitleaks/releases/download/v$version/gitleaks_${version}_windows_x64.zip" -OutFile $archive
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expectedSha) { throw 'Gitleaks archive checksum mismatch.' }
Expand-Archive -LiteralPath $archive -DestinationPath $toolRoot
& (Join-Path $toolRoot 'gitleaks.exe') git $Repository --redact --log-opts=--all --config (Join-Path $Repository '.gitleaks.toml')
if ($LASTEXITCODE -ne 0) { throw 'The Git history secret scan failed.' }
