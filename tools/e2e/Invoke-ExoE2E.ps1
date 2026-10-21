<#
.SYNOPSIS
  End-to-end test of the INSTALLED Exo Launcher on this PC. Produces a verifiable artifact.

.DESCRIPTION
  Closes Exo politely, relaunches it with the shell WebView reachable on a loopback-only
  CDP port (WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS in the child's environment only), drives
  the real UI through tools\e2e\exo-e2e.mjs, checks the host side natively (window state,
  app.log, Steam cache, CPU), then closes Exo, verifies the debug port is
  gone, restores every file it touched byte-for-byte, and starts Exo again normally.

  Artifact: e2e-reports\<yyyyMMdd-HHmmss>\ with report.json, summary.md, screenshots\*.png,
  app-log-excerpt.txt and SHA256SUMS.txt (verify: cd into the folder, `sha256sum -c SHA256SUMS.txt`
  in Git Bash, or run this script with -Verify <folder>).

  Exit code: 0 all PASS/SKIP, 1 any FAIL, 2 the harness itself could not run.

.PARAMETER Exe          Exo executable. Default %LOCALAPPDATA%\ExoLauncher\app\ExoLauncher.exe.
.PARAMETER Onboarding   Also walk first-run onboarding (settings.json is backed up, flipped, restored, verified).
.PARAMETER CaptureHostedWindows  Add desktop screenshots of the Discord/Music/Devices rooms (they show the
                        provider's own window, which can contain private messages; off by default).
.PARAMETER UpscalerCycle  Also restore one game's upscaler files (a game without anti-cheat whose originals
                        Exo saved) and bring them back with Library > Upscalers > Update all: the game ends on
                        the newest files it had. Writes into that game's folder.
.PARAMETER RealGame     A Steam game id (steam:<app>, never Riot or kernel anti-cheat) to start for real outside Exo
                        through steam://rungameid: game.runState, its linked mouse profile, Exo's Stop, and the
                        default profile coming back. It shows the game's window for a few seconds.
.PARAMETER OutRoot      Report root. Default <repo>\e2e-reports.
.PARAMETER Verify       Re-check an existing report folder's SHA256SUMS.txt and exit.
#>
[CmdletBinding()]
param(
    [string]$Exe = (Join-Path $env:LOCALAPPDATA 'ExoLauncher\app\ExoLauncher.exe'),
    [switch]$Onboarding,
    [switch]$CaptureHostedWindows,
    [switch]$UpscalerCycle,
    [string]$RealGame,
    [string]$OutRoot = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')) 'e2e-reports'),
    [int[]]$CdpPorts = (9339..9349),
    [int]$IdleSeconds = 30,
    [string]$Verify
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 1

# ------------------------------------------------------------------ verify mode
function Test-Manifest([string]$dir) {
    $manifest = Join-Path $dir 'SHA256SUMS.txt'
    if (-not (Test-Path $manifest)) { Write-Host "no SHA256SUMS.txt in $dir"; return $false }
    $ok = $true
    foreach ($line in Get-Content $manifest) {
        if ($line -notmatch '^([0-9a-f]{64})  (.+)$') { continue }
        $path = Join-Path $dir $Matches[2]
        $actual = if (Test-Path $path) { (Get-FileHash -Algorithm SHA256 $path).Hash.ToLowerInvariant() } else { 'missing' }
        if ($actual -ne $Matches[1]) { Write-Host "MISMATCH $($Matches[2])"; $ok = $false }
    }
    Write-Host ($(if ($ok) { "OK: every file in $dir matches SHA256SUMS.txt" } else { 'FAILED' }))
    return $ok
}
if ($Verify) { exit $(if (Test-Manifest $Verify) { 0 } else { 1 }) }

# ------------------------------------------------------------------ constants
$appData = Join-Path $env:LOCALAPPDATA 'ExoLauncher'
$logPath = Join-Path $appData 'logs\app.log'
$settingsPath = Join-Path $appData 'settings.json'
$cachePath = Join-Path $appData 'library-cache.json'
$prefsPath = Join-Path $appData 'web-client-preferences.json'
$markerPath = Join-Path $appData '.onboarded'
$driver = Join-Path $PSScriptRoot 'exo-e2e.mjs'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$outDir = Join-Path $OutRoot $runId
$shots = Join-Path $outDir 'screenshots'
$backupDir = Join-Path ([IO.Path]::GetTempPath()) "exo-e2e-backup-$runId"   # holds settings (may contain keys): never in the artifact
New-Item -ItemType Directory -Force $shots, $backupDir | Out-Null

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class ExoE2EWin {
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    public static int[] ClientOrigin(IntPtr h) { var p = new POINT(); ClientToScreen(h, ref p); return new[] { p.X, p.Y }; }
    [DllImport("user32.dll")] static extern int GetWindowRgnBox(IntPtr h, out RECT r);
    /// Visible rectangle: the window rect clipped by its window region (Exo crops hosted clients' own title bars).
    public static int[] VisibleRect(IntPtr h) {
        RECT w; GetWindowRect(h, out w); RECT r;
        int kind = GetWindowRgnBox(h, out r);   // 0 = error/no region
        if (kind == 0) return new[] { w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top };
        return new[] { w.Left + r.Left, w.Top + r.Top, r.Right - r.Left, r.Bottom - r.Top };
    }
    public static int[] Rect(IntPtr h) { RECT r; GetWindowRect(h, out r); return new[] { r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top }; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int size);
    /// Every top-level window of a process: "hwnd|class|title|visible|owner|exstyle hex|backdrop" (backdrop -1 when DWM will not say).
    public static string[] OfProcess(int target) {
        var list = new List<string>();
        EnumWindows((h, l) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid == target) {
                var c = new StringBuilder(128); GetClassName(h, c, 128); var t = new StringBuilder(256); GetWindowText(h, t, 256);
                int b; if (DwmGetWindowAttribute(h, 38, out b, 4) != 0) b = -1;
                list.Add(h.ToInt64() + "|" + c + "|" + t + "|" + IsWindowVisible(h) + "|" + GetWindow(h, 4).ToInt64() + "|" + GetWindowLongPtr(h, -20).ToInt64().ToString("X") + "|" + b);
            }
            return true;
        }, IntPtr.Zero);
        return list.ToArray();
    }
    // Real mouse input for the title-band drag check (window chrome only; never a store client).
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool repaint);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    public static int[] CursorPos() { POINT p; GetCursorPos(out p); return new[] { p.X, p.Y }; }
    public static void SetCursor(int x, int y) { SetCursorPos(x, y); }
    /// Top-level window under a screen point.
    public static IntPtr RootAt(int x, int y) { var p = new POINT { X = x, Y = y }; return GetAncestor(WindowFromPoint(p), 2); }
    /// WM_NCHITTEST of the window under a point (1 client, 2 caption, 9 maximize button).
    public static long HitTest(int x, int y) {
        var p = new POINT { X = x, Y = y };
        return SendMessage(WindowFromPoint(p), 0x84, IntPtr.Zero, (IntPtr)((y << 16) | (x & 0xFFFF))).ToInt64();
    }
    /// Press at (x, y), move by (dx, dy) in steps, release.
    public static void Drag(int x, int y, int dx, int dy) {
        SetCursorPos(x, y); System.Threading.Thread.Sleep(80);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(120);
        for (int i = 1; i <= 12; i++) { SetCursorPos(x + dx * i / 12, y + dy * i / 12); System.Threading.Thread.Sleep(25); }
        System.Threading.Thread.Sleep(120);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }
    public static void DoubleClick(int x, int y) {
        SetCursorPos(x, y); System.Threading.Thread.Sleep(80);
        for (int i = 0; i < 2; i++) { mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(60); }
    }
    /// Top-level windows whose owner is `owner`: "hwnd|pid|class|visible|visible x,y,w,h|window x,y,w,h".
    public static string[] Owned(IntPtr owner) {
        var list = new List<string>();
        EnumWindows((h, l) => {
            if (GetWindow(h, 4) == owner) {
                uint pid; GetWindowThreadProcessId(h, out pid);
                var sb = new StringBuilder(128); GetClassName(h, sb, 128);
                list.Add(h.ToInt64() + "|" + pid + "|" + sb + "|" + IsWindowVisible(h) + "|" + string.Join(",", VisibleRect(h)) + "|" + string.Join(",", Rect(h)));
            }
            return true;
        }, IntPtr.Zero);
        return list.ToArray();
    }
}
'@
Add-Type -AssemblyName System.Drawing
function Save-ScreenRect([int[]]$r, [string]$path) {
    $bmp = [System.Drawing.Bitmap]::new($r[2], $r[3]); $g = [System.Drawing.Graphics]::FromImage($bmp)
    try { $g.CopyFromScreen($r[0], $r[1], 0, 0, $bmp.Size); $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png) } finally { $g.Dispose(); $bmp.Dispose() }
}

