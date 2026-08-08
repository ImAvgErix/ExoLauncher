## 1.0.1

**Distinct Exo Launcher brand mark.**

- New app icon and logo: mint launch chevron (separate from Hub).
- Title-bar logo asset; README brand mark; favicon update.

# Changelog

## 1.0.0 - 2026-08-07

Phase 1 public release + polish pass.

### Product
- Fixed 1400×900 AMOLED shell (WinUI 3 + React + WebView2); optional resize
- Library UI: covers (cached + monogram fallback), title, store dot, favorites, sort, recents
- Detail rail: **Play | Install | Update**, secondary actions (folder, uninstall, favorite), install speed
- Install progress in Exo (percent, speed when known, cancel)
- Shared `IStoreAdapter`: auth, library, install, update, launch, uninstall, progress, cleanup
- **Local:** portable register in place (optional copy) + direct launch
- **Epic:** Legendary CLI install/update/launch with stdout progress (GUI optional) + Sign in
- **GOG:** gogdl download/repair/launch; Galaxy optional + Sign in
- **Riot:** fixed product tiles; official RiotClientServices; hide UI; no Vanguard kill
- **Steam:** appmanifest library; playtime best-effort; minimized install/launch
- Stubs: Xbox / EA / Ubisoft / Battle.net / Amazon (honest handoff messages)
- Dependencies panel + consent installers
- Settings: store agents matrix, default install root, portable copy, update check, sort
- Opt-in GitHub release update check
- Docs: honest store matrix, architecture, vendor pin strategy
- Tests: CLI helpers, LocalAdapter fixture, bridge parity

### Fixes
- Cover art virtual host + progressive warm
- Test fixture pollution filtered from Local library
- Version pipeline aligned to 1.0.0
- Phase-2 stubs no longer report false install/launch success

