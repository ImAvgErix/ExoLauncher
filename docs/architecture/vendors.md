# Vendor backends (developer notes)

How Exo drives each store's tool or client: commands, progress parsing, the GOG owned-games fallback and the Steam helper's internals. Private to this repo. The public, user-level summary is `docs/VENDORS.md`, which `tools/Publish-Docs.ps1` copies to the public repo; keep internals here, not there.

Exo Launcher prefers shelling out to mature open tools over re-implementing store protocols.

## Strategy

| Backend | Store | License | Pin approach |
| --- | --- | --- | --- |
| [Legendary](https://github.com/derrod/legendary) | Epic | GPL-3.0 | Exo downloads a fixed release from GitHub and checks its size and SHA-256 |
| [heroic-gogdl](https://github.com/Heroic-Games-Launcher/heroic-gogdl) | GOG | GPL-3.0 | Same |
| [Nile](https://github.com/imLinguin/nile) | Amazon Games | GPL-3.0 | Same |
| Official clients | Steam, Epic Games, GOG Galaxy, Riot, Xbox, EA app, Ubisoft Connect, Battle.net, Amazon Games, Rockstar Games Launcher | Proprietary | Detected from exact executables and registration; never redistributed by Exo |

Exo does not bundle these GPL tools into its installer: it downloads them when a feature first needs them and runs each as its own program. A copy you put on PATH or in `%LocalAppData%\ExoLauncher\tools\` is used instead. Adapters say so plainly when a backend is missing.

Steam, Epic, GOG, and Riot have wired game-library actions. Xbox, EA app,
Ubisoft Connect, Battle.net, Amazon Games, and Rockstar Games Launcher list
only proven on-disk installs and launch those titles; Exo does not invent
account state, ownership, or install/update through those clients.

## Legendary (Epic)

```text
legendary auth
legendary list --json --include-non-installable
legendary list-installed --json
legendary install <AppName> -y --base-path <path>
legendary install <AppName> -y --update-only
legendary launch <AppName>
legendary uninstall <AppName> -y
```

Progress is parsed from DLManager stdout (`Progress: N%`, and the raw speed on the `+ Download` line; the `+ Disk` line is write speed and is ignored). gogdl and Nile print the same lines without the `%`. `Adapters/Cli/CliProgress.cs` reads all three; tool lines never reach the UI, which gets Exo's own status text.

## gogdl (GOG)

```text
gogdl auth
gogdl download <id> --platform windows --path <root>   # writes <root>\<GameFolder>
gogdl update <id> --platform windows --path <game folder>
gogdl repair <id> --platform windows --path <game folder>   # verifies the installed build only
gogdl launch <game folder> <id> --platform windows
```

`download` appends the game's own folder name to `--path`; `update`, `repair` and `launch` take the folder that holds `goggame-<id>.info`. Exo downloads into a staging folder and promotes that game folder to `GOG\<id>`. `repair` never moves to a newer build, so Update runs `update`.

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