# ------------------------------------------------------------------ report plumbing
$checks = [System.Collections.Generic.List[object]]::new()
$notes = [System.Collections.Generic.List[string]]::new()
function Add-Check([string]$Id, [string]$Name, [string]$Status, [string]$Reason = '', [hashtable]$Evidence = @{}, [double]$Ms = 0, [string[]]$Shots = @()) {
    $checks.Add([ordered]@{ id = $Id; name = $Name; status = $Status; reason = $Reason; durationMs = [math]::Round($Ms); evidence = $Evidence; screenshots = @($Shots) })
    $color = @{ PASS = 'Green'; FAIL = 'Red'; SKIP = 'Yellow' }[$Status]
    Write-Host ('{0,-4} {1,-34} {2}' -f $Status, $Id, $Reason) -ForegroundColor $color
}
function Measure-Check([string]$Id, [string]$Name, [scriptblock]$Body) {
    # $Body returns @{ fail = @(...); skip = 'reason'; evidence = @{}; shots = @() }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try {
        $r = & $Body
        $fail = @($r.fail | Where-Object { $_ })
        $status = if ($r.skip) { 'SKIP' } elseif ($fail.Count) { 'FAIL' } else { 'PASS' }
        $reason = if ($r.skip) { $r.skip } else { $fail -join '; ' }
        $ev = if ($r.evidence) { $r.evidence } else { @{} }
        $sh = if ($r.shots) { @($r.shots) } else { @() }
        Add-Check $Id $Name $status $reason $ev $sw.Elapsed.TotalMilliseconds $sh
    } catch {
        Add-Check $Id $Name 'FAIL' ('error: ' + $_.Exception.Message) @{} $sw.Elapsed.TotalMilliseconds
    }
}
function Redact([string]$text) {
    if (-not $text) { return $text }
    $text = $text.Replace($env:LOCALAPPDATA, '%LOCALAPPDATA%').Replace($env:APPDATA, '%APPDATA%').Replace($env:USERPROFILE, '%USERPROFILE%')
    $text = [regex]::Replace($text, [regex]::Escape($env:USERNAME), '<user>', 'IgnoreCase')
    $text = [regex]::Replace($text, [regex]::Escape($env:COMPUTERNAME), '<pc>', 'IgnoreCase')
    $text = [regex]::Replace($text, '[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}', '<email>', 'IgnoreCase')
    # Discord's window title names the open DM or channel.
    if ($text -match 'Discord') { $text = [regex]::Replace($text, 'title "[^"]*"', 'title "<redacted>"') }
    return $text
}

$nodeExe = (Get-Command node -ErrorAction SilentlyContinue).Source
if (-not $nodeExe) { $nodeExe = 'C:\Program Files\nodejs\node.exe' }
function Invoke-Driver([string]$Suite, [int]$Port, [string]$Arg = '', [int]$TimeoutSec = 300) {
    $argList = @("`"$driver`"", $Suite, '--port', $Port, '--out', "`"$outDir`"", '--settings', "`"$settingsPath`"", '--cache', "`"$cachePath`"")
    if ($Arg) { $argList += @('--arg', "`"$Arg`"") }
    $so = Join-Path $backupDir "$Suite.out"; $se = Join-Path $backupDir "$Suite.err"
    $p = Start-Process -FilePath $nodeExe -ArgumentList $argList -NoNewWindow -PassThru -RedirectStandardOutput $so -RedirectStandardError $se
    if (-not $p.WaitForExit($TimeoutSec * 1000)) { $p.Kill(); throw "driver suite $Suite timed out" }   # node only, never Exo
    $text = Get-Content $so -Raw
    if (-not $text) { throw "driver suite $Suite failed: " + (Get-Content $se -Raw) }
    $r = $text | ConvertFrom-Json
    foreach ($c in $r.checks) {
        $ev = @{}; foreach ($pp in $c.evidence.PSObject.Properties) { $ev[$pp.Name] = $pp.Value }
        Add-Check $c.id $c.name $c.status $c.reason $ev $c.durationMs @($c.screenshots)
    }
    return $r.data
}

# ------------------------------------------------------------------ process helpers
function Get-Exo { @(Get-Process ExoLauncher -ErrorAction SilentlyContinue) }
function Get-ExoWebViewBrowsers {
    @(Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" -ErrorAction SilentlyContinue |
      Where-Object { $_.CommandLine -and $_.CommandLine -like "*$appData\webview*" })
}
function Close-Exo([int]$TimeoutSec = 45) {
    foreach ($p in Get-Exo) {
        if ($p.MainWindowHandle -eq 0) { $p.Refresh() }
        if (-not $p.CloseMainWindow()) { throw "Exo (pid $($p.Id)) has no main window to close; close it by hand. The harness never kills Exo." }
        if (-not $p.WaitForExit($TimeoutSec * 1000)) { throw "Exo (pid $($p.Id)) did not exit within $TimeoutSec s after a normal close. Not killed." }
    }
    # The shared WebView2 browser and hosted clients close with Exo; the next launch needs them gone
    # (a surviving browser would be reused without the debug port).
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline -and ((Get-ExoWebViewBrowsers).Count -or @(Get-Process Discord, Spotify -ErrorAction SilentlyContinue).Count)) { Start-Sleep -Milliseconds 250 }
    if ((Get-ExoWebViewBrowsers).Count) { throw 'Exo WebView2 processes still running 30 s after Exo exited' }
}
function Start-ExoDebug([int]$Port) {
    # Through WMI, so Exo runs outside whatever app package started this script (an MSIX-packaged
    # terminal redirects a child's new AppData files into its own sandbox). The debug port is in
    # Exo's environment only: this script's environment plus the WebView2 argument.
    $vars = @([Environment]::GetEnvironmentVariables().GetEnumerator() |
        Where-Object { $_.Key -notin 'WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS', 'EXO_CDP', 'EXOOS_CDP', 'AETHER_CDP', 'EXO_CDP_PORT' } |
        ForEach-Object { "$($_.Key)=$($_.Value)" }) + "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=$Port"
    $startup = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ EnvironmentVariables = [string[]]$vars }
    $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = "`"$Exe`""; CurrentDirectory = (Split-Path $Exe); ProcessStartupInformation = $startup }
    if ($r.ReturnValue -ne 0) { throw "Win32_Process.Create returned $($r.ReturnValue) for Exo" }
    return [Diagnostics.Process]::GetProcessById([int]$r.ProcessId)
}
function Start-ExoNormal {
    # Through the shell so Exo inherits Explorer's environment, not this script's, and outlives it.
    Start-Process explorer.exe -ArgumentList "`"$Exe`""
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline -and -not (Get-Exo | Where-Object { $_.MainWindowHandle -ne 0 })) { Start-Sleep -Milliseconds 250 }
    return (Get-Exo | Select-Object -First 1)
}
function Test-HostedFit([string]$win, $surface, $dpr) {
    # The provider window must sit exactly over the room's surface (client origin + CSS rect * DPR).
    if (-not $win -or -not $surface) { return $null }
    $w = [int[]]$win.Split('|')[4].Split(','); $o = [ExoE2EWin]::ClientOrigin($hwnd); $d = [double]$dpr
    $want = @([math]::Round($o[0] + $surface[0] * $d), [math]::Round($o[1] + $surface[1] * $d), [math]::Round($surface[2] * $d), [math]::Round($surface[3] * $d))
    $off = [math]::Max([math]::Max([math]::Abs($w[0] - $want[0]), [math]::Abs($w[1] - $want[1])), [math]::Max([math]::Abs($w[2] - $want[2]), [math]::Abs($w[3] - $want[3])))
    return @{ window = $w; expected = $want; maxOffsetPx = $off }
}
function Get-Listeners([int]$Port) { @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) }
function Wait-MainWindow([Diagnostics.Process]$p, [int]$TimeoutSec = 30) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $p.Refresh()
        if ($p.HasExited) { throw 'Exo exited during startup' }
        if ($p.MainWindowHandle -ne 0 -and [ExoE2EWin]::IsWindowVisible($p.MainWindowHandle)) { return $p.MainWindowHandle }
        Start-Sleep -Milliseconds 100
    }
    return [IntPtr]::Zero
}

# ------------------------------------------------------------------ log helpers
$script:logRotated = $false
function Read-From([string]$path, [long]$offset) {
    if (-not (Test-Path $path)) { return '' }
    $fs = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')
    try { if ($offset -le $fs.Length) { $fs.Seek($offset, 'Begin') | Out-Null } else { return '' }; return [IO.StreamReader]::new($fs).ReadToEnd() } finally { $fs.Dispose() }
}
function Get-RunLog {
    # Every line appended since the run started, across AppLog's 2 MB rotation to app.log.1.
    $len = if (Test-Path $logPath) { (Get-Item $logPath).Length } else { 0 }
    if ($len -lt $script:logOffsetStart) { $script:logRotated = $true }
    $text = if ($script:logRotated) { (Read-From "$logPath.1" $script:logOffsetStart) + (Read-From $logPath 0) } else { Read-From $logPath $script:logOffsetStart }
    return @($text -split "`r?`n" | Where-Object { $_ })
}
function Wait-LogLine([string]$Pattern, [datetime]$SinceUtc, [int]$TimeoutSec) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
        foreach ($l in Get-RunLog) {
            if ($l -match $Pattern -and $l -match '^\[([^\]]+)\]' -and [DateTimeOffset]::Parse($Matches[1]).UtcDateTime -ge $SinceUtc) { return $l }
        }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    return $null
}
function LineUtc([string]$line) { if ($line -match '^\[([^\]]+)\]') { [DateTimeOffset]::Parse($Matches[1]).UtcDateTime } }

