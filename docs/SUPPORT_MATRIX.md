# Supported features

The active product is WinUI 3 plus the React/WebView2 shell. Build with the exact SDK in `global.json` and Node 24; CI installs both. `VERSION` supplies the app version, and the private UI package mirrors it.

| Area | Shipped behavior | Prerequisites and limits |
|---|---|---|
| Steam | Library, launch and supported install/update/remove actions | Official Steam client, ownership and native helper |
| Epic / GOG | Library and provider-supported downloads/actions | Authenticated Legendary / gogdl; provider failures remain visible |
| Riot | Detected games and supported patch/launch path | Official account/client and patch service |
| Other stores | Detect and launch proven local installations | No claim of a full remote ownership catalog |
| Local games | Add folders and launch | A valid local executable; game files remain under user control |
| Upscalers | Read-only detection; explicit swap/restore with backups | Existing supported DLL, stopped game, valid destination/signature; no title or anti-cheat certification |
| Friends | Embedded Discord website | Discord login and service availability; messaging and calls are provided by Discord |
| Music | Embedded selected provider, mini controls and hidden playback | Provider login/subscription where required; paused hidden views suspend after an idle delay |
| Wishlist | Import/export and minimal metadata saved on this PC | Shared across local account switches; not cloud synchronization or live regional pricing |

Packaged smoke runs use an owned disposable data directory and no store adapters beyond Local. CI runs native fixture tests, UI behavior tests, production UI build, Release publish and packaged navigation. Real vendor login/downloads, full accessibility certification and live self-update still require their corresponding test environments.
