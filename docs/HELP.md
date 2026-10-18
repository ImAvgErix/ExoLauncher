# Getting started and help

## Install and update

1. Download **ExoLauncher-Setup.exe** from the [latest release](https://github.com/ImAvgErix/ExoLauncher/releases/latest).
2. Run it and open Exo. Setup asks for your game stores, then your music service and Discord.
3. Exo finds your games. Updates download in the background and install when you choose; your settings, library and sign-ins stay.

Exo needs **Windows 11 x64** and installs for your Windows user only. The installer isn't code-signed yet, so SmartScreen may warn the first time: choose **More info**, then **Run anyway**. Each release lists the installer's SHA-256.

## Library

- **A game is missing.** Press the round **Refresh** button next to **+**. Exo reads every store again. For Epic, GOG or Amazon, sign in under **Settings > Game stores**. For a game outside any store, press **+** and pick its folder or executable.
- **Play, install and update.** Double-click an installed game, or open its page. Updates and downloads show in **Downloads & updates** at the bottom of the Library. Downloads can pause while you play and wait for the hours you set in **Settings > Downloads**.
- **Covers.** Replace cover on a game's page uses your own picture; Refresh art fetches the store's again.

## GPU

Library > GPU holds your graphics card's settings.
- **Profile** applies Exo's NVIDIA driver profile (lowest latency, G-SYNC full screen only) and shows your driver. **Update** installs NVIDIA's newest Game Ready driver clean and driver-only, with no NVIDIA app, PhysX or HDMI audio; Exo puts your profile and color back after. It waits while a game runs.
- **Display** has each screen's refresh rate, output color, scaling (with **Override the scaling set by games**), brightness to hue, and RTX Video. A change to the picture comes back by itself after 15 seconds unless you keep it.
- **HDR** turns HDR on only while a game with real HDR runs. **SDR looks right in HDR** draws the desktop with gamma 2.2 while HDR is on, so blacks stay black.

## Upscalers and Shaders

- **Upscalers** (Library > GPU > Upscalers, or a game's page) update DLSS, FSR and XeSS files a game already has. Close the game first. **Restore** puts the game's own files back. A game with anti-cheat changes only after you allow it on its page, at your own risk; Riot games are never changed. DLSS presets are part of Library > GPU > Profile, on an NVIDIA graphics card.
- **Shaders** (Library > GPU > Shaders) precompiles games' shaders. Press **Compile all ready**, or **Compile** on a game's page. It compiles again by itself after a graphics driver update, while your PC is idle. The **i** button shows its cache and version.

## Store

Browse, search and wishlist games. **Get on Steam** and the other buttons open the store's own app (or its page when the app isn't installed) to buy. Prices are in your Windows region's currency. Wishlisted games on sale show on the Store tab.

## Discord

The Discord room is your own Discord app. Sign in to Discord there. Exo runs Discord with Equicord and Exo's plugin set, which includes FakeNitro; Discord's terms forbid client mods, and you can turn any plugin off in Discord's settings under Equicord. Discord's own settings (text size, zoom, notifications, voice) are where you change Discord.

**Exo's own Discord client** (Settings > Music and Discord, off by default) shows your servers, DMs and messages in Exo's own room instead. Open the Discord room, then on your phone open Discord, go to **You > Scan QR code** and scan the code; confirm on the phone. Exo never sees your password. Click a voice channel to join it, or **Call** in a DM; **Mute**, **Deafen** and **Leave** sit above your name, and Settings > Music and Discord has **Noise suppression in calls**. Video and screen share stay in the Discord app for now. **Sign out** is under Settings > Music and Discord. Discord's terms don't allow other clients, so use it at your own risk.

## Music

Pick Spotify, Apple Music, YouTube Music or Cider in **Settings > Music and Discord**, then press **Sign in** in the Music room and sign in on the service's own page. Spotify needs its desktop app, which Exo can install for you. **Songs**, under Home, lists every song you saved. The mini player in the top bar controls playback from every room.

If the Music room stays empty or signed out, check that the service works in your browser, then sign in again from the Music room.

## Devices

Plug in a supported mouse or its receiver and open **Devices**. Only the settings your mouse has are shown; every change is written to the mouse and read back. Save a profile, link it to games, and it switches on by itself when one starts. If the mouse isn't found, wake it, check its battery and connection, then open Devices again.

## Your data

Everything Exo keeps is in `%LocalAppData%\ExoLauncher`. Exo has no account, ads, analytics or telemetry. The [privacy policy](../PRIVACY.md) lists every network request.

## Report a problem

Check [existing issues](https://github.com/ImAvgErix/ExoLauncher/issues) first, then [open a report](https://github.com/ImAvgErix/ExoLauncher/issues/new/choose). Include your Exo version (Settings > About) and, if you can, the log at `%LocalAppData%\ExoLauncher\logs\app.log`. It contains no passwords or keys.
