# .NET 11 release readiness

`global.json` pins .NET SDK `11.0.100-preview.7.26381.103` with prereleases allowed and
`rollForward: latestPatch`, so a newer 11.0.100 prerelease (an RC) builds it too. Keep the
pin until a stable .NET 11 SDK and runtime are available and pass release validation.
As of September 8, 2026, the official [.NET 11 downloads](https://dotnet.microsoft.com/en-us/download/dotnet/11.0)
still listed previews; the [.NET release schedule](https://github.com/dotnet/core/blob/main/release-notes/README.md)
places the final release in November 2026.

Once a stable SDK is published, review its release notes and supported Windows
versions, install the exact candidate, and run:

```powershell
./tools/Test-RuntimeCandidate.ps1 -CandidateSdkVersion <reviewed-11.0-SDK-version> `
  -PackageDirectory ./artifacts/runtime-candidate/package -BuildAndPublish
```

The gate selects the exact SDK from a disposable working directory (never rewriting the
repository's pin), builds the full Release solution and publishes the self-contained
app, checks that the package declares a .NET 11 runtime, and writes
`artifacts/runtime-candidate/runtime-candidate.json`: SDK, packaged runtime, executable
hash, OS build, architecture, CPU, GPU and driver. The manual Runtime candidate workflow
(`.github/workflows/runtime-candidate.yaml`) runs the same gate. Preview SDKs and
runtimes are rejected unless `-AllowPreviewBaseline` records the current baseline. It
runs no tests and does not start the app: run `dotnet test` and the E2E suite
(AGENTS.md › Gates, Testing) on the candidate build as well.

The host declares Windows build 17763 as its minimum (`TargetPlatformMinVersion` in
`ExoLauncher/ExoLauncher.csproj`). Run the candidate package on that build itself with
`-RequireMinimumWindowsBuild`, and on the supported x64 CPU/GPU targets; a newer
developer or CI machine is not evidence for the minimum build.

Before changing `global.json`, keep the target-machine evidence, review the manual
Windows SDK targeting entries in `Directory.Build.props`, and confirm whether the
stable SDK supports the `10.0.28000.0` target directly. Remove those entries only after
restore, WinUI resource indexing, native capture and the packaged app pass without
them. Keep the tested pin if any of these fail.
