# Exo Launcher — agent and product rules

Same family as **Exo** and **ExoOS**. Same quiet voice. Same honesty bar.

## Product line

> One UI. Other clients become invisible dependencies — not apps you open.  
> Buy in a browser if you must. Install, update, and launch only in Exo.

## Hard stops

1. **Do not fake full store replacement.** Riot/Vanguard, EAC, BattlEye, Steam DRM often require their services.
2. **Anti-cheat safe always.** No game binary edits, no kernel hacks, no bypass tooling. Never kill `vgk` / `vgc`.
3. **Consent before any download / install / elevated action.** Never silent-force redistributables.
4. **No account, no ads, no tray agent, no analytics by default.**
5. **No emoji in UI chrome.** Match Exo / ExoOS language.
6. **Prefer Legendary / gogdl / Nile / official agents** over re-implementing CDN/DRM protocols.
7. **Do not ship dead RADS/LeagueDownloader CDN scrapers** as the main Riot path.

## Stack (do not drift)

| Layer | Stack |
| --- | --- |
| Shell | WinUI 3 · Windows App SDK · .NET 10 |
| UI | React · TypeScript · WebView2 |
| Bridge | JSON-RPC (`WebHostBridge` ↔ `ui/src/lib/host.ts`) |
| Native | C# adapters + orchestrator |
| Target | Windows 11 x64 |

## UI non-negotiables

- Fixed **1400×900** AMOLED shell
- Primary action is **Play | Install | Update** (one pill)
- Install progress lives in Exo (percent, speed if known, cancel)
- Library = cover + title + store dot; detail = CTA + 3 facts + launch note

## Bridge methods (keep UI strings in sync)

`library.get`, `library.refresh`, `game.get`, `game.launch`, `game.install`, `game.update`, `game.cancelInstall`, `game.progress`, `deps.list`, `deps.offerInstall`, `stores.matrix`, `settings.get`, `settings.set`, `shell.minimize`, `shell.close`, `shell.openUrl`, `app.version`

## Day-to-day

```powershell
pwsh -File Run-ExoLauncher.ps1
dotnet test ExoLauncher.sln -c Debug -p:Platform=x64
```

## Voice

Straight, minimal, no hype. Prefer “not yet” over vaporware.
