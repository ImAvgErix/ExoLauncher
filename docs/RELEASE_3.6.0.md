# Exo Launcher 3.6.0

_October 17, 2026_

This update adds Exo Capture for instant replay, recording and snips, your game's NVIDIA graphics settings, more than one account per store, and hours only from where they are really kept.

### Capture
- **Exo Capture.** The Capture button in the top bar saves an instant replay, starts or stops a recording, or takes a snip: drag a rectangle, then copy or save it from the card that appears. Hotkeys do the same. Instant replay runs only while a game is running, by default, and holds the last minutes in memory until you save. _Verified: on screen._
- **Captures sheet.** Every replay, recording and snip, by game, with a player, trim, favourites, Send to Discord (copies the file for you to paste), and a storage limit that never deletes favourites. Trim _Checked in code only._ The rest _Verified: on screen._
- **Settings > Capture**, beside Graphics: frame rate (60 fps, or 120 where your screen runs that fast), quality, length of the replay, which sounds to keep (game, Discord voice, microphone, each on its own track), the folder and hotkeys. Video is encoded on the graphics card (HEVC, or AV1 where the card has it; HDR games stay HDR). HDR and AV1 _Checked in code only._
- **Replay follows the game however you start it.** A game started from Steam, a desktop shortcut or anywhere else turns replay on, not only games started from Exo. _Verified: on screen._
- **No outline in your clips.** Exo asks Windows to capture without the yellow border, where Windows allows it. _Checked in code only._
- **Light on games.** Exo Capture reads Windows' capture frames directly instead of copying each one, takes no more frames than the clip keeps, and gives the game's frames first claim on the graphics card; the game keeps its own fast path to the screen. In Hell is Us at 1080p on an RTX 3070, with the card at full load, replay at 60 fps (the default) cost about 3% of average frame rate (213 to 207), and at 120 fps about 5%, down from 7%; Exo Capture used under 1% of the processor and about 150 MB of memory. _Verified: end-to-end._
- **Games Windows cannot capture.** A game in exclusive full screen gives Windows capture no picture; after five seconds without one, Exo Capture takes the frames from inside the game with OBS Studio's own signed game capture hook, injected the way OBS's anti-cheat compatible mode does. Never for Riot games, while FACEIT or ESEA runs, or when another app like OBS already captures that game. The hook itself _Verified: end-to-end._ The switch to it _Checked in code only._

### Graphics
- **Your game's NVIDIA profile on its page.** A game's page shows the settings the NVIDIA driver holds for it (low latency, frame rate limit, V-Sync, texture filtering, DLSS overrides and more), each with Driver default to put NVIDIA's value back. Settings > Graphics holds the same for every game. _Verified: on screen._
- **DLSS rows only where they apply.** A DLSS row shows only on a game that ships that DLSS file. _Verified: on screen._
- **Import a Profile Inspector file.** Import profile reads a `.nip` file and lists what it would change before anything is written; Undo import puts back what it replaced. _Checked in code only._
- **AMD and Intel** cards show what their driver reports, read-only. _Checked in code only._

### Accounts
- **Who you are signed in as.** Settings > Game stores shows the Steam, Epic, GOG and Amazon accounts on this PC. _Verified: on screen._
- **Switch Steam accounts in one click.** Pick another account Steam remembers and Exo restarts Steam signed in as it. _Checked in code only._
- **More than one Epic account.** Add an account, switch to it, or bring in one Epic already knows. _Checked in code only._
- **Hours from all your accounts.** Turn on "Count hours from all my accounts" and a game you play on two accounts, like Rocket League on an alt, shows the hours from both. Rocket League's Epic hours _Verified: on screen._ Adding up two accounts _Checked in code only._

### Hours
- **Only real hours.** Hours come from the store or the game itself, never from Exo's own timing; a game with no real source shows none, rather than a guess. Hours can now also go down when the store says so, so a stale figure no longer sticks. _Verified: on screen._
- **More sources.** Steam and GOG first, then GOG Galaxy 2.0's database for games on platforms you connected to it, then tracker.gg, now for Rainbow Six Siege and Destiny 2 too, and Xbox with an OpenXBL key you paste. Each figure says where it came from. _Checked in code only._
- **Battle.net last played.** Battle.net games show when you last played them. _Checked in code only._

### Fixes
- **Instant replay survives alt-tab.** Minimizing a game no longer throws away the replay it was holding. _Verified: on screen._
- **The capture storage limit only removes Exo's own captures,** and sends them to the Recycle Bin. With the save folder set to Videos, it could permanently delete your other videos. _Checked in code only._
- **Saving an edited snip works for full-size screenshots.** Pictures over about 750 KB never saved. _Checked in code only._
- **The capture quality slider shows where it is set,** and has a focus ring. _Verified: on screen._
- **Exo Mouse runs on its own address,** so the Devices add-on can only reach the mouse functions Exo gives it, never the rest of Exo. _Verified: on screen._
- **Mouse profiles are never wiped by a failed read,** and a game profile the mouse only partly takes says so. _Checked in code only._
- **Accounts stay the account you chose.** After an Epic or GOG switch or sign-out, the next sign-in no longer brings back the old account. Signing out of GOG no longer falls back to Heroic's account. Amazon shows only the signed-in account's games. Exo no longer spends Epic's sign-in tokens itself, which could sign you out of Legendary or the Epic Games Launcher. _Checked in code only._
- **Hours are counted once.** A game on two stores no longer counts the same tracker.gg or GOG Galaxy figure twice after a background refresh. Hours from a mistyped Riot ID go away when you correct it. Steam hours stay when Steam is closed. GOG games get their last played date from GOG Galaxy. _Checked in code only._
- **Stop leaves your other programs alone.** Stopping a game only ends processes from the game's own folder, never a browser or Discord it opened. EA's anti-cheat launcher and GameGuard are never ended. _Checked in code only._
- **Upscaler and Discord changes are safe against interruptions.** A DLSS backup is verified before the game's file is replaced, and linked FSR files are put back together if one fails. DLSS changes wait while the game's store is updating it. Discord can no longer be left without its app files. _Checked in code only._
- **Smaller fixes.** Queued badges show on cards owned on two stores, Store shows ™ titles as in your library, Ctrl+K no longer lists hidden extras, the Store home no longer hangs after a network drop, Riot banner art loads, and a broken download-queue file no longer stops Exo from starting. _Checked in code only._

[Download ExoLauncher-Setup.exe](https://github.com/ImAvgErix/ExoLauncher/releases/download/v3.6.0/ExoLauncher-Setup.exe) · [Full changelog](https://github.com/ImAvgErix/ExoLauncher/blob/main/CHANGELOG.md) · [Privacy policy](https://github.com/ImAvgErix/ExoLauncher/blob/main/PRIVACY.md)
