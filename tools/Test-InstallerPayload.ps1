param([string]$EvidenceDirectory = (Join-Path $PSScriptRoot '..\artifacts\installer-fixtures'))
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('exo-installer-fixture-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $root "O'Brien profile\app.incoming-123"
[IO.Directory]::CreateDirectory((Join-Path $payload 'wwwroot')) | Out-Null
$required = @('ExoLauncher.exe', 'ExoLauncher.dll', 'ExoLauncher.deps.json', 'ExoLauncher.runtimeconfig.json', 'wwwroot\index.html')
foreach ($relative in $required) { [IO.File]::WriteAllText((Join-Path $payload $relative), 'fixture-' + $relative) }
& (Join-Path $PSScriptRoot 'installer\New-PayloadManifest.ps1') -PayloadRoot $payload -Version '1.2.3' | Out-Null
. (Join-Path $PSScriptRoot 'installer\Verify-Payload.ps1') -DefinitionsOnly
. (Join-Path $PSScriptRoot 'installer\Close-ExoLauncher.ps1') -DefinitionsOnly
$checks = [Collections.Generic.List[string]]::new()
$null = Test-ExoPayload $payload; $checks.Add('complete payload accepted')
function MustReject([scriptblock]$Action, [string]$Name) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw "Fixture was not rejected: $Name" }
    $checks.Add($Name)
}
$file = Join-Path $payload 'ExoLauncher.dll'
$original = [IO.File]::ReadAllText($file)
[IO.File]::WriteAllText($file, $original.Replace('fixture', 'changed'))
MustReject { Test-ExoPayload $payload } 'same-size corruption rejected'
[IO.File]::WriteAllText($file, $original)
[IO.File]::Move($file, (Join-Path $root 'held.dll'))
MustReject { Test-ExoPayload $payload } 'missing DLL rejected'
[IO.File]::Move((Join-Path $root 'held.dll'), $file)
[IO.File]::WriteAllText((Join-Path $payload 'unexpected.dll'), 'unexpected')
MustReject { Test-ExoPayload $payload } 'unexpected DLL rejected'
[IO.File]::Delete((Join-Path $payload 'unexpected.dll'))
$null = Test-ExoPayload $payload
$data = Join-Path $root "O'Brien profile"
$profiles = @(Get-ExoProfileRoots (Join-Path $data 'app') $data)
foreach ($profile in $profiles) {
    if (-not (Test-ExoWebViewCommandLine ('msedgewebview2.exe --user-data-dir="' + $profile + '" --flag') $profiles)) { throw 'Owned profile was not matched.' }
}
if (Test-ExoWebViewCommandLine ('msedgewebview2.exe --user-data-dir="' + $profiles[0] + '-another-app"') $profiles) { throw 'Sibling profile was matched.' }
if (Test-ExoWebViewCommandLine ('msedgewebview2.exe --other="' + $profiles[0] + '"') $profiles) { throw 'Unrelated argument was matched.' }
$checks.Add('all current and legacy WebView profiles matched exactly')
$checks.Add('apostrophe and spaces remain literal paths')
New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
$result = @{ ok = $true; powershell = $PSVersionTable.PSVersion.ToString(); checks = @($checks); fixtureRoot = $root }
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'installer-payload-fixtures.json') -Encoding UTF8
$result | ConvertTo-Json -Depth 5
# Keep only the isolated fixture for inspection; no application or installer was executed.
