# Builds the native parts of Exo's Discord voice (voice\out): dave.dll, Discord's libdave (MIT) for DAVE
# end-to-end encryption, with mlspp and OpenSSL 3 linked in; opus.dll (BSD); and exo_apm.dll, WebRTC's echo
# cancellation (BSD). Pinned sources and pinned vcpkg baselines, built with the Visual Studio C++ tools and
# Visual Studio's own vcpkg.
#   pwsh -File voice\build.ps1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$src = Join-Path $here 'third_party'
$dave = Join-Path $src 'libdave'
$daveCommit = '8de72b1'
$out = Join-Path $here 'out'
New-Item -ItemType Directory -Force -Path $src, $out | Out-Null
if (-not (Test-Path (Join-Path $dave '.git'))) { git clone -q https://github.com/discord/libdave.git $dave }
git -C $dave fetch -q origin
git -C $dave checkout -q $daveCommit
$cpp = Join-Path $dave 'cpp'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio C++ tools (x64) are not installed.' }
$devCmd = Join-Path $vs 'Common7\Tools\VsDevCmd.bat'
# Visual Studio's vcpkg in manifest mode fetches the ports at each manifest's pinned baseline
# (libdave's own vcpkg submodule predates Visual Studio 18 and cannot find it).
$vcpkg = Join-Path $vs 'VC\vcpkg'
$env:VCPKG_ROOT = $vcpkg
function Invoke-Dev([string]$command) {
    cmd /c "`"$devCmd`" -arch=x64 -host_arch=x64 -no_logo && $command"
    if ($LASTEXITCODE) { throw "Failed ($LASTEXITCODE): $command" }
}

# libdave's dependencies (OpenSSL 3, mlspp from its overlay port), from its own manifest.
$deps = Join-Path $here 'build\dave-deps'
Invoke-Dev "`"$vcpkg\vcpkg.exe`" install --x-manifest-root=`"$cpp\vcpkg-alts\openssl_3`" --x-install-root=`"$deps`" --triplet x64-windows-static --disable-metrics"

$daveBuild = Join-Path $here 'build\dave'
Invoke-Dev ("cmake -S `"$cpp`" -B `"$daveBuild`" -G Ninja -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON -DTESTING=OFF " +
    "-DCMAKE_TOOLCHAIN_FILE=`"$vcpkg\scripts\buildsystems\vcpkg.cmake`" -DVCPKG_MANIFEST_MODE=OFF -DVCPKG_INSTALLED_DIR=`"$deps`" " +
    "-DVCPKG_TARGET_TRIPLET=x64-windows-static -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded")
Invoke-Dev "cmake --build `"$daveBuild`" --target libdave"
Get-ChildItem $daveBuild -Recurse -Filter '*.dll' | Where-Object Name -match 'dave' | Select-Object -First 1 | Copy-Item -Destination (Join-Path $out 'dave.dll') -Force

$opus = Join-Path $here 'build\opus'
Invoke-Dev "`"$vcpkg\vcpkg.exe`" install --x-manifest-root=`"$here\opus`" --x-install-root=`"$opus`" --triplet x64-windows --disable-metrics"
Copy-Item (Join-Path $opus 'x64-windows\bin\opus.dll') $out -Force
Copy-Item (Join-Path $dave 'LICENSE') (Join-Path $out 'LICENSE-libdave.txt') -Force
Copy-Item (Join-Path $opus 'x64-windows\share\opus\copyright') (Join-Path $out 'LICENSE-opus.txt') -Force
# Echo cancellation: exo_apm.dll, WebRTC's AEC3 from webrtc-audio-processing 2.1 (BSD-3-Clause, pinned in voice\apm\subprojects)
# behind Exo's small C interface (voice\apm\exo_apm.cc), with Abseil and the static C runtime linked in. Meson comes from a
# pinned pip package in a venv of its own; WebRTC's code needs C++20 under MSVC.
$venv = Join-Path $here 'build\meson-venv'
$meson = Join-Path $venv 'Scripts\meson.exe'
if (-not (Test-Path $meson)) {
    python -m venv $venv
    & (Join-Path $venv 'Scripts\python.exe') -m pip install -q meson==1.9.1
    if ($LASTEXITCODE) { throw 'Meson did not install.' }
}
$apmBuild = Join-Path $here 'build\apm'
if (-not (Test-Path (Join-Path $apmBuild 'build.ninja'))) {
    Invoke-Dev "`"$meson`" setup `"$apmBuild`" `"$here\apm`" --backend=ninja -Dcpp_std=c++20 -Dwebrtc-audio-processing:cpp_std=c++20 -Dabseil-cpp:cpp_std=c++20"
}
Invoke-Dev "ninja -C `"$apmBuild`" exo_apm.dll"
Copy-Item (Join-Path $apmBuild 'exo_apm.dll') $out -Force
$wap = Join-Path $here 'apm\subprojects\webrtc-audio-processing'
$apmLicenses = 'COPYING', 'webrtc\common_audio\third_party\ooura\LICENSE', 'webrtc\common_audio\third_party\spl_sqrt_floor\LICENSE',
    'webrtc\modules\third_party\fft\LICENSE', 'webrtc\third_party\pffft\LICENSE', 'webrtc\third_party\rnnoise\COPYING'
Set-Content (Join-Path $out 'LICENSE-webrtc-audio-processing.txt') (($apmLicenses | ForEach-Object { "== $_`n" + (Get-Content -Raw (Join-Path $wap $_)) }) -join "`n")
Copy-Item (Join-Path $here 'apm\subprojects\abseil-cpp-20240722.0\LICENSE') (Join-Path $out 'LICENSE-abseil.txt') -Force

# DeepFilterNet 3 noise suppression (MIT/Apache-2.0): Exo fetches the project's own Windows plugin at run time
# (NoiseSuppressor.Plugin, same pin); it is kept here only for the tests, and its licenses ship with Exo.
$dfnTag = 'v0.5.6'
$dfnUrl = "https://github.com/Rikorose/DeepFilterNet/releases/download/$dfnTag/deep_filter_ladspa-0.5.6-x86_64-pc-windows-msvc.dll"
$dfnHash = 'F97E34275EB884B6076A96D261683D2A43C6B2D97DDF9E346BC06F7C8057EB04'
$dfn = Join-Path $here 'build\dfn\deep_filter.dll'
New-Item -ItemType Directory -Force -Path (Split-Path $dfn) | Out-Null
if (-not (Test-Path $dfn) -or (Get-FileHash $dfn -Algorithm SHA256).Hash -ne $dfnHash) { Invoke-WebRequest $dfnUrl -OutFile $dfn }
if ((Get-FileHash $dfn -Algorithm SHA256).Hash -ne $dfnHash) { throw 'DeepFilterNet download did not match its pinned SHA-256.' }
foreach ($license in 'LICENSE-MIT', 'LICENSE-APACHE') {
    Invoke-WebRequest "https://raw.githubusercontent.com/Rikorose/DeepFilterNet/$dfnTag/$license" -OutFile (Join-Path $out "LICENSE-deepfilternet-$($license.Substring(8).ToLowerInvariant()).txt")
}
Write-Output "Voice: $(Get-ChildItem $out -Filter *.dll | ForEach-Object Name)"
