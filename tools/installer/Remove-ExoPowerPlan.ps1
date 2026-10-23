param(
    [Parameter(Mandatory = $true)][string]$DataDirectory
)

# Exo's uninstaller: back to the plan Exo's power plan replaced (Balanced when there's none or it's gone), then
# Exo's plan is deleted. The plan and the one it replaced come from Exo's own state file (PowerPlan.cs, Saved).
$ErrorActionPreference = 'SilentlyContinue'
$guid = '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
$state = Join-Path $DataDirectory 'exo-power-plan.json'
if (-not (Test-Path -LiteralPath $state)) { exit 0 }
$saved = Get-Content -LiteralPath $state -Raw | ConvertFrom-Json
$plan = [string]$saved.Plan
if ($plan -notmatch $guid) { exit 0 }

if ((powercfg /getactivescheme | Out-String) -match $plan) {
    $back = [string]$saved.Previous
    if ($back -notmatch $guid -or -not ((powercfg /list | Out-String) -match $back)) { $back = '381b4222-f694-41f0-9685-ff5bb260df2e' }
    powercfg /setactive $back | Out-Null
}
powercfg /delete $plan | Out-Null
Remove-Item -LiteralPath $state -Force
exit 0
