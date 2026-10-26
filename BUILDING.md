# Building Exo Launcher from source

You may build and run Exo on your own devices to check it against the official release (see [LICENSE](LICENSE)). This page is the whole path from a clean Windows 11 PC to a running build.

## What you need

- Windows 11 x64.
- Visual Studio 2022 or newer, or its Build Tools, with the **.NET desktop development** workload and the Windows 11 SDK (10.0.26100). Add **Desktop development with C++** only if you build the add-ons.
- The .NET SDK named in [global.json](https://github.com/ImAvgErix/ExoLauncher/blob/main/global.json) (10.0.x; stable releases only).
- Node.js 24.19 or newer (CI uses 26) with npm.
- Git.

## Build and run

```powershell
git clone https://github.com/ImAvgErix/ExoLauncher.git
cd ExoLauncher
npm ci --prefix ui
npm ci --prefix mouse
pwsh -File Run-ExoLauncher.ps1
```

`Run-ExoLauncher.ps1` builds the Debug host and the UI, then starts Exo. `-NoBuild` starts the last build.

## The same checks CI runs

From `ui/`:

```powershell
npm run check
npm run dead
npm run build
```

From `mouse/`: `npm run check`, then `npm run build`.

From the repository root:

```powershell
dotnet build ExoLauncher.sln -c Release -p:Platform=x64
dotnet test --project ExoLauncher.Tests -c Release -p:Platform=x64
```

The red-team proofs in `security/attacks/` and the installer checks (`tools\Test-InstallerPayload.ps1`, `tools\Test-UpdateRollback.ps1`) run without installing anything.

## The add-ons

Three programs ship beside Exo and are separate projects under their own licenses:

| Add-on | License | Source | Build |
| --- | --- | --- | --- |
| Exo Capture | GPL-3.0-or-later | [`capture/`](https://github.com/ImAvgErix/ExoLauncher/blob/main/capture/README.md), also [exo-capture](https://github.com/ImAvgErix/exo-capture) | `pwsh -File capture\build.ps1` (Visual Studio C++ tools, CMake, Ninja) |
| Exo Mouse | AGPL-3.0-or-later | [`mouse/`](https://github.com/ImAvgErix/ExoLauncher/blob/main/mouse/README.md), also [exo-mouse](https://github.com/ImAvgErix/exo-mouse) | `npm run build` in `mouse/` |
| Exo Shaders | GPL-3.0-or-later | [exo-shaders](https://github.com/ImAvgErix/exo-shaders) | its own README |

## Checking a build against the release

- The installer of each release has a SHA-256 beside it. That checks your download, not your build.
- Builds are not promised to be byte-for-byte reproducible: a different compiler, SDK or ReadyToRun pass changes the bytes. To check the official release against the source, read what it does rather than hash it: [PRIVACY.md](PRIVACY.md) lists every network destination, [SECURITY.md](https://github.com/ImAvgErix/ExoLauncher/blob/main/SECURITY.md) the trust boundaries, and the tests in `ExoLauncher.Tests` (`NetworkDestinationsTests`, `BannedNativeImportsTests`) fail the build when code strays from them.
- A shipped release is made by [`tools/Build-Release.ps1`](https://github.com/ImAvgErix/ExoLauncher/blob/main/tools/Build-Release.ps1): a clean self-contained publish, the UI and add-ons, then the NSIS installer.

## When something does not build

Open an [issue](https://github.com/ImAvgErix/ExoLauncher/issues) with the command you ran and the last lines of its output. Leave out tokens, account details and personal paths.
