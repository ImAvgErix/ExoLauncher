<p align="center">
  <img src="docs/media/logo.png" alt="Exo Launcher" width="104" />
</p>

<h1 align="center">Exo Launcher</h1>

<p align="center"><strong>Your PC games. One calm home.</strong></p>

<p align="center">
  A focused AMOLED home for Steam, Epic Games, GOG, Riot, and portable Windows games.
</p>

<p align="center">
  <a href="https://github.com/ImAvgErix/ExoLauncher/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/ImAvgErix/ExoLauncher?style=flat-square&label=release&color=79f2c0" /></a>
  <a href="https://github.com/ImAvgErix/ExoLauncher/releases/latest"><img alt="Downloads" src="https://img.shields.io/github/downloads/ImAvgErix/ExoLauncher/total?style=flat-square&color=111111" /></a>
  <a href="LICENSE"><img alt="MIT license" src="https://img.shields.io/github/license/ImAvgErix/ExoLauncher?style=flat-square&color=111111" /></a>
</p>

<p align="center">
  <a href="https://github.com/ImAvgErix/ExoLauncher/releases/latest/download/ExoLauncher-Setup.exe"><strong>Download for Windows</strong></a>
  ·
  <a href="https://github.com/ImAvgErix/ExoLauncher/issues">Report an issue</a>
  ·
  <a href="https://www.buymeacoffee.com/UhhErix">Support development</a>
</p>

Exo Launcher gives supported PC games one installed-first library and one obvious action: **Play**, **Install**, or **Update**. The official stores still own authentication, licenses, downloads, DRM, and anti-cheat; Exo coordinates them and keeps their chrome out of the way when it safely can.

There is no Exo account, profile, cloud sync, ad platform, or analytics layer. Install it, let it discover the launchers already on the PC, and play.

## One library instead of launcher clutter

- **Installed-first home** — covers, favorites, recent games, and live running/update state without a wall of store filters.
- **One game action** — launch, install, update, uninstall, cancel, or stop the exact selected game from its Exo detail view.
- **Smart discovery** — typo-tolerant, punctuation-insensitive search can find titles even when the query is not exact.
- **High-quality artwork** — portrait cover discovery and local caching prefer provider and catalog art, with a clean fallback when no real cover exists.
- **Lifetime context where providers expose it** — native store playtime wins; Exo records local sessions only as a fallback.
- **Native achievement moments** — Steam and Epic unlocks can use real achievement art, provider-aware rarity, exact screen placement, and a compact reward cue.
- **Quiet game sessions** — supported store windows and store audio are kept in the background during Exo-driven sessions, then restored without terminating protected services, overlays, or anti-cheat.
- **Portable games welcome** — add a local game folder without handing ownership of the original files to Exo.
- **Built-in updates** — Exo can check GitHub Releases, verify the published installer digest, and perform an atomic per-user update.

## What is new in 1.0.24

Version 1.0.24 turns the last large feature pass into a safer everyday launcher:

- **Exact store actions** — Steam install and update requests reach the selected app ID without being intercepted by generic runtime setup; Epic cold launches wait for the official client's command listener before handing off the exact game URI.
- **Multi-store games without double cards** — exact same-title Steam/Epic entries share one card while every source keeps its own install, update, playtime, account scope, and action target.
- **Account-safe discovery** — active Steam/Epic account caches are isolated; a machine-wide install can remain visible when account identity is unknown without being presented as owned.
- **Session-true achievement moments** — Exo snapshots progress before handoff and only attributes a new unlock after a successful Exo-driven game session. Manual refreshes reconcile silently.
- **A new Exo reward cue** — an original 1.18-second futuristic stereo sound replaces the generic sample, paired with a unified AMOLED trophy plate, real provider artwork, rarity color, a brief scale-and-fade entrance, and exact nine-point placement.
- **Broader official-client awareness** — Settings honestly detects and opens Xbox, EA app, Ubisoft Connect, Battle.net, Amazon Games, and Rockstar Games Launcher without inventing libraries, accounts, or unsupported game actions.
- **Cleaner, faster surfaces** — compact no-waste Settings, high-resolution cover upgrades, restored card lift, constrained pinned-game overflow, pure-black surfaces, and zero idle Core Audio enumeration when Exo is not driving a store.

Read the complete [changelog](CHANGELOG.md).

## Store reality, stated plainly

Exo is a launcher—not a DRM, ownership, or anti-cheat bypass.

- Steam, Epic Games, GOG, and Riot provide Exo's wired game-library actions; portable games launch directly.
- Xbox, EA app, Ubisoft Connect, Battle.net, Amazon Games, and Rockstar Games Launcher are honest presence/Open integrations in this release. Exo does not claim unsupported libraries, account connections, installs, or launches for them.
- Store clients and required services may still be needed for authentication, licensing, patching, multiplayer, or anti-cheat.
- Optional helpers such as Legendary and gogdl are used only for supported actions that need them. See the [vendor/backend notes](docs/VENDORS.md).
- Achievement reading and Exo achievement notifications currently cover **Steam and Epic** data. Unsupported providers are not reported as if they had zero achievements.
- Quiet Game Mode is best-effort store-window and store-audio control. Vendor overlays, chat messages, game notifications, and Windows notifications remain owned by those systems.

## Install

1. Download **[ExoLauncher-Setup.exe](https://github.com/ImAvgErix/ExoLauncher/releases/latest/download/ExoLauncher-Setup.exe)**.
2. Run the installer. Exo installs per-user under `%LOCALAPPDATA%\ExoLauncher\app`.
3. Open Exo Launcher and let the library discover supported installed games.

**Requirement:** Windows 11 x64.

The current public installer is not code-signed, so Windows SmartScreen may ask for confirmation. GitHub publishes a SHA-256 digest beside the release asset; verify it when you want an independent integrity check.

## Local-first by design

Launcher settings, library metadata, cover caches, achievement baselines, and fallback sessions stay on the PC. Exo Launcher does not require an Exo identity and does not upload store credentials, machine paths, friends, chat, or gameplay telemetry to an Exo service.

Read the full [privacy statement](PRIVACY.md).

## Build from source

The desktop host is WinUI 3 on .NET 10; the embedded interface is React, TypeScript, and Vite.

```powershell
npm --prefix ui ci
npm --prefix ui run build
dotnet test ExoLauncher.sln -c Debug -p:Platform=x64
dotnet run --project ExoLauncher/ExoLauncher.csproj -c Debug -p:Platform=x64
```

## Support and the Exo family

- Found a bug or a bad game match? [Open an issue](https://github.com/ImAvgErix/ExoLauncher/issues).
- Want to help keep Exo free? [Buy me a coffee](https://www.buymeacoffee.com/UhhErix).
- Related projects: [Exo Hub](https://github.com/ImAvgErix/ExoHub) · [Exo OS](https://github.com/ImAvgErix/ExoOS) · [Exo Link](https://github.com/ImAvgErix/ExoLink)

## License

MIT © 2026 Erix ([ImAvgErix](https://github.com/ImAvgErix))

<p align="center"><sub>Presence without weight.</sub></p>
