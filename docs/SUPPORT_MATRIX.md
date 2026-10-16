# Supported features

What Exo Launcher 3.5 does in each area, and what it depends on. Windows 11 x64.

| Area | What Exo does | Depends on, and limits |
|---|---|---|
| Steam | Owned and installed games, play time, achievements, install, update, remove and launch through Steam | The official Steam client, signed in; a Steam Web API key is optional |
| Epic / GOG / Amazon | Library, play time, achievements, install, update and launch | Legendary, gogdl and Nile, which Exo downloads at fixed, checksummed versions; your sign-in in the store's own page |
| Riot | Installed games anywhere, update notices, exact sizes, launch; tracker.gg hours with your Riot ID | The official Riot Client; Riot games are never modified |
| Xbox, EA app, Ubisoft Connect, Battle.net, Rockstar, itch.io, others | Find installed games and launch them; Xbox games use their real names, Store art and sizes | The store's own client where it needs one; no remote ownership catalog unless the store offers one |
| Local games | Add a folder or a game and launch it | A valid executable; the files stay yours |
| Store | Steam-ranked shelves, search, game pages with trailers, regional Steam prices, other official stores' prices, Epic free games, a wishlist with sale notices | Public storefront data; Get opens the store's own app or page to buy |
| Upscalers | Update DLSS, FSR and XeSS files a game already ships, with byte-checked restore; set NVIDIA's global DLSS presets | Files are signature-, version- and checksum-checked; the game must be closed; anti-cheat games only when you allow each one; never Riot games; presets need an NVIDIA GPU |
| Shaders | Exo Shaders precompiles games' shaders for your current graphics driver | Ships with Exo; DirectX 12 and Vulkan games it supports; recordings stay on this PC |
| Discord | Your installed Discord desktop app inside Exo, with Equicord and Exo's plugin set | Discord's own account; calls, video and screen share are Discord's; client mods are against Discord's terms |
| Music | Exo's own player for Spotify, Apple Music and YouTube Music, plus Cider hosted as is; mini player in the top bar | The service's own account and subscription; Spotify needs its desktop app, Apple Music and YouTube Music play through their web players |
| Devices | Exo Mouse: DPI, polling, lift-off, switches, lighting, profiles per game for mice OpenMouse supports, and the Finalmouse Starlight X | A supported mouse or receiver; no vendor software or driver needed |

Exo is verified on a real Windows 11 PC with its end-to-end script (`tools/e2e/Invoke-ExoE2E.ps1`) and on-screen checks. Stores, services and hardware Exo can't test on that PC are marked as checked in code only in the changelog.
