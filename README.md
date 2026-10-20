<p align="center"><img src="docs/media/logo.png" alt="Exo" width="88"></p>
<h1 align="center">Exo Launcher 3.12</h1>
<p align="center"><strong>Your games, your music, your friends and your mouse. One app.</strong></p>
<p align="center">Exo brings every game store, Discord, Spotify, Apple Music, YouTube Music and your mouse software into one fast, quiet window for Windows 11.</p>
<p align="center"><a href="https://github.com/ImAvgErix/ExoLauncher/releases/latest/download/ExoLauncher-Setup.exe"><strong>Download for Windows 11</strong></a> · <a href="https://github.com/ImAvgErix/ExoLauncher/releases/tag/v3.12.0">What's new in 3.12</a> · <a href="CHANGELOG.md">Changelog</a> · <a href="PRIVACY.md">Privacy</a></p>

![The Exo Library: every game from every store in one grid](docs/media/library.jpg)

## Why one app instead of eight

A normal gaming PC runs Steam, the Epic Games Launcher, Riot Client, Discord, Spotify, a mouse app and more, each with its own window, its own tray icon, its own updater and its own built-in browser holding memory. Exo replaces all of that with one window you actually use, and keeps the apps it depends on quiet in the background.

- **One library for every store.** Steam, Epic, GOG, Amazon, Riot, Xbox, EA, Ubisoft, Battle.net, Rockstar, itch.io and your own folders in one grid, with one search, one sort, play time, last launched, sizes and achievements. Play, install and update from the same page.
- **Less memory, not more.** The apps Exo hosts are kept as lean as they can be:
  - Spotify starts hidden behind Exo and stops drawing while you can't see it, and Discord needs no app at all: Exo has its own client.
  - A paused music player is frozen until you reach for it, then wakes before your click lands.
  - While Steam, Epic, EA, Ubisoft, GOG Galaxy or Battle.net sits hidden behind Exo, Exo gives the memory their built-in browser holds back to Windows. On our test PC, Steam's browser helpers went from about 280 MB to under 80 MB.
  - Behind a game, Exo itself drops to low priority and Windows efficiency mode, and its rooms stop working.
- **Faster to use.** Rooms switch instantly because their apps are already running. Store pages open in about half a second. The Library is usable about half a second after Exo's window appears.
- **Your music, your way.** Spotify, Apple Music and YouTube Music all play in Exo's own player: the same look, the same controls, your whole song library one click away, Jam and Friend activity for Spotify, lyrics that follow the song, and a mini player in the top bar that works from every room.
- **Game-aware.** Downloads pause while you play. Your mouse switches to a game's profile when it starts. Exo goes quiet behind a game and comes back when you do.
- **Better games.** Update DLSS, FSR and XeSS in every game with checked, official files and a one-click restore, pick the best NVIDIA driver profile for your card, and precompile shaders so games stop stuttering.
- **Yours.** No Exo account, no ads, no analytics, no telemetry. Your library, settings and keys stay on your PC.

## How it works

Exo is a native Windows app (WinUI 3 and .NET) with its interface drawn in an embedded Microsoft Edge WebView2.

