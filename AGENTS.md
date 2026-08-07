# Exo Launcher — agent and product rules

Same family as **Exo** and **ExoOS**. Same quiet voice. Same honesty bar.

## Product line

> One UI. Other clients become invisible dependencies — not apps you open.

## Hard stops

1. **Do not fake full store replacement.** Riot + Vanguard, EAC, BattlEye, Steam DRM often require their services. Document limits; never claim “delete every other binary and still play VALORANT online.”
2. **Anti-cheat safe always.** No game binary edits, no kernel hacks, no bypass tooling.
3. **Consent before any download / install / elevated action.** Never silent-force redistributables.
4. **No account, no ads, no tray agent, no analytics by default.**
5. **No emoji in UI chrome.** Match Exo / ExoOS language.

## Stack (do not drift)

| Layer | Stack |
| --- | --- |
| Shell | WinUI 3 · Windows App SDK · .NET 10 |
| UI | React · TypeScript · WebView2 |
| Bridge | JSON-RPC (`WebHostBridge` ↔ `ui/src/lib/host.ts`) |
| Native | C# adapters + launch orchestration |
| Scripts | PowerShell only where needed; prefer SHA-256 integrity for shipped scripts |
| Target | Windows 11 x64 |

## UI non-negotiables

- Fixed **1400×900** AMOLED shell
- True black `#000`, quiet charcoal surfaces, Geist
- White primary pill CTA, hairline borders, status pills
- Zen: one primary action, lots of air, no left mega-nav, no stats dump
- Library = cover + title + store dot
- Detail = Play + 3 facts (playtime / size / status) + one honest launch note

## Architecture map

```
ExoLauncher/                 WinUI host + C# services
  Adapters/                  IStoreAdapter implementations
  Services/WebHostBridge.cs  JSON-RPC methods
  wwwroot/                   Built React UI (from ui/)
ui/                          React + TypeScript source
```

### Bridge methods (keep UI strings in sync)

| Method | Purpose |
| --- | --- |
| `library.get` / `library.refresh` | Discover games |
| `game.get` / `game.launch` | Detail + launch |
| `deps.list` / `deps.offerInstall` | Runtime detection + consent install |
| `settings.get` / `settings.set` | User prefs |
| `shell.minimize` / `shell.close` / `shell.openUrl` | Window chrome |
| `stores.matrix` | Agent present matrix |

Every new `WebHostBridge` method needs a matching call site in `ui/src`.

## Store adapter rules

- **Local / DRM-free** — first-class direct exe.
- **Steam** — protocol / silent client; Steam may stay installed.
- **Epic** — Legendary preferred; Epic GUI optional.
- **GOG** — Galaxy or offline builds.
- **Riot** — RiotClientServices minimized → product; optional UI close after exit; **Vanguard stays**.
- **Xbox / EA / Ubisoft / Battle.net** — agent present; Exo is UI only.

## Day-to-day commands

```powershell
pwsh -File Run-ExoLauncher.ps1
# or
cd ui; npm ci; npm run build; cd ..
dotnet build ExoLauncher.sln -c Debug -p:Platform=x64
```

## Voice

Straight, minimal, no hype. Same README tone as Exo. Prefer “not yet” over vaporware.
