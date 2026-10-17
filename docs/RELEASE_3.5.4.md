# Exo Launcher 3.5.4

_October 17, 2026_

This update counts hours for Minecraft and Roblox, keeps Fortnite's hours moving, finds more of your owned Steam and Epic games, and lets Update all in Upscalers include anti-cheat games at your risk.

### Library
- **Every owned Steam game shows up.** On a Steam install that has seen many store pages, Exo asked Steam about only the first 20,000 apps it knew of and gave up after four seconds, so owned games past that point never reached "owned, not installed". Exo now asks about every app in batches, keeps the answers it got if Steam slows down, and keeps games it verified before until Steam answers for them. With a Steam Web API key, an owned app Steam has no details for on this PC is shown once the Steam store says it is a game. _Checked in code only._
- **Games that share a name are both kept.** Two games with the same title in one store, like Prey (2006) and Prey (2017), no longer collapse into one. _Checked in code only._
- **Every owned Epic game shows up.** Exo now also lists Epic games another store delivers, such as ones activated on the EA app; Install opens Epic Games Launcher, which sends you there. Deluxe and other editions are no longer dropped as add-ons, and a large Epic library has five minutes to list on the first run instead of 90 seconds. _Checked in code only._
- **Hours for Minecraft and Roblox.** Exo now sees Minecraft (Java and Bedrock) and Roblox while they run, even when you start them outside Exo, and counts their hours. Roblox's earlier sessions come from its own logs and Minecraft Java's from each singleplayer world's stats, once. Roblox _Verified: on screen._ Minecraft _Checked in code only._
- **Fortnite's hours keep going up.** For Epic games Exo dropped its own session time whenever Epic reported any hours, so hours froze when Epic's figure did. Exo now adds the time it saw since Epic's last figure, without counting anything twice. _Checked in code only._
- **Hours no longer creep up on their own.** A background refresh could add Exo's minutes again to totals that already held them, for Riot and other non-Steam games. _Checked in code only._
- **Real covers for games no store sells.** osu! and other local games looked up their art on Steam by title, found nothing (or a different game) and fell back to their icon. With a SteamGridDB key saved, Exo now finds them on SteamGridDB by their exact title. _Checked in code only._
- **Update all can include anti-cheat games.** In Upscalers, with anti-cheat games waiting, Update all first names each game and its anti-cheat and says the ban risk plainly, then updates them too at your risk, or only the others. Riot games are never changed. _Checked in code only._

### Devices
- **Exo's own dropdowns.** Every dropdown in Devices opens Exo's menu instead of Windows' list. _Checked in code only._
- **Each fact once.** Under the mouse: battery, signal, a fixed polling rate and firmware; sensitivity and a polling rate you can change stay in their panels only. _Verified: on screen._

### Updates
- **Updates are not blocked by programs Exo started.** A store launcher Exo opened kept Exo's folder in use after Exo closed, which could stop the next update from swapping it. _Checked in code only._

[Download ExoLauncher-Setup.exe](https://github.com/ImAvgErix/ExoLauncher/releases/download/v3.5.4/ExoLauncher-Setup.exe) · [Full changelog](https://github.com/ImAvgErix/ExoLauncher/blob/main/CHANGELOG.md) · [Privacy policy](https://github.com/ImAvgErix/ExoLauncher/blob/main/PRIVACY.md)