- **Stores.** Exo reads what each store already keeps on your PC (Steam's library and manifests, Riot Client's records, the Xbox app's game configs and more) and uses each store's own client to launch. Epic, GOG and Amazon run through the open-source tools Legendary, gogdl and Nile. You sign in on each store's own page, and the store keeps its own sign-in. Downloads, ownership, DRM and anti-cheat stay the stores' job.
- **Discord.** Exo's own Discord client: servers, DMs, Friends, voice calls, GIFs, stickers and full profiles in Exo's look, with no Discord app to install, including your camera and Go Live screen share in calls.
- **Music.** Each service plays the music itself, hidden behind Exo: Spotify's desktop app, Apple Music's web player and YouTube Music's web player. Exo drives them through their own in-app interfaces and draws everything you see, so your account, downloads and sound quality are the service's own.
- **Devices and Shaders.** These are separate open-source programs that ship with Exo and talk to it only through messages: Exo Mouse talks to your mouse directly over USB, and Exo Shaders precompiles shaders for your graphics driver.
- **Memory.** Exo watches which window is in front and what you can see. A room you can't see costs as little as possible: its renderer stops drawing, its memory is trimmed, and its processes run in efficiency mode. Nothing a game, download or call needs is ever closed.

## The rooms

### Library

Every game you own, from every store, in one grid, plus the games your Steam Family shares; a game that is not installed carries a small mark. Sort by name, last launched, time played, size or store. Refresh reads every store again, measures sizes, reads achievements and fetches art.

![A game page: play, time played, last launched, size, Properties, upscalers and shaders](docs/media/game.jpg)

A game's page shows where it's from, time played, last launched, its size and achievements, with a long Play and a smaller Remove, and one row each for its upscalers and shaders that opens their System page. Open folder, Verify files and the cover tools live in its Properties, where you can also replace its cover with your own art.

**Properties** has what Steam's and Epic's own Properties have: launch options, language, cloud saves, betas, automatic updates, moving the game to another drive, and its DLC and optional content, such as a high-resolution texture pack, switched on or off with the space it takes or frees shown first.

**More than one account?** Switch between your Steam accounts, or your Riot accounts for League and VALORANT, in Settings > Game stores.

### GPU

![System > Display: refresh rate, output color, scaling and its override, color and RTX Video](docs/media/gpu.jpg)

The System room is your graphics card in one place:
- **Profile:** Exo's NVIDIA driver profile for the lowest latency, and NVIDIA's newest Game Ready driver installed clean, driver only, with no NVIDIA app, telemetry, PhysX or HDMI audio.
- **Display:** refresh rate, output color, scaling and **Override the scaling set by games**, brightness to hue, and RTX Video Super Resolution and HDR. A picture change undoes itself after 15 seconds unless you keep it.
- **HDR:** on only while a game with real HDR runs, and SDR drawn at gamma 2.2 while it is, so blacks stay black.

### Upscalers

![System > Graphics > Upscalers](docs/media/upscalers.jpg)

System > Graphics > Upscalers lists every installed game that can get newer DLSS, FSR or XeSS and updates them all at once.
- **Files:** every file is the vendor's own, checked for its signature, exact version and checksum before it goes in, and Restore puts the game's originals back byte for byte. A running game waits until you close it.
- **Anti-cheat:** a game with anti-cheat changes only after you allow it for that game, at your own risk. Riot games are never changed.

### Shaders

![System > Shaders: Exo Shaders shows which games are compiled](docs/media/shaders.jpg)

Exo Shaders precompiles each game's shaders for the graphics driver you have now, so a game doesn't stutter the first time it meets a new effect. It shows which games are compiled and keeps a queue. When your driver updates, it compiles them again while your PC is idle.

### Store

![The Store: featured games, Epic's free games and new and trending games](docs/media/store.jpg)

Steam's top sellers, new releases, specials and upcoming games, Epic's free games, and search, with prices in your own currency, review scores and Steam Deck ratings. Wishlist a game and Exo tells you when it goes on sale.

![A Store game page: trailers, buying options, description, details and system requirements](docs/media/store-game.jpg)

A game's Store page fits on one screen: trailers and screenshots on the left, buying options on the right with each official store's price, then the description, details and system requirements. Get opens the store's own app; the prices Exo shows come from official stores only.

### Discord

![The Discord room: Exo's own Discord client (names, messages and pictures blurred in this picture)](docs/media/discord.jpg)

Exo's own Discord client, laid out like Discord in Exo's look: your servers, DMs, Friends, voice calls that are end-to-end encrypted and cleaned of background noise, GIFs, stickers, emoji with your favorites, full profiles, and moderation where you have the permission. You sign in with a QR code from Discord's phone app, never a password. Chats you have opened show instantly, notifications follow Discord's own rules and clear when you read the chat anywhere, and the unread count shows on the Discord tab and Exo's taskbar button. The names, messages and pictures in this picture are blurred for privacy.

### Music

![The Music room: Exo's own player, here with Spotify (your library blurred in this picture)](docs/media/music.jpg)

Exo's own player for Spotify, Apple Music and YouTube Music:
- **Browsing:** your library down the left, Songs for every song you saved, Home, search, playlists, albums, artists and podcasts.
- **Playing:** a queue you can jump around in, lyrics that follow the song, and the mini player in the top bar.
- **Spotify extras:** Jam with friends, see what they're listening to, and pick the device that plays. A Spotify link someone sends you in Discord opens right here, and a Jam link joins it.

### Devices

![The Devices room: Exo Mouse with a Finalmouse Starlight X](docs/media/devices.jpg)

Exo Mouse talks to your mouse directly, with no vendor app, driver or account:
- **Status:** your mouse's battery, signal and firmware.
- **Settings:** DPI, polling rate, lift-off, switches, lighting and more, showing only what your mouse has.
- **Profiles:** each one switches on by itself when a linked game starts.
- **Compatibility:** it works with the mice OpenMouse supports and fully with the Finalmouse Starlight X.

### Settings

![Settings: a floating panel over the room you were in](docs/media/settings.jpg)

Text size, reduce motion, download hours and folders, your music service, Lucid, your game stores and their accounts, optional keys and updates, in one short page that floats over the room you were in. **Lucid**, if you use it, switches your headphones' EQ when your device or game changes; Exo starts it with you, keeps it out of sight and keeps it updated.

## Privacy and safety

- **No Exo account, no ads, no analytics, no telemetry.** Exo has no server of its own. Your library, settings, covers, play time and keys stay in `%LocalAppData%\ExoLauncher`. Optional keys are encrypted with Windows.
- **Your sign-ins stay with each service.** You sign in on the store's or service's own page or window. Exo never sees your passwords.
- **What leaves your PC** is only what a feature needs: store lookups, cover art, Store pages, prices, and the update check. The [privacy policy](PRIVACY.md) lists every destination and what is sent.
- **Your choices, your risk, said plainly.** Discord's terms don't allow other clients, so Exo's Discord client is used at your own risk. Changing upscaler files in a game with anti-cheat can get an account banned, which is why Exo asks for each game and never touches Riot games.

## Install

1. Download **[ExoLauncher-Setup.exe](https://github.com/ImAvgErix/ExoLauncher/releases/latest/download/ExoLauncher-Setup.exe)**.
2. Run it and choose Install, then Launch Exo. Setup walks through your game stores, music, Discord and captures, and offers to remove the apps Exo replaces.
3. Exo finds your games, starts Discord and your music app behind it, and keeps itself up to date.

**Requirements:** Windows 11 x64. Exo installs for your Windows user only, in `%LOCALAPPDATA%\ExoLauncher\app`, and needs no administrator rights. The installer isn't code-signed yet, so Windows SmartScreen may warn the first time; choose **More info**, then **Run anyway**. Each release lists the installer's SHA-256 so you can check your download.

## License and legal

- **Exo Launcher** is free for personal use under its own [terms](LICENSE). Its source is private, and it is provided as is, without warranty.
- **Open-source parts that ship with Exo** keep their own licenses, and their source is public:
  - [Exo Mouse](https://github.com/ImAvgErix/exo-mouse) (AGPL-3.0-or-later), built on OpenMouse.
  - [Exo Shaders](https://github.com/ImAvgErix/exo-shaders) (GPL-3.0-or-later), based on SCSKiller.
  - uBlock Origin (GPL-3.0).
  
  Every bundled library and tool is listed in [Third-party notices](THIRD_PARTY_NOTICES.md).
- **Not affiliated.** Exo Launcher is an independent product, not affiliated with or endorsed by Valve, Epic Games, GOG, Riot Games, Microsoft, EA, Ubisoft, Blizzard, Amazon, Discord, Spotify, Apple, Google, NVIDIA, AMD, Intel, Finalmouse or any other company it works with. Their names, logos and game art belong to their owners. Your use of each service stays under that service's own terms.

[Report an issue](https://github.com/ImAvgErix/ExoLauncher/issues) · [Support development](https://www.buymeacoffee.com/UhhErix)

© 2026 Erix ([ImAvgErix](https://github.com/ImAvgErix)).