# ------------------------------------------------------------------ files the run may touch
# installed-games.json: runstate.outside adds and removes a portable game; store\wishlist-v2.json: store.wishlist adds and removes one.
$installedPath = Join-Path $appData 'installed-games.json'
$wishlistPath = Join-Path $appData 'store\wishlist-v2.json'
$guarded = @($settingsPath, $markerPath, $prefsPath, $installedPath, $wishlistPath)
$original = @{}
function Save-Guarded {
    foreach ($f in $guarded) {
        $b = Join-Path $backupDir ([IO.Path]::GetFileName($f) + '.bak')
        if (Test-Path $f) { Copy-Item $f $b -Force; $original[$f] = (Get-FileHash -Algorithm SHA256 $f).Hash } else { $original[$f] = $null }
    }
}
function Compare-Guarded {
    $diff = @()
    foreach ($f in $guarded) {
        $now = if (Test-Path $f) { (Get-FileHash -Algorithm SHA256 $f).Hash } else { $null }
        if ($now -ne $original[$f]) { $diff += [IO.Path]::GetFileName($f) }
    }
    return $diff
}
function Restore-Guarded {
    foreach ($f in $guarded) {
        $b = Join-Path $backupDir ([IO.Path]::GetFileName($f) + '.bak')
        if ($null -eq $original[$f]) { if (Test-Path $f) { Remove-Item $f -Force } }
        else { [IO.File]::WriteAllBytes($f, [IO.File]::ReadAllBytes($b)) }
    }
}

# ------------------------------------------------------------------ Optimize state
# Every app is always optimized (AppOptimization.IsOn): a stored "off" from an older Exo no longer applies.
function Get-Optimized($settings, [string]$id) { return $id -in 'spotify', 'youtube' }
function Get-SpotifyFiles {
    $apps = Join-Path $env:APPDATA 'Spotify\Apps'
    $spa = Join-Path $apps 'xpui.spa'
    if (-not (Test-Path $spa)) { return $null }
    $ev = [ordered]@{ hash = (Get-FileHash -Algorithm SHA256 $spa).Hash.ToLowerInvariant(); marker = $null }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($spa)
    try {
        foreach ($e in $zip.Entries | Where-Object { $_.FullName -like '*.css' }) {
            $r = [IO.StreamReader]::new($e.Open()); $text = $r.ReadToEnd(); $r.Dispose()
            if ($text -match 'exo-amoled:(v\d+)') { $ev.marker = $Matches[1]; break }
        }
    } finally { $zip.Dispose() }
    try { $state = Get-Content "$spa.exo-state.json" -Raw | ConvertFrom-Json; $ev.stock = $ev.hash -eq $state.originalHash; $ev.recordedPatched = $ev.hash -eq $state.patchedHash } catch { $ev.stock = -not $ev.marker }
    $ev.prefs = @(Get-ChildItem (Join-Path $env:APPDATA 'Spotify\Users') -Recurse -Filter prefs -ErrorAction SilentlyContinue | ForEach-Object { Select-String -Path $_.FullName -Pattern '^ui\.hide_(hpto|promoted_songs)=true$' | ForEach-Object { $_.Line } } | Sort-Object -Unique)
    return $ev
}
$spotifyInstalled = Test-Path (Join-Path $env:APPDATA 'Spotify\Spotify.exe')
# ================================================================== run
$started = Get-Date
Write-Host "Exo E2E $runId -> $outDir"
if (-not (Test-Path $Exe)) { Write-Host "Exo not found at $Exe"; exit 2 }
$nodeVersion = (& $nodeExe --version) 2>$null
if (-not $nodeVersion -or [int]($nodeVersion.TrimStart('v').Split('.')[0]) -lt 22) { Write-Host "Node 22+ required (global WebSocket); found $nodeVersion"; exit 2 }

$exeInfo = (Get-Item $Exe).VersionInfo
$gitCommit = $null; $gitDirty = $null
try { $gitCommit = (git -C $repoRoot rev-parse HEAD 2>$null); $gitDirty = [bool](git -C $repoRoot status --porcelain 2>$null) } catch { }
$steamAtStart = [bool](Get-Process steam -ErrorAction SilentlyContinue)
$wasRunning = [bool](Get-Exo).Count

Save-Guarded
$settingsAtStart = Get-Content $settingsPath -Raw | ConvertFrom-Json
$port = $null; $exo = $null; $hwnd = [IntPtr]::Zero; $webviewVersion = $null; $harnessError = $null
$script:logOffsetStart = 0

