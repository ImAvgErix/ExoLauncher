# Exo Launcher — domain

A Windows 11 library UI. Steam, Epic, GOG, Riot, and other official clients stay where the vendor installed them. Exo commands those clients and hides their chrome. It is not a DRM, ownership, or anti-cheat bypass.

## Glossary

| Term | Meaning |
| --- | --- |
| **Exo Launcher** | This app. WinUI 3 host + React UI in WebView2. GOG login also uses WebView2. |
| **Now banner** | The featured title on home: downloading, playing, updating, or last launched. |
| **Library** | Installed (and owned-uninstalled) titles shown as covers. Details open as an overlay. |
| **Store backend** | The official client Exo detects and commands. Trees are not copied into Exo. |
| **Steam IPC** | `ExoLauncher.SteamIpc` via `steamclient64.dll`. Install/update/uninstall proof is ACF `BytesDownloaded`, not Steam chrome. |
| **Legendary** | Epic agent. |
| **gogdl** | GOG agent. |
| **Local debug build** | `Run-ExoLauncher.ps1` → `ExoLauncher\bin\x64\Debug\…\ExoLauncher.exe`. |
| **Shipped install** | `%LOCALAPPDATA%\ExoLauncher\app\ExoLauncher.exe`. Do not write here unless shipping. |
| **Upscaler swap** | Optional, user-requested replace of DLSS / FSR / XeSS files the game already ships. Refused on anti-cheat titles. Originals stay as `.exo-bak`. |
| **Remove** | Uninstall. Two clicks in the UI. |

## Avoid

- Calling Exo a store, a Steam replacement, or a bypass.
- “DepotDownloader”, nested Steam, OCR/SendInput as the Steam install path.
- Killing `vgk` / `vgc` / Vanguard / EAC / BattlEye.
- Product names other than Exo, Exo OS, Exo Launcher, Exo Control.
