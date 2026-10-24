<#
  Windows side of Exo's end-to-end suite (tools/e2e/lib/win.mjs calls it): what the screen really shows and the real
  mouse, which the page's own CDP view can't stand in for (3.13.3's 4K page was whole in CDP's screenshots and cut
  off on the screen). Each command prints one JSON object. Physical pixels throughout (per-monitor DPI aware).

  window                     Exo's main window: client rectangle on screen, maximized, foreground, its monitor.
  monitors                   Every monitor: bounds, work area, DPI scale, primary.
  front                      Brings Exo's window to the front.
  capture -Out <png>         The screen under Exo's client area, as shown (Exo is brought to the front first).
  click -X <x> -Y <y>        One real left click at a screen point; the cursor goes back where it was.
  move -X <x> -Y <y>         The real cursor to a screen point (hover).
  place -X -Y -W -H          Restores Exo's window and puts it at that rectangle.
  maximize | restore         Exo's window.
  proc                       Exo and the WebView2 processes under it: CPU seconds and memory.
  udp                        UDP endpoints Exo holds (a voice call holds some).
  shown -Name <process>      That process's windows a person could see: visible, not minimized or cloaked, on a monitor.
  precompiled                Which of Exo's own assemblies shipped without ReadyToRun code.
  snip -Name <Return|Escape> Waits for Exo Capture's snip overlay to show (uncloaked), then gives it that key as a
                             message to its own window (no system-wide input): Return snips the screen under the
                             cursor, Escape cancels. Prints when it showed (Unix ms).
#>
param(
    [Parameter(Mandatory)][string]$Command,
    [string]$Out,
    [string]$Name,
    [int]$X, [int]$Y, [int]$W, [int]$H
)
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class ExoWin {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct MONITORINFOEX {
    public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }
  [StructLayout(LayoutKind.Sequential)] public struct WINDOWPLACEMENT { public int length, flags, showCmd; public POINT min, max; public RECT normal; }
  public delegate bool MonitorEnum(IntPtr h, IntPtr dc, ref RECT r, IntPtr data);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint f);
  [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint f);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFOEX i);
  [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnum cb, IntPtr data);
  [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr m, int type, out uint x, out uint y);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr e);
  public delegate bool WindowEnum(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(WindowEnum cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int size);
  /** Top-level windows of these processes that show on a monitor (DWMWA_CLOAKED = 14, MONITOR_DEFAULTTONULL = 0). */
  public static List<string> Shown(int[] pids) {
    var set = new HashSet<int>(pids); var list = new List<string>();
    EnumWindows((h, l) => {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (!set.Contains((int)pid) || !IsWindowVisible(h) || IsIconic(h)) return true;
      int cloaked; if (DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return true;
      RECT r; GetWindowRect(h, out r);
      if (r.R - r.L < 32 || r.B - r.T < 32 || MonitorFromWindow(h, 0) == IntPtr.Zero) return true;
      list.Add((r.R - r.L) + "x" + (r.B - r.T) + " at " + r.L + "," + r.T);
      return true;
    }, IntPtr.Zero);
    return list;
  }
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  /** Exo Capture's snip overlay once it shows: { at (Unix ms), hwnd }, or null after the timeout. */
  public static long[] SnipShown(int timeoutMs) {
    var clock = System.Diagnostics.Stopwatch.StartNew();
    while (clock.ElapsedMilliseconds < timeoutMs) {
      IntPtr h = FindWindow("ExoCapturePopup", "Exo Capture snip");
      int cloaked = 1;
      if (h != IntPtr.Zero && IsWindowVisible(h) && DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked == 0)
        return new long[] { DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), h.ToInt64() };
      System.Threading.Thread.Sleep(1);
    }
    return null;
  }
  public static List<IntPtr> Monitors() {
    var list = new List<IntPtr>();
    EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr dc, ref RECT r, IntPtr d) => { list.Add(h); return true; }, IntPtr.Zero);
    return list;
  }
}
'@
# Per-monitor DPI aware v2: every rectangle and point in physical pixels.
[ExoWin]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null

