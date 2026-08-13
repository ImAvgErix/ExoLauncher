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
Ubisoft Connect, Battle.net, Amazon Games, and Rockstar Games Launcher list
only proven on-disk installs and launch those titles; Exo does not invent
account state, ownership, or install/update through those clients.

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

Exo commands the already-running official Steam client through a small helper
(`ExoLauncher.SteamIpc.exe` → `steamclient64.dll` IClientAppManager). Steam
stays on disk where Valve installed it. Exo does not copy the Steam tree, does
not click Steam chrome, and does not use DepotDownloader.

```text
install / update / uninstall <appid>   via the live Steam client
steam://rungameid/<appid>              launch
```

Progress is Steam’s live download job: `appmanifest_*.acf` when those counters
are moving, otherwise the job totals from `content_log` plus bytes in
`steamapps/downloading/<appid>`. A leftover full ACF or a one-shot
`download 0/total` line is not shown as 0% or 100%. The helper identifies
`IClientAppManager` by RTTI on this Steam build (`GetIClientAppManager` is
engine slot 43; `InstallApp` takes app id, folder index, and a legacy flag).

## Other stores

Epic, GOG, and Riot already command official agents (Legendary, gogdl,
RiotClientServices). Xbox, EA app, Ubisoft Connect, Battle.net, Amazon Games,
and Rockstar Games Launcher stay list + launch until an official agent exists.