try {
    Close-Exo
    $port = $CdpPorts | Where-Object { -not (Get-Listeners $_).Count } | Select-Object -First 1
    if (-not $port) { throw "no free CDP port in $($CdpPorts -join ',')" }

    # ---------------------------------------------------------- 1. startup
    $script:logOffsetStart = if (Test-Path $logPath) { (Get-Item $logPath).Length } else { 0 }
    
    $launchUtc = [DateTime]::UtcNow
    $exo = Start-ExoDebug $port
    Measure-Check 'startup.window' 'Window visible after launch' {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $script:hwnd = Wait-MainWindow $exo 30
        if ($script:hwnd -eq [IntPtr]::Zero) { return @{ fail = @('no visible main window within 30 s') } }
        @{ fail = @(if ([ExoE2EWin]::IsIconic($script:hwnd)) { 'window started minimized' }); evidence = @{ visibleAfterMs = [math]::Round($sw.Elapsed.TotalMilliseconds); rect = [ExoE2EWin]::Rect($script:hwnd) } }
    }
    $hwnd = $script:hwnd
    Measure-Check 'startup.cdp-loopback' 'Debug port is loopback-only and belongs to the WebView' {
        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Date) -lt $deadline -and -not (Get-Listeners $port).Count) { Start-Sleep -Milliseconds 200 }
        $l = Get-Listeners $port
        if (-not $l.Count) { return @{ fail = @("nothing listens on $port") } }
        $addrs = @($l.LocalAddress | Sort-Object -Unique)
        $owners = @($l | ForEach-Object { (Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).Name } | Sort-Object -Unique)
        try { $script:webviewVersion = (Invoke-RestMethod "http://127.0.0.1:$port/json/version" -TimeoutSec 5).Browser } catch { }
        @{ fail = @(($addrs | Where-Object { $_ -notin '127.0.0.1', '::1' } | ForEach-Object { "listens on $_" }) + ($owners | Where-Object { $_ -ne 'msedgewebview2' } | ForEach-Object { "owned by $_" })); evidence = @{ port = $port; addresses = $addrs; owners = $owners } }
    }
    $webviewVersion = $script:webviewVersion
    Invoke-Driver 'startup' $port | Out-Null
    Measure-Check 'startup.milestones' 'Log milestones: webview-visible, library-covers-painted' {
        $vis = Wait-LogLine 'PERF startup milestone=webview-visible' $launchUtc 60
        $cov = Wait-LogLine 'UI PERF library-covers-painted' $launchUtc 60
        $usable = Wait-LogLine 'UI Library usable after' $launchUtc 5
        $ev = @{}
        if ($vis -match 'elapsedMs=(\d+)') { $ev.webviewVisibleMs = [int]$Matches[1] }
        if ($cov -match 'after (\d+) ms covers=(\d+)') { $ev.coversPaintedAfterMs = [int]$Matches[1]; $ev.coversPainted = [int]$Matches[2] }
        if ($usable -match 'after (\d+) ms') { $ev.libraryUsableAfterMs = [int]$Matches[1] }
        if ($cov) { $ev.launchToCoversMs = [math]::Round(((LineUtc $cov) - $launchUtc).TotalMilliseconds) }
        @{ fail = @($(if (-not $vis) { 'no webview-visible milestone' }), $(if (-not $cov) { 'no library-covers-painted line' })); evidence = $ev }
    }

    # ---------------------------------------------------------- 2. library
    $lib = Invoke-Driver 'library' $port
    # Fast wheel scrolling: identity of every card's art, paint once settled, pixels served per card.
    $null = Invoke-Driver 'covers' $port 'scroll'

    # Upscalers, read only: dlss.status for every installed game, anti-cheat consent, cached
    # read time and one game sheet. Nothing is updated, restored or downloaded.
    $upscalersUtc = [DateTime]::UtcNow
    $null = Invoke-Driver 'upscalers' $port
    Measure-Check 'upscalers.readonly' 'Reading upscaler status wrote nothing into any game' {
        $writes = @(Get-RunLog | Where-Object { $_ -match 'Upscaler (write|restore|download|original replaced):' -and (LineUtc $_) -ge $upscalersUtc })
        @{ fail = @($writes | ForEach-Object { 'app.log: ' + (Redact $_) }); evidence = @{ writeLines = $writes.Count } }
    }
    # Consent to change an anti-cheat game exists only where the user gave it: the run neither
    # grants nor withdraws one (it opens the confirmation and cancels).
    Measure-Check 'upscalers.consent-readonly' 'The run gave or withdrew no anti-cheat consent' {
        $f = @()
        $lines = @(Get-RunLog | Where-Object { $_ -match 'Upscaler consent( withdrawn)?:' -and (LineUtc $_) -ge $upscalersUtc })
        $f += @($lines | ForEach-Object { 'app.log: ' + (Redact $_) })
        # Absent and empty are the same "no consent"; Exo writes {} on its first save after an update.
        $before = if ($settingsAtStart.upscalerAntiCheatConsent) { $settingsAtStart.upscalerAntiCheatConsent | ConvertTo-Json -Compress -Depth 3 } else { "{}" }
        $nowConsent = (Get-Content $settingsPath -Raw | ConvertFrom-Json).upscalerAntiCheatConsent
        $after = if ($nowConsent) { $nowConsent | ConvertTo-Json -Compress -Depth 3 } else { "{}" }
        if ($before -ne $after) { $f += 'settings.json upscalerAntiCheatConsent changed during the run' }
        $count = if ($settingsAtStart.upscalerAntiCheatConsent) { @($settingsAtStart.upscalerAntiCheatConsent.PSObject.Properties).Count } else { 0 }
        @{ fail = $f; evidence = @{ consentLines = $lines.Count; savedConsents = $count } }
    }
    if ($UpscalerCycle) { $null = Invoke-Driver 'upscalercycle' $port '' 720 }

    # System, read only: Graphics (driver and profile), and Shaders, Exo's own board for the shader
    # add-on's engine; the game pages read its compiled records. Exo's old built-in copy is gone.
    $null = Invoke-Driver 'shaders' $port '' 300
    Measure-Check 'shaders.no-builtin' 'Exo keeps no SCSKiller of its own' {
        $old = Join-Path $env:LOCALAPPDATA 'ExoLauncher\tools\scskiller'
        $f = @()
        if (Test-Path $old) { $f += 'tools\scskiller still exists' }
        @{ fail = $f; evidence = @{ oldCopy = (Test-Path $old) } }
    }
    # Exo hands the add-on's engine its installed games (exo.library) when the engine starts; the
    # engine keeps them in its data folder's exo-library.json. The engine starts with its page, not with Exo.
    Measure-Check 'shaders.addon.sync' "The shader add-on's engine got Exo's installed games this run (exo-library.json)" {
        if (-not (Test-Path (Join-Path $appData 'addons\shaders\engine\ExoShaders.Service.exe'))) { return @{ skip = 'the shader add-on is not installed' } }
        if (-not (Get-RunLog | Where-Object { $_ -match 'Shaders add-on engine started' })) { return @{ skip = 'the engine did not run this run (it starts with Exo)' } }
        $file = Join-Path $env:LOCALAPPDATA 'SCSKiller\exo-library.json'
        if (-not (Test-Path $file)) { return @{ fail = @('no SCSKiller\exo-library.json') } }
        $item = Get-Item $file
        $games = @()
        try { $j = Get-Content $file -Raw | ConvertFrom-Json; $games = @(if ($j.PSObject.Properties['games']) { $j.games } else { $j }) } catch { }
        # The engine saves the list only when it changed, so the file matches Exo's installed copies
        # rather than carrying this run's time.
        $installed = @(foreach ($g in (Get-Content $cachePath -Raw | ConvertFrom-Json).games) {
            $copies = if ($g.PSObject.Properties['variants'] -and @($g.variants).Count) { @($g.variants) } else { @($g) }
            foreach ($v in $copies) { if ($v.installed -and $v.PSObject.Properties['path'] -and $v.path -and (Test-Path -LiteralPath $v.path)) { $v.id } }
        }) | Sort-Object -Unique
        $listed = @($games | ForEach-Object { $_.id }) | Sort-Object -Unique
        $missing = @($installed | Where-Object { $_ -notin $listed }); $extra = @($listed | Where-Object { $_ -notin $installed })
        @{ fail = @($(if (-not $games.Count) { 'exo-library.json lists no games' }), $(if ($missing.Count) { "installed in Exo but not sent: $($missing -join ', ')" }), $(if ($extra.Count) { "sent but not installed in Exo: $($extra -join ', ')" }))
           evidence = @{ games = $games.Count; installedInExo = $installed.Count; writtenUtc = $item.LastWriteTimeUtc.ToString('o'); sample = @($games | Select-Object -First 4 | ForEach-Object { $_.title }) } }
    }

    # ---------------------------------------------------------- 2b. Store room and library additions
    # Real stores; every change is put back (wishlist entry removed, the sort restored, Show tools
    # switched back) and the files are guarded. Nothing is bought, launched or opened: the page's
    # store.open and game.launch requests are held and answered with an error.
    $currency = try { [Globalization.RegionInfo]::CurrentRegion.ISOCurrencySymbol } catch { '' }
    $null = Invoke-Driver 'store' $port $currency 300
    $null = Invoke-Driver 'libraryplus' $port '' 400
    # A portable copy of ping.exe added through the add-portable call, started by this run outside Exo.
    $clientsBefore = @(Get-Process steam, EpicGamesLauncher -ErrorAction SilentlyContinue | ForEach-Object Name | Sort-Object -Unique)
    $null = Invoke-Driver 'runstate' $port '' 180
    if ($RealGame) { $null = Invoke-Driver 'realgame' $port $RealGame 400; [ExoE2EWin]::SetForegroundWindow($hwnd) | Out-Null }
    Measure-Check 'runstate.cleanup' 'The temporary portable game left nothing behind, and adding it quit no store app' {
        $clientsAfter = @(Get-Process steam, EpicGamesLauncher -ErrorAction SilentlyContinue | ForEach-Object Name | Sort-Object -Unique)
        $quit = @($clientsBefore | Where-Object { $_ -notin $clientsAfter })
        $left = @(Get-ChildItem $env:USERPROFILE -Directory -Filter 'exo-e2e-portable-*' -ErrorAction SilentlyContinue)
        $cat = if (Test-Path $installedPath) { (Get-Content $installedPath -Raw | ConvertFrom-Json).games } else { @() }
        $entries = @($cat | Where-Object { "$($_.id) $($_.title) $($_.installPath)" -match 'exo-e2e' })
        @{ fail = @(($left | ForEach-Object { "folder $($_.Name) still exists" }) + ($entries | ForEach-Object { "installed-games.json still lists $($_.id)" }) + ($quit | ForEach-Object { "$_ quit while the portable game was added and removed" })); evidence = @{ folders = $left.Count; entries = $entries.Count; storeAppsBefore = $clientsBefore; storeAppsAfter = $clientsAfter } }
    }

    # ---------------------------------------------------------- 3. settings
    $set = Invoke-Driver 'settings' $port
    # The patch version Exo writes today, read from the source so the check follows a bump.
    $spotifyMarker = if ((Get-Content (Join-Path $repoRoot 'ExoLauncher\Helpers\SpotifyAmoledTheme.cs') -Raw) -match 'exo-amoled:(v\d+) \*/') { $Matches[1] } else { 'v?' }
    Measure-Check 'room.spotify.files' "Spotify's xpui.spa matches Optimize Spotify" {
        if (-not $spotifyInstalled) { return @{ skip = 'Spotify is not installed' } }
        $ev = Get-SpotifyFiles
        if (-not $ev) { return @{ fail = @('Spotify has no Apps\xpui.spa') } }
        $on = Get-Optimized $settingsAtStart 'spotify'; $ev.optimized = $on
        # Exo writes xpui.spa only while Spotify is stopped, right before it starts it. A Spotify
        # that was already running when Exo started keeps its files until its next start in Exo.
        $sp = @(Get-Process Spotify -ErrorAction SilentlyContinue | Sort-Object StartTime | Select-Object -First 1)
        $startedThisRun = $sp.Count -and $sp[0].StartTime.ToUniversalTime() -ge $launchUtc
        $ev.spotifyStartedThisRun = [bool]$startedThisRun
        $fail = @()
        if ($on -and $ev.marker -ne $spotifyMarker) { if ($startedThisRun) { $fail += "Optimize Spotify is on but xpui.spa carries $(if ($ev.marker) { $ev.marker } else { 'no' }) patch" } else { $ev.note = 'applies at the next Spotify start in Exo' } }
        if (-not $on -and $ev.marker) { if ($startedThisRun) { $fail += 'Optimize Spotify is off but xpui.spa is still patched' } else { $ev.note = 'stock returns at the next Spotify start in Exo' } }
        @{ fail = $fail; evidence = $ev }
    }

    # Read only: Exo's Spotify preferences, written while Spotify is stopped right before Exo starts it.
    Measure-Check 'room.spotify.prefs' "Optimize Spotify's preferences: track-change notifications off (ui.track_os_notifications_enabled), promos hidden" {
        if (-not $spotifyInstalled) { return @{ skip = 'Spotify is not installed' } }
        if (-not (Get-Optimized $settingsAtStart 'spotify')) { return @{ skip = 'Optimize Spotify is off' } }
        $want = @('ui.hide_hpto=true', 'ui.hide_promoted_songs=true', 'ui.track_os_notifications_enabled=false')
        $files = @(Get-ChildItem (Join-Path $env:APPDATA 'Spotify\Users') -Recurse -Filter prefs -ErrorAction SilentlyContinue)
        if (-not $files.Count) { return @{ skip = 'Spotify has no signed-in user prefs yet' } }
        $sp = @(Get-Process Spotify -ErrorAction SilentlyContinue | Sort-Object StartTime | Select-Object -First 1)
        $startedThisRun = [bool]($sp.Count -and $sp[0].StartTime.ToUniversalTime() -ge $launchUtc)
        $missing = @(foreach ($p in $files) { $lines = Get-Content $p.FullName; foreach ($w in $want) { if ($w -notin $lines) { "$($p.Directory.Name.Substring(0, [math]::Min(6, $p.Directory.Name.Length)))…: $w" } } })
        # The key earlier builds wrote, which Spotify does not read: Exo takes it out on every pass.
        $dead = @(foreach ($p in $files) { if ('ui.track_notifications_enabled=false' -in (Get-Content $p.FullName)) { "$($p.Directory.Name.Substring(0, [math]::Min(6, $p.Directory.Name.Length)))…" } })
        $ev = @{ users = $files.Count; missing = $missing; spotifyStartedThisRun = $startedThisRun; trackNotifications = @(foreach ($p in $files) { (Get-Content $p.FullName | Where-Object { $_ -like 'ui.track_*notifications_enabled=*' }) -join ' ' }); deadKey = $dead }
        if (($missing.Count -or $dead.Count) -and -not $startedThisRun) { return @{ skip = "Spotify kept running from before this run; Exo writes its preferences at its next start in Exo"; evidence = $ev } }
        @{ fail = @($missing | ForEach-Object { "prefs lacks $_" }) + @($dead | ForEach-Object { "prefs still has the dead ui.track_notifications_enabled line ($_)" }); evidence = $ev }
    }

    # ---------------------------------------------------------- 4. Discord room (Exo's own client: nothing hosted to check)
    # The client opens, Ctrl+K finds #mod-chat, Ctrl+1..5 work from its message box. Nothing is sent.
    $null = Invoke-Driver 'room' $port 'Discord'

    # ---------------------------------------------------------- 5. Music room
    $prefs = if (Test-Path $prefsPath) { Get-Content $prefsPath -Raw | ConvertFrom-Json } else { $null }
    $music = if ($prefs -and $prefs.PSObject.Properties['musicService']) { $prefs.musicService } else { 'unknown' }
    $roomMusicUtc = [DateTime]::UtcNow
    $musicRoom = Invoke-Driver 'room' $port 'Music'
    Measure-Check 'room.music.provider' 'Music provider is hosted (no playback started)' {
        $ev = @{ provider = $music; settingsShows = $set.musicService }
        if ($music -in 'youtubemusic', 'youtube', 'apple') { return @{ evidence = $ev } }  # web view; room.music.open and music.apple.native cover it
        # Exo's own Spotify room: Spotify runs hidden as the engine and none of its windows is on screen.
        if ($musicRoom.spotifyNative) {
            $ev.native = $true
            $shown = @([ExoE2EWin]::Owned($hwnd) | Where-Object { $f = $_.Split('|'); (Get-Process -Id $f[1] -ErrorAction SilentlyContinue).Name -eq 'Spotify' -and $f[3] -eq 'True' })
            $ev.visibleSpotifyWindows = $shown.Count
            $ev.spotifyRunning = [bool](Get-Process Spotify -ErrorAction SilentlyContinue)
            return @{ fail = @($(if ($shown.Count) { "Spotify's own window is visible over Exo's Spotify room" }), $(if (-not $ev.spotifyRunning) { 'Spotify is not running behind the room' })); evidence = $ev }
        }
        # Since launch: an earlier suite (Ctrl+4 in library.shortcuts) may have opened Music, and the client stays embedded.
        $line = Wait-LogLine 'desktop client embedded' $launchUtc 45
        if ($line -match 'Discord desktop|SCSKiller desktop') { $line = $null; $line = Get-RunLog | Where-Object { $_ -match 'desktop client embedded' -and $_ -notmatch 'Discord|SCSKiller' -and (LineUtc $_) -ge $launchUtc } | Select-Object -First 1 }
        $deadline = (Get-Date).AddSeconds(20); $win = $null
        do {
            $win = [ExoE2EWin]::Owned($hwnd) | Where-Object { $f = $_.Split('|'); $n = (Get-Process -Id $f[1] -ErrorAction SilentlyContinue).Name; $n -and $n -notin 'Discord', 'ExoLauncher', 'msedgewebview2', 'SCSKiller' -and $f[3] -eq 'True' } | Select-Object -First 1
            if (-not $win) { Start-Sleep -Milliseconds 250 }
        } while (-not $win -and (Get-Date) -lt $deadline)
        if ($win) { $ev.process = (Get-Process -Id $win.Split('|')[1]).Name }
        $ev.logLine = Redact $line; $ev.ownedWindow = $win
        $discordHidden = -not ([ExoE2EWin]::Owned($hwnd) | Where-Object { $f = $_.Split('|'); (Get-Process -Id $f[1] -ErrorAction SilentlyContinue).Name -eq 'Discord' -and $f[3] -eq 'True' })
        $ev.discordHiddenWhileInMusic = $discordHidden
        $fit = Test-HostedFit $win $musicRoom.surface $musicRoom.dpr; $ev.fit = $fit
        $s = @()
        if ($win -and $CaptureHostedWindows) { Save-ScreenRect ([ExoE2EWin]::Rect($hwnd)) (Join-Path $shots 'room.music.desktop.png'); $s += 'screenshots/room.music.desktop.png' }
        @{ fail = @($(if (-not $line) { 'no provider "desktop client embedded" log line after opening Music' }), $(if (-not $win) { 'no visible provider window owned by Exo while Music is open' }), $(if (-not $discordHidden) { 'Discord window still visible in the Music room' }), $(if ($fit -and $fit.maxOffsetPx -gt 4) { "provider window $($fit.window -join ',') does not cover the room $($fit.expected -join ',')" })); evidence = $ev; shots = $s }
    }

    # Exo's own Spotify room reads home, library, a search, the queue and the player through the hidden client.
    if ($musicRoom.spotifyNative) { $null = Invoke-Driver 'spotify' $port }
    if ($music -eq 'apple') { $null = Invoke-Driver 'apple' $port }

    # ---------------------------------------------------------- 5b. Devices room
    # Devices is Exo Mouse in a frame (the driver checks it settles and that Exo relays only HID for
    # it); here, that Exo no longer starts, adopts or owns OpenMouse Desktop. Nothing is written to
    # the mouse and nothing is installed or started by the run.
    $devicesRoom = Invoke-Driver 'devices' $port
    Measure-Check 'room.devices.native' 'Exo Mouse settled and Exo hosts no OpenMouse Desktop window' {
        $ev = @{ mouse = $devicesRoom.devicesState }
        $line = Get-RunLog | Where-Object { $_ -match 'OpenMouse Desktop (desktop client embedded|desktop launched|window adopted)' -and (LineUtc $_) -ge $launchUtc } | Select-Object -First 1
        $owned = @([ExoE2EWin]::Owned($hwnd) | Where-Object { (Get-Process -Id ($_.Split('|')[1]) -ErrorAction SilentlyContinue).Name -eq 'openmouse-desktop' })
        $ev.logLine = Redact $line; $ev.ownedOpenMouseWindows = $owned.Count
        $fail = @()
        if ($line) { $fail += 'Exo still starts or embeds OpenMouse Desktop' }
        if ($owned.Count) { $fail += "Exo owns $($owned.Count) OpenMouse Desktop window(s)" }
        if (-not $devicesRoom.devicesState) { $fail += 'Exo Mouse did not settle' }
        @{ fail = $fail; evidence = $ev }
    }

    # Spotify's page is reachable only over the private DevTools pipe Exo holds, so the
    # Spotify comparisons below SKIP; YouTube Music is still compared on Exo's CDP port.
    $spotifyPort = ''

    # ---------------------------------------------------------- mini player = the provider's own song and clock
    # Read-only: the top-bar mini player against the provider page's own title and clock
    # (YouTube Music on Exo's CDP port; Spotify's page is private to Exo, so it SKIPs). Never plays, pauses or seeks.
    # It runs first, on the shell this run just started: the mini player's volume must be the
    # provider's own level from the start (music.miniplayer.volume).
    $null = Invoke-Driver 'miniplayer' $port "${music}:$spotifyPort"

    # ---------------------------------------------------------- mini player volume = the app's own
    # Spotify only, and SKIPs: its page is reachable only over Exo's private pipe. Nothing plays; the
    # driver puts the original level back. Other providers SKIP.
    $null = Invoke-Driver 'volume' $port $spotifyPort

    # ---------------------------------------------------------- back to Library
    Invoke-Driver 'home' $port | Out-Null
    Start-Sleep -Milliseconds 800
    Measure-Check 'room.hidden-on-leave' 'Hosted windows hide when Library is shown' {
        $visible = @([ExoE2EWin]::Owned($hwnd) | Where-Object { $_.Split('|')[3] -eq 'True' } | ForEach-Object { $f = $_.Split('|'); (Get-Process -Id $f[1] -ErrorAction SilentlyContinue).Name + ' ' + $f[2] })
        @{ fail = @($visible | ForEach-Object { "still visible: $_" }); evidence = @{ visibleOwned = $visible } }
    }

    # ---------------------------------------------------------- 8. minimize
    Measure-Check 'window.minimize' 'Minimize button minimizes (not hidden to tray) and restores' {
        $before = [ExoE2EWin]::IsIconic($hwnd)
        $null = Invoke-Driver 'minimize' $port
        $deadline = (Get-Date).AddSeconds(5)
        while ((Get-Date) -lt $deadline -and -not [ExoE2EWin]::IsIconic($hwnd)) { Start-Sleep -Milliseconds 100 }
        $iconic = [ExoE2EWin]::IsIconic($hwnd); $visible = [ExoE2EWin]::IsWindowVisible($hwnd)
        # Long enough for the hidden page's timers and the Low memory target to act on the covers.
        Start-Sleep -Seconds 20
        [ExoE2EWin]::ShowWindow($hwnd, 9) | Out-Null   # SW_RESTORE
        $deadline = (Get-Date).AddSeconds(5)
        while ((Get-Date) -lt $deadline -and [ExoE2EWin]::IsIconic($hwnd)) { Start-Sleep -Milliseconds 100 }
        $restored = -not [ExoE2EWin]::IsIconic($hwnd) -and [ExoE2EWin]::IsWindowVisible($hwnd)
        @{ fail = @($(if ($before) { 'window was already minimized' }), $(if (-not $iconic) { 'window is not minimized after the Minimize button' }), $(if (-not $visible) { 'window was hidden (tray-style) instead of minimized' }), $(if (-not $restored) { 'window did not restore' }))
           evidence = @{ iconicAfterClick = $iconic; visibleAfterClick = $visible; restored = $restored } }
    }
    $null = Invoke-Driver 'covers' $port 'restore'
    $refreshUtc = [DateTime]::UtcNow
    $null = Invoke-Driver 'covers' $port 'refresh'
    Measure-Check 'covers.refresh-scanned' 'The library refresh behind covers.refresh really rescanned' {
        $scan = Wait-LogLine 'PERF library-scan totalMs=\d+ force=true' $refreshUtc 10
        @{ fail = @($(if (-not $scan) { 'no forced library-scan line after the refresh (covers.refresh proved nothing)' })); evidence = @{ scanLine = (Redact $scan) } }
    }
    # A tour of every room after the minimize above; back on Library each card keeps its art.
    $null = Invoke-Driver 'covers' $port 'rooms'

    # ---------------------------------------------------------- 8b. title band drag
    # Real mouse input on Exo's own title band (window chrome only): the empty band moves the
    # window, the current room's tab does not, double-click maximizes and restores. Run at the
    # current size and again 240 px narrower, so stale caption regions after a resize fail too.
    Invoke-Driver 'home' $port | Out-Null
    Measure-Check 'window.drag' 'Title band: empty band drags, a tab does not, double-click maximizes (two sizes)' {
        if ([ExoE2EWin]::IsZoomed($hwnd)) { [ExoE2EWin]::ShowWindow($hwnd, 9) | Out-Null; Start-Sleep -Milliseconds 800 }
        $home0 = [ExoE2EWin]::Rect($hwnd); $cursor = [ExoE2EWin]::CursorPos()
        $fail = @(); $ev = @{ start = $home0; passes = @() }
        try {
            foreach ($narrower in 0, 240) {
                [ExoE2EWin]::MoveWindow($hwnd, $home0[0], $home0[1], $home0[2] - $narrower, $home0[3], $true) | Out-Null
                [ExoE2EWin]::SetForegroundWindow($hwnd) | Out-Null
                Start-Sleep -Milliseconds 900
                $pts = Invoke-Driver 'chrome' $port
                $pass = @{ size = "$($home0[2] - $narrower)x$($home0[3])"; points = $pts }
                $ev.passes += $pass
                if (-not $pts.band) { $fail += "$($pass.size): no empty point in the title band"; continue }
                $o = [ExoE2EWin]::ClientOrigin($hwnd); $d = [double]$pts.dpr
                $band = @([int][math]::Round($o[0] + $pts.band[0] * $d), [int][math]::Round($o[1] + $pts.band[1] * $d))
                $tab = @([int][math]::Round($o[0] + $pts.tab[0] * $d), [int][math]::Round($o[1] + $pts.tab[1] * $d))
                if ([ExoE2EWin]::RootAt($band[0], $band[1]) -ne $hwnd) { return @{ skip = "another window covers Exo's title band"; evidence = $ev } }
                $pass.hitTest = @{ band = [ExoE2EWin]::HitTest($band[0], $band[1]); tab = [ExoE2EWin]::HitTest($tab[0], $tab[1]) }
                if ($pts.maximize) { $pass.hitTest.maximize = [ExoE2EWin]::HitTest([int][math]::Round($o[0] + $pts.maximize[0] * $d), [int][math]::Round($o[1] + $pts.maximize[1] * $d)) }

                $r0 = [ExoE2EWin]::Rect($hwnd)
                [ExoE2EWin]::Drag($band[0], $band[1], 80, 50); Start-Sleep -Milliseconds 500
                $r1 = [ExoE2EWin]::Rect($hwnd)
                $pass.bandMoved = @(($r1[0] - $r0[0]), ($r1[1] - $r0[1]))
                if ([math]::Abs($pass.bandMoved[0] - 80) -gt 6 -or [math]::Abs($pass.bandMoved[1] - 50) -gt 6) { $fail += "$($pass.size): dragging the empty band moved the window by $($pass.bandMoved -join ',') instead of 80,50" }
                [ExoE2EWin]::MoveWindow($hwnd, $r0[0], $r0[1], $r0[2], $r0[3], $true) | Out-Null; Start-Sleep -Milliseconds 500

                [ExoE2EWin]::Drag($tab[0], $tab[1], 80, 50); Start-Sleep -Milliseconds 500
                $r2 = [ExoE2EWin]::Rect($hwnd)
                $pass.tabMoved = @(($r2[0] - $r0[0]), ($r2[1] - $r0[1]))
                if ($pass.tabMoved[0] -ne 0 -or $pass.tabMoved[1] -ne 0) { $fail += "$($pass.size): dragging a room tab moved the window by $($pass.tabMoved -join ',')"; [ExoE2EWin]::MoveWindow($hwnd, $r0[0], $r0[1], $r0[2], $r0[3], $true) | Out-Null }

                [ExoE2EWin]::DoubleClick($band[0], $band[1])
                $deadline = (Get-Date).AddSeconds(3); while ((Get-Date) -lt $deadline -and -not [ExoE2EWin]::IsZoomed($hwnd)) { Start-Sleep -Milliseconds 100 }
                $pass.doubleClickMaximized = [ExoE2EWin]::IsZoomed($hwnd)
                if (-not $pass.doubleClickMaximized) { $fail += "$($pass.size): double-clicking the empty band did not maximize"; continue }
                Start-Sleep -Milliseconds 900
                $max = Invoke-Driver 'chrome' $port
                $o = [ExoE2EWin]::ClientOrigin($hwnd); $d = [double]$max.dpr
                if ($max.band) { [ExoE2EWin]::DoubleClick([int][math]::Round($o[0] + $max.band[0] * $d), [int][math]::Round($o[1] + $max.band[1] * $d)) }
                $deadline = (Get-Date).AddSeconds(3); while ((Get-Date) -lt $deadline -and [ExoE2EWin]::IsZoomed($hwnd)) { Start-Sleep -Milliseconds 100 }
                $pass.doubleClickRestored = -not [ExoE2EWin]::IsZoomed($hwnd)
                if (-not $pass.doubleClickRestored) { $fail += "$($pass.size): double-clicking the maximized band did not restore"; [ExoE2EWin]::ShowWindow($hwnd, 9) | Out-Null; Start-Sleep -Milliseconds 800 }
            }
        } finally {
            if ([ExoE2EWin]::IsZoomed($hwnd)) { [ExoE2EWin]::ShowWindow($hwnd, 9) | Out-Null; Start-Sleep -Milliseconds 500 }
            [ExoE2EWin]::MoveWindow($hwnd, $home0[0], $home0[1], $home0[2], $home0[3], $true) | Out-Null
            [ExoE2EWin]::SetCursor($cursor[0], $cursor[1])
        }
        $ev.end = [ExoE2EWin]::Rect($hwnd)
        @{ fail = $fail; evidence = $ev }
    }

    # ---------------------------------------------------------- 9. idle
    Invoke-Driver 'home' $port | Out-Null
    Start-Sleep -Seconds 5
    Measure-Check 'idle.cpu' "Idle on Library $IdleSeconds s: Exo host CPU < 2% of one core" {
        $exo.Refresh()
        $wv = @(Get-ExoWebViewBrowsers | ForEach-Object { $_.ProcessId }) + @(Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | Where-Object { $_.ParentProcessId -in @(Get-ExoWebViewBrowsers).ProcessId } | ForEach-Object { $_.ProcessId }) | Sort-Object -Unique
        $cpu0 = $exo.TotalProcessorTime.TotalSeconds
        $w0 = (@($wv | ForEach-Object { (Get-Process -Id $_ -ErrorAction SilentlyContinue).TotalProcessorTime.TotalSeconds }) | Measure-Object -Sum).Sum
        $sw = [Diagnostics.Stopwatch]::StartNew()
        Start-Sleep -Seconds $IdleSeconds
        $exo.Refresh()
        $wall = $sw.Elapsed.TotalSeconds
        $hostPct = [math]::Round(100 * ($exo.TotalProcessorTime.TotalSeconds - $cpu0) / $wall, 2)
        $w1 = (@($wv | ForEach-Object { (Get-Process -Id $_ -ErrorAction SilentlyContinue).TotalProcessorTime.TotalSeconds }) | Measure-Object -Sum).Sum
        @{ fail = @($(if ($hostPct -ge 2) { "Exo host used $hostPct% of one core" })); evidence = @{ hostCpuPctOfOneCore = $hostPct; webviewCpuPctOfOneCore = [math]::Round(100 * ($w1 - $w0) / $wall, 2); webviewProcesses = @($wv).Count; seconds = [math]::Round($wall, 1) } }
    }
    # Behind another window (Exo visible, not minimized, not focused), the way it sits behind a game.
    # The other window is the harness's own small form; it closes after the measurement.
    Measure-Check 'perf.background-cpu' "Exo in the background 60 s (another window focused, Exo not minimized): host CPU < 2% of one core" {
        Add-Type -AssemblyName System.Windows.Forms
        $form = [System.Windows.Forms.Form]::new()
        $form.Text = 'Exo E2E: background'; $form.Width = 320; $form.Height = 120; $form.StartPosition = 'Manual'; $form.TopMost = $true; $form.ShowInTaskbar = $false
        $r = [ExoE2EWin]::Rect($hwnd); $form.Left = $r[0] + 40; $form.Top = $r[1] + $r[3] - 180
        try {
            $form.Show(); $form.Activate(); [ExoE2EWin]::SetForegroundWindow($form.Handle) | Out-Null
            for ($i = 0; $i -lt 12; $i++) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 250 }
            $page = Invoke-Driver 'activity' $port
            $exo.Refresh()
            $wv = @(Get-ExoWebViewBrowsers | ForEach-Object { $_.ProcessId }) + @(Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | Where-Object { $_.ParentProcessId -in @(Get-ExoWebViewBrowsers).ProcessId } | ForEach-Object { $_.ProcessId }) | Sort-Object -Unique
            $cpu0 = $exo.TotalProcessorTime.TotalSeconds
            $w0 = (@($wv | ForEach-Object { (Get-Process -Id $_ -ErrorAction SilentlyContinue).TotalProcessorTime.TotalSeconds }) | Measure-Object -Sum).Sum
            $sw = [Diagnostics.Stopwatch]::StartNew()
            while ($sw.Elapsed.TotalSeconds -lt 60) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 250 }
            $exo.Refresh()
            $wall = $sw.Elapsed.TotalSeconds
            $hostPct = [math]::Round(100 * ($exo.TotalProcessorTime.TotalSeconds - $cpu0) / $wall, 2)
            $w1 = (@($wv | ForEach-Object { (Get-Process -Id $_ -ErrorAction SilentlyContinue).TotalProcessorTime.TotalSeconds }) | Measure-Object -Sum).Sum
            $ev = @{ hostCpuPctOfOneCore = $hostPct; webviewCpuPctOfOneCore = [math]::Round(100 * ($w1 - $w0) / $wall, 2); webviewProcesses = @($wv).Count; seconds = [math]::Round($wall, 1); pageInactive = $page.inactive; pageHidden = $page.hidden; exoMinimized = [ExoE2EWin]::IsIconic($hwnd); exoVisible = [ExoE2EWin]::IsWindowVisible($hwnd) }
            @{ fail = @($(if ($hostPct -ge 2) { "Exo host used $hostPct% of one core in the background" }), $(if ($page.inactive -ne $true) { 'the page did not go inactive behind another window (the measurement is not of the background state)' })); evidence = $ev }
        } finally {
            $form.Close(); $form.Dispose()
            [ExoE2EWin]::SetForegroundWindow($hwnd) | Out-Null
        }
    }
    Measure-Check 'account.removed' 'No Exo account left on this PC (session, online cache, per-PC id) and no account traffic in this run' {
        $roots = @($appData, (Join-Path $env:APPDATA 'ExoLauncher'))
        $left = @(foreach ($r in $roots) { foreach ($n in 'auth.bin', 'auth.bin.tmp', 'online-cache', 'device-id') { $p = Join-Path $r $n; if (Test-Path -LiteralPath $p) { $p } } })
        $run = @(Get-RunLog)
        $lines = @($run | Where-Object { $_ -match '(?i)presence|/v1/profile|library activity|exo-id|account\.(get|sign)|online\.(health|account)' })
        $fail = @($lines | ForEach-Object { 'log: ' + (Redact $_) }) + @($left | ForEach-Object { [IO.Path]::GetFileName($_) + ' still present after start' })
        @{ fail = $fail; evidence = @{ leftovers = @($left | ForEach-Object { [IO.Path]::GetFileName($_) }); accountLogLines = $lines.Count; signInRemovedThisRun = [bool]($run -match 'Removed the retired Exo account sign-in') } }
    }
    Measure-Check 'build.precompiled' 'Shipped assemblies are ReadyToRun (a stale publish once shipped plain IL)' {
        $appDir = Split-Path $Exe
        $plain = @('ExoLauncher.dll', 'Microsoft.WinUI.dll') | Where-Object {
            $stream = [IO.File]::OpenRead((Join-Path $appDir $_))
            try { ([Reflection.PortableExecutable.PEReader]::new($stream)).PEHeaders.CorHeader.ManagedNativeHeaderDirectory.Size -eq 0 }
            finally { $stream.Dispose() }
        }
        @{ fail = @($plain | ForEach-Object { "$_ is not precompiled" }); evidence = @{ notPrecompiled = @($plain) } }
    }
    Measure-Check 'idle.screenshot' 'Library after idle (screenshot)' { $null = Invoke-Driver 'home' $port; @{ evidence = @{} } }

    # ---------------------------------------------------------- 7. Steam
    Measure-Check 'steam.ownership' 'Steam rows verified (0 unverified while Steam runs)' {
        $ev = @{ steamRunningAtStart = $steamAtStart }
        if (-not $steamAtStart) {
            # Exo never starts Steam just to check ownership; previously verified titles stay owned.
            $ev.ownershipLine = Redact (Wait-LogLine 'Steam ownership source=' $launchUtc 30)
            Start-Sleep -Seconds 5
            $ev.steamStartedByExo = [bool](Get-Process steam -ErrorAction SilentlyContinue)
            if ($ev.steamStartedByExo) { return @{ fail = @('Steam was closed and Exo started it at launch'); evidence = $ev } }
            return @{ skip = 'Steam is closed and Exo left it closed'; evidence = $ev }
        }
        $own = Wait-LogLine 'Steam ownership source=' $launchUtc 90
        $ev.ownershipLine = Redact $own
        # The cache is written after the scan; wait for one saved in this run.
        $deadline = (Get-Date).AddSeconds(30); $cache = $null
        do { try { $cache = Get-Content $cachePath -Raw | ConvertFrom-Json } catch { } ; if ($cache -and ([DateTimeOffset]$cache.savedAtUtc).UtcDateTime -ge $launchUtc) { break }; Start-Sleep 1 } while ((Get-Date) -lt $deadline)
        $steam = @($cache.games | Where-Object store -eq 'Steam')
        $unverified = @($steam | Where-Object entitlementState -eq 'unverified')
        $ev.cacheSavedAtUtc = $cache.savedAtUtc; $ev.cacheWrittenThisRun = ([DateTimeOffset]$cache.savedAtUtc).UtcDateTime -ge $launchUtc
        $ev.steamRows = $steam.Count; $ev.unverified = $unverified.Count; $ev.unverifiedTitles = @($unverified | Select-Object -First 10 | ForEach-Object title)
        $ev.byState = @{}; foreach ($g in ($steam | Group-Object entitlementState)) { $ev.byState[$g.Name] = $g.Count }
        @{ fail = @($(if (-not $steam.Count) { 'no Steam rows in library-cache.json' }), $(if ($unverified.Count) { "$($unverified.Count) Steam rows unverified while Steam runs" })); evidence = $ev }
    }
} catch {
    $harnessError = $_.Exception.Message
    Add-Check 'harness' 'Harness ran to completion' 'FAIL' ('error: ' + $harnessError)
}

