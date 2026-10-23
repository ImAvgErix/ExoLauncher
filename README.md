<p align="center"><img src="docs/media/logo.png" alt="Exo" width="88"></p>
<h1 align="center">Exo Launcher 3.14</h1>
<p align="center"><strong>Your games, your music, your friends and your mouse. One app.</strong></p>
<p align="center">Exo brings every game store, Discord, Spotify, Apple Music, YouTube Music, your captures and your mouse software into one fast, quiet window for Windows 11.</p>
<p align="center"><a href="https://github.com/ImAvgErix/ExoLauncher/releases/latest/download/ExoLauncher-Setup.exe"><strong>Download for Windows 11</strong></a> · <a href="https://github.com/ImAvgErix/ExoLauncher/releases/tag/v3.14.1">What's new in 3.14</a> · <a href="CHANGELOG.md">Changelog</a> · <a href="PRIVACY.md">Privacy</a></p>

![The Exo Library: every game from every store in one grid](docs/media/library.jpg)

## Why one app instead of eight

A gaming PC usually runs Steam, the Epic Games Launcher, Riot Client, Discord, Spotify, a capture app, a mouse app and more, each with its own window, tray icon, updater and built-in browser holding memory. Exo replaces all of that with one window you actually use, and keeps the apps it depends on quiet in the background.

- **One library for every store.** Steam, Epic, GOG, Amazon, Riot, Xbox, EA, Ubisoft, Battle.net, Rockstar, itch.io, Minecraft, Roblox, Paradox, Wargaming and any game you add yourself in one grid, with one search, one sort, play time, last launched, sizes and achievements. Play, install, update and uninstall from the same page.
- **Less memory, not more.** The apps Exo hosts are kept as lean as they can be:
  - Spotify starts hidden behind Exo and stops drawing while you can't see it, and Discord needs no app at all: Exo has its own client.
  - A paused music player is frozen until you reach for it, then wakes before your click lands.
  - Store apps running in the background stay hidden and out of the tray, even one that started with Windows. While Steam, Epic, EA, Ubisoft, GOG Galaxy or Battle.net sits hidden, Exo gives the memory its built-in browser holds back to Windows; Steam's browser helpers drop from about 280 MB to under 80 MB.
  - Behind a game, Exo itself drops to low priority and Windows efficiency mode, and its rooms stop all work.
- **Fast.** Rooms switch instantly because their apps are already running, Store pages open in about half a second, and the Library is ready about half a second after Exo's window appears.
- **Your music, your way.** Spotify, Apple Music and YouTube Music all play in Exo's own player: the same look, the same controls, your whole song library one click away, Jam and Friend activity for Spotify, lyrics that follow the song, and a mini player in the top bar that works from every room.
- **Game-aware.** Downloads pause while you play. Your mouse switches to a game's profile when it starts. Instant replay runs while you play. Exo goes quiet behind a game and comes back when you do.
- **Better games.** DLSS, FSR (FSR 4 included) and XeSS updated in every game with the vendors' own signed files the day they release them, and a one-click restore; the best NVIDIA driver profile for your card; and shaders precompiled so games stop stuttering.
- **Made to look at.** Light, Dark or Void (pure black, for OLED screens), or matched to Windows. Inter type, Liquid Glass on everything that floats, one icon family, and motion that answers at once and settles. Every screen gets the same layout: a 1440p or 4K screen shows what a 1080p one does, only sharper.
- **Yours.** No Exo account, no ads, no analytics, no telemetry. Your library, settings and keys stay on your PC.

## How it works

Exo is a native Windows app (WinUI 3 and .NET) with its interface drawn in an embedded Microsoft Edge WebView2.

