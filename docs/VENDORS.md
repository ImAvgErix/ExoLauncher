# Vendor backends

Exo Launcher prefers shelling out to mature open tools over re-implementing store protocols.

## Strategy

| Backend | Store | License | Pin approach |
| --- | --- | --- | --- |
| [Legendary](https://github.com/derrod/legendary) | Epic | GPL-3.0 | User-installed CLI on PATH, or drop `legendary.exe` into `%LocalAppData%\ExoLauncher\tools\` or `ExoLauncher/tools/` |
| [heroic-gogdl](https://github.com/Heroic-Games-Launcher/heroic-gogdl) | GOG | GPL-3.0 | Same: PATH or `tools/gogdl.exe` |
| Official clients | Steam, Epic Games, GOG Galaxy, Riot, Xbox, EA app, Ubisoft Connect, Battle.net, Amazon Games, Rockstar Games Launcher | Proprietary | Detected from exact executables/registration; never redistributed by Exo |

Exo does **not** vendor GPL binaries into the MIT app binary. Users install backends themselves. Adapters fail honestly when a backend is missing.

Steam, Epic, GOG, and Riot have wired game-library actions. Xbox, EA app,
Ubisoft Connect, Battle.net, Amazon Games, and Rockstar Games Launcher are
presence/Open-only integrations in 1.0.24: Exo does not invent their libraries,
account state, ownership, install state, achievements, or per-game actions.

## Legendary (Epic)

```text
legendary auth
legendary list-installed --json
legendary install <AppName> -y --base-path <path>
legendary install <AppName> -y --update-only
legendary launch <AppName>
legendary uninstall <AppName> -y
```

Progress is parsed from DLManager stdout (`Progress: N%`, MiB/s lines).

## gogdl (GOG)

```text
gogdl auth
gogdl download <id> --platform windows --path <path>
gogdl repair <id> --platform windows --path <path>
gogdl launch --path <path> <id>
```

Galaxy is optional for the happy path.

gogdl has **no stable “list owned” CLI**. Owned-but-not-installed titles come from:

1. GOG registry (installed)
2. Library JSON caches (Heroic `gog_store/library.json`, or `%LocalAppData%\ExoLauncher\gog-owned.json`)

Drop a products array into `gog-owned.json` after auth if you want Exo to show not-yet-installed owned titles.

## Riot

Official flags only (no CDN scrapers):

```text
RiotClientServices.exe --launch-product=<id> --launch-patchline=live
RiotClientServices.exe --uninstall-product=<id> --uninstall-patchline=live
RiotClientInstall.exe --skip-to-install
```

Vanguard (`vgk` / `vgc`) is never force-closed.

## Steam

```text
steam://install/<appid>
steam://rungameid/<appid>
```

Library from `appmanifest_*.acf`. Steam runtime usually remains installed.
