# .NET 11 release readiness

The repository intentionally pins .NET SDK `11.0.100-preview.7.26381.103`. Keep the
pin until a stable .NET 11 SDK and runtime are available and pass release validation.
As of September 8, 2026, the official [.NET 11 downloads](https://dotnet.microsoft.com/en-us/download/dotnet/11.0)
still list previews; the [.NET release schedule](https://github.com/dotnet/core/blob/main/release-notes/README.md)
places the final release in November 2026. A stable-runtime migration cannot be
completed or certified against a release that is not yet available.

Once a stable SDK is published, review its release notes and supported Windows
versions, install the exact candidate, and run:

```powershell
./tools/Test-RuntimeCandidate.ps1 -CandidateSdkVersion <reviewed-11.0-SDK-version> `
  -PackageDirectory ./artifacts/runtime-candidate/package -BuildAndPublish
```

The gate selects the exact SDK from a disposable working directory, builds the full
Release solution, runs the native tests, publishes the self-contained app, inspects
the packaged runtime and exercises packaged startup with isolated application data.
It never rewrites the repository SDK pin. The manual Runtime candidate workflow runs
this same gate. Preview SDKs and runtimes are rejected unless the explicit
`-AllowPreviewBaseline` flag is used for a current-baseline measurement.

The native shell currently declares Windows build 17763 as its minimum. Run the
candidate package on that actual build with `-RequireMinimumWindowsBuild`, and run
it on the supported x64 CPU/GPU targets. A newer developer or CI machine is not
evidence for the minimum build. Each report records OS build, architecture, CPU,
GPU, driver, executable hash and packaged runtime; target-machine gaps stay visible.

Before changing `global.json`, retain successful target-machine evidence, review the
manual Windows SDK targeting entries in `Directory.Build.props`, and confirm whether
the stable SDK supports the `10.0.28000.0` target directly. Remove exceptions only
after restore, WinUI resource indexing, native capture and the packaged startup gate
pass without them. Keep the tested existing pin if any of these gates fail.
