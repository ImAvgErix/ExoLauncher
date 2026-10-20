# Exo Launcher 3.12.0

_October 20, 2026_

This update gives Exo a new installer and a new first-run setup in Exo's own look, and lets Exo clear out the apps it replaces.

### Install and setup
- **A new installer in Exo's look.** Install, Update and Uninstall open in one small window with Exo's buttons, switch, colors and motion, and Exo's own minimize and close. Options holds the desktop shortcut, and a bar shows real progress while Exo copies. _Verified: on screen._
- **The installer cleans up after itself.** Every install and update removes copies left by earlier updates, old logs and downloaded installers, so only the app remains. _Verified: end-to-end._
- **A new first-run setup.** It opens with a welcome, then walks through game stores, music, Discord, captures and this PC in Settings' own rows and buttons, each step sliding in from the side it comes from. A step with nothing to set up on your PC is left out. _Verified: on screen._
- **Captures in setup.** The keys for saving a replay, recording and snipping, and Print Screen, ready to change before your first game. _Verified: on screen._

### Clean up
- **Remove the apps Exo replaces.** Setup and Settings > Clean up list Discord, Snipping Tool, Photos, Xbox Game Bar, Clipchamp and the NVIDIA App when your PC has them. Remove runs the app's own uninstaller, then deletes every folder, registry key, startup entry and shortcut it leaves, as Revo Uninstaller does. Anything in Program Files asks for Windows' permission once. Spotify, the Xbox app and the NVIDIA driver are never offered: Exo needs them. _Checked in code only._
- **After a removal, nothing nags.** Without Snipping Tool, Print Screen opens Exo's snip; without Game Bar, Windows stops asking to install it again. _Checked in code only._
- **Leftovers cleared for you.** What apps you already uninstalled left in your folders is cleared when setup or Settings > Clean up opens. _Verified: end-to-end._

[Download ExoLauncher-Setup.exe](https://github.com/ImAvgErix/ExoLauncher/releases/download/v3.12.0/ExoLauncher-Setup.exe) · [Full changelog](https://github.com/ImAvgErix/ExoLauncher/blob/main/CHANGELOG.md) · [Privacy policy](https://github.com/ImAvgErix/ExoLauncher/blob/main/PRIVACY.md)
