# Exo Launcher

**Built quiet. Tuned sharp.**

One calm **Windows game library**. Other store clients become **invisible backends** — not apps you open every day.

You should not open Steam / Epic / Riot / GOG / Xbox / EA / Ubisoft / Battle.net as your daily UI. Those clients may still exist on disk. **Exo Launcher** is the surface you use.

> Full “delete every other binary and still play VALORANT online” is **impossible**. This project does not pretend otherwise.

[![License](https://img.shields.io/github/license/ImAvgErix/Exo-Launcher?style=flat-square)](LICENSE)
[![Platform](https://img.shields.io/badge/Windows%2011-x64-0078d4?style=flat-square)](https://github.com/ImAvgErix/Exo-Launcher)
[![.NET](https://img.shields.io/badge/.NET-10-512bd4?style=flat-square)](https://dotnet.microsoft.com/)

<p align="center">
  <a href="CHANGELOG.md">Changelog</a>
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

Same family as **Exo** and **Exo OS** — AMOLED shell, quiet language, honest limits.

---

## Store matrix (honest)

| Store | Day-to-day UI | Backend still needed? | Phase 1 |
| --- | --- | --- | --- |
| **Local / DRM-free** | Exo Launcher | No | Discover + direct launch |
| **Steam** | Exo Launcher | **Yes** — Steam for DRM | Library + `steam://run` |
| **Epic** | Exo Launcher | Legendary preferred | Manifest + launch |
| **GOG** | Exo Launcher | Optional offline | Registry + exe / Galaxy |
| **Riot** | Exo Launcher | **Yes** — Riot Client; **Vanguard for VALORANT** | Minimized client path |
| **Xbox · EA · Ubisoft · Battle.net** | Exo Launcher | **Yes** — their agents | Agent-present stubs |

**Hard constraints:** Vanguard, EAC, BattlEye, Steam DRM require their services. No bypass. No game binary edits. Consent before downloads or elevation.

---

## Phase 1

| | |
| --- | --- |
| **Library** | Cover + title + store dot |
| **Detail** | Play · playtime / size / status · one honest launch note |
| **Launch** | Detect → deps → backend minimized → game → optional store UI cleanup |
| **Dependencies** | VC++ / DirectX / .NET detect · official installers with consent |
| **Settings** | Close store UI after launch · ask before redistributables · minimize while playing · anti-cheat safe always |

---

## Install

**Needs:** Windows 11 x64 · .NET 10 SDK (build) · WebView2

```powershell
git clone https://github.com/ImAvgErix/Exo-Launcher.git
cd Exo-Launcher
pwsh -File Run-ExoLauncher.ps1
```

---

## Architecture

| Layer | Stack |
| --- | --- |
| Shell | WinUI 3 · Windows App SDK · .NET 10 |
| UI | React · TypeScript · WebView2 · Geist · AMOLED |
| Bridge | JSON-RPC host ↔ UI |
| Native | C# store adapters + launch orchestration |

---

## Family

| Product | Role |
| --- | --- |
| **[Exo](https://github.com/ImAvgErix/Exo)** | Per-module gaming optimizers |
| **[Exo OS](https://github.com/ImAvgErix/ExoOS)** | Full Windows transform — Balanced or Extreme |
| **[Exocord](https://github.com/ImAvgErix/exocord)** | Native desktop chat & voice |
| **[Exo Launcher](https://github.com/ImAvgErix/Exo-Launcher)** | Game library UI (this repo) |

---

## License

MIT © 2026 Erix ([ImAvgErix](https://github.com/ImAvgErix)) — see [LICENSE](LICENSE)

<p align="center"><sub>Built quiet. Tuned sharp.</sub></p>