# ---------------------------------------------------------- close, port gone, log scan
function Stop-DebugRun {
    try { Close-Exo } catch { $script:notes.Add('close: ' + $_.Exception.Message); return $false }
    return $true
}
$closed = Stop-DebugRun
Measure-Check 'shutdown.port-closed' 'Exo closed normally and the debug port is gone' {
    $l = if ($port) { Get-Listeners $port } else { @() }
    @{ fail = @($(if (-not $closed) { 'Exo did not close normally' }), $(if ($l.Count) { "port $port still listening" })); evidence = @{ port = $port; exoRunning = [bool](Get-Exo).Count } }
}
$runLines = @(Get-RunLog)
Measure-Check 'startup.log-clean' 'No ERROR / Exception / unknown method in app.log during the run' {
    # "Held by the E2E run." is the answer the run itself gives a request it holds (store.retry, store.offers-open).
    $bad = @($runLines | Where-Object { $_ -match '^\[[^\]]+\] ERROR |Exception|unknown method' -and $_ -notmatch 'Held by the E2E run\.' })
    @{ fail = @($(if ($bad.Count) { "$($bad.Count) bad line(s): " + ((Redact ($bad[0])).Substring(0, [math]::Min(220, (Redact $bad[0]).Length))) })); evidence = @{ runLines = $runLines.Count; badLines = @($bad | Select-Object -First 20 | ForEach-Object { Redact $_ }); logRotated = $script:logRotated } }
}

