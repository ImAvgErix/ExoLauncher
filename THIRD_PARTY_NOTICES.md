# Third-party notices

Exo Launcher's own code is proprietary (see [LICENSE](LICENSE)). The installer also carries the components below, each under its own license, and Exo downloads a few tools at run time. Nothing here changes those licenses; where a license needs its full text, the text ships next to the component or is linked.

## Separate programs shipped with Exo

These run as their own programs. Exo talks to them only through messages, and Exo Launcher is not covered by their licenses.

| Program | License | Source |
| --- | --- | --- |
| **Exo Mouse** (the Devices room), built on OpenMouse's protocol library `@openmouse/protocol` 0.42.2 | AGPL-3.0-or-later | [github.com/ImAvgErix/exo-mouse](https://github.com/ImAvgErix/exo-mouse) |
| **Exo Shaders** (Library > Shaders), an unofficial fork of [SCSKiller](https://github.com/BlueHeisenberg/SCSKiller) | GPL-3.0-or-later, with an additional permission under section 7 (`LICENSE-EXCEPTION.txt`) | [github.com/ImAvgErix/exo-shaders](https://github.com/ImAvgErix/exo-shaders). Its own third-party notices ship in `addons\shaders\THIRD-PARTY-NOTICES.md`. Its native helpers come unmodified from SCSKiller 1.3.0 (see `engine/prebuilt/SOURCE.md` in its repository). |
| **Exo Capture** (instant replay, recording and snips; `addons\capture`) | GPL-3.0-or-later | [github.com/ImAvgErix/exo-capture](https://github.com/ImAvgErix/exo-capture) |
| **OBS Studio game capture files** 32.2.2 (`graphics-hook`, `inject-helper`, `get-graphics-offsets`; 32- and 64-bit), OBS Project and contributors, unmodified and signed by OBS Project, LLC, used by Exo Capture only for a game Windows capture gets no picture from | GPL-2.0-or-later | [github.com/obsproject/obs-studio/tree/32.2.2](https://github.com/obsproject/obs-studio/tree/32.2.2) (`plugins/win-capture`) |
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
| libdave (Discord, DAVE end-to-end encryption for calls; `dave.dll`) | 8de72b1 | MIT |
| mlspp (Cisco, linked into `dave.dll`) | as pinned by libdave | BSD-2-Clause |
| OpenSSL 3 (linked into `dave.dll`) | as pinned by libdave | Apache-2.0 |
| Opus (Xiph.Org, `opus.dll`) | 1.5 | BSD-3-Clause |
| DeepFilterNet 3 (Hendrik Schröter, noise suppression in calls; `exo-deepfilter.dll`, Exo's build of its C interface, libDF, with the low-latency model and the Rust crates it links: tract, ndarray and the rest, each MIT or Apache-2.0) | 0.5.6 | MIT or Apache-2.0 |
| webrtc-audio-processing (freedesktop.org's copy of WebRTC's audio processing; its AEC3 echo canceller in calls, built into `exo_apm.dll`), with the code it carries: Ooura's and Mark Olesen's FFTs, pffft, rnnoise and spl_sqrt_floor, each under its own permissive license | 2.1 | BSD-3-Clause |
| Abseil C++ (Google, linked into `exo_apm.dll`) | 20240722.0 | Apache-2.0 |
| React, React DOM | 19.3.0 | MIT |
| clsx | 2.1.1 | MIT |
| qrcode-generator (Kazuhiko Arase), the Discord sign-in code | 2.0.4 | MIT |
| hls.js | 1.7.3 | Apache-2.0 |
| dash.js | 4.7.4 | BSD-3-Clause |
| Inter font (Rasmus Andersson and the Inter Project Authors), the official release's variable fonts: InterVariable.woff2 and InterVariable-Italic.woff2 in the UI (`ui/public/fonts`) and Exo Mouse, InterVariable.ttf in the app | 4.1 | SIL Open Font License 1.1 |
| Tabler Icons (Paweł Kuna), through Iconify's `@iconify-json/tabler`: every icon in the UI, Exo Mouse and setup | 3.48.0 | MIT |
| Simple Icons, the marks of a Discord profile's connected accounts (`ui/src/components/discord/connectionMarks.ts`; Skype, Xbox and Amazon from 8.15.0) | 15.22.0 | CC0 1.0 |
| emojibase-data (Miles Johnson), the Discord room's emoji names, categories and keywords (`ui/src/components/discord/emoji.json`, trimmed from it) | 17.0.0 | MIT |

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

Steam, Valve, Epic Games, GOG, Riot Games, VALORANT, League of Legends, Xbox, Microsoft Store, EA, Ubisoft, Battle.net, Amazon Games, Discord, Spotify, Apple Music, YouTube Music, NVIDIA, DLSS, AMD, FSR, Intel, XeSS, Finalmouse and the other names, logos and game art shown in Exo or in these documents belong to their owners. Exo Launcher is an independent product and is not affiliated with, endorsed by or sponsored by any of them. Game covers, trailers and store data are shown as the stores and services provide them.
