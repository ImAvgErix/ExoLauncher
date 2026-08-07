# Exo Launcher security model

## Trust boundary

- The desktop process runs as the signed-in user (`asInvoker`).
- Store backends (Steam, Riot Client, etc.) remain their vendors’ processes.
- Dependency installs open the vendor’s official page — Exo Launcher does not silent-force system changes.

## Anti-cheat safe (always)

- No game binary edits.
- No kernel hacks.
- No “bypass” for Vanguard, EAC, BattlEye, or Steam DRM.
- Cleanup after launch only soft-closes store **UI** processes — never anti-cheat services (`vgk`, `vgc`, EasyAntiCheat, BattlEye).

## Process hardening

- DLL search is tightened before WinUI starts.
- WebView2 DevTools are disabled in Release builds.
- Content is served from a virtual host mapping over shipped `wwwroot` assets.

## Reporting

Open a GitHub issue on [ImAvgErix/exo-launcher](https://github.com/ImAvgErix/exo-launcher) for security-relevant bugs. Do not file “bypass” requests — they will be closed.
