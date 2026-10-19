# Exo Launcher privacy statement

**Exo Launcher is local-first. It has no Exo account, no ads, no analytics and no telemetry.**

Your library, installs, updates, game launches and settings work offline. Exo has no server of its own that they depend on.

## What stays on this PC

Exo keeps its data in `%LocalAppData%\ExoLauncher`. This includes:

- settings (`settings.json`), the library cache, playtime Exo recorded, hours Xbox reported (`reported-hours.json`: game titles and minutes only), window position, cover art and thumbnail caches, achievement cache, and upscaler backups;
- copies of custom game covers you choose. They are stored locally and never uploaded;
- your Store wishlist (`store/wishlist-v2.json`) and the last sale price Exo told you about, the Store's saved pages (`store/home-v5.json`, Steam's public facts about the games it showed in `store/items-v2.json`, tag names in `store/tags-v1.json`), and your Exo Mouse profiles (`mouse/profiles.json`);
- the store tools Exo downloads (Legendary, gogdl, Nile) and their data, plus the browser data from the Epic and GOG sign-in windows;
- Exo Shaders, the shader engine behind Library > Shaders, keeps its compile records, recordings and settings in `%LOCALAPPDATA%\SCSKiller` (the same folder SCSKiller uses). Exo passes it your installed games' names and folders so it can find their shaders; nothing leaves your PC (see **Exo Shaders** under **Other network requests** for the files it may download);
- Exo Capture, the add-on behind the Capture button, saves the instant replays, recordings and snips you ask for in your captures folder (`Videos\Exo Captures` unless you choose another), and keeps its settings, thumbnails and which game each capture belongs to in `capture` under Exo's data folder. It captures the game's window or the screen through Windows; only when Windows gets no picture from a game (exclusive full screen) does it load OBS Studio's game capture hook into that game to copy its frames, never into Riot games or while FACEIT or ESEA runs. Every replay and recording has four sound tracks: the mix, the game (on the desktop, everything you hear except Discord), Discord voice while Discord runs, and your default microphone. There is no setting to leave one out; mute a track in your player or editor instead. While instant replay or a recording runs, your microphone is in use and Windows shows its microphone indicator. Instant replay holds the last minutes in memory, by default only while a game is running, and writes nothing until you save. Captures never leave your PC: Send to Discord copies the file for you to paste into a chat yourself. A storage limit you set deletes the oldest captures; favorites are never deleted. A capture you delete goes to the Recycle Bin;
- the browser profile used for YouTube Music and Apple Music;
- logs (see **Logs** below).

To find your games, Exo reads local folders, store manifests, registry keys and data saved by store clients on this PC. For hours, it also reads GOG Galaxy 2.0's database (`galaxy-2.0.db`, read-only) for the time Galaxy holds for games on the other platforms you connected to it. Exo does not upload your library, install folders, launch settings, playtime or achievements. Some services see a game title or store id when Exo looks up artwork or hours for it; they are listed under **Other network requests**.

To free memory, Exo reads the process list on this PC (names, parent processes, and the command line of a Battle.net process to tell its renderers apart) to find the browser helpers of Steam, the Epic Games Launcher, the EA app, Ubisoft Connect, GOG Galaxy and Battle.net, and lowers their memory use while they are hidden. It reads nothing inside them, keeps nothing and sends nothing.

**Keys and sign-ins stay local.** Optional keys you paste (Steam Web API, itch.io, SteamGridDB, OpenXBL) and your saved Riot ID are encrypted with Windows (DPAPI) for your Windows user. They are not written to `settings.json`, not shown back in the app, and only sent to the service they belong to (see **Other network requests**). Other programs running as the same Windows user can still get at them.

Store sign-ins stay with the official tools:

- Epic sign-in goes to Legendary.
- GOG sign-in goes to gogdl.
- Amazon sign-in goes to Nile.

Exo opens the store's own sign-in page, passes the result to that tool, and does not keep its own copy of the token.

**Accounts in Settings > Game stores.** To show who each store is signed in as, Exo reads the display names in Steam's `config\loginusers.vdf`, Legendary's `user.json`, Nile's `user.json`, and the Epic Games Launcher's account id in the registry (to tell whether it uses a different account). Only display names reach the page; account names, ids and tokens are never logged. Switch account on Steam does what Steam's own account picker does: it closes Steam, marks that account as the one to sign in to in `loginusers.vdf` and the registry (`AutoLoginUser`, `RememberPassword`), and starts Steam again. Exo never sees a Steam password and never touches `config.vdf`. Epic accounts you add for their hours each get their own Legendary folder in `%LocalAppData%\ExoLauncher\epic-accounts`, and the last Epic playtime of each account signed in on this PC is kept in `epic-playtime.json` under a one-way tag. With **Count hours from all my accounts** on, Exo adds up the hours each Steam account on this PC and each of those Epic accounts recorded; nothing is sent anywhere new.

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

