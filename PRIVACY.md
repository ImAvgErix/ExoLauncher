# Exo Launcher privacy statement

**Exo Launcher is local-first. It has no Exo account, no ads, no analytics and no telemetry.**

Your library, installs, updates, game launches and settings work offline. Exo has no server of its own that they depend on.

## What stays on this PC

Exo keeps its data in `%LocalAppData%\ExoLauncher`. This includes:

- settings (`settings.json`), the library cache, playtime Exo recorded, window position, cover art and thumbnail caches, achievement cache, and upscaler backups;
- copies of custom game covers you choose. They are stored locally and never uploaded;
- the store tools Exo downloads (Legendary, gogdl, Nile) and their data, plus the browser data from the Epic and GOG sign-in windows;
- Nothing for SCSKiller. Library > Shaders shows SCSKiller's own app, which keeps its data in its own folder (`%LOCALAPPDATA%\SCSKiller`); Exo only reads its game list and per-game records there to show Compiled on a game's sheet. The first start of this version deletes the copy of SCSKiller an earlier Exo downloaded to `tools\scskiller`, after taking out any recorder that copy put in a game's folder;
- the browser profile used for YouTube Music;
- logs (see **Logs** below).

To find your games, Exo reads local folders, store manifests, registry keys and data saved by store clients on this PC. Exo does not upload your library, install folders, launch settings, playtime or achievements. Some services see a game title or store id when Exo looks up artwork or hours for it; they are listed under **Other network requests**.

**Keys and sign-ins stay local.** Optional keys you paste (Steam Web API, itch.io, SteamGridDB) and your saved Riot ID are encrypted with Windows (DPAPI) for your Windows user. They are not written to `settings.json`, not shown back in the app, and only sent to the service they belong to (see **Other network requests**). Other programs running as the same Windows user can still get at them.

Store sign-ins stay with the official tools:

- Epic sign-in goes to Legendary.
- GOG sign-in goes to gogdl.
- Amazon sign-in goes to Nile.

Exo opens the store's own sign-in page, passes the result to that tool, and does not keep its own copy of the token.

## Exo accounts (removed)

Exo 3.4 removed the optional Exo account. The app has no sign-in, sign-up, sign-out, handle, badges, export or delete screens any more, and **it never contacts the Exo account service (exo-id)**. Recent versions synced nothing to the account, so your library, playtime, settings and keys are already on this PC and keep working as before.

**On this PC.** The first time Exo 3.4 or later starts, it deletes what an earlier version saved for the account, in `%LocalAppData%\ExoLauncher` and `%AppData%\ExoLauncher`:

- `auth.bin`, the session token, encrypted with Windows (DPAPI), that kept you signed in, together with the account id, handle and email it held, plus any unfinished copy (`auth.bin.tmp`);
- the `online-cache` folder of saved account responses;
- `device-id`, the per-PC id very old versions sent with a library activity summary.

This is a local cleanup only: Exo does not sign out with the service or send it anything. It runs again on every start, so a copy restored from a backup is removed too. Exo writes one line to its log when it removes a saved sign-in.

**On the service.** If you created an account with an earlier version, the service still holds what it held before: your account id, display name, email, a salted password hash, session records (which may include an IP address and an app or browser identifier), and anything older versions stored, such as a handle, profile fields, a library activity summary, online status, preferences, friends or store links. Exo 3.4 does not add to it or change it. That data stays on the service (Cloudflare Workers with a Cloudflare D1 database) until the service is shut down and its data deleted. Until then, Exo 3.3.x can still download a copy (Settings > Exo account > Download) or delete the account (Settings > Exo account > Delete account); deleting removes the account and everything linked to it. Sessions end after 7 days, and the service removes expired ones every hour.

## Connected apps

Exo can show some desktop apps inside its own window. Those apps keep their own sign-in, data and privacy policies. Exo does not read your messages or playback history, and it does not send them anywhere.

### Discord

- **Hosting.** The Discord room shows your installed Discord desktop app. Once setup is finished, Exo starts Discord hidden when Exo opens, so the room opens instantly. Discord closes when Exo closes.
- **Settings Exo changes.** To fit Discord in its window, Exo edits a few window settings in Discord's `settings.json`.
- **"Optimize Discord" (off by default).** When you press Run, Exo:
  - downloads a fixed Equicord build from Equicord's GitHub releases and checks its size and SHA-256;
  - changes Discord's app files so Discord loads Equicord, keeping Discord's original file next to it;
  - writes Equicord settings, including plugins that block Discord's analytics, clean tracking parameters from links and hide clutter, and keeps a small file beside them (`exo-applied.json`) recording which values Exo set, so your own changes in Equicord are kept.

  After the first install, Equicord updates itself. Undo puts Discord's original file back. Exo also undoes the change if a Discord update gets stuck. If you installed Equicord yourself, Exo leaves it alone.

### Spotify, Apple Music and Cider

These are your installed desktop apps. When Exo starts one of them:

- **Spotify.** While **Optimize Spotify** is on (the default), Exo recolors it and hides ads, promos and install prompts by adding CSS to Spotify's local `xpui.spa` file, and turns on two of Spotify's own preferences that hide home-page takeovers and promoted songs; Undo puts the original file back and removes those two lines. It turns on Spotify's "system media controls" preference so the mini player can follow playback. It also starts Spotify with a debugging port that only this PC can reach, and uses it to hide Spotify's window buttons and, while optimized, to pause Canvas videos.
- **Cider.** Exo adds a marked block of CSS to Cider's own settings file to hide its window buttons and, while **Optimize Cider** is on, to turn off blurred panels and smooth scrolling. The block is removed when Cider closes.
- **Apple Music.** While **Optimize Apple Music** is on (the default), Exo runs a local helper that recolors the app.

