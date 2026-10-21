# Exo Launcher — domain

A Windows 11 library UI. Steam, Epic, GOG, Riot, and other official clients stay where the vendor installed them. Exo commands those clients and hides their chrome. It is not a DRM, ownership, or anti-cheat bypass.

## Glossary

| Term | Meaning |
| --- | --- |
| **Exo Launcher** | This app. React 19 + Exo's own icon set with native controls in a WinUI 3 / WebView2 host (`ui/src/`). Rooms: Library, Friends, Music, Devices; Settings is the gear. GOG login may still use a hidden WebView2 profile. |
| **Now banner** | Compact active-state dock for downloading, playing, or updating. |
| **Library** | Installed (and owned-uninstalled) titles shown as covers. Details open as an overlay. |
| **Store backend** | The official client Exo detects and commands. Trees are not copied into Exo. |
| **Steam IPC** | `ExoLauncher.SteamIpc` via `steamclient64.dll`. Install/update/uninstall proof is ACF `BytesDownloaded`, not Steam chrome. |
| **Legendary** | Epic agent. |
| **gogdl** | GOG agent. |
| **Local debug build** | `Run-ExoLauncher.ps1` → `ExoLauncher\bin\x64\Debug\…\ExoLauncher.exe`. |
| **Shipped install** | `%LOCALAPPDATA%\ExoLauncher\app\ExoLauncher.exe`. After host or UI changes, publish here via `app.incoming-dev` swap. |
| **Upscaler swap** | Optional, user-requested replace of DLSS / FSR / XeSS files the game already ships, with official files pinned by SHA-256. Requires a first-shipped `.dlsss` backup. Never in games with anti-cheat (store, title or marker files) unless the user allowed it for that game after a ban-risk confirmation (settings `upscalerAntiCheatConsent`, tied to the anti-cheat named); never in Riot games or with Riot Vanguard. No OptiScaler inject. Not applied on Play. |
| **Remove** | Uninstall. Two clicks in the UI. |
| **Friends** | Exo's own Discord client (`discord.*`, `Services/DiscordClient`): Discord's servers, DMs, messages and calls drawn by Exo. Discord owns the account and data; Exo has no roster, profile or social service of its own. The Discord desktop app is not hosted. |
| **Music** | The selected provider's official client. Spotify: Exo's own room (`spotify.*`) over the official desktop app running hidden as the audio engine, driven through the platform APIs and GraphQL its own UI uses; Exo never handles audio, tokens or entitlements. Apple Music: Exo's own room (`applemusic.*`) over Apple's web player (music.apple.com) running hidden in WebView2, driven through the MusicKit instance the page itself sets up. YouTube Music the same way (`ytmusic.*`). The mini player's song and transport come from Windows media sessions; its volume is the app's own (Spotify, YouTube Music, Apple Music). No native MusicKit or Spotify SDK; Apple Music uses only the MusicKit JS Apple's own page runs. |
| **Exo app** | Developer credentials Exo registers with Twitch (IGDB). Users only Sign in / Connect. Paste fields exist on Debug (or `EXO_SHOW_DEV_KEYS`), not the shipped Settings. |

Retired and deleted, not to be revived: the Optimize room and `optimize.*` RPCs; the Exo roster/Profile rooms and `friends.*`/`profile.*`/`online.*` social RPCs; the Exo account (`account.*`, `online.health`, `online.account.*`, the exo-id service, sign-in, handles, badges, the onboarding account step); the Discord Social SDK; the hosted Discord desktop app (its install, update repair, Equicord patch, `friends.setBounds`/`status`/`install`/`open` and the `discordNative` setting); native MusicKit/Spotify SDK players; the embedded Spotify web player, its page-script transport, and the Exo Jam sheet (Spotify's desktop app has Jams natively); keyboard, headset and gamepad device support; the native mirror UI models under `ExoLauncher/Ui`.

## Avoid

- Treating Exo as a DRM or ownership bypass. Exo has no storefront; Buy links open the vendor's own store page.
- “DepotDownloader”, nested Steam, OCR/SendInput as the Steam install path.
- Killing `vgk` / `vgc` / Vanguard / EAC / BattlEye.
- Product names other than Exo Launcher, Exo OS, and Exo Browser.
- Reviving Optimize or shipping it as a separate Exo Hub exe.
