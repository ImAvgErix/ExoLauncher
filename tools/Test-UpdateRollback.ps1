param([string]$EvidenceDirectory = (Join-Path $PSScriptRoot '..\artifacts\installer-fixtures'))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'installer\Complete-ExoUpdate.ps1') -DefinitionsOnly
$root = Join-Path ([IO.Path]::GetTempPath()) ('exo-rollback-fixture-' + [guid]::NewGuid().ToString('N'))
$app = Join-Path $root 'app'; $previous = Join-Path $root 'app.previous-123'
[IO.Directory]::CreateDirectory($app) | Out-Null
[IO.Directory]::CreateDirectory($previous) | Out-Null
$exe = Join-Path $app 'ExoLauncher.exe'
[IO.File]::WriteAllText($exe, 'new fixture')
[IO.File]::WriteAllText((Join-Path $previous 'ExoLauncher.exe'), 'old fixture')
[IO.File]::WriteAllText((Join-Path $root 'settings.json'), 'user data unchanged')
$nonce = [guid]::NewGuid().ToString('N')
$receipt = Join-Path $root 'receipt.json'
Write-ExoUpdateJson $receipt @{ schema=1; nonce=$nonce; version='1.2.3'; executable=$exe; processId=123; healthy=$true }
$checks = [Collections.Generic.List[string]]::new()
if (-not (Test-ExoStartupReceipt $receipt $nonce '1.2.3' $exe 123)) { throw 'Valid startup receipt rejected.' }
foreach ($case in @(
    @{nonce=[guid]::NewGuid().ToString('N'); version='1.2.3'; pid=123},
    @{nonce=$nonce; version='1.2.4'; pid=123},
    @{nonce=$nonce; version='1.2.3'; pid=456}
)) {
    if (Test-ExoStartupReceipt $receipt $case.nonce $case.version $exe $case.pid) { throw 'Unrelated startup receipt accepted.' }
}
$checks.Add('receipt matches nonce version executable and process')
$outside = Join-Path ([IO.Path]::GetTempPath()) 'app.previous-999'
$rejected = $false
try { Restore-ExoUpdateBackup $root $outside | Out-Null } catch { $rejected = $true }
if (-not $rejected) { throw 'Escaped rollback root was accepted.' }
$checks.Add('escaped rollback paths rejected before mutation')
$failed = Restore-ExoUpdateBackup $root $previous
if ([IO.File]::ReadAllText($exe) -ne 'old fixture') { throw 'Previous payload was not restored.' }
if ([IO.File]::ReadAllText((Join-Path $failed 'ExoLauncher.exe')) -ne 'new fixture') { throw 'Failed payload was not preserved.' }
if ([IO.File]::ReadAllText((Join-Path $root 'settings.json')) -ne 'user data unchanged') { throw 'User data changed during rollback.' }
$checks.Add('rollback restores previous payload and preserves failed payload and user data')
# A scanner holding the previous payload must not leave the install with no app folder.
$lockedRoot = Join-Path ([IO.Path]::GetTempPath()) ('exo-rollback-locked-' + [guid]::NewGuid().ToString('N'))
$lockedApp = Join-Path $lockedRoot 'app'; $lockedPrevious = Join-Path $lockedRoot 'app.previous-456'
[IO.Directory]::CreateDirectory($lockedApp) | Out-Null
[IO.Directory]::CreateDirectory($lockedPrevious) | Out-Null
[IO.File]::WriteAllText((Join-Path $lockedApp 'ExoLauncher.exe'), 'new fixture')
[IO.File]::WriteAllText((Join-Path $lockedPrevious 'ExoLauncher.exe'), 'old fixture')
$hold = [IO.File]::Open((Join-Path $lockedPrevious 'ExoLauncher.exe'), 'Open', 'Read', 'None')
$threw = $false
try { Restore-ExoUpdateBackup $lockedRoot $lockedPrevious | Out-Null } catch { $threw = $true } finally { $hold.Dispose() }
if (-not $threw) { throw 'A rollback that could not move the previous payload reported success.' }
if ([IO.File]::ReadAllText((Join-Path $lockedApp 'ExoLauncher.exe')) -ne 'new fixture') { throw 'A failed rollback left no app folder.' }
if (-not [IO.File]::Exists((Join-Path $lockedPrevious 'ExoLauncher.exe'))) { throw 'A failed rollback lost the previous payload.' }
Remove-Item -LiteralPath $lockedRoot -Recurse -Force
$checks.Add('a rollback that cannot move the previous payload puts the app folder back')
$outcome = Join-Path $root 'update-result.json'
Write-ExoUpdateJson $outcome @{ schema=1; status='checking-startup'; version='1.2.3' }
Write-ExoUpdateJson $outcome @{ schema=1; status='rolled-back'; version='1.2.3' }
if (([IO.File]::ReadAllText($outcome) | ConvertFrom-Json).status -ne 'rolled-back') { throw 'Durable result was not replaced.' }
$checks.Add('durable update result is atomically replaced')
New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
$result = @{ ok=$true; powershell=$PSVersionTable.PSVersion.ToString(); checks=@($checks); fixtureRoot=$root }
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'update-rollback-fixtures.json') -Encoding UTF8
$result | ConvertTo-Json -Depth 5
# Fake payload bytes only: no process, installer or real app tree was started or changed.
