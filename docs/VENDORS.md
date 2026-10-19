# Game stores and the tools Exo uses

Exo Launcher doesn't replace any store's downloads, sign-in or copy protection. For each store it uses the store's own client or a well-known open-source tool, and those keep doing the store's job.

| Store | What Exo uses | License | How Exo gets it |
| --- | --- | --- | --- |
| Steam | The official Steam client, through a small Exo helper | Proprietary (Valve) | Already on your PC; Exo never copies or redistributes it |
| Epic Games | [Legendary](https://github.com/derrod/legendary) | GPL-3.0 | Downloaded the first time a feature needs it, at a fixed version checked by size and SHA-256 |
| GOG | [heroic-gogdl](https://github.com/Heroic-Games-Launcher/heroic-gogdl) | GPL-3.0 | Same as Legendary |
| Amazon Games | [Nile](https://github.com/imLinguin/nile) | GPL-3.0 | Same as Legendary |
| Riot | The official Riot Client | Proprietary (Riot Games) | Already on your PC |
| Xbox, EA app, Ubisoft Connect, Battle.net, Rockstar Games Launcher, itch.io | The store's own client, where the game needs one | Proprietary | Already on your PC |

Legendary, gogdl and Nile aren't bundled in Exo's installer. Each runs as its own program, and a copy you put on PATH or in `%LocalAppData%\ExoLauncher\tools\` is used instead of the download. If a tool is missing, Exo says so plainly.

## What Exo does

- **Steam:** lists your owned and installed games and installs, updates, removes and launches them through the Steam client you're signed in to. Download progress is Steam's own.
- **Epic, GOG and Amazon:** lists your library and installs, updates and launches games through Legendary, gogdl and Nile. You sign in on the store's own page.
- **Riot:** finds Riot games installed anywhere, shows when Riot needs to update them, and launches them with the Riot Client's own commands.
- **Xbox, EA app, Ubisoft Connect, Battle.net, Rockstar and others:** finds the games installed on your PC and launches them.

## What Exo doesn't do

- It doesn't download games from store servers itself, use unofficial downloaders, or click through a store's window for you.
- It doesn't invent ownership, account state, installs or updates for stores that don't offer them to other apps. For those, Exo shows only what's installed on your PC.
- It never modifies Riot games and never closes anti-cheat such as Riot Vanguard.
- It doesn't edit game files, except upscaler files you choose to update in System > Graphics > Upscalers, with a restore.
