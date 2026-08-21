# ADR-0002: React WebView2 shell

## Status

Accepted — 2026-08-17

## Context

The 1.0.80 React shell (AMOLED, Geist, cover tiles, Now banner, white Play pill, hairline) was cleaner than the native WinUI 3 port. Store backends (Steam IPC, Legendary, gogdl, Riot) stay in C#.

## Decision

The product UI is React 19 + Tailwind 4 in WebView2, built from `ui/` into `ExoLauncher/wwwroot`. React Bits Pro supplies motion: silk + frame + depth + staggered title on Now, a short boot preloader, empty-library frame, and fade lists in Settings. Do not put WebGL or 3D tilt on every library tile.

`WebHostBridge` is the JSON-RPC host. Steam install/update/uninstall still goes through `ExoLauncher.SteamIpc`. WebView2 also remains for GOG login.

## Consequences

- Ship with `npm run build` (via `BuildWebUi`) + `dotnet publish`.
- Do not drop marketing heroes or unmodified App UI dashboards onto the library. Harmonize any block to Exo tokens first.
- License key stays in gitignored `ui/.env.local`. Never commit it.
