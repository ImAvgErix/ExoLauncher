# Exo Launcher — domain

A Windows 11 launcher. Steam, Epic, GOG, Riot and the other official clients stay where the vendor installed them; Exo commands them and hides their chrome. It is not a DRM, ownership or anti-cheat bypass.

## Glossary

| Term | Meaning |
| --- | --- |
| **Exo Launcher** | This app: React 19 with Exo's own controls and Tabler icons in a WinUI 3 / WebView2 host (`ui/src/`). Rooms: Library, Store, Discord, Music, System, Devices; Settings is the gear. GOG and Epic sign-in use a short-lived WebView2 window of their own (`Services/StoreAuthWindow`). |
| **Appearance** | Light, Dark or Void (AMOLED black), or Match Windows (Light or Dark, as Windows' apps are). `<html data-exo-theme>`; DESIGN.md › Tokens. |
| **Exo Island** | The Bar's live capsule left of the room tabs: a Discord call outside the Discord room. Nothing live, nothing drawn. |
| **Library** | Installed and owned-not-installed titles as covers, with Continue, Friends are playing and folders. Details open as a sheet over it. |
| **Store backend** | The official client Exo detects and commands. Its files are never copied into Exo. |
| **Steam IPC** | `ExoLauncher.SteamIpc` via `steamclient64.dll`. Install/update/uninstall proof is ACF `BytesDownloaded`, not Steam chrome. |
| **Legendary** | Epic agent. |
| **gogdl** | GOG agent. |
| **Local debug build** | `Run-ExoLauncher.ps1` → `ExoLauncher\bin\x64\Debug\…\ExoLauncher.exe`. |
| **Shipped install** | `%LOCALAPPDATA%\ExoLauncher\app\ExoLauncher.exe`. After host or UI changes, publish and swap it in with `tools\swap-dev.ps1` (AGENTS.md › Ship). |
| **Upscaler swap** | User-requested replacement of DLSS / FSR / XeSS files the game already ships, with official files pinned by SHA-256, after a first-shipped `.dlsss` backup. Never in games with anti-cheat (store, title or marker files) unless the user allowed it for that game after a ban-risk confirmation (settings `upscalerAntiCheatConsent`, tied to the anti-cheat named); never in Riot games or with Riot Vanguard. No OptiScaler inject. Not applied on Play. |
| **Uninstall / Remove** | Uninstall removes a store game through its store; Remove takes a game added from a folder or .exe out of the Library and leaves its files. Two clicks (the button, then Confirm). |
| **Discord** | Exo's own Discord client (`discord.*`, `Services/DiscordClient`; the room's id is still `friends`): Discord's servers, DMs, messages and calls drawn by Exo. Discord owns the account and data; Exo has no roster, profile or social service of its own. The Discord desktop app is not hosted. |
| **Music** | Exo's own player over the selected service, which runs hidden as the engine and is driven through its own APIs: Spotify's desktop app (`spotify.*`, the platform APIs and GraphQL its own UI uses), Apple Music's web player (`applemusic.*`, the MusicKit instance music.apple.com sets up), YouTube Music's web player (`ytmusic.*`). Exo never handles audio, tokens or entitlements. The mini player's song comes from the service's page (Spotify falls back to its Windows media session) and its volume is the app's own. No native MusicKit or Spotify SDK. |
| **Exo app** | Developer credentials Exo registers with Twitch (IGDB). Users only Sign in / Connect. Paste fields show in Debug (or with `EXO_SHOW_DEV_KEYS`), not in the shipped Settings. |

Retired and deleted, not to be revived: the Optimize room and `optimize.*` RPCs; the Exo roster/Profile rooms and `friends.*`/`profile.*`/`online.*` social RPCs (`friends.badge` is the Discord room's unread count); the Exo account (`account.*`, `online.health`, `online.account.*`, the exo-id service, sign-in, handles, badges, the onboarding account step); the Discord Social SDK; the hosted Discord desktop app (its install, update repair, Equicord patch, `friends.setBounds`/`status`/`install`/`open` and the `discordNative` setting); native MusicKit/Spotify SDK players; the embedded Spotify web player, its page-script transport and the Exo Jam sheet; keyboard, headset and gamepad device support; the native mirror UI models under `ExoLauncher/Ui`.

## Avoid

- Treating Exo as a DRM or ownership bypass. Exo sells nothing: the Store room's buttons open the vendor's own store.
- "DepotDownloader", nested Steam, OCR/SendInput as the Steam install path.
- Killing `vgk` / `vgc` / Vanguard / EAC / BattlEye.
- Product names other than Exo Launcher, Exo OS and Exo Browser (and Exo's own add-ons: Exo Capture, Exo Mouse, Exo Shaders).
- Reviving Optimize or shipping it as a separate Exo Hub exe.