function Get-ExoWindow {
    $exe = Join-Path $env:LOCALAPPDATA 'ExoLauncher\app\ExoLauncher.exe'
    $p = Get-Process ExoLauncher -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and $_.Path -eq $exe } | Select-Object -First 1
    if (-not $p) { $p = Get-Process ExoLauncher -ErrorAction SilentlyContinue | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1 }
    if (-not $p) { throw 'Exo has no window' }
    $p
}

function Monitor-Info([IntPtr]$m) {
    $i = New-Object ExoWin+MONITORINFOEX; $i.cbSize = [Runtime.InteropServices.Marshal]::SizeOf($i)
    [ExoWin]::GetMonitorInfo($m, [ref]$i) | Out-Null
    $dx = [uint32]96; $dy = [uint32]96; [ExoWin]::GetDpiForMonitor($m, 0, [ref]$dx, [ref]$dy) | Out-Null
    [ordered]@{
        device = $i.szDevice; primary = [bool]($i.dwFlags -band 1); scale = [math]::Round($dx / 96, 3)
        x = $i.rcMonitor.L; y = $i.rcMonitor.T; w = $i.rcMonitor.R - $i.rcMonitor.L; h = $i.rcMonitor.B - $i.rcMonitor.T
        work = [ordered]@{ x = $i.rcWork.L; y = $i.rcWork.T; w = $i.rcWork.R - $i.rcWork.L; h = $i.rcWork.B - $i.rcWork.T }
    }
}

function Window-Info($p) {
    $h = $p.MainWindowHandle
    $c = New-Object ExoWin+RECT; [ExoWin]::GetClientRect($h, [ref]$c) | Out-Null
    $o = New-Object ExoWin+POINT; [ExoWin]::ClientToScreen($h, [ref]$o) | Out-Null
    [ordered]@{
        pid = $p.Id; hwnd = [int64]$h
        client = [ordered]@{ x = $o.X; y = $o.Y; w = $c.R - $c.L; h = $c.B - $c.T }
        maximized = [ExoWin]::IsZoomed($h); minimized = [ExoWin]::IsIconic($h)
        foreground = ([ExoWin]::GetForegroundWindow() -eq $h)
        monitor = Monitor-Info ([ExoWin]::MonitorFromWindow($h, 2))
    }
}

function Bring-Front($p) {
    $h = $p.MainWindowHandle
    if ([ExoWin]::IsIconic($h)) { [ExoWin]::ShowWindow($h, 9) | Out-Null }
    # Windows lets a process take the foreground right after a key press of its own: a tap of Alt, then the switch.
    [ExoWin]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); [ExoWin]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
    [ExoWin]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Milliseconds 250
}

