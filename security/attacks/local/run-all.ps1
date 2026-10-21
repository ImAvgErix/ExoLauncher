# Runs the whole local attack suite and prints a pass/fail summary.
#   1. Attack 1 — loopback DevTools port hijack (script, throwaway victim)
#   2-5. harness\LocalAttacks.csproj (C#, links real product code)
# Temp folders / throwaway processes only; never touches real Spotify/Discord/Exo.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = (Resolve-Path (Join-Path $here '..\..\..')).Path

Write-Host '################ Attack 1: loopback DevTools port ################'
& (Join-Path $here 'attack1_loopback_devtools.ps1')
$a1 = $LASTEXITCODE
Write-Host ''
& (Join-Path $here 'attack1_port_discovery.ps1')

Write-Host ''
Write-Host '################ Attacks 2-5: C# harness ################'
# argv-echo stub (harmless argv recorder / long-lived target).
dotnet build -c Release --nologo -v quiet (Join-Path $here 'argv-echo\argv-echo.csproj') | Out-Null
# The attack harness (compile-links the real product predicates).
dotnet build -c Release --nologo -v quiet (Join-Path $here 'harness\LocalAttacks.csproj') | Out-Null
dotnet run -c Release --no-build --project (Join-Path $here 'harness\LocalAttacks.csproj')
$a25 = $LASTEXITCODE

Write-Host ''
Write-Host "Attack 1 exit: $a1 (0=proven-vulnerable)"
Write-Host "Attacks 2-5 exit: $a25 (0=all assertions held)"
exit ([int]($a25 -ne 0))
