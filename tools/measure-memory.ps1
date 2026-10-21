# Memory of the running Exo Launcher process tree (host + every WebView2 child).
# Usage: pwsh -File tools\measure-memory.ps1 [-Label after]
# Same state each run: installed exe, sitting on Library, 3 minutes after launch, no interaction.
param(
    [string]$Label = '',
    [int]$ExoProcessId = 0,
    [ValidateRange(1, 60)][int]$SampleSeconds = 5,
    [string]$OutputPath = ''
)

$ErrorActionPreference = 'Stop'
if ($OutputPath -and -not (Test-Path -LiteralPath (Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))) -PathType Container)) {
    throw 'The output directory must already exist.'
}
if ($OutputPath -and (Test-Path -LiteralPath $OutputPath)) { throw 'Choose a new output filename; captured measurements are not overwritten.' }
if (-not ('ExoResourceWindows' -as [type])) {
    Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class ExoResourceWindows {
    private delegate bool Callback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Callback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    public static uint ForegroundProcess() {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        return pid;
    }
    public static uint[] OwnedProcesses(IntPtr owner) {
        var ids = new HashSet<uint>();
        if (!EnumWindows((window, state) => {
            if (GetWindow(window, 4) == owner) {
                GetWindowThreadProcessId(window, out var pid);
                ids.Add(pid);
            }
            return true;
        }, IntPtr.Zero)) throw new InvalidOperationException("Window ownership could not be enumerated.");
        var result = new uint[ids.Count];
        ids.CopyTo(result);
        return result;
    }
}
'@
}
$hosts = @(Get-Process ExoLauncher -ErrorAction Stop | Where-Object { -not $ExoProcessId -or $_.Id -eq $ExoProcessId })
if ($hosts.Count -ne 1) { throw 'Select one running Exo instance with -ExoProcessId.' }
$main = $hosts[0]
if ($main.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Exo has no main window; wait until startup completes.' }
$mainStarted = $main.StartTime.ToUniversalTime().Ticks
$all = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, Name, ExecutablePath, CreationDate)
$byId = @{}
$children = @{}
foreach ($entry in $all) {
    $byId[[int]$entry.ProcessId] = $entry
    $parentId = [int]$entry.ParentProcessId
    if (-not $children.ContainsKey($parentId)) { $children[$parentId] = [Collections.Generic.List[int]]::new() }
    $children[$parentId].Add([int]$entry.ProcessId)
}
function Get-TreeIds([int]$RootId) {
    $seen = [Collections.Generic.HashSet[int]]::new()
    $queue = [Collections.Generic.Queue[int]]::new()
    $queue.Enqueue($RootId)
    while ($queue.Count) {
        $current = $queue.Dequeue()
        if (-not $seen.Add($current)) { continue }
        $current
        foreach ($childId in $children[$current]) {
            if ($byId[$childId].CreationDate -ge $byId[$current].CreationDate) { $queue.Enqueue($childId) }
        }
    }
}
$issues = [Collections.Generic.List[string]]::new()
$membership = @{}
$membership[$main.Id] = 'exo'
$keys = @{ 'Discord.exe' = 'discord'; 'Spotify.exe' = 'spotify' }
$owned = @([ExoResourceWindows]::OwnedProcesses($main.MainWindowHandle) | Sort-Object -Unique)
$hostedTrees = [Collections.Generic.HashSet[int]]::new()
foreach ($rootId in $owned) {
    $root = $byId[[int]$rootId]
    if (-not $root -or -not $keys.ContainsKey($root.Name)) { continue }
    $key = $keys[$root.Name]
    $membership[[int]$rootId] = $key
    $tree = @(Get-TreeIds ([int]$rootId))
    foreach ($childId in $tree) { [void]$hostedTrees.Add($childId) }
    if (-not $root.ExecutablePath) { $issues.Add("$key install path is unreadable; descendants are excluded."); continue }
    $directory = if ($root.ExecutablePath -match '^(.*?\\WindowsApps\\[^\\]+)\\') { $Matches[1] } else { [IO.Path]::GetDirectoryName($root.ExecutablePath) }
    $prefix = $directory.TrimEnd('\') + '\'
    foreach ($candidateId in ($tree | Sort-Object -Unique)) {
        $candidate = $byId[$candidateId]
        if (-not $candidate) { $issues.Add("$key membership changed during capture."); continue }
        $path = $candidate.ExecutablePath
        if (-not $path) { $issues.Add("$key process $candidateId path is unreadable."); continue }
        if ($path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            if ($membership.ContainsKey($candidateId) -and $membership[$candidateId] -ne $key) { $issues.Add("Process $candidateId appears in multiple cohorts."); continue }
            $membership[$candidateId] = $key
        }
    }
}
foreach ($candidateId in @(Get-TreeIds $main.Id)) {
    if ($hostedTrees.Contains($candidateId) -or $membership.ContainsKey($candidateId)) { continue }
    $candidate = $byId[$candidateId]
    if ($candidate.Name -ne 'msedgewebview2.exe') { continue }
    if (-not $candidate.ExecutablePath) { $issues.Add("WebView process $candidateId path is unreadable."); continue }
    if ($candidate.ExecutablePath -match '\\Microsoft\\EdgeWebView\\Application\\') { $membership[$candidateId] = 'webview' }
}
function Read-Members {
    foreach ($memberId in ($membership.Keys | Sort-Object)) {
        try {
            $process = Get-Process -Id $memberId -ErrorAction Stop
            try {
                $started = $process.StartTime.ToUniversalTime().Ticks
                if ([Math]::Abs(($process.StartTime.ToUniversalTime() - $byId[$memberId].CreationDate.ToUniversalTime()).TotalMilliseconds) -gt 1) {
                    $issues.Add("Process $memberId identity changed."); continue
                }
                $process.Refresh()
                [pscustomobject]@{
                    Pid = $memberId; StartedUtcTicks = $started; Group = $membership[$memberId]; Name = $process.ProcessName
                    WorkingSetBytes = $process.WorkingSet64; PrivateBytes = $process.PrivateMemorySize64
                    CpuMilliseconds = $process.TotalProcessorTime.TotalMilliseconds
                    SampleTimestamp = [Diagnostics.Stopwatch]::GetTimestamp(); Threads = $process.Threads.Count
                }
            }
            finally { $process.Dispose() }
        }
        catch { $issues.Add("Process $memberId could not be sampled.") }
    }
}
$foregroundBefore = [ExoResourceWindows]::ForegroundProcess()
$clock = [Diagnostics.Stopwatch]::StartNew()
$before = @(Read-Members)
Start-Sleep -Seconds $SampleSeconds
$after = @(Read-Members)
$clock.Stop()
$foregroundAfter = [ExoResourceWindows]::ForegroundProcess()
$beforeById = @{}
foreach ($row in $before) { $beforeById[$row.Pid] = $row }
$rows = @($after | ForEach-Object {
        $prior = $beforeById[$_.Pid]
        $cpu = if ($prior -and $prior.StartedUtcTicks -eq $_.StartedUtcTicks -and $_.CpuMilliseconds -ge $prior.CpuMilliseconds) {
            $elapsedMilliseconds = ($_.SampleTimestamp - $prior.SampleTimestamp) * 1000.0 / [Diagnostics.Stopwatch]::Frequency
            [Math]::Round(($_.CpuMilliseconds - $prior.CpuMilliseconds) / $elapsedMilliseconds * 100, 3)
        }
        else { $issues.Add("Process $($_.Pid) has no comparable CPU sample."); $null }
        $_ | Add-Member CpuPercentOneCore $cpu -PassThru
    })
$main.Refresh()
if ($main.HasExited -or $main.StartTime.ToUniversalTime().Ticks -ne $mainStarted) { throw 'Exo exited or restarted during measurement.' }
$ownedAfter = @([ExoResourceWindows]::OwnedProcesses($main.MainWindowHandle) | Sort-Object -Unique)
if (($owned -join ',') -ne ($ownedAfter -join ',')) { $issues.Add('Owned windows changed during measurement; repeat in a stable room.') }
$allAfter = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, CreationDate)
foreach ($entry in $allAfter) {
    $previous = $byId[[int]$entry.ProcessId]
    if ($membership.ContainsKey([int]$entry.ParentProcessId) -and
        (-not $previous -or $entry.CreationDate -ne $previous.CreationDate)) {
        $issues.Add('A managed process started a new child during measurement; repeat in a stable room.')
    }
}
$groups = @($rows | Group-Object Group | ForEach-Object {
        [pscustomobject]@{
            Key = $_.Name; Processes = $_.Count
            WorkingSetBytes = ($_.Group | Measure-Object WorkingSetBytes -Sum).Sum
            PrivateBytes = ($_.Group | Measure-Object PrivateBytes -Sum).Sum
            CpuPercentOneCore = [Math]::Round(($_.Group | Measure-Object CpuPercentOneCore -Sum).Sum, 3)
            Threads = ($_.Group | Measure-Object Threads -Sum).Sum
        }
    })