- **Exo's own Discord client.** The Discord room is Exo's own client; Exo does not run, install or change the Discord desktop app. You sign in by scanning a QR code with Discord's phone app, so Exo never sees your password. Exo keeps the sign-in on this PC, encrypted for your Windows account (`discord-client.bin` in Exo's data folder), and sends it only to Discord. Messages are shown, never saved or logged, and only channels your roles let you see are shown. In a call, your microphone is used only while you're in it, and in Discord settings > Voice & Video only while its level meter shows (Windows shows its microphone indicator); its sound is cleaned of noise on this PC (DeepFilterNet), encrypted end to end with Discord's DAVE protocol and sent only to Discord's voice server. Mentions and DMs show as Windows notifications with the message's text, as Discord's app does. Discord settings change your Discord account's own settings on Discord, as Discord's apps do; what Discord keeps per device (Accessibility, Streamer Mode, the camera, keybinds, sounds) stays on this PC. Your email and phone number are shown masked and never reach the page in full. Discord's sounds are fetched once from discord.com and kept in Exo's data folder. Keybinds read the keys you hold only while you're in a call, and never record or send them. Sign out removes the sign-in and ends it on Discord. Discord's terms do not allow third-party clients; that risk comes with signing in.

### Spotify

This is your installed desktop app. When Exo starts it:

