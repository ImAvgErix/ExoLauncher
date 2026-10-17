# Exo Launcher privacy statement

**Exo Launcher is local-first. It has no Exo account, no ads, no analytics and no telemetry.**

Your library, installs, updates, game launches and settings work offline. Exo has no server of its own that they depend on.

## What stays on this PC

Exo keeps its data in `%LocalAppData%\ExoLauncher`. This includes:

- settings (`settings.json`), the library cache, playtime Exo recorded, window position, cover art and thumbnail caches, achievement cache, and upscaler backups;
- copies of custom game covers you choose. They are stored locally and never uploaded;
- your Store wishlist (`wishlist-v2.json`) and the last sale price Exo told you about, the Store's saved pages (`store/home-v5.json`, Steam's public facts about the games it showed in `store/items-v1.json`, tag names in `store/tags-v1.json`), and your Exo Mouse profiles (`mouse/profiles.json`);
- the store tools Exo downloads (Legendary, gogdl, Nile) and their data, plus the browser data from the Epic and GOG sign-in windows;
- Exo Shaders, the shader engine behind Library > Shaders, keeps its compile records, recordings and settings in `%LOCALAPPDATA%\SCSKiller` (the same folder SCSKiller uses). Exo passes it your installed games' names and folders so it can find their shaders; nothing leaves your PC (see **Exo Shaders** under **Other network requests** for the files it may download);
- the browser profile used for YouTube Music and Apple Music;
- logs (see **Logs** below).

To find your games, Exo reads local folders, store manifests, registry keys and data saved by store clients on this PC. Exo does not upload your library, install folders, launch settings, playtime or achievements. Some services see a game title or store id when Exo looks up artwork or hours for it; they are listed under **Other network requests**.

To free memory, Exo reads the process list on this PC (names, parent processes, and the command line of a Battle.net process to tell its renderers apart) to find the browser helpers of Steam, the Epic Games Launcher, the EA app, Ubisoft Connect, GOG Galaxy and Battle.net, and lowers their memory use while they are hidden. It reads nothing inside them, keeps nothing and sends nothing.

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
- **Equicord (always on).** The first time Discord starts through Exo, and whenever Discord updates, Exo:
  - downloads a fixed Equicord build from Equicord's GitHub releases and checks its size and SHA-256;
  - changes Discord's app files so Discord loads Equicord, keeping Discord's original file next to it;
  - writes Equicord settings, including plugins that block Discord's analytics, clean tracking parameters from links and hide clutter, plus FakeNitro (Nitro emoji, stickers and stream quality without Nitro, which Discord's terms forbid; turn it off in Equicord's plugins if you'd rather not), and keeps a small file beside them (`exo-applied.json`) recording which values Exo set, so your own changes in Equicord are kept;
  - once, only where an earlier Exo set Discord's zoom, reads Discord's zoom from its own storage on your PC (the `AccessibilityStore` entry, nothing else) and puts it back to 100% if it is still the value Exo set. Nothing leaves your PC, and a zoom you choose in Discord later is never changed.

  After the first install, Equicord updates itself. Exo puts Discord's original file back if a Discord update gets stuck. If you installed Equicord yourself, Exo leaves it alone. Discord's terms forbid client modifications; that risk comes with using Discord through Exo.

### Spotify and Cider

These are your installed desktop apps. When Exo starts one of them:

- **Spotify.** Exo recolors it and hides ads, promos and install prompts by adding CSS to Spotify's local `xpui.spa` file, and turns on two of Spotify's own preferences that hide home-page takeovers and promoted songs. It turns on Spotify's "system media controls" preference so the mini player can follow playback. It also starts Spotify with a debugging port that only this PC can reach, and uses it to hide Spotify's window buttons and to pause Canvas videos.
- **Cider.** Exo adds a marked block of CSS to Cider's own settings file to hide its window buttons and to turn off blurred panels and smooth scrolling. The block is removed when Cider closes.

