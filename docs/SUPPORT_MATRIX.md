# Supported features

What Exo Launcher 3.10 does in each area, and what it depends on. Windows 11 x64.

| Area | What Exo does | Depends on, and limits |
|---|---|---|
| Steam | Owned, installed and Steam Family games, play time, achievements, install, update, remove and launch through Steam; Properties (launch options, language, cloud saves, betas, updates, move, DLC on or off); switching between the accounts Steam remembers | The official Steam client, signed in; a Steam Web API key is optional, and without it uninstalled family games read as owned |
| Epic / GOG / Amazon | Library, play time, achievements, install, update and launch; Properties (launch options, repair, Epic cloud saves and optional content) | Legendary, gogdl and Nile, which Exo downloads at fixed, checksummed versions; your sign-in in the store's own page |
| Riot | Installed games anywhere, update notices, exact sizes, launch; tracker.gg hours with your Riot ID; switching between saved Riot accounts | The official Riot Client; Riot games are never modified |
| Xbox, EA app, Ubisoft Connect, Battle.net, Rockstar, itch.io, others | Find installed games and launch them; Xbox games use their real names, Store art and sizes | The store's own client where it needs one; no remote ownership catalog unless the store offers one |
| Local games | Add a folder or a game and launch it | A valid executable; the files stay yours |
| Store | Steam-ranked shelves, search, game pages with trailers, regional Steam prices, other official stores' prices, Epic free games, a wishlist with sale notices | Public storefront data; Get opens the store's own app or page to buy |
| GPU | Profile: Exo's NVIDIA driver profile for the lowest latency, and the newest Game Ready driver installed clean and driver-only. Display: refresh rate, output color, scaling and its override, brightness to hue, RTX Video. HDR: on only while an HDR game runs, with SDR drawn at gamma 2.2 while HDR is on | Profile, driver and most of Display need an NVIDIA GPU (AMD and Intel get refresh rate, output color and color sliders where their drivers allow); the driver comes from NVIDIA and is checked for NVIDIA's signature; HDR needs an HDR screen |
| Upscalers | Update DLSS, FSR and XeSS files a game already ships, with byte-checked restore | Files are signature-, version- and checksum-checked; the game must be closed; anti-cheat games only when you allow each one; never Riot games; presets need an NVIDIA GPU |
| Shaders | Exo Shaders precompiles games' shaders for your current graphics driver | Ships with Exo; DirectX 12 and Vulkan games it supports; recordings stay on this PC |
| Discord | Exo's own Discord client: servers, DMs, messages, files, emoji with favorites, reactions, notifications, moderation and calls with noise suppression, camera and Go Live | Discord's own account; other clients are against Discord's terms; it signs in by QR code from Discord's phone app |
| Music | Exo's own player for Spotify, Apple Music and YouTube Music; mini player in the top bar; Spotify Jam and Spotify links from Discord | The service's own account and subscription; Spotify needs its desktop app, Apple Music and YouTube Music play through their web players |
| Devices | Exo Mouse: DPI, polling, lift-off, switches, lighting, profiles per game for mice OpenMouse supports, and the Finalmouse Starlight X | A supported mouse or receiver; no vendor software or driver needed |

Exo is verified on a real Windows 11 PC, on screen and with an automated end-to-end run of the installed app. Each changelog entry says how it was checked: Verified: on screen, Verified: end-to-end, or Checked in code only for stores, services and hardware that PC can't test.
