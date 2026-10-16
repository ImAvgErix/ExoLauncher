# Third-party notices

Exo Launcher's own code is proprietary (see [LICENSE](LICENSE)). The installer also carries the components below, each under its own license, and Exo downloads a few tools at run time. Nothing here changes those licenses; where a license needs its full text, the text ships next to the component or is linked.

## Separate programs shipped with Exo

These run as their own programs. Exo talks to them only through messages, and Exo Launcher is not covered by their licenses.

| Program | License | Source |
| --- | --- | --- |
| **Exo Mouse** (the Devices room), built on OpenMouse's protocol library `@openmouse/protocol` 0.42.2 | AGPL-3.0-or-later | [github.com/ImAvgErix/exo-mouse](https://github.com/ImAvgErix/exo-mouse) |
| **Exo Shaders** (Library > Shaders), an unofficial fork of [SCSKiller](https://github.com/BlueHeisenberg/SCSKiller) | GPL-3.0-or-later, with an additional permission under section 7 (`LICENSE-EXCEPTION.txt`) | [github.com/ImAvgErix/exo-shaders](https://github.com/ImAvgErix/exo-shaders). Its own third-party notices ship in `addons\shaders\THIRD-PARTY-NOTICES.md`. Its native helpers come unmodified from SCSKiller 1.3.0-beta.2 (see `engine/prebuilt/SOURCE.md` in its repository). |
| **uBlock Origin** 1.74.0, Raymond Hill and contributors, unmodified, loaded into the embedded music and sign-in browser | GPL-3.0 (full text in the extension's `LICENSE.txt`) | [github.com/gorhill/uBlock/tree/1.74.0](https://github.com/gorhill/uBlock/tree/1.74.0) |

The source for every build of these programs that Exo ships is published at the links above, at the version in that release.

## Libraries and assets inside Exo

| Component | Version | License |
| --- | --- | --- |
| .NET runtime (self-contained) | 10 / 11 | MIT |
| Windows App SDK (WinUI) | 2.x | Microsoft Software License Terms |
| Microsoft Edge WebView2 SDK | 1.0.4191.47 | BSD-3-Clause |
| Microsoft.Data.Sqlite | 10.0.12 | MIT |
| SQLitePCLRaw | 3.0.5 | Apache-2.0 |
| SQLite | bundled with SQLitePCLRaw | Public domain |
| React, React DOM | 19.3.0 | MIT |
| clsx | 2.1.1 | MIT |
| hls.js | 1.7.3 | Apache-2.0 |
| dash.js | 4.7.4 | BSD-3-Clause |
| Hugeicons free icons and React bindings | 4.3.4, 1.1.10 | MIT |
| Geist font (Vercel), via Fontsource | 5.3.0 | SIL Open Font License 1.1 |

The WebView2 Runtime itself is Microsoft's, installed and updated by Windows.

## Downloaded when a feature needs it

Exo does not ship these; it downloads a fixed release, checks its size and SHA-256, and runs it as its own program.

| Tool | Used for | License |
| --- | --- | --- |
| [Legendary](https://github.com/derrod/legendary) | Epic Games library, installs and launches | GPL-3.0 |
| [gogdl](https://github.com/Heroic-Games-Launcher/heroic-gogdl) | GOG library, installs and launches | GPL-3.0 |
| [Nile](https://github.com/imLinguin/nile) | Amazon Games library, installs and launches | GPL-3.0 |
| [Equicord](https://github.com/Equicord/Equicord) | Discord client modification, loaded by Discord | GPL-3.0 |
| NVIDIA DLSS, AMD FidelityFX / FSR, Intel XeSS files | Upscaler updates, only for files a game already ships | Each vendor's SDK license |

Upscaler builds that NVIDIA, AMD and Intel don't publish on GitHub come from [DLSS Swapper](https://github.com/beeradmoore/dlss-swapper)'s public archive; Exo uses its release list and download archive only, not its code.

## Trademarks

Steam, Valve, Epic Games, GOG, Riot Games, VALORANT, League of Legends, Xbox, Microsoft Store, EA, Ubisoft, Battle.net, Amazon Games, Discord, Spotify, Apple Music, YouTube Music, Cider, NVIDIA, DLSS, AMD, FSR, Intel, XeSS, Finalmouse and the other names, logos and game art shown in Exo or in these documents belong to their owners. Exo Launcher is an independent product and is not affiliated with, endorsed by or sponsored by any of them. Game covers, trailers and store data are shown as the stores and services provide them.
