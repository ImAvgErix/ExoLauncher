# Builds Exo Setup's window (setup\out\ExoSetup.exe): its screens from ui\ (vite.setup.config.ts), packed into the exe,
# then the C++ shell with the Visual Studio C++ tools, CMake and Ninja. -Engine appends Exo's NSIS install engine and
# writes the finished installer to -Out (Build-Release does this).
#   pwsh -File setup\build.ps1 [-Engine path\to\engine.exe -AppBytes n -Out path\to\ExoLauncher-Setup.exe]
[CmdletBinding()]
param([string]$Engine, [long]$AppBytes, [string]$Out)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$repo = Split-Path -Parent $here
$version = (Get-Content (Join-Path $repo 'VERSION') -Raw).Trim()
$build = Join-Path $here 'build'
$outDir = Join-Path $here 'out'
New-Item -ItemType Directory -Force -Path $build, $outDir | Out-Null

# The screens, then one pack the exe serves from memory: [u32 count] then [u16 name][name][u32 size][bytes] per file.
Push-Location (Join-Path $repo 'ui')
npx vite build --config vite.setup.config.ts --logLevel warn
if ($LASTEXITCODE) { throw "Setup screens build failed ($LASTEXITCODE)." }
Pop-Location
$ui = Join-Path $build 'ui'
Move-Item -Force (Join-Path $ui 'setup.html') (Join-Path $ui 'index.html')
Copy-Item (Join-Path $repo 'ui\public\logo.svg') (Join-Path $ui 'logo.svg') -Force
# Inter 4.1, as Exo draws it (ui/src/fonts.css loads /fonts): the upright cut is all the screens use.
New-Item -ItemType Directory -Force (Join-Path $ui 'fonts') | Out-Null
Copy-Item (Join-Path $repo 'ui\public\fonts\InterVariable.woff2'), (Join-Path $repo 'ui\public\fonts\LICENSE-Inter.txt') (Join-Path $ui 'fonts') -Force
$files = Get-ChildItem -Recurse -File $ui
$pack = [IO.MemoryStream]::new()
$writer = [IO.BinaryWriter]::new($pack)
$writer.Write([uint32]$files.Count)
foreach ($file in $files) {
    $name = [Text.Encoding]::UTF8.GetBytes($file.FullName.Substring($ui.Length + 1).Replace('\', '/'))
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    $writer.Write([uint16]$name.Length); $writer.Write($name)
    $writer.Write([uint32]$bytes.Length); $writer.Write($bytes)
}
$writer.Flush()
[IO.File]::WriteAllBytes((Join-Path $build 'ui.pack'), $pack.ToArray())

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio C++ tools (x64) are not installed.' }
$devCmd = Join-Path $vs 'Common7\Tools\VsDevCmd.bat'
$native = Join-Path $build 'native'
$commands = @(
    "`"$devCmd`" -arch=x64 -host_arch=x64 -no_logo",
    "cmake -S `"$here`" -B `"$native`" -G Ninja -DCMAKE_BUILD_TYPE=Release -DEXO_VERSION=$version",
    "cmake --build `"$native`""
) -join ' && '
cmd /c $commands
if ($LASTEXITCODE) { throw "Exo Setup build failed ($LASTEXITCODE)." }
$shell = Join-Path $outDir 'ExoSetup.exe'
Copy-Item (Join-Path $native 'ExoSetup.exe') $shell -Force
Write-Output "Exo Setup window: $shell"

# The finished installer: the window, then the engine, its size, the app's size (for progress) and the mark.
if ($Engine) {
    $target = [IO.File]::Open($Out, [IO.FileMode]::Create)
    try {
        foreach ($part in $shell, $Engine) { $source = [IO.File]::OpenRead($part); $source.CopyTo($target); $source.Dispose() }
        $tail = [IO.BinaryWriter]::new($target)
        $tail.Write([uint64](Get-Item $Engine).Length)
        $tail.Write([uint64]$AppBytes)
        $tail.Write([Text.Encoding]::ASCII.GetBytes('EXOSETUP'))
        $tail.Flush()
    } finally { $target.Dispose() }
    Write-Output "Exo Launcher Setup: $Out"
}
