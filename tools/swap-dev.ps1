$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$pub = Join-Path $repo 'ExoLauncher\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\publish'
$app = Join-Path $env:LOCALAPPDATA 'ExoLauncher\app'
if (-not (Test-Path -LiteralPath (Join-Path $pub 'ExoLauncher.exe'))) { throw 'publish output is missing ExoLauncher.exe' }
if (-not (Test-Path -LiteralPath (Join-Path $app 'ExoLauncher.exe'))) { throw 'installed app directory is missing' }
$incoming = Join-Path $env:LOCALAPPDATA ('ExoLauncher\app.incoming-dev-' + [guid]::NewGuid().ToString('N'))
$previous = Join-Path $env:LOCALAPPDATA ('ExoLauncher\app.previous-dev-' + [guid]::NewGuid().ToString('N'))
robocopy $pub $incoming /E /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -gt 7) { throw "robocopy failed with exit code $LASTEXITCODE" }
if (-not (Test-Path -LiteralPath (Join-Path $incoming 'ExoLauncher.exe'))) { throw 'staged copy is missing ExoLauncher.exe' }
# dotnet publish carries no add-ons (Build-Release.ps1 adds them): keep each installed one the
# publish lacks, or the swap drops Exo Capture and Settings says it is not installed.
$installedAddons = Join-Path $app 'addons'
if (Test-Path -LiteralPath $installedAddons) {
    foreach ($addon in Get-ChildItem -LiteralPath $installedAddons -Directory) {
        $target = Join-Path $incoming ('addons\' + $addon.Name)
        if (Test-Path -LiteralPath $target) { continue }
        robocopy $addon.FullName $target /E /NFL /NDL /NJH /NJS | Out-Null
        if ($LASTEXITCODE -gt 7) { throw "robocopy of add-on $($addon.Name) failed with exit code $LASTEXITCODE" }
    }
}
$targets = @(Get-Process ExoLauncher -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $app 'ExoLauncher.exe') })
foreach ($process in $targets) {
    if (-not $process.CloseMainWindow()) { throw 'Exo did not accept a normal close. Exit Exo before swapping.' }
    if (-not $process.WaitForExit(20000)) { throw 'Exo has not finished releasing hosted apps. Swap canceled; no processes were killed.' }
}
Rename-Item -LiteralPath $app -NewName (Split-Path $previous -Leaf)
try {
    Rename-Item -LiteralPath $incoming -NewName (Split-Path $app -Leaf)
}
catch {
    if (-not (Test-Path -LiteralPath $app)) { Rename-Item -LiteralPath $previous -NewName (Split-Path $app -Leaf) }
    throw
}
# Keep only the build we just replaced for rollback; older backups only
# accumulate (~1 GB each per swap).
Get-ChildItem (Split-Path $app -Parent) -Directory -Filter 'app.previous-dev-*' |
Where-Object { $_.FullName -ne $previous } |
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
Get-ChildItem (Split-Path $app -Parent) -Directory -Filter 'app.incoming-dev-*' |
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
Test-Path -LiteralPath (Join-Path $app 'ExoLauncher.exe')