# ---------------------------------------------------------- 6. onboarding (optional)
if ($Onboarding -and -not $harnessError) {
    try {
        $raw = [IO.File]::ReadAllText($settingsPath)
        $flipped = $raw -replace '"onboardingComplete"\s*:\s*true', '"onboardingComplete": false'
        if ($flipped -notmatch '"onboardingComplete": false') { throw 'settings.json has no onboardingComplete=true to flip' }
        [IO.File]::WriteAllText($settingsPath, $flipped, [Text.UTF8Encoding]::new($false))
        $obUtc = [DateTime]::UtcNow
        $exo = Start-ExoDebug $port
        $null = Wait-MainWindow $exo 30
        $null = Invoke-Driver 'onboarding' $port
        if (-not (Stop-DebugRun)) { throw 'Exo did not close after onboarding' }
    } catch {
        Add-Check 'onboarding.harness' 'Onboarding run' 'FAIL' ('error: ' + $_.Exception.Message)
        try { Stop-DebugRun | Out-Null } catch { }
    }
    Measure-Check 'onboarding.restore' 'Settings restored byte-for-byte; Exo opens on Library again' {
        Restore-Guarded
        $diff = Compare-Guarded
        $exo = Start-ExoDebug $port
        $null = Wait-MainWindow $exo 30
        $homeData = Invoke-Driver 'home' $port
        $closedOk = Stop-DebugRun
        $diffAfter = Compare-Guarded
        @{ fail = @($(if ($diff.Count) { 'restore mismatch: ' + ($diff -join ', ') }), $(if (-not $homeData.onLibrary) { 'Exo did not open on Library after restore' }), $(if ($diffAfter.Count) { 'Exo rewrote after restore: ' + ($diffAfter -join ', ') }), $(if (-not $closedOk) { 'Exo did not close' }))
           evidence = @{ restoredFiles = @($guarded | ForEach-Object { [IO.Path]::GetFileName($_) }); openedOnLibrary = [bool]$homeData.onLibrary } }
    }
}

