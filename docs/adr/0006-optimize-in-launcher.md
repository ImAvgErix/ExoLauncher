# ADR-0006: Optimize lives in Launcher

## Status

Superseded / retired — 2026-09-01

Optimize is not a live product. The room and `optimize.*` RPCs are gone.
Do not revive them. Upscaler swap and mouse profiles are the remaining tune
surfaces. This file is historical.

## Context

Exo Hub was a separate WinUI optimizer (NVIDIA, AMD, Windows, Internet, Steam, Discord, Spotify, Brave) with Detect → Snapshot → Apply → Verify and an elevated script helper. Exo Launcher is the daily library. Users were asked to install two AMOLED apps for one PC.

The all-in-one product is one process and one window. Extreme Windows strip (Exo OS) stays a different trust model and must not share Apply with G-SYNC.

## Decision (historical)

Fold Hub **engine + scripts + elevation helper** into Exo Launcher as an **Optimize** room.

- JSON-RPC is namespaced `optimize.*`. Hub’s `module.*` names are not added at the Launcher root.
- `ExoLauncher.exe` stays `asInvoker`. Apply/Repair use Hub’s SHA-256 closed PowerShell elevation (`runas`), not a second helper exe and not this window.
- Detect and status never prompt UAC.
- `Exo.NvDisplay` is built as a small FDD helper next to the NVIDIA kit. NVIDIA driver download/install/sweep stays out of this ADR.
- Optimizer kit state stays under `%LocalAppData%\Exo` because the shipped scripts already write there. Launcher settings stay under `%LocalAppData%\ExoLauncher`.
- Steam overlay / Discord lean must not restamp machine-wide HAGS / Game Mode / power. Those stay the Windows module.
- Anti-cheat processes are never killed.
- Signed-out library, install, update, and Play stay complete. Optimize does not gate them.
- Exo OS is opened with `optimize.openOs` if installed; otherwise the user is sent to its download. Playbooks are not imported.
- Chat and voice are not this ADR.

`optimize.detect|apply|repair|cancel|verifyAll|openOs` were wired to the ported Hub engine. That wiring is retired.

## Consequences

- One Start-menu app for play. Tune is upscaler swap plus mouse profiles, not an Optimize room.
- Hub’s `%LocalAppData%\Exo` install is legacy; do not write there from Launcher.
- Do not copy Hub `exocord/` inject leftovers into this repo.
- Do not revive `optimize.*` RPCs or the script-elevation helper.
