# Changelog

## 0.1.0 — 2026-08-07

Phase 1 initial public release.

- Fixed 1400×900 AMOLED shell (WinUI 3 + React + WebView2)
- Library UI: covers, title, store dot; detail with Play + three facts + launch note
- Game model: id, title, store, installed, path, playtime, size, deps, launchNote
- Store adapters: Local, Steam, Epic, GOG, Riot (real discovery/launch shapes); Xbox / EA / Ubisoft / Battle.net (agent-present stubs)
- Launch orchestration: backend minimized when needed, no anti-cheat bypass
- Dependencies panel: VC++ / DirectX / .NET / WebView2 detect + official installer links with consent
- Settings: close store clients after launch, auto-install redist (ask path), minimize while playing, anti-cheat safe (always on)
- Honest README store matrix — no “delete every binary and still play VALORANT online” fiction
