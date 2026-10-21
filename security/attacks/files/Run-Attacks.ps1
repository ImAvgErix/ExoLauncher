#!/usr/bin/env pwsh
# Reruns the Exo Launcher security attack suite (ExoLauncher.Tests).
# Each test calls the real host methods with launches/downloads stubbed or
# intercepted; nothing dangerous runs and no external service is contacted.
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
Push-Location $repo
try {
    dotnet test (Join-Path $repo 'ExoLauncher.Tests\ExoLauncher.Tests.csproj') -c Debug -p:Platform=x64 @args
} finally {
    Pop-Location
}