- **Stores.** Exo reads what each store already keeps on your PC (Steam's library and manifests, Riot Client's records, the Xbox app's game configs and more) and uses each store's own client to launch. Epic, GOG and Amazon run through the open-source tools Legendary, gogdl and Nile, and a game Epic's own launcher installed updates in that launcher, out of sight. You sign in on each store's own page, and the store keeps its own sign-in. Downloads, ownership, DRM and anti-cheat stay the stores' job.
- **Discord.** Exo's own Discord client: servers, DMs, Friends, voice and video calls, Go Live, GIFs, stickers and full profiles in Exo's look, with no Discord app to install.
- **Music.** Each service plays the music itself, hidden behind Exo: Spotify's desktop app, Apple Music's web player and YouTube Music's web player. Exo drives them through their own in-app interfaces and draws everything you see, so your account, downloads and sound quality are the service's own.
- **Captures, Devices and Shaders.** These are separate open-source programs that ship with Exo and talk to it only through messages: Exo Capture records your games on the graphics card's own encoder, Exo Mouse talks to your mouse directly over USB, and Exo Shaders precompiles shaders for your graphics driver.
- **Memory.** Exo watches which window is in front and what you can see. A room you can't see costs as little as possible: its renderer stops drawing, its memory is trimmed, and its processes run in efficiency mode. Nothing a game, download or call needs is ever closed.

## The rooms

### Library

Every game you own, from every store, in one grid, plus the games your Steam Family shares; a game that is not installed carries a small mark. **Continue** keeps the games you're playing one click from Play, and **Friends are playing** shows the games your Discord friends are in right now. Sort by name, installed first, last launched, time played, size or store. Refresh reads every store again, measures sizes, reads achievements and fetches art.

**Folders.** + > New folder: name it, pick its color and tick its games. It sits at the front of the grid and opens into a panel of its games. Right-click a game to add it to a folder. A folder is also how you keep favorites.

**Add any game.** + > Add a game picks a game's own .exe, wherever it is; Find a game in a folder finds the .exe for you.

![A game page: play, time played, last launched, size, Properties, upscalers and shaders](docs/media/game.jpg)

A game's page shows where it's from, time played, last launched, its size and achievements, with Play, Uninstall (Remove for a game you added yourself) and Properties. A row each for its upscalers and shaders opens their System page, and HDR sets whether the game plays in HDR.

**Properties** has what Steam's and Epic's own Properties have: launch options, language, cloud saves, the Steam overlay, betas, automatic updates, checking its files, moving the game to another drive, and its DLC and optional content, such as a high-resolution texture pack, switched on or off with the space it takes or frees shown first. Its Artwork replaces the cover with your own picture.

**More than one account?** Switch between your Steam accounts, or your Riot accounts for League and VALORANT, in Settings > Game stores.

### System

![System > Display: refresh rate, output color, scaling and its override, color and RTX Video](docs/media/gpu.jpg)

The System room is your graphics card and screen, in three pages: Graphics, Display and Shaders.
- **Driver and profile:** NVIDIA's newest driver for your card, looked for at most once an hour and installed clean, driver only, with no NVIDIA app, telemetry, PhysX or HDMI audio (on a Radeon or Intel graphics, AMD's or Intel's newest); and Exo's NVIDIA driver profile for the lowest latency, with or without G-SYNC.
- **HDR:** on only while a game with real HDR runs, and SDR drawn at gamma 2.2 while it is, so blacks stay black.
- **Display:** the refresh rates and colors your screen really takes, scaling and **Override the scaling set by games**, color from brightness to hue, and RTX Video Super Resolution and HDR. A picture change undoes itself after 15 seconds unless you keep it.

### Upscalers

![System > Graphics > Upscalers](docs/media/upscalers.jpg)

System > Graphics > Upscalers lists every installed game that can get newer DLSS, FSR or XeSS and updates them all at once.
- **New releases:** when Exo opens it checks NVIDIA's, AMD's and Intel's own releases, so a new DLSS, FSR 4 or XeSS reaches your games without waiting for an Exo update.
- **Files:** every file is the vendor's own, checked for its signature, exact version and checksum before it goes in, and Restore puts the game's originals back byte for byte. A running game waits until you close it.
- **Anti-cheat:** a game with anti-cheat changes only after you allow it for that game, at your own risk. Riot games are never changed.

### Shaders

![System > Shaders: Exo Shaders shows which games are compiled](docs/media/shaders.jpg)

Exo Shaders precompiles each game's shaders for the graphics driver you have now, so a game doesn't stutter the first time it meets a new effect. It shows which games are compiled and keeps a queue, and skips a game Windows already compiled for your card. When your driver updates, it compiles them again while your PC is idle, holding its memory use down so the rest of your PC stays quick.

### Store

![The Store: featured games, Epic's free games and new and trending games](docs/media/store.jpg)

Steam's big new and upcoming games, new and trending, the most anticipated, top sellers, sales and the most wishlisted, Epic's free games, and search, with prices in your own currency and review scores. Wishlist a game and Exo tells you when it goes on sale.

![A Store game page: trailers, buying options, description, details and system requirements](docs/media/store-game.jpg)

Every game's Store page has the same layout, on one screen: trailers and screenshots with the details under them on the left; on the right the logo, each official store's price, the description and the system requirements, with your own PC's parts beside them and a check or a cross. Get opens the store's own app; the prices Exo shows come from official stores only.

### Discord

![The Discord room: Exo's own Discord client (names, messages and pictures blurred in this picture)](docs/media/discord.jpg)

Exo's own Discord client, laid out like Discord in Exo's look: your servers, DMs, Friends, forums, polls, app commands, GIFs, stickers and emoji with your favorites, full profiles, Discord's Shop, and moderation where you have the permission. You sign in with a QR code from Discord's phone app, never a password. Drag servers to reorder them or make folders, and an invite link joins the server right here. Chats you have opened show instantly, notifications follow Discord's own rules and clear when you read the chat anywhere, and the unread count shows on the Discord tab and Exo's taskbar button. The names, messages and pictures in this picture are blurred for privacy.

**Calls** are end-to-end encrypted, with your camera and Go Live screen share. Noise suppression takes fans, keys and the room out of your voice, even on a busy PC; echo cancellation lets you call on speakers; and Noise Suppression for Others can clean up everyone else's voice too.

### Music

![The Music room: Exo's own player, here with Spotify (your library blurred in this picture)](docs/media/music.jpg)

Exo's own player for Spotify, Apple Music and YouTube Music:
- **Browsing:** your library down the left, Songs for every song you saved, Home, search, playlists, albums, artists and podcasts.
- **Playing:** a queue you can jump around in, lyrics that follow the song, and the mini player in the top bar.
- **Spotify extras:** Jam with friends, see what they're listening to, and pick the device that plays. A Spotify link someone sends you in Discord opens right here, and a Jam link joins it.

### Captures

The Capture button in the top bar, or its keys, saves an instant replay of the last minutes, starts or stops a recording, or takes a snip of any part of your screen. Exo Capture records on your graphics card's own encoder (NVIDIA, AMD or Intel), so games keep their frame rate.
- **Captures:** every replay, recording and snip, sorted by game, with a player, trim, favorites, a copy sized for Discord and Send to Discord.
- **Space:** a storage limit and an age you set; your favorites are always kept.

### Devices

![The Devices room: Exo Mouse](docs/media/devices.jpg)

Exo Mouse talks to your mouse directly, with no vendor app, driver or account:
- **Status:** your mouse's battery, signal and firmware, with its battery and profile in the top bar.
- **Settings:** DPI, polling rate, lift-off, buttons, lighting and more, showing only what your mouse has.
- **Profiles:** each one switches on by itself when a linked game starts.
- **Compatibility:** it works with the mice OpenMouse supports, wired or on their receiver.

### Settings

![Settings: a floating panel over the room you were in](docs/media/settings.jpg)

Appearance (Match Windows, Light, Dark or Void), Glass, text size and reduce motion; download hours and the install folder; your music service; capture keys and instant replay; your game stores, their accounts and their notifications; Clean up for the apps Exo replaces; optional keys and updates. One page that floats over the room you were in. If you use Lucid, Exo switches your headphones' EQ when your device or game changes, starts it with you and keeps it out of sight.

## Privacy and safety

- **No Exo account, no ads, no analytics, no telemetry.** Exo has no server of its own. Your library, settings, covers, play time and keys stay in `%LocalAppData%\ExoLauncher`, and your captures in your Videos folder. Optional keys and your Discord sign-in are encrypted with Windows.
- **Your sign-ins stay with each service.** You sign in on the store's or service's own page or window, and to Discord with your phone. Exo never sees your passwords.
- **What leaves your PC** is only what a feature needs: store lookups, cover art, Store pages, prices, driver and upscaler releases, the update check, and what you do in Discord and your music service. The [privacy policy](PRIVACY.md) lists every destination and what is sent.
- **Your choices, your risk, said plainly.** Discord's terms don't allow other clients, so Exo's Discord client is used at your own risk. Changing upscaler files in a game with anti-cheat can get an account banned, which is why Exo asks for each game and never touches Riot games.

## Install

1. Download **[ExoLauncher-Setup.exe](https://github.com/ImAvgErix/ExoLauncher/releases/latest/download/ExoLauncher-Setup.exe)**.
2. Run it and choose Install, then Launch Exo. First run walks through your game stores, music, Discord, captures and this PC's graphics, and offers to remove the apps Exo replaces.
3. Exo finds your games and starts your music service out of sight. Each new version of Exo shows in Settings > About, and installs when you choose.

**Requirements:** Windows 11 x64. Exo installs for your Windows user only, in `%LOCALAPPDATA%\ExoLauncher\app`, and needs no administrator rights. The installer isn't code-signed yet, so Windows SmartScreen may warn the first time; choose **More info**, then **Run anyway**. Each release lists the installer's SHA-256 so you can check your download.

## License and legal

- **Exo Launcher** is free for personal use under its own [terms](LICENSE), provided as is, without warranty. Its source is in this repository for anyone to read and check, but it is not open source: copying it, forking it outside GitHub or publishing changed versions needs written permission.
- **Open-source parts that ship with Exo** keep their own licenses, and their source is public:
  - [Exo Mouse](https://github.com/ImAvgErix/exo-mouse) (AGPL-3.0-or-later), built on OpenMouse.
  - [Exo Capture](https://github.com/ImAvgErix/exo-capture) (GPL-3.0-or-later), with OBS Studio's game capture hook.
  - [Exo Shaders](https://github.com/ImAvgErix/exo-shaders) (GPL-3.0-or-later), based on SCSKiller.
  - uBlock Origin Lite, SponsorBlock and Better Lyrics (GPL-3.0), in the music players' browser.
  
  Every bundled library and tool is listed in [Third-party notices](THIRD_PARTY_NOTICES.md).
- **Not affiliated.** Exo Launcher is an independent product, not affiliated with or endorsed by Valve, Epic Games, GOG, Riot Games, Microsoft, EA, Ubisoft, Blizzard, Amazon, Discord, Spotify, Apple, Google, NVIDIA, AMD, Intel, OBS Studio or any other company it works with. Their names, logos and game art belong to their owners. Your use of each service stays under that service's own terms.

[Report an issue](https://github.com/ImAvgErix/ExoLauncher/issues) · [Support development](https://www.buymeacoffee.com/UhhErix)

© 2026 Erix ([ImAvgErix](https://github.com/ImAvgErix)).