- **Spotify.** Exo recolors it and hides ads, promos and install prompts by adding CSS to Spotify's local `xpui.spa` file, and turns on two of Spotify's own preferences that hide home-page takeovers and promoted songs. It turns on Spotify's "system media controls" preference so the mini player can follow playback. It also starts Spotify with a debugging port that only this PC can reach, and uses it to hide Spotify's window buttons and to pause Canvas videos.

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
| GitHub (Legendary, gogdl, Nile, DeepFilterNet releases) | Download of a fixed version, checked against its SHA-256 | When a feature first needs the tool, and DeepFilterNet the first time Exo's own Discord client starts with noise suppression on |
| Epic, GOG, Amazon (through Legendary, gogdl, Nile) | Your own store sign-in, library, installs, updates | When you sign in, refresh, install or play |
| Epic account and library services | Your Legendary/Heroic or Epic Games Launcher sign-in, used to read your Epic playtime | When your library refreshes, if you're signed in to Epic on this PC |
| Legendary's optional content lists (api.legendary.gl) | An installed Epic game's app name, to read which parts of it (such as high resolution textures or language packs) can be left out | When you open Properties for an installed Epic game that has such parts, once per game per run |
| GOG (gameplay.gog.com, api.gog.com, gamesdb.gog.com) | Your gogdl sign-in for achievements and owned games; product ids for artwork | When your library or achievements refresh |
| Steam | Nothing is sent. Owned games come from the Steam client running on this PC | When your library refreshes |
| Steam Web API | Your pasted key and Steam id, for owned games and achievements; to the Steam store, the app id of an owned app Steam has no details for on this PC, to tell a game from a tool or DLC | Only if you pasted a key |
| itch.io API | Your pasted key, for owned games | Only if you pasted a key |
| tracker.gg | Your Riot ID (Name#TAG), to read public Valorant / League of Legends hours | About 12 seconds after Exo starts (at most every 12 hours), when you save your Riot ID, and when you refresh. Exo uses a Riot ID you saved, or one it reads from the Riot Client on this PC. Riot sign-in data is never sent. |
| PCGamingWiki | A game's title or Steam app id, to read whether it outputs HDR | Only when the main screen can show HDR: once per game when you open its page or start it with HDR for HDR games on, then at most every 30 days |
| OpenXBL (api.xbl.io) | Your pasted OpenXBL key, to read your Xbox account's games played and the time played Xbox records for them (MinutesPlayed) | Only if you pasted a key: about 15 seconds after Exo starts (at most every 12 hours), when you save the key, and when you refresh |
| Cover art: Steam CDN and Steam store search, Epic store catalog, GOG, Microsoft Store catalog, and the image servers a store's own data points to | Steam app ids, store product ids, and game titles as search terms; for a picture a store's own data names (Riot, Ubisoft, EA, Battle.net, Xbox, IGDB, SteamGridDB), the request for that picture | When a game needs artwork |
| SteamGridDB | Your pasted key and a Steam app id | Only if you pasted a key |
| IGDB (Twitch) | Developer credentials | Developer builds only |
| Vendor upscaler files (NVIDIA DLSS, AMD FidelityFX SDK and Intel XeSS repositories on GitHub, raw.githubusercontent.com at pinned commits; other DLSS, FSR 3.1 and XeSS builds from DLSS Swapper's archive, dlss-swapper-downloads.beeradmoore.com) | File downloads, checked against SHA-256 values built into Exo, or for archive builds the list's size and MD5 and then the SHA-256 Exo recorded, and always the vendor's signature and version | When you choose Update all or a version for a game's DLSS / FSR / XeSS file |
| Exo's own Discord client (discord.com, gateway.discord.gg, remote-auth-gateway.discord.gg, cdn.discordapp.com, Discord's media proxy on discordapp.net, Discord's voice servers on discord.media, Discord's file uploads for apps on storage.googleapis.com) | Your Discord sign-in and what you do in the Discord room: the servers, channels and messages you open, the messages you send, edit or delete, files you attach (a file for an app's command or form goes to Discord's upload storage first, as Discord's app sends it), reactions, typing and read marks; pictures from Discord's image server, and a picture you copy or save with Copy Image or Save Image; in a call, your microphone's sound, end-to-end encrypted | Only while you are signed in to Exo's own Discord client |
| GIF hosts Discord points to (klipy.com, tenor.com, giphy.com) | The GIFs you see in the GIF picker and in messages, loaded by the Discord room | When the GIF picker or a message with a GIF shows |
| NVIDIA (driver lookup and download: `www.nvidia.com`, `gfwsl.geforce.com`, `api-forums.nvidia.com`, `us.download.nvidia.com`, `international.download.nvidia.com`) | Your NVIDIA card's model name, to find its newest Game Ready, Studio or RTX Enterprise driver, and NVIDIA's GeForce forum announcements of hotfix drivers; then the driver download itself, checked against NVIDIA's signature | When System > Graphics shows (at most once a day), and the download only when you choose Update or Reinstall |
| DLSS Swapper's release list (beeradmoore.github.io/dlss-swapper/manifest.json) | Nothing about you or your games; an ordinary download | When the upscaler page or sheet is opened, at most once a day |
| Store room: Steam store and Steam Web API (store search, ranked lists, game pages), CheapShark (prices from official stores only), Epic free games | Search terms, Steam app ids, and the app ids on your wishlist | When you open the Store; a minute after Exo starts and every 6 hours, a wishlist price check and (once you have opened the Store) the home page |
| Store room: Steam tag names (IStoreService/GetTagList) | Nothing personal: only the language (English) | When a game page opens, at most once a week |
| Music artwork (Spotify, YouTube Music and Apple Music image servers) | The address of the playing song's cover, as the service's own page names it; for a Spotify song whose app names no cover, the song's public Spotify link, sent to Spotify's oEmbed (`open.spotify.com`) to learn its cover | When a song starts in a web player, so the mini player and Windows' media controls can show its cover |
| Spotify share links (spotify.link, spotify.app.link) | The short link you paste into the Jam panel or click in Discord, without cookies or your account, to learn which Jam, song, album or playlist it opens; Exo reads only the link's own share page or redirect and does not load the Spotify page it names | When you join a Jam or open a Spotify link from one |
| Exo Shaders (GitHub: Oodle and zlib-ng codec libraries, and some games' public archive keys) | Nothing about you: ordinary downloads of fixed files | Only when a game it compiles needs them. Exo Shaders makes no calls to SCSKiller's servers: no account, no community recordings, no diagnostics and no daily check |
| Lucid's version feed (raw.githubusercontent.com/tekkusai/Lucid) | Nothing about you: an ordinary request for the newest Lucid version, to say in Settings > Lucid that an update is ready. Lucid's own window downloads and installs it; Exo never does | Only when Lucid is installed: when Settings > Lucid opens, at most once a day |
| Microsoft (Visual C++ and other runtimes), Spotify | Official installer downloads | Only when you choose to install them |

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
- The clipboard is read only when you open **Activate a key**, to spot a game key, and only on this PC. Exo then opens the store's own redeem page; it does not send the key anywhere itself. Exo Capture writes to the clipboard when you snip or press Copy or Send to Discord, and never reads it. In Exo's own Discord client, Copy Image puts the picture on the clipboard and Save Image saves it in your Downloads folder.