$result = [pscustomobject]@{
    Label = $Label; SampledAtUtc = [DateTime]::UtcNow.ToString('o'); ExoPid = $main.Id
    Executable = $main.Path; UptimeSeconds = [int]((Get-Date) - $main.StartTime).TotalSeconds
    ManagedAssemblySha256 = (Get-FileHash -LiteralPath ([IO.Path]::Combine([IO.Path]::GetDirectoryName($main.Path), 'ExoLauncher.dll')) -Algorithm SHA256).Hash
    Scope = 'Exo, its descendant WebView runtime, and clients with windows owned by Exo; wait for all clients to finish startup.'
    CpuBasis = 'Percent of one logical CPU; 100 means one core fully occupied. Working sets are summed, not unique physical RAM.'
    SampleSeconds = [Math]::Round($clock.Elapsed.TotalSeconds, 3)
    ForegroundBefore = $foregroundBefore; ForegroundAfter = $foregroundAfter
    ManagedForeground = $membership.ContainsKey([int]$foregroundAfter)
    ForegroundChanged = $foregroundBefore -ne $foregroundAfter
    ExpectedProcesses = $membership.Count; ReadableProcesses = $rows.Count
    Complete = $issues.Count -eq 0 -and $before.Count -eq $membership.Count -and $rows.Count -eq $membership.Count
    Groups = $groups; Processes = $rows; Issues = @($issues | Sort-Object -Unique)
}
if ($OutputPath) { $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputPath -Encoding utf8 }
$result | ConvertTo-Json -Depth 6
$main.Dispose()
