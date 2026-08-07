# Changelog

## 0.1.0 — 2026-08-07

Phase 1 public release.

- Fixed 1400×900 AMOLED shell (WinUI 3 + React + WebView2)
- Library UI: covers, title, store dot; detail with **Play | Install | Update** + three facts + launch note
- Install progress view in Exo (percent, speed when known, cancel)
- Shared `IStoreAdapter`: auth, library, install, update, launch, uninstall, progress, cleanup
- **Local:** portable folder install + direct launch
- **Epic:** Legendary CLI install/update/launch with stdout progress parsing (GUI optional)
- **GOG:** gogdl download/repair/launch; Galaxy optional
- **Riot:** fixed product tiles; official RiotClientServices flags; hide UI; cleanup without Vanguard
- **Steam:** appmanifest library; minimized `steam://install` / `steam://rungameid`
- Stubs: Xbox / EA / Ubisoft / Battle.net / Amazon (Nile)
- Dependencies panel + consent installers
- Settings: close store UI, ask-first redist, minimize while playing, anti-cheat always, AMOLED
- Docs: honest store matrix, architecture, vendor pin strategy
- Tests: CLI helpers, LocalAdapter fixture, bridge parity
