param([string]$InstallRoot, [string]$PreviousDirectory, [string]$Version, [switch]$DefinitionsOnly)
$ErrorActionPreference = 'Stop'
$updateDefinitionsOnly = $DefinitionsOnly
. (Join-Path $PSScriptRoot 'Verify-Payload.ps1') -DefinitionsOnly
. (Join-Path $PSScriptRoot 'Close-ExoLauncher.ps1') -DefinitionsOnly

function Get-ExoManagedUpdatePaths([string]$Root, [string]$Previous) {
    $full = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $backup = [IO.Path]::GetFullPath($Previous).TrimEnd('\')
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetDirectoryName($backup), $full) -or
        [IO.Path]::GetFileName($backup) -notmatch '^app\.previous-[0-9]+$') { throw 'Unexpected rollback location.' }
    foreach ($path in @($full, $backup, (Join-Path $full 'app'))) {
        if ([IO.Directory]::Exists($path) -and ([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Managed update paths cannot be links.' }
    }
    @{ root=$full; previous=$backup; app=(Join-Path $full 'app') }
}

function Write-ExoUpdateJson([string]$Path, [object]$Value) {
    $temporary = $Path + '.tmp-' + [guid]::NewGuid().ToString('N')
    try {
        [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 6 -Compress), [Text.UTF8Encoding]::new($false))
        if ([IO.File]::Exists($Path)) { [IO.File]::Replace($temporary, $Path, [NullString]::Value) }
        else { [IO.File]::Move($temporary, $Path) }
    } finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
}

function Test-ExoStartupReceipt([string]$Path, [string]$Nonce, [string]$Version, [string]$Executable, [int]$ProcessId) {
    try {
        if (-not [IO.File]::Exists($Path) -or ([IO.FileInfo]$Path).Length -gt 4096 -or
            ([IO.File]::GetAttributes($Path) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $false }
        $receipt = [IO.File]::ReadAllText($Path) | ConvertFrom-Json
        return $receipt.schema -eq 1 -and $receipt.healthy -eq $true -and $receipt.nonce -ceq $Nonce -and
            $receipt.version -ceq $Version -and $receipt.processId -eq $ProcessId -and
            [StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath($receipt.executable), $Executable)
    } catch { return $false }
}

function Restore-ExoUpdateBackup([string]$Root, [string]$Previous) {
    $paths = Get-ExoManagedUpdatePaths $Root $Previous
    if (-not [IO.File]::Exists((Join-Path $paths.previous 'ExoLauncher.exe'))) { throw 'The previous app payload is unavailable.' }
    # Resolve and verify both whole-tree moves beneath the explicitly named managed root.
    $failed = Join-Path $paths.root ('app.failed-' + [guid]::NewGuid().ToString('N'))
    $hadApp = [IO.Directory]::Exists($paths.app)
    if ($hadApp) { Move-Item -LiteralPath $paths.app -Destination $failed }
    try {
        # A plain rename (Move-Item moves *into* an existing folder and leaves an empty one behind when it fails, so a
        # retry would nest the payload). A scanner holding the just-renamed tree usually lets go within a second or two.
        for ($attempt = 1; ; $attempt++) {
            try { [IO.Directory]::Move($paths.previous, $paths.app); break }
            catch { if ($attempt -ge 5) { throw }; Start-Sleep -Milliseconds 500 }
        }
    } catch {
        # Never leave the install with no app folder: put the replacement back so the shortcut still opens something.
        if ($hadApp -and -not [IO.Directory]::Exists($paths.app)) { [IO.Directory]::Move($failed, $paths.app) }
        throw
    }
    return $failed
}

if ($updateDefinitionsOnly) { return }
try { $paths = Get-ExoManagedUpdatePaths $InstallRoot $PreviousDirectory }
catch {
    # The swap is done but this can't check it: said where the app looks (update-error.log), never a silent exit.
    try { [IO.File]::WriteAllText((Join-Path $InstallRoot 'update-error.log'), $_.Exception.Message) } catch { }
    Write-Error $_.Exception.Message -ErrorAction Continue
    exit 1
}
$exe = Join-Path $paths.app 'ExoLauncher.exe'
$nonce = [guid]::NewGuid().ToString('N')
$pending = Join-Path $paths.root 'update-pending.json'
$receipt = Join-Path $paths.root ('update-health-' + $nonce + '.json')
$outcome = Join-Path $paths.root 'update-result.json'
$child = $null
try {
    $manifest = Test-ExoPayload $paths.app
    if ($manifest.version -cne $Version) { throw 'The installed payload version does not match this update.' }
    Write-ExoUpdateJson $pending @{ schema=1; nonce=$nonce; version=$Version; executable=$exe; createdUtc=[DateTimeOffset]::UtcNow.ToString('o') }
    Write-ExoUpdateJson $outcome @{ schema=1; version=$Version; status='checking-startup'; atUtc=[DateTimeOffset]::UtcNow.ToString('o') }
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.WorkingDirectory = $paths.app; $start.EnvironmentVariables['EXO_UPDATE_HEALTH_NONCE'] = $nonce
    $child = [Diagnostics.Process]::Start($start)
    if ($null -eq $child) { throw 'Windows did not start the replacement app.' }
    # Longer than the app's own 45 s after its page loads: a cold first start of a new build on a slow disk takes a while.
    $deadline = [DateTime]::UtcNow.AddSeconds(180)
    $healthy = $false
    while (-not $child.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        if (Test-ExoStartupReceipt $receipt $nonce $Version $exe $child.Id) { $healthy = $true; break }
        Start-Sleep -Milliseconds 200
    }
    if (-not $healthy) { throw 'The replacement did not reach a usable startup within three minutes.' }
    Write-ExoUpdateJson $outcome @{ schema=1; version=$Version; status='succeeded'; atUtc=[DateTimeOffset]::UtcNow.ToString('o'); processId=$child.Id }
    # Only this setup's validated previous payload can be removed. A link or a
    # locked file leaves the backup for inspection without undoing a healthy update.
    try {
        $paths = Get-ExoManagedUpdatePaths $InstallRoot $PreviousDirectory
        $null = @(Get-ExoPayloadFiles $paths.previous)
        Remove-Item -LiteralPath $paths.previous -Recurse -Force
    } catch { Write-Warning 'The previous payload remains preserved because cleanup could not finish.' }
    exit 0
} catch {
    $message = $_.Exception.Message
    $status = 'failed-preserved'; $failedPayload = $null
    try {
        if ((Stop-ExoInstalledApp $paths.app $paths.root $PID) -ne 0) { throw 'The replacement is still running; the rollback payload remains preserved.' }
        $failedPayload = Restore-ExoUpdateBackup $paths.root $paths.previous
        $status = 'rolled-back'
        $restart = [Diagnostics.ProcessStartInfo]::new((Join-Path $paths.app 'ExoLauncher.exe'))
        $restart.UseShellExecute = $false; $restart.CreateNoWindow = $true; $restart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $restart.WorkingDirectory = $paths.app
        $old = [Diagnostics.Process]::Start($restart)
        if ($null -ne $old) { $old.Dispose() }
    } catch { $message += ' ' + $_.Exception.Message }
    Write-ExoUpdateJson $outcome @{ schema=1; version=$Version; status=$status; message=$message; atUtc=[DateTimeOffset]::UtcNow.ToString('o'); failedPayload=$failedPayload }
    [IO.File]::WriteAllText((Join-Path $paths.root 'update-error.log'), $message)
    Write-Error $message -ErrorAction Continue
    exit 1
} finally {
    if ($null -ne $child) { $child.Dispose() }
    foreach ($path in @($pending, $receipt)) { try { [IO.File]::Delete($path) } catch { } }
}