switch ($Command) {
    'window' { Window-Info (Get-ExoWindow) | ConvertTo-Json -Depth 5 -Compress }
    'monitors' { @([ExoWin]::Monitors() | ForEach-Object { Monitor-Info $_ }) | ConvertTo-Json -Depth 5 -Compress }
    'front' { $p = Get-ExoWindow; Bring-Front $p; Window-Info $p | ConvertTo-Json -Depth 5 -Compress }
    'capture' {
        $p = Get-ExoWindow; Bring-Front $p
        $info = Window-Info $p; $c = $info.client
        $bmp = New-Object System.Drawing.Bitmap $c.w, $c.h
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($c.x, $c.y, 0, 0, $bmp.Size); $g.Dispose()
        $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
        $info | ConvertTo-Json -Depth 5 -Compress
    }
    'click' {
        $was = New-Object ExoWin+POINT; [ExoWin]::GetCursorPos([ref]$was) | Out-Null
        [ExoWin]::SetCursorPos($X, $Y) | Out-Null; Start-Sleep -Milliseconds 80
        [ExoWin]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 50; [ExoWin]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 150; [ExoWin]::SetCursorPos($was.X, $was.Y) | Out-Null
        '{"ok":true}'
    }
    'move' { [ExoWin]::SetCursorPos($X, $Y) | Out-Null; '{"ok":true}' }
    'place' {
        $p = Get-ExoWindow; $h = $p.MainWindowHandle
        [ExoWin]::ShowWindow($h, 9) | Out-Null; Start-Sleep -Milliseconds 200
        [ExoWin]::SetWindowPos($h, [IntPtr]::Zero, $X, $Y, $W, $H, 0x0014) | Out-Null   # no z-order change, no activate
        Start-Sleep -Milliseconds 300
        Window-Info $p | ConvertTo-Json -Depth 5 -Compress
    }
    'maximize' { $p = Get-ExoWindow; [ExoWin]::ShowWindow($p.MainWindowHandle, 3) | Out-Null; Start-Sleep -Milliseconds 400; Window-Info $p | ConvertTo-Json -Depth 5 -Compress }
    'restore' { $p = Get-ExoWindow; [ExoWin]::ShowWindow($p.MainWindowHandle, 9) | Out-Null; Start-Sleep -Milliseconds 400; Window-Info $p | ConvertTo-Json -Depth 5 -Compress }
    'proc' {
        $exo = Get-ExoWindow
        $all = Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, Name
        $tree = @($exo.Id); $grew = $true
        while ($grew) { $kids = @($all | Where-Object { $tree -contains $_.ParentProcessId -and $tree -notcontains $_.ProcessId } | ForEach-Object ProcessId); $grew = $kids.Count -gt 0; $tree += $kids }
        $procs = @($tree | ForEach-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
        $byName = [ordered]@{}
        foreach ($g in ($procs | Group-Object Name)) {
            $byName[$g.Name] = [ordered]@{ cpuSeconds = [math]::Round(($g.Group | Measure-Object -Property CPU -Sum).Sum, 3); workingSetMB = [math]::Round(($g.Group | Measure-Object -Property WorkingSet64 -Sum).Sum / 1MB, 1) }
        }
        [ordered]@{
            pid = $exo.Id; count = $procs.Count
            cpuSeconds = [math]::Round(($procs | Measure-Object -Property CPU -Sum).Sum, 3)
            workingSetMB = [math]::Round(($procs | Measure-Object -Property WorkingSet64 -Sum).Sum / 1MB, 1)
            exoWorkingSetMB = [math]::Round($exo.WorkingSet64 / 1MB, 1)
            names = @($procs | Group-Object Name | ForEach-Object { "$($_.Name) x$($_.Count)" })
            byName = $byName
        } | ConvertTo-Json -Depth 5 -Compress
    }
    'udp' { $p = Get-ExoWindow; [ordered]@{ count = @(Get-NetUDPEndpoint -OwningProcess $p.Id -ErrorAction SilentlyContinue).Count } | ConvertTo-Json -Compress }
    'shown' {
        $ids = [int[]]@(Get-Process -Name $Name -ErrorAction SilentlyContinue | ForEach-Object Id)
        [ordered]@{ running = $ids.Count -gt 0; shown = @(if ($ids.Count) { [ExoWin]::Shown($ids) }) } | ConvertTo-Json -Compress
    }
    'precompiled' {
        $dir = Split-Path (Get-ExoWindow).Path
        $names = 'ExoLauncher.dll', 'Microsoft.WinUI.dll'
        $plain = @($names | Where-Object {
            $stream = [IO.File]::OpenRead((Join-Path $dir $_))
            try { ([Reflection.PortableExecutable.PEReader]::new($stream)).PEHeaders.CorHeader.ManagedNativeHeaderDirectory.Size -eq 0 }
            finally { $stream.Dispose() }
        })
        [ordered]@{ checked = $names; plain = $plain } | ConvertTo-Json -Compress
    }
    'snip' {
        # Ready: the type above is compiled; the caller starts the snip after this line.
        [Console]::Out.WriteLine('{"ready":' + [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() + '}'); [Console]::Out.Flush()
        $shown = [ExoWin]::SnipShown(15000)
        if (-not $shown) { '{"shown":false}'; break }
        # WM_KEYDOWN, VK_RETURN (13) or VK_ESCAPE (27).
        [ExoWin]::PostMessage([IntPtr]$shown[1], 0x100, [IntPtr]$(if ($Name -eq 'Return') { 13 } else { 27 }), [IntPtr]0) | Out-Null
        [ordered]@{ shown = $true; at = $shown[0] } | ConvertTo-Json -Compress
    }
    default { throw "unknown command $Command" }
}
