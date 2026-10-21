param(
    [Parameter(Mandatory)][ValidatePattern('^11\.0\.\d+(?:-[0-9A-Za-z.-]+)?$')][string]$CandidateSdkVersion,
    [Parameter(Mandatory)][string]$PackageDirectory,
    [string]$EvidenceDirectory = (Join-Path $PSScriptRoot '..\artifacts\runtime-candidate'),
    [switch]$BuildAndPublish,
    [switch]$AllowPreviewBaseline,
    [switch]$RequireMinimumWindowsBuild
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$evidenceRoot = [IO.Path]::GetFullPath($EvidenceDirectory)
$packageRoot = [IO.Path]::GetFullPath($PackageDirectory)
if ($CandidateSdkVersion.Contains('-') -and -not $AllowPreviewBaseline) {
    throw 'A release candidate gate requires a stable SDK. Use -AllowPreviewBaseline only to record the intentional current preview baseline.'
}
$sdkLines = @(& dotnet --list-sdks)
if ($LASTEXITCODE -ne 0 -or -not ($sdkLines | Where-Object { ($_ -split ' ')[0] -eq $CandidateSdkVersion })) {
    throw "SDK $CandidateSdkVersion is not installed. Install that exact reviewed SDK before running this gate."
}
$osBuild = [Environment]::OSVersion.Version.Build
$minimumBuild = 17763
if ($RequireMinimumWindowsBuild -and $osBuild -ne $minimumBuild) {
    throw "This is Windows build $osBuild. The required minimum-build check must run on build $minimumBuild itself."
}
New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
$selectionDirectory = Join-Path $evidenceRoot ('sdk-selection-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $selectionDirectory | Out-Null
@{ sdk = @{ version = $CandidateSdkVersion; rollForward = 'disable'; allowPrerelease = [bool]$AllowPreviewBaseline } } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $selectionDirectory 'global.json')
# The dotnet host resolves its SDK from the current directory. The repository's
# global.json remains intact while absolute project paths use the selected SDK.
Push-Location -LiteralPath $selectionDirectory
try {
    $selected = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $selected -ne $CandidateSdkVersion) { throw 'The exact candidate SDK was not selected.' }
    if ($BuildAndPublish) {
        & dotnet build (Join-Path $repoRoot 'ExoLauncher.sln') -c Release -p:Platform=x64 -m:1 -p:WindowsAppSDKSelfContained=true -p:PublishReadyToRun=false *> (Join-Path $evidenceRoot 'build.log')
        if ($LASTEXITCODE -ne 0) { throw 'Candidate build failed; see build.log.' }
        & dotnet publish (Join-Path $repoRoot 'ExoLauncher\ExoLauncher.csproj') -c Release -p:Platform=x64 -m:1 -p:WindowsAppSDKSelfContained=true -p:PublishReadyToRun=false -o $packageRoot *> (Join-Path $evidenceRoot 'publish.log')
        if ($LASTEXITCODE -ne 0) { throw 'Candidate publish failed; see publish.log.' }
    }
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $packageRoot 'ExoLauncher.runtimeconfig.json') -Raw | ConvertFrom-Json
    $frameworks = @($runtimeConfig.runtimeOptions.includedFrameworks) + @($runtimeConfig.runtimeOptions.frameworks) + @($runtimeConfig.runtimeOptions.framework)
    $runtime = $frameworks | Where-Object name -eq 'Microsoft.NETCore.App' | Select-Object -First 1
    if (-not $runtime -or $runtime.version -notmatch '^11\.0\.') { throw 'The package does not declare a .NET 11 runtime.' }
    if ($runtime.version.Contains('-') -and -not $AllowPreviewBaseline) { throw 'The package still contains a preview runtime.' }
    $exe = Join-Path $packageRoot 'ExoLauncher.exe'
    $cpu = @(Get-CimInstance Win32_Processor -ErrorAction SilentlyContinue | Select-Object Name, Architecture)
    $gpu = @(Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | Select-Object Name, DriverVersion)
    $report = @{
        schemaVersion = 1; capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        sdk = $selected; packagedRuntime = $runtime.version
        previewBaseline = [bool]$AllowPreviewBaseline
        rebuiltAndPublished = [bool]$BuildAndPublish
        executableSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
        os = [Environment]::OSVersion.VersionString; osBuild = $osBuild
        processArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        cpu = $cpu; gpu = $gpu
        minimumWindowsBuild = $minimumBuild; minimumWindowsBuildTested = ($osBuild -eq $minimumBuild)
        scope = 'This report validates only this package and machine. Other supported CPU/GPU and minimum-OS targets require their own runs.'
    }
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'runtime-candidate.json')
    Write-Output "Runtime candidate evidence: $evidenceRoot"
} finally { Pop-Location }