All of these changes stay on this PC. The mini player reads what is playing from Windows' media controls.

**Exo's Music room for Spotify** draws Spotify's pages itself. Through that same local debugging port it asks the Spotify app for what the room shows (your library, home, search, playlists, albums, artists, podcasts, the queue, devices, recently played, lyrics of the playing song, your friends' activity, and your Jam) and passes on what you click (play, queue, like, add to or remove from a playlist, start, leave or end a Jam, remove someone from it). Spotify makes every one of these requests itself, with its own sign-in, exactly as when you click in Spotify; Exo never reads your token or cookies. Only small summaries (names, picture links, durations) reach Exo's window, and nothing is stored. A Jam's invite link reaches Exo only to be copied when you press Copy invite link. Album art, profile pictures and lyrics' covers load in Exo's window straight from Spotify's image servers (`*.scdn.co`, `*.spotifycdn.com`).

### Your mouse (Exo Mouse)

The Devices room is Exo Mouse, which is part of Exo's download and talks to your mouse directly over USB HID. It reads and writes your mouse's own settings (such as DPI, polling rate, buttons, lighting, battery level and onboard profiles). That data stays on this PC: profiles you save in Exo Mouse (a name, the settings to put back and the games they are for) are kept in Exo's data folder on this PC (`mouse\profiles.json`), nothing is sent anywhere, and Exo needs no OpenMouse app, vendor software or driver for it. To show a picture of your mouse, Exo Mouse loads its product render from OpenMouse's image server (img.openmouse.app); the request names the model's image file, and the image is then kept in Exo's browser cache.

Earlier versions of Exo answered the OpenMouse web panel through a bridge of their own and could stop the OpenMouse Bridge app to do it. That bridge is gone. The first time this version starts, if Exo had stopped the OpenMouse Bridge app, Exo puts its start with Windows back exactly as it was and writes the profiles Exo kept into the app's own settings file, then deletes Exo's copy. It does not start the app.

### YouTube Music

YouTube Music runs inside Exo as a web page (Microsoft Edge WebView2), using a browser profile stored under `%LocalAppData%\ExoLauncher`. You sign in to Google there, and Google handles that data.

Exo's Music room draws YouTube Music's pages itself and keeps that page out of sight behind it (it shows only so you can sign in). Exo asks the page for what the room shows (home, search, your library, playlists, albums, artists, the queue, lyrics) and passes on what you click (play, like, shuffle, repeat); the page makes every request itself with your Google sign-in, as when you click in it, and Exo never reads its cookies or tokens. Only small summaries reach Exo's window, and nothing is stored. Covers load in Exo's window straight from Google's image servers (`*.googleusercontent.com`, `i.ytimg.com`).

### Apple Music

Apple Music runs inside Exo as Apple's own web player (music.apple.com) in the same browser profile. You sign in to Apple there, in Apple's own sign-in window, and Apple handles that data. Exo's Music room draws Apple Music's pages itself and keeps that page out of sight behind it (it shows only so you can sign in). Exo asks the page's own Apple Music player (MusicKit) for what the room shows (home, recently played, search, your library, playlists, albums, artists, the queue, lyrics of the playing song) and passes on what you click (play, add to the queue, favorite, add to or remove from your library or a playlist, shuffle, repeat, volume). The page makes every request itself, with Apple's own developer token and your sign-in; Exo never reads, stores or copies those tokens or its cookies. Music plays inside Apple's page, protected as Apple protects it; Exo never handles the audio. Only small summaries (names, picture links, durations) reach Exo's window, and nothing is stored. Covers load in Exo's window straight from Apple's image servers (`*.mzstatic.com`). The Apple Music app for Windows is no longer used.

Exo adds these browser extensions to that profile:

- **uBlock Origin** blocks ads and trackers. It downloads filter-list updates from the internet.
- **SponsorBlock** asks sponsor.ajay.app about the video that is playing.
- **Better Lyrics** fetches lyrics from outside lyrics services.

These extensions have their own privacy policies. Pages that ask for your microphone, camera, clipboard or notifications prompt you by default. You can change this in Settings.

## Other network requests

| Destination | What is sent | When |
| --- | --- | --- |
| GitHub (ExoLauncher releases) | Version lookup; update download | When Exo starts, and when you press **Check**. The download happens only when you choose to install. Update checks cannot be turned off. |
| GitHub (Legendary, gogdl, Nile, Equicord releases) | Download of a fixed version, checked against its SHA-256 | When a feature first needs the tool, and Equicord the first time Discord starts through Exo |
| Epic, GOG, Amazon (through Legendary, gogdl, Nile) | Your own store sign-in, library, installs, updates | When you sign in, refresh, install or play |
| Epic account and library services | Your Legendary/Heroic or Epic Games Launcher sign-in, used to read your Epic playtime | When your library refreshes, if you're signed in to Epic on this PC |
| GOG (gameplay.gog.com, api.gog.com, gamesdb.gog.com) | Your gogdl sign-in for achievements and owned games; product ids for artwork | When your library or achievements refresh |
| Steam | Nothing is sent. Owned games come from the Steam client running on this PC | When your library refreshes |
| Steam Web API | Your pasted key and Steam id, for owned games and achievements | Only if you pasted a key |
| itch.io API | Your pasted key, for owned games | Only if you pasted a key |
| tracker.gg | Your Riot ID (Name#TAG), to read public Valorant / League of Legends hours | About 12 seconds after Exo starts (at most every 12 hours), and when you refresh. Exo uses a Riot ID you saved, or one it reads from the Riot Client on this PC. Riot sign-in data is never sent. |
| Cover art: Steam CDN and Steam store search, Epic store catalog, GOG, Microsoft Store catalog, and the image servers a store's own data points to | Steam app ids, store product ids, and game titles as search terms; for a picture a store's own data names (Riot, Ubisoft, EA, Battle.net, Xbox, IGDB, SteamGridDB), the request for that picture | When a game needs artwork |
| SteamGridDB | Your pasted key and a Steam app id | Only if you pasted a key |
| IGDB (Twitch) | Developer credentials | Developer builds only |
| Vendor upscaler files (NVIDIA DLSS, AMD FidelityFX SDK and Intel XeSS repositories on GitHub, raw.githubusercontent.com at pinned commits; other DLSS, FSR 3.1 and XeSS builds from DLSS Swapper's archive, dlss-swapper-downloads.beeradmoore.com) | File downloads, checked against SHA-256 values built into Exo, or for archive builds the list's size and MD5 and then the SHA-256 Exo recorded, and always the vendor's signature and version | When you choose Update all or a version for a game's DLSS / FSR / XeSS file |
| DLSS Swapper's release list (beeradmoore.github.io/dlss-swapper/manifest.json) | Nothing about you or your games; an ordinary download | When the upscaler page or sheet is opened, at most once a day |
| Store room: Steam store and Steam Web API (store search, ranked lists, game pages), CheapShark (prices from official stores only), Epic free games | Search terms, Steam app ids, and the app ids on your wishlist | When you open the Store, and a wishlist price check a minute after Exo starts and every 6 hours |
| Store room: Steam tag names (IStoreService/GetTagList) | Nothing personal: only the language (English) | When a game page opens, at most once a week |
| Music artwork (Spotify, YouTube Music and Apple Music image servers) | The address of the playing song's cover, as the service's own page names it | When a song starts in a web player, so the mini player and Windows' media controls can show its cover |
| Exo Shaders (GitHub: Oodle and zlib-ng codec libraries, and some games' public archive keys) | Nothing about you: ordinary downloads of fixed files | Only when a game it compiles needs them. Exo Shaders makes no calls to SCSKiller's servers: no account, no community recordings, no diagnostics and no daily check |
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
