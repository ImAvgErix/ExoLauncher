# Attack 1 (supporting) — the random loopback port is not a real barrier.
#
# SpotifyControls picks a RANDOM free loopback port per session "so any local
# process cannot find and drive the user's Spotify" (its own comment). This
# shows a same-user process trivially recovers that port by reading the
# target's command line via CIM/WMI (Win32_Process.CommandLine) — the same way
# the attacker in attack1_loopback_devtools.ps1 could, defeating the obscurity.
#
# Uses the throwaway victim.mjs, never the user's real Spotify. It will also
# read (never connect to) a running real Spotify's flags if present.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$node = (Get-Command node -ErrorAction SilentlyContinue).Source
if (-not $node) { Write-Host 'NOT PROVEN: node unavailable.'; exit 3 }

$l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$l.Start(); $port = ([System.Net.IPEndPoint]$l.LocalEndpoint).Port; $l.Stop()
$p = Start-Process -FilePath $node -ArgumentList @("--inspect=127.0.0.1:$port", (Join-Path $here 'victim.mjs')) -PassThru -WindowStyle Hidden
try {
    Start-Sleep -Milliseconds 800
    $cl = (Get-CimInstance Win32_Process -Filter "ProcessId=$($p.Id)").CommandLine
    Write-Host "Victim real loopback port: $port"
    Write-Host "Command line read by a same-user process: $cl"
    if ($cl -match '(?:--inspect|--remote-debugging-port)[=\s]127\.0\.0\.1:(\d+)' -or $cl -match '(?:--inspect)=127\.0\.0\.1:(\d+)') {
        Write-Host "DISCOVERED PORT: $($Matches[1]) — per-session randomness provides only obscurity, not access control."
    }
} finally {
    try { if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force } } catch {}
}

Write-Host "`n=== Real Spotify flag check (read-only, NO connection) ==="
# The attack above works on any DevTools port. Exo is protected only if it never gives Spotify one:
# since the fix it launches Spotify with --remote-debugging-pipe (Helpers/SpotifyDevTools.cs).
$controls = Get-Content (Join-Path $PSScriptRoot '..\..\..\ExoLauncher\Helpers\SpotifyControls.cs') -Raw
$staticPort = $controls -match 'remote-debugging-port'
$staticPipe = $controls -match 'SpotifyDevTools\.PipeArgument'
$running = @(Get-CimInstance Win32_Process -Filter "Name='Spotify.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -match 'remote-debugging-port' })
Write-Host "Exo's Spotify launch: pipe=$staticPipe, port=$staticPort; running Spotify with a port: $($running.Count)"
if ($staticPipe -and -not $staticPort -and $running.Count -eq 0) {
    Write-Host 'RESULT for Exo: REFUSED - Spotify gets a private pipe, so there is no port for another program to find.'
} else {
    Write-Host 'RESULT for Exo: PROVEN-VULNERABLE - Spotify runs with a DevTools port.'
    exit 1
}
