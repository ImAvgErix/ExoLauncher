# Exo Launcher

**Presence without weight.**

**One calm Windows library. Other store clients become invisible backends.**


[![Release](https://img.shields.io/github/v/release/ImAvgErix/ExoLauncher?style=flat-square&color=111)](https://github.com/ImAvgErix/ExoLauncher/releases/latest)

One UI. Other clients become invisible dependencies — not apps you open.  
Buy in a browser if you must. Install, update, and launch only in Exo.

Full “delete every other store binary and still play online multiplayer” is **impossible** for closed anti-cheat ecosystems (Riot/Vanguard, EAC, BattlEye, Steam DRM). Exo hides and orchestrates those backends; it does not crack or bypass them.

[![License](https://img.shields.io/github/license/ImAvgErix/ExoLauncher?style=flat-square)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2011%20x64-0078d4?style=flat-square)](https://github.com/ImAvgErix/ExoLauncher)
[![.NET](https://img.shields.io/badge/.NET-10-512bd4?style=flat-square)](https://dotnet.microsoft.com/)

<p align="center">
  <a href="https://github.com/ImAvgErix/ExoLauncher/releases/latest"><strong>Download Exo Launcher</strong></a>
  &nbsp;·&nbsp;
  <a href="CHANGELOG.md">Changelog</a>
  &nbsp;·&nbsp;
  <a href="docs/VENDORS.md">Vendors</a>
  &nbsp;·&nbsp;
  <a href="PRIVACY.md">Privacy</a>
  &nbsp;·&nbsp;
  <a href="SECURITY.md">Security</a>
  &nbsp;·&nbsp;
  <a href="AGENTS.md">Agents</a>
</p>

---

## Product line

> One UI. Other clients become invisible dependencies — not apps you open.  
> Buy in a browser if you must. Install, update, and launch only in Exo.

Same family as [Exo](https://github.com/ImAvgErix/Exo) and ExoOS — AMOLED shell, quiet language, honest limits.

---

## Store support matrix

| Store | Library | Install in Exo | Launch in Exo | Other client on disk |
| --- | --- | --- | --- | --- |
| **Local** | Yes | Yes (folder / portable) | Yes | No |
| **Epic** | Yes | Yes (**Legendary**) | Yes | Optional |
| **GOG** | Yes | Yes (**gogdl**) | Yes | Optional (Galaxy) |
| **Riot** | Fixed tiles | Orchestrated / hidden official installer | Yes | **Yes** (required; Vanguard for online) |
| **Steam** | Yes (`appmanifest`) | Minimized Steam (`steam://install`) | Yes (`steam://rungameid`) | **Usually yes** |
| **Amazon** | Detect | Best-effort (Nile / agent) | Best-effort | Usually yes |
| **Xbox / EA / Ubisoft / Battle.net** | Detect | Best-effort phase-2 stubs | Best-effort | Usually yes |

### Honest notes

- **Epic:** Legendary is the first-class path. Epic Games Launcher GUI is optional when Legendary is signed in.
- **GOG:** heroic-gogdl is the happy path. Galaxy is not required for install/launch when gogdl is present.
- **Riot:** No public “install this product only” CDN API. Exo runs official `RiotClientServices` / bootstrap flags, hides UI, watches disk, force-closes Riot **UX** only. **Vanguard stays.**
- **Steam:** Runtime usually remains installed. Anonymous SteamCMD is for servers — not owned paid games. Exo never logs Steam passwords.
- **Anti-cheat:** No game binary edits, no kernel hacks, no bypass features.

---

## Architecture

```
  React UI (WebView2)  ←JSON-RPC→  WebHostBridge
                                        │
                              LaunchOrchestrator
                                        │
              ┌───────────┬─────────────┼─────────────┬──────────┐
              ▼           ▼             ▼             ▼          ▼
           Local     Epic(Legendary)  GOG(gogdl)    Riot      Steam
           + stubs: Xbox / EA / Ubisoft / Battle.net / Amazon(Nile)
```

| Layer | Stack |
| --- | --- |
| Shell | WinUI 3 · Windows App SDK · .NET 10 |
| UI | React · TypeScript · WebView2 (AMOLED, Geist) |
| Bridge | JSON-RPC (`WebHostBridge` ↔ `ui/src/lib/host.ts`) |
| Native | C# adapters + install/launch orchestration + deps detect |
| Backends | Legendary, gogdl, official store agents (see [VENDORS](docs/VENDORS.md)) |

Flow:

```
Detect library → Select title → Consent → Install/Update (Exo progress) → Backend minimized → Launch → Optional store-UI cleanup
```

---

## Phase 1 features

| | |
| --- | --- |
| **Library** | Cover mark + title + store dot; filter; refresh |
| **Detail** | Primary **Play \| Install \| Update**, playtime / size / status, honest launch note |
| **Install UI** | Exo progress (percent, speed when known, cancel) — not another launcher window as the main UI |
| **Local** | Portable folder register/copy + direct launch |
| **Epic** | Legendary auth/library/install/update/launch |
| **GOG** | gogdl download/repair/launch; registry library |
| **Riot** | Fixed catalog; official flags; hide UI; cleanup without touching Vanguard |
| **Steam** | Manifest library; minimized install/launch |
| **Deps** | VC++ / DirectX / .NET / WebView2; official installers with consent |
| **Settings** | Close store UI after launch (on), auto-redist (ask), minimize while playing (on), anti-cheat safe (always), AMOLED theme |

---

## Install

**Requirements:** Windows 11 x64, [.NET 10 SDK](https://dotnet.microsoft.com/) (build), WebView2 Runtime, Node.js 20+ (UI build), PowerShell 7 recommended.

Optional backends: [Legendary](https://github.com/derrod/legendary), [gogdl](https://github.com/Heroic-Games-Launcher/heroic-gogdl) on PATH or `%LocalAppData%\ExoLauncher\tools\`.

### Build and run

```powershell
git clone https://github.com/ImAvgErix/ExoLauncher.git
cd ExoLauncher
pwsh -File Run-ExoLauncher.ps1
```

```powershell
cd ui; npm ci; npm run build; cd ..
dotnet build ExoLauncher.sln -c Release -p:Platform=x64
dotnet test ExoLauncher.sln -c Debug -p:Platform=x64 --no-build
pwsh -File Run-ExoLauncher.ps1 -NoBuild
```

### One-liner (after a release exists)

```powershell
irm https://raw.githubusercontent.com/ImAvgErix/ExoLauncher/main/Install-ExoLauncher.ps1 | iex
```

Install script verifies SHA-256 when GitHub provides a digest.

---

## Design notes

- Fixed **1400×900** true-black shell
- White primary pill CTA; hairline borders; quiet status pills
- No left mega-nav, no window-chrome spam, no stats dump, no emoji in chrome
- Same quiet language as Exo / ExoOS

---

## Documentation

| Doc | |
| --- | --- |
| [CHANGELOG](CHANGELOG.md) | Release notes |
| [VENDORS](docs/VENDORS.md) | Legendary / gogdl / Nile pin strategy |
| [AGENTS](AGENTS.md) | Rules for future agents |
| [PRIVACY](PRIVACY.md) | Local-first privacy |
| [SECURITY](SECURITY.md) | Trust boundary + anti-cheat posture |
| [LICENSE](LICENSE) | MIT © 2026 Erix (ImAvgErix) |

---


## Family

| Product | Role |
| --- | --- |
| **[Exo](https://github.com/ImAvgErix/Exo)** | Per-module gaming optimizers |
| **[Exo OS](https://github.com/ImAvgErix/ExoOS)** | Full Windows transform — Balanced or Extreme |
| **[Exocord](https://github.com/ImAvgErix/Exocord)** | Native desktop chat & voice |
| **[Exo Launcher](https://github.com/ImAvgErix/ExoLauncher)** | One library UI (this repo) |

---
## License

MIT © 2026 Erix ([ImAvgErix](https://github.com/ImAvgErix)) — [LICENSE](LICENSE) · [PRIVACY.md](PRIVACY.md) · [SECURITY.md](SECURITY.md)


Legendary and gogdl are separate GPL projects you install yourself; they are not redistributed inside the Exo Launcher binary.
