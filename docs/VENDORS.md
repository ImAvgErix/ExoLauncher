# Vendor backends

Exo Launcher prefers shelling out to mature open tools over re-implementing store protocols.

## Strategy

| Backend | Store | License | Pin approach |
| --- | --- | --- | --- |
| [Legendary](https://github.com/derrod/legendary) | Epic | GPL-3.0 | User-installed CLI on PATH, or drop `legendary.exe` into `%LocalAppData%\ExoLauncher\tools\` or `ExoLauncher/tools/` |
| [heroic-gogdl](https://github.com/Heroic-Games-Launcher/heroic-gogdl) | GOG | GPL-3.0 | Same: PATH or `tools/gogdl.exe` |
| [Nile](https://github.com/imLinguin/nile) | Amazon | GPL-3.0 | Optional phase-2; PATH or Amazon Games app fallback |
| Official clients | Steam, Riot, Xbox, EA, Ubisoft, Battle.net | Proprietary | Detected on disk; started minimized; never redistributed by Exo |

Exo does **not** vendor GPL binaries into the MIT app binary. Users install backends themselves. Adapters fail honestly when a backend is missing.

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
