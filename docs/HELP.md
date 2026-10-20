# Getting started and help

## Install and update

1. Download **ExoLauncher-Setup.exe** from the [latest release](https://github.com/ImAvgErix/ExoLauncher/releases/latest).
2. Run it, choose Install, then Launch Exo. Setup asks for your game stores, your music service, Discord, your capture keys, Lucid and your PC's graphics settings, then offers to remove the apps Exo replaces (Settings > Clean up has the same list later).
3. Exo finds your games. Updates download in the background and install when you choose; your settings, library and sign-ins stay.

Exo needs **Windows 11 x64** and installs for your Windows user only. The installer isn't code-signed yet, so SmartScreen may warn the first time: choose **More info**, then **Run anyway**. Each release lists the installer's SHA-256.

To uninstall, open **Windows Settings > Apps > Installed apps**, choose **Exo Launcher**, then **Uninstall**. Your games, settings and accounts stay on your PC.

## Library

- **A game is missing.** Press the round **Refresh** button next to **+**. Exo reads every store again. For Epic, GOG or Amazon, sign in under **Settings > Game stores**. For a game outside any store, press **+** and pick its folder or executable.
- **Play, install and update.** Double-click an installed game, or open its page. Updates and downloads show in **Downloads & updates** at the bottom of the Library. Downloads can pause while you play and wait for the hours you set in **Settings > Downloads**.
- **Covers.** Replace cover on a game's page uses your own picture; Refresh art fetches the store's again.
- **Properties.** On an installed game's page, or right-click its card: launch options, language, cloud saves, betas, updates, moving it to another drive, and its DLC or optional content (for example a high-resolution texture pack) on or off. Anything that downloads or deletes shows its size and asks first.
- **Steam Family.** Games your Steam Family shares show in the Library next to your own and install through Steam.
- **More than one account.** **Settings > Game stores** lists the Steam accounts Steam remembers and the Riot accounts Exo saved: **Switch** restarts that store's client as the other account, and **Add account** opens its own sign-in.

## GPU

The System room holds your graphics card's settings, in Graphics, Display and Shaders.
- **Profile** applies Exo's NVIDIA driver profile (lowest latency, G-SYNC full screen only) and shows your driver. **Update** installs NVIDIA's newest Game Ready driver clean and driver-only, with no NVIDIA app, PhysX or HDMI audio; Exo puts your profile and color back after. It waits while a game runs.
- **Display** has each screen's refresh rate, output color, scaling (with **Override the scaling set by games**), brightness to hue, and RTX Video. A change to the picture comes back by itself after 15 seconds unless you keep it.
- **HDR** turns HDR on only while a game with real HDR runs. **SDR looks right in HDR** draws the desktop with gamma 2.2 while HDR is on, so blacks stay black.

## Upscalers and Shaders

- **Upscalers** (System > Graphics; a game's page shows where it stands and opens it) update DLSS, FSR and XeSS files a game already has. Close the game first. **Restore** puts the game's own files back. A game with anti-cheat changes only after you allow it in its Properties, at your own risk; Riot games are never changed. DLSS presets are part of System > Graphics, on an NVIDIA graphics card.
- **Shaders** (System > Shaders) precompiles games' shaders. Press **Compile all ready**; a game's Shaders row opens it here. It compiles again by itself after a graphics driver update, while your PC is idle. The **i** button shows its cache and version.

## Store

Browse, search and wishlist games. **Get on Steam** and the other buttons open the store's own app (or its page when the app isn't installed) to buy. Prices are in your Windows region's currency. Wishlisted games on sale show on the Store tab.

## Discord

The Discord room is **Exo's own Discord client**: your servers, DMs and messages in Exo's own room. Exo no longer runs the Discord desktop app inside it. Open the Discord room, then on your phone open Discord, go to **You > Scan QR code** and scan the code; confirm on the phone. Exo never sees your password. Click a voice channel to join it, or **Call** in a DM; **Mute**, **Deafen** and **Leave** sit above your name. Share Your Screen in a call opens Exo's own picker (an app or a screen, with its sound), and Turn On Camera shares your camera. Discord's own User Settings open from the gear in your Discord bar, laid out as in Discord: your profile and account, privacy and who can message you, notifications and sounds, Voice & Video (input profile, Push to Talk, sensitivity, echo cancellation, attenuation, camera), Appearance, Accessibility, keybinds, language, Activity Privacy, connections and authorized apps, Developer Mode and **Log Out**. Settings that belong to your Discord account change it everywhere you use Discord. Right-click a person for their menu (with Timeout, Kick, Ban and Roles where you have the permission), a message for its menu, or a picture to copy or save it. Right-click an emoji in the picker to add it to your favorites. Notifications follow your Discord notification settings and go away when you read the chat, here or in any Discord app. Discord's terms don't allow other clients, so use it at your own risk.

## Music

Pick Spotify, Apple Music or YouTube Music in **Settings > Music**, then press **Sign in** in the Music room and sign in on the service's own page. Spotify needs its desktop app, which Exo can install for you. **Songs**, under Home, lists every song you saved. A Spotify link someone sends you in Discord opens here, and pasting a Jam link into **Jam** joins it. The mini player in the top bar controls playback from every room.

If the Music room stays empty or signed out, check that the service works in your browser, then sign in again from the Music room.

## Devices

Plug in a supported mouse or its receiver and open **Devices**. Only the settings your mouse has are shown; every change is written to the mouse and read back. Save a profile, link it to games, and it switches on by itself when one starts. If the mouse isn't found, wake it, check its battery and connection, then open Devices again.

## Your data

Everything Exo keeps is in `%LocalAppData%\ExoLauncher`. Exo has no account, ads, analytics or telemetry. The [privacy policy](../PRIVACY.md) lists every network request.

## Report a problem

Check [existing issues](https://github.com/ImAvgErix/ExoLauncher/issues) first, then [open a report](https://github.com/ImAvgErix/ExoLauncher/issues/new/choose). Include your Exo version (Settings > About) and, if you can, the log at `%LocalAppData%\ExoLauncher\logs\app.log`. It contains no passwords or keys.