All of these changes stay on this PC. The mini player reads what is playing from Windows' media controls.

### Your mouse (Exo Mouse)

The Devices room is Exo Mouse, which is part of Exo's download and talks to your mouse directly over USB HID. It reads and writes your mouse's own settings (such as DPI, polling rate, buttons, lighting, battery level and onboard profiles). That data stays on this PC: profiles you save in Exo Mouse (a name, the settings to put back and the games they are for) are kept in Exo's data folder on this PC (`mouse\profiles.json`), nothing is sent anywhere, and Exo needs no OpenMouse app, vendor software or driver for it. To show a picture of your mouse, Exo Mouse loads its product render from OpenMouse's image server (img.openmouse.app); the request names the model's image file, and the image is then kept in Exo's browser cache.

Earlier versions of Exo answered the OpenMouse web panel through a bridge of their own and could stop the OpenMouse Bridge app to do it. That bridge is gone. The first time this version starts, if Exo had stopped the OpenMouse Bridge app, Exo puts its start with Windows back exactly as it was and writes the profiles Exo kept into the app's own settings file, then deletes Exo's copy. It does not start the app.

### YouTube Music

YouTube Music runs inside Exo as a web page (Microsoft Edge WebView2), using a browser profile stored under `%LocalAppData%\ExoLauncher`. You sign in to Google there, and Google handles that data.

Exo adds these browser extensions to that profile:

- **uBlock Origin** blocks ads and trackers. It downloads filter-list updates from the internet.
- **SponsorBlock** asks sponsor.ajay.app about the video that is playing.
- **Better Lyrics** fetches lyrics from outside lyrics services.

These extensions have their own privacy policies. Pages that ask for your microphone, camera, clipboard or notifications prompt you by default. You can change this in Settings.

## Other network requests

| Destination | What is sent | When |
| --- | --- | --- |
| GitHub (ExoLauncher releases) | Version lookup; update download | When Exo starts, and when you press **Check**. The download happens only when you choose to install. Update checks cannot be turned off. |
| GitHub (Legendary, gogdl, Nile, Equicord releases) | Download of a fixed version, checked against its SHA-256 | When a feature first needs the tool, or when you press Run on **Optimize Discord** and Equicord is not downloaded yet |
| Epic, GOG, Amazon (through Legendary, gogdl, Nile) | Your own store sign-in, library, installs, updates | When you sign in, refresh, install or play |
| Epic account and library services | Your Legendary/Heroic or Epic Games Launcher sign-in, used to read your Epic playtime | When your library refreshes, if you're signed in to Epic on this PC |
| GOG (gameplay.gog.com, api.gog.com, gamesdb.gog.com) | Your gogdl sign-in for achievements and owned games; product ids for artwork | When your library or achievements refresh |
| Steam | Nothing is sent. Owned games come from the Steam client running on this PC | When your library refreshes |
| Steam Web API | Your pasted key and Steam id, for owned games and achievements | Only if you pasted a key |
| itch.io API | Your pasted key, for owned games | Only if you pasted a key |
| tracker.gg | Your Riot ID (Name#TAG), to read public Valorant / League of Legends hours | About 12 seconds after Exo starts (at most every 12 hours), and when you refresh. Exo uses a Riot ID you saved, or one it reads from the Riot Client on this PC. Riot sign-in data is never sent. |
| Cover art: Steam CDN and Steam store search, Epic store catalog, GOG, Microsoft Store catalog | Steam app ids, store product ids, and game titles as search terms | When a game needs artwork |
| SteamGridDB | Your pasted key and a Steam app id | Only if you pasted a key |
| IGDB (Twitch) | Developer credentials | Developer builds only |
| Vendor upscaler files (NVIDIA DLSS, AMD FidelityFX SDK and Intel XeSS repositories on GitHub, raw.githubusercontent.com at pinned commits) | File downloads, checked against SHA-256 values built into Exo | Only when you choose Update all or a version for a game's DLSS / FSR / XeSS file |
| SCSKiller's own app | Exo sends SCSKiller nothing. Its app makes its own requests (its update feed, its Patreon sign-in, the community recordings) under [its own privacy terms](https://github.com/BlueHeisenberg/SCSKiller) | Whenever the app runs, inside Exo or from its tray |
| Microsoft (Visual C++ and other runtimes), Discord, Spotify | Official installer downloads | Only when you choose to install them |

Store and service websites (store pages, key redemption, sign-up pages) open in your normal browser.

## Logs

Exo writes a diagnostic log to `%LocalAppData%\ExoLauncher\logs\app.log`. It holds up to about 2 MB, plus one older file. The log can include:

- game titles and folder names;
- app and store tool messages;
- errors from the app's interface;
- crash details.

Before writing, Exo removes things that look like tokens, passwords, keys, account or store ids, and email addresses. **Logs never leave your PC.** Exo does not upload them. **Open logs** in Settings only opens the folder. If you share a log for support, check it first.

## What Exo Launcher does not do

- No ads, analytics, telemetry or crash-reporting service. Crashes are written to the local log only.
- No Exo account, and no online dependency for your library or game actions.
- No online status, current game, library activity, playtime or per-PC id sent anywhere.
- No uploading of your library, install folders, playtime history or store sign-ins.
- No friends list, profiles, profile pictures, handles, badges, chat or settings sync in the app.
- No kernel driver, anti-cheat bypass, or silent approval of Windows security prompts.
- The clipboard is read only when you open **Activate a key**, to spot a game key, and only on this PC. Exo then opens the store's own redeem page; it does not send the key anywhere itself.
