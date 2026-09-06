# Supported features

The 3.0 installer packages the selected restored WinUI 3 and React/WebView2 app build. The application payload retains its original internal 2.2.0 version label.

| Area | Shipped behavior | Prerequisites and limits |
|---|---|---|
| Steam | Library, launch and supported install/update/remove actions | Official Steam client, ownership and native helper |
| Epic / GOG | Library and provider-supported downloads/actions | Authenticated Legendary / gogdl; provider failures remain visible |
| Riot | Detected games and supported patch/launch path | Official account/client and patch service |
| Other stores | Detect and launch proven local installations | No claim of a full remote ownership catalog |
| Local games | Add folders and launch | A valid local executable; game files remain under user control |
| Upscalers | Detection and explicit swap/restore with backups | Existing supported DLL, stopped game, valid destination/signature; no title or anti-cheat certification |
| Devices | Supported mouse detection, settings and profiles | Supported HID device/receiver; acknowledged writes only; this is not a general peripheral manager |
| Friends | Embedded Discord website | Discord login and service availability; messaging and calls are provided by Discord |
| Music | Embedded selected provider, mini controls and hidden playback | Provider login/subscription where required |
| Exo account | Optional password sign-in and profile | Deployed Worker; hosted email recovery/verification, magic links and Google sign-in are not currently enabled |
| Wishlist | Wishlist saved on this PC | Shared across local account switches; not cloud synchronization or live regional pricing |

Store integrations, music, Discord and mouse functionality depend on their respective services and supported hardware.
