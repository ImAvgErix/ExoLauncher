param(
    [string]$AppDirectory,
    [string]$DataDirectory,
    [int]$SetupProcessId = 0,
    [switch]$DefinitionsOnly
)
$ErrorActionPreference = 'Stop'

function Test-ExoWebViewCommandLine([string]$CommandLine, [string[]]$ProfileRoots) {
    if ([string]::IsNullOrWhiteSpace($CommandLine)) { return $false }
    $match = [regex]::Match($CommandLine, '(?:^|\s)--user-data-dir=(?:"([^"\r\n]+)"|([^\s"\r\n]+))', 'IgnoreCase')
    if (-not $match.Success) { return $false }
    $value = if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[2].Value }
    try { $full = [IO.Path]::GetFullPath($value).TrimEnd('\') } catch { return $false }
    foreach ($root in $ProfileRoots) {
        if ([StringComparer]::OrdinalIgnoreCase.Equals($full, [IO.Path]::GetFullPath($root).TrimEnd('\'))) { return $true }
    }
    return $false
}

function Get-ExoProfileRoots([string]$AppDirectory, [string]$DataDirectory) {
    @('webview', 'webview-debug', 'discord-web', 'apple-music-web', 'youtube-music-web', 'spotify-web') |
        ForEach-Object { Join-Path $DataDirectory $_ }
    Join-Path $AppDirectory 'ExoLauncher.exe.WebView2'
}

function Stop-ExoInstalledApp([string]$AppDirectory, [string]$DataDirectory, [int]$SetupProcessId) {
$app = [IO.Path]::GetFullPath($AppDirectory).TrimEnd('\')
$data = [IO.Path]::GetFullPath($DataDirectory).TrimEnd('\')
if (-not [StringComparer]::OrdinalIgnoreCase.Equals($app, (Join-Path $data 'app'))) { throw 'Unexpected managed app location.' }
$target = Join-Path $app 'ExoLauncher.exe'
$profiles = @(Get-ExoProfileRoots $app $data)

function Get-InstalledExoProcess {
    @(Get-CimInstance Win32_Process -Filter "Name = 'ExoLauncher.exe'" | Where-Object {
        $_.ProcessId -ne $SetupProcessId -and $_.ExecutablePath -and
        [StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath($_.ExecutablePath), $target)
    })
}

# Ask Exo to close the way a click on its close button does, so it saves its state and lets its store
# clients and add-ons go; the updater already did when it started this. Then allow 8 s for that.
if ($env:EXO_SILENT_INSTALL -ne '1') {
    foreach ($process in @(Get-InstalledExoProcess)) {
        try { [void](Get-Process -Id $process.ProcessId -ErrorAction Stop).CloseMainWindow() } catch { }
    }
}
$deadline = [DateTime]::UtcNow.AddSeconds(8)
while (@(Get-InstalledExoProcess).Count -gt 0 -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 200 }
$deadline = [DateTime]::UtcNow.AddSeconds(5)
do {
    $matches = @(Get-InstalledExoProcess)
    foreach ($process in $matches) { Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue }
    if ($matches.Count -eq 0) { break }
    Start-Sleep -Milliseconds 100
} while ([DateTime]::UtcNow -lt $deadline)
if (@(Get-InstalledExoProcess).Count -gt 0) { return 1 }

$deadline = [DateTime]::UtcNow.AddSeconds(5)
do {
    $web = @(Get-CimInstance Win32_Process -Filter "Name = 'msedgewebview2.exe'" | Where-Object {
        Test-ExoWebViewCommandLine $_.CommandLine $profiles
    })
    foreach ($process in $web) { Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue }
    if ($web.Count -eq 0) { return 0 }
    Start-Sleep -Milliseconds 100
} while ([DateTime]::UtcNow -lt $deadline)
return 1
}

if ($DefinitionsOnly) { return }
exit (Stop-ExoInstalledApp $AppDirectory $DataDirectory $SetupProcessId)
