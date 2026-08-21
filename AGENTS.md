# Exo Launcher — agent and product rules

Same family as **Exo** and **Exo OS**. Same quiet voice. Same honesty bar.

Products: **Exo**, **Exo OS**, **Exo Launcher**, **Exo Control**. Do not invent others.

## Product line

> One UI. Other clients become invisible dependencies — not apps you open.
> Buy in a browser if you must. Install, update, and launch only in Exo.

## Hard stops

1. **Do not fake full store replacement.** Riot/Vanguard, EAC, BattlEye, Steam DRM often require their services.
2. **Anti-cheat safe always.** No game binary edits, no kernel hacks, no bypass tooling. Never kill `vgk` / `vgc` / Vanguard / EAC / BattlEye.
3. **Consent before any download / install / elevated action.** Never silent-force redistributables.
4. **Online accounts are optional.** Signed-out and offline library, install, update, launch, and local settings stay complete. No ads, tray agent, or analytics.
5. **No emoji in UI chrome.** Match Exo / Exo OS language.
6. **Prefer Legendary / gogdl / official agents** over re-implementing CDN/DRM protocols.
7. **Steam install/update/uninstall** goes through `ExoLauncher.SteamIpc` (`steamclient64.dll` IPC). Not OCR, not SendInput, not DepotDownloader, not nesting Steam under Exo.
8. **Do not ship dead RADS/LeagueDownloader CDN scrapers** as the main Riot path.

## Two copies of the app

| Copy | Path | When to use |
| --- | --- | --- |
| **Local debug** | `ExoLauncher\bin\x64\Debug\…\ExoLauncher.exe` via `pwsh -File Run-ExoLauncher.ps1` | Default for agent work in this repo. |
| **Shipped install** | `%LOCALAPPDATA%\ExoLauncher\app\ExoLauncher.exe` | Only when the human is testing the install. Do not write here unless asked to ship. |

Do not stop at repo tests for a UI/host ship. For local-debug audits, do not touch the shipped install.

## Stack (do not drift)

| Layer | Stack |
| --- | --- |
| Shell | WinUI 3 · Windows App SDK · .NET 10 · WebView2 host |
| UI | React 19 + Tailwind 4 + React Bits Pro (Vite) in `ui/` |
| Host | `WebHostBridge` JSON-RPC (`ui/src/lib/host.ts`) |
| Native | C# adapters + orchestrator |
| Target | Windows 11 x64 |
| Remote | `ImAvgErix/ExoLauncher` |

## UI non-negotiables

- Default **1400×900** AMOLED shell (resizable, maximizable, minimum 1100×700)
- Primary action is **Play | Install | Update** (one pill)
- Install progress lives in Exo (percent, speed if known, cancel)
- Library = cover + title + store name; details open as an overlay, not a separate route
- Tile click does not steal Now (`retainNow`)
- Remove is two clicks (`Confirm remove`)
- Do not Play VALORANT/League from an agent session. Do not Newest on anti-cheat titles. Do not uninstall real library games.

## Day-to-day

```powershell
dotnet test ExoLauncher.sln -c Debug -p:Platform=x64
pwsh -File Run-ExoLauncher.ps1
```

Hands: **Cua Driver** for the running Exo window (`cua-driver call`, or Cursor MCP `cua-driver`). Websites: **browser-use** on Helium, not CUA. Never kill anti-cheat processes. Point UI review at `ui/src/`, not leftover `ExoLauncher/Controls/` XAML.

## Voice

Straight, minimal, no hype. Prefer “not yet” over vaporware.

## Agent skills

Skills and MCPs live on this machine (`~/.cursor/skills`, `~/.agents/skills`, Cursor MCP). Load a skill’s `SKILL.md` when the job matches. Do not copy skill bodies into this repo.

### Issue tracker

GitHub Issues on `ImAvgErix/ExoLauncher` via `gh`. See `docs/agents/issue-tracker.md`.

### Triage labels

Canonical roles, same strings: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: `CONTEXT.md` + `docs/adr/`. See `docs/agents/domain.md`.

### Online identity and social

For exo-id, public profiles, friends, media, or presence work, read [ADR-0005](docs/adr/0005-online-profiles-presence.md) and [the service contract](services/exo-id/CONTRACT.md). Keep bearer/store credentials and filesystem paths inside native code; React receives bounded DTOs and browser-safe media URLs. Treat unavailable presence as **unknown**, not offline. A missing provider capability stays unavailable; never fabricate credentials, ids, or results.

### Hands and review (this machine)

- **Cua Driver** — live Windows observe/click on the local debug build. Websites go through browser-use.
- **GitHub** — issues, PRs, labels (`gh` / GitHub MCP).
- **Engineering skills** — triage, tickets, domain, architecture, grilling (read `docs/agents/` first).
- **UI/code review** — unslop, security/performance audits, code review. Point them at `ExoLauncher/` XAML + C#, not the shipped install.