# ---------------------------------------------------------- restore everything touched, relaunch normally
$changed = Compare-Guarded
if ($changed.Count) { $notes.Add('Exo changed during the run and was restored: ' + ($changed -join ', ')); Restore-Guarded }
Measure-Check 'cleanup.restored' 'Guarded files identical to the start of the run (SHA256)' {
    $diff = Compare-Guarded
    @{ fail = @($diff | ForEach-Object { "$_ differs after restore" }); evidence = @{ files = @($guarded | ForEach-Object { [IO.Path]::GetFileName($_) }); changedDuringRunAndRestored = @($changed) } }
}
$final = $null
if (-not (Get-Exo).Count) { $final = Start-ExoNormal }
Measure-Check 'cleanup.relaunched' 'Exo running normally without a debug port' {
    Start-Sleep -Seconds 5
    $l = if ($port) { Get-Listeners $port } else { @() }
    @{ fail = @($(if (-not (Get-Exo).Count) { 'Exo is not running' }), $(if ($l.Count) { "port $port is listening" })); evidence = @{ running = [bool](Get-Exo).Count } }
}
Remove-Item $backupDir -Recurse -Force -ErrorAction SilentlyContinue

# ---------------------------------------------------------- artifact
$finished = Get-Date
[IO.File]::WriteAllLines((Join-Path $outDir 'app-log-excerpt.txt'), [string[]]@($runLines | ForEach-Object { Redact $_ }), [Text.UTF8Encoding]::new($false))
$os = Get-CimInstance Win32_OperatingSystem
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$gpu = @(Get-CimInstance Win32_VideoController | ForEach-Object Name)
Add-Type -AssemblyName System.Windows.Forms
$screens = @([System.Windows.Forms.Screen]::AllScreens | ForEach-Object { "$($_.Bounds.Width)x$($_.Bounds.Height)$(if ($_.Primary) { ' primary' })" })
$counts = [ordered]@{ PASS = @($checks | Where-Object status -eq 'PASS').Count; FAIL = @($checks | Where-Object status -eq 'FAIL').Count; SKIP = @($checks | Where-Object status -eq 'SKIP').Count }
$report = [ordered]@{
    schema = 'exo-e2e/1'
    runId = $runId
    startedAt = $started.ToString('o'); finishedAt = $finished.ToString('o'); durationSec = [math]::Round(($finished - $started).TotalSeconds, 1)
    command = "tools\e2e\Invoke-ExoE2E.ps1" + $(if ($Onboarding) { ' -Onboarding' }) + $(if ($UpscalerCycle) { ' -UpscalerCycle' }) + $(if ($RealGame) { " -RealGame $RealGame" }) + $(if ($CaptureHostedWindows) { ' -CaptureHostedWindows' }) + $(if ($PSBoundParameters.ContainsKey('Exe')) { ' -Exe <path>' })
    app = [ordered]@{ exe = (Redact $Exe); productVersion = $exeInfo.ProductVersion; fileVersion = $exeInfo.FileVersion; buildCommit = ($exeInfo.ProductVersion -split '\+')[1] }
    repo = [ordered]@{ commit = $gitCommit; dirty = $gitDirty }
    machine = [ordered]@{ os = "$($os.Caption) $($os.Version)"; cpu = $cpu.Name.Trim(); logicalCores = [Environment]::ProcessorCount; memoryGB = [math]::Round($os.TotalVisibleMemorySize / 1MB, 1); gpus = $gpu; screens = $screens; webview2 = $webviewVersion; node = $nodeVersion; powershell = $PSVersionTable.PSVersion.ToString() }
    environment = [ordered]@{ exoWasRunning = $wasRunning; steamRunningAtStart = $steamAtStart; musicService = $music; spotifyOptimized = (Get-Optimized $settingsAtStart 'spotify'); cdpPort = $port }
    summary = $counts
    result = if ($counts.FAIL) { 'FAIL' } else { 'PASS' }
    notes = @($notes)
    checks = @($checks)
}
$json = $report | ConvertTo-Json -Depth 12
[IO.File]::WriteAllText((Join-Path $outDir 'report.json'), $json, [Text.UTF8Encoding]::new($false))

