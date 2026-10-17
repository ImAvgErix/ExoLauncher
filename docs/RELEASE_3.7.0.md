# Exo Launcher 3.7.0

_October 17, 2026_

This update adds the GPU room with Exo's own NVIDIA profile for your card, HDR only for games that really use it, a lighter Exo that starts nothing it doesn't need, and Inter as Exo's typeface.

### GPU
- **One GPU button.** Library's Upscalers button is now GPU, with Profile, Display, HDR, Upscalers and Shaders in one place. Settings > Graphics is gone. _Verified: on screen._
- **Exo's profile for your card.** Use Exo's profile writes the best NVIDIA settings for your card's series (GTX 10 and 16, RTX 20 to 50, and workstation cards by generation) for the lowest latency and most performance: maximum performance power, low latency on, High performance texture filtering, the newest DLSS files for every game, and the best DLSS model for your card (Recommended on RTX 40 and 50, K on RTX 20 and 30, frame generation C on all). Pick G-SYNC or No G-SYNC; with G-SYNC the frame cap follows your refresh rate, even when it changes. Frame generation switches stay yours. Undo puts your old settings back. _Checked in code only._
- **Import a Profile Inspector file** from the Profile tab, as before. _Checked in code only._
- **Display shows only what your screen takes.** Colour format, depth and range offered only where the screen supports them, the best one picked for you, and every change kept only if you confirm within 15 seconds. NVIDIA colour settings (brightness, contrast, gamma, digital vibrance and hue) sit beside them. _Verified: on screen._
- **Exo Shaders on one page,** with its shader cache and queue in their own dialogs. _Verified: end-to-end._

### HDR
- **HDR only for games that use it.** Exo checks your screen first: how bright it really gets and whether Windows HDR is calibrated. Then it turns HDR on only while a game that supports HDR runs (from PCGamingWiki), and puts your desktop back after. Each game page has Auto, Always or Never. _Checked in code only._

### Capture
- **Snips open the editor** straight away, and you can delete captures from the grid. _Checked in code only._
- **Game, Discord voice and microphone are always kept on their own tracks,** with the mix as the first track, so any player plays it. _Checked in code only._
- **Smarter encoder choice** on NVIDIA, AMD and Intel cards, and HDR snips stay HDR. _Checked in code only._

### Lighter
- **Nothing starts with Exo just to be ready.** Steam is no longer started to check what you own: games Exo confirmed before stay yours, and Steam starts when you play, install, update, verify or switch accounts. Discord starts when you open its room, Music when you open Music, and Exo Shaders when you open its page or a driver update leaves games to rebuild. A Discord or music app you already have open is still picked up at once. On this PC, with Steam closed, Exo and what it starts went from about 1.4 GB of memory at rest to about 0.6 GB. Discord, Music and Exo Shaders _Verified: on screen._ Steam staying closed _Checked in code only._
- **Idle music lets go.** A player that has slept for 15 minutes with nothing playing is closed (the web player, or a music app Exo started; never one you opened), and opens again with Music. Exo Shaders stops once it has nothing to do and its page is closed. _Checked in code only._
- **Trailers stop off screen.** A Store trailer stops and lets go of its video when you leave the Store or minimize Exo, and starts again when you come back. _Checked in code only._
- **Smaller and quieter.** The installer is about a quarter smaller (64.6 MB from 87.6 MB), Exo uses about 30 MB less memory sitting on Library, and it does about a quarter of the background work while idle: one check for games started outside Exo instead of three every five seconds. _Verified: end-to-end._

### Look
- **Inter everywhere.** Exo, Exo Mouse and Exo Capture's windows use Inter in place of Geist. _Verified: on screen._

### Changes
- **tracker.gg is only for Riot games.** Rainbow Six Siege and Destiny 2 hours come from Steam. _Checked in code only._
- **Optimize is always on.** The Optimize rows are gone; the fixes they applied stay applied. _Verified: end-to-end._

[Download ExoLauncher-Setup.exe](https://github.com/ImAvgErix/ExoLauncher/releases/download/v3.7.0/ExoLauncher-Setup.exe) · [Full changelog](https://github.com/ImAvgErix/ExoLauncher/blob/main/CHANGELOG.md) · [Privacy policy](https://github.com/ImAvgErix/ExoLauncher/blob/main/PRIVACY.md)
