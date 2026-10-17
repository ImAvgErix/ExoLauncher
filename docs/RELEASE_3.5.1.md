# Exo Launcher 3.5.1

_October 17, 2026_

This update closes the holes a security review of Exo found, keeps Exo further away from anti-cheat, and makes text sizes and hover effects match across the app.

### Security and privacy
- **Spotify can only be reached by Exo.** Exo used to talk to the Spotify it hosts over a debug port on your PC, which another program running as you could find and use to drive your signed-in Spotify. Exo now talks to it over a private connection only Exo holds; there is no port to find. _Verified: on screen._
- **The mouse page stays in its lane.** Exo Mouse, which runs inside Devices, can reach only its own mouse, profile and game-list calls. A message it sends straight to Exo is now refused instead of reaching the rest of Exo. _Checked in code only._
- **Logs hide secrets that carry no label.** Exo's log already hid tokens and passwords written next to a label; it now also hides sign-in tokens written on their own, while download checksums, links and file paths stay readable for support. _Checked in code only._
- **Every attack from the review is kept as a test.** Dangerous links, requests aimed at your PC or network, paths that try to escape a folder, tampered updates and corrupt files are all refused, and each is now a test that runs on every change. _Checked in code only._

### Games
- **More anti-cheat Exo never closes.** When Exo has to stop a game, it now leaves FACEIT, PunkBuster, XIGNCODE3, GameGuard, ACE, EA's anti-cheat, Riot's Vanguard tray and every game's own BattlEye or Easy Anti-Cheat launcher running, in the whole game process tree, not only the top process. A close it refuses is no longer counted as done. _Checked in code only._
- **A broken upscaler list can't get in the way.** If the list of newer DLSS, FSR and XeSS versions arrives damaged, Exo keeps the versions it ships with. _Checked in code only._

### Look and feel
- **One set of text sizes.** Settings, game pages, Library shelves, first run, Upscalers and the Activate key popover now use Exo's three text sizes, so nothing stands a size apart. _Verified: on screen._
- **Hovers never fade.** The music track list and the command menu's Clear search now light up with Exo's grey hover instead of turning see-through, like every other control. _Verified: on screen._
- **DLSS preset names fit.** Ray Reconstruction presets D and F read on one line. _Verified: on screen._

[Download ExoLauncher-Setup.exe](https://github.com/ImAvgErix/ExoLauncher/releases/download/v3.5.1/ExoLauncher-Setup.exe) · [Full changelog](https://github.com/ImAvgErix/ExoLauncher/blob/main/CHANGELOG.md) · [Privacy policy](https://github.com/ImAvgErix/ExoLauncher/blob/main/PRIVACY.md)