$md = [Text.StringBuilder]::new()
[void]$md.AppendLine("# Exo E2E $runId — $($report.result)")
[void]$md.AppendLine()
[void]$md.AppendLine("Exo $($exeInfo.ProductVersion) · repo $gitCommit$(if ($gitDirty) { ' (dirty)' }) · $($report.machine.os) · WebView2 $webviewVersion · $($report.durationSec) s")
[void]$md.AppendLine("PASS $($counts.PASS) · FAIL $($counts.FAIL) · SKIP $($counts.SKIP)")
[void]$md.AppendLine()
[void]$md.AppendLine('| Status | Check | Time | Detail | Screens |')
[void]$md.AppendLine('| --- | --- | ---: | --- | --- |')
foreach ($c in $checks) {
    $detail = if ($c.reason) { $c.reason } else { '' }
    $detail = $detail.Replace('|', '\|')
    $sh = (@($c.screenshots) | Where-Object { $_ } | ForEach-Object { "[$(Split-Path $_ -Leaf)]($_)" }) -join ' '
    [void]$md.AppendLine("| $($c.status) | $($c.id) — $($c.name) | $($c.durationMs) ms | $detail | $sh |")
}
if ($notes.Count) { [void]$md.AppendLine(); [void]$md.AppendLine('Notes:'); foreach ($n in $notes) { [void]$md.AppendLine("- $n") } }
[void]$md.AppendLine()
[void]$md.AppendLine('Evidence per check is in report.json. Verify this folder: `sha256sum -c SHA256SUMS.txt` (Git Bash) or `tools\e2e\Invoke-ExoE2E.ps1 -Verify <this folder>`.')
[IO.File]::WriteAllText((Join-Path $outDir 'summary.md'), $md.ToString(), [Text.UTF8Encoding]::new($false))

$sums = Get-ChildItem $outDir -Recurse -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object FullName | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -Algorithm SHA256 $_.FullName).Hash.ToLowerInvariant(), $_.FullName.Substring($outDir.Length + 1).Replace('\', '/')
}
# LF endings so `sha256sum -c` in Git Bash reads it as-is.
[IO.File]::WriteAllText((Join-Path $outDir 'SHA256SUMS.txt'), (($sums -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))

Write-Host ''
Write-Host "Result: $($report.result)  PASS $($counts.PASS)  FAIL $($counts.FAIL)  SKIP $($counts.SKIP)"
Write-Host "Artifact: $outDir"
if ($harnessError -and -not $checks.Count) { exit 2 }
exit $(if ($counts.FAIL) { 1 } else { 0 })
