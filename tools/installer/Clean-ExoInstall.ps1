param([string]$InstallRoot)
# After a successful install or update: what earlier runs left behind under Exo's own folder.
# Only these, directly under the root and never through a link: unfinished staging and backup
# trees (app.incoming-*, app.previous-*, app.failed-*, and the developer's app.*-dev-* swaps),
# health receipts older than a day, and the last failure note. The live app and user data stay.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
if (-not [IO.Directory]::Exists($root) -or [IO.Path]::GetFileName($root) -notmatch '^ExoLauncher') { exit 0 }
$removed = 0
foreach ($dir in [IO.DirectoryInfo]::new($root).GetDirectories()) {
    if ($dir.Name -notmatch '^app\.(incoming|previous|failed)(-dev)?-[0-9A-Za-z]+$') { continue }
    if ($dir.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
    try { Remove-Item -LiteralPath $dir.FullName -Recurse -Force; $removed++ }
    catch { Write-Warning "Left $($dir.Name): it is in use." }
}
$day = (Get-Date).AddDays(-1)
foreach ($file in [IO.DirectoryInfo]::new($root).GetFiles('update-health-*.json')) {
    if ($file.LastWriteTime -lt $day) { try { $file.Delete() } catch { } }
}
$note = Join-Path $root 'update-error.log'
if ([IO.File]::Exists($note)) { try { [IO.File]::Delete($note) } catch { } }
# Downloaded installers the in-app updater no longer needs (it keeps none once an update is in).
$updates = Join-Path $root 'updates'
if ([IO.Directory]::Exists($updates)) {
    foreach ($file in [IO.DirectoryInfo]::new($updates).GetFiles('ExoLauncher-Setup-*')) {
        if ($file.LastWriteTime -lt (Get-Date).AddMinutes(-10)) { try { $file.Delete() } catch { } }
    }
}
Write-Output "Cleaned $removed leftover folder(s)."
exit 0
