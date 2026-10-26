# Builds Exo Capture (capture\out\ExoCapture.exe) with the Visual Studio C++ tools, CMake and Ninja.
#   pwsh -File capture\build.ps1 [-Configuration Release|Debug] [-Test [test options]]
# -Test also builds and runs the self-test of Shrink (build\<configuration>\ExoCaptureShrinkTest.exe); options after it go to the test.
[CmdletBinding()]
param([ValidateSet('Release', 'Debug')][string]$Configuration = 'Release', [switch]$Test, [Parameter(ValueFromRemainingArguments)][string[]]$TestArgs)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio C++ tools (x64) are not installed.' }
$devCmd = Join-Path $vs 'Common7\Tools\VsDevCmd.bat'
$build = Join-Path $here "build\$Configuration"
$out = Join-Path $here 'out'
New-Item -ItemType Directory -Force -Path $build, $out | Out-Null
$commands = @(
    "`"$devCmd`" -arch=x64 -host_arch=x64 -no_logo",
    "cmake -S `"$here`" -B `"$build`" -G Ninja -DCMAKE_BUILD_TYPE=$Configuration",
    "cmake --build `"$build`""
) -join ' && '
cmd /c $commands
if ($LASTEXITCODE) { throw "Exo Capture build failed ($LASTEXITCODE)." }
Copy-Item (Join-Path $build 'ExoCapture.exe') $out -Force
Copy-Item (Join-Path $here 'LICENSE') $out -Force
New-Item -ItemType Directory -Force -Path (Join-Path $out 'obs') | Out-Null
Copy-Item (Join-Path $here 'third_party\obs\*') (Join-Path $out 'obs') -Force
Write-Output "Exo Capture: $(Join-Path $out 'ExoCapture.exe')"
if ($Test) {
    cmd /c "`"$devCmd`" -arch=x64 -host_arch=x64 -no_logo && cmake --build `"$build`" --target ExoCaptureShrinkTest"
    if ($LASTEXITCODE) { throw "The Shrink self-test did not build ($LASTEXITCODE)." }
    & (Join-Path $build 'ExoCaptureShrinkTest.exe') @TestArgs
    if ($LASTEXITCODE) { throw "The Shrink self-test failed ($LASTEXITCODE)." }
}
