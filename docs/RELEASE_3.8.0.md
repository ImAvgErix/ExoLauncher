# Exo Launcher 3.8.0

_October 17, 2026_

This update adds Exo's own Discord client as a preview, NVIDIA's newest driver installed clean and driver only, NVIDIA Control Panel's display settings in GPU > Display, SDR that looks right while HDR is on, and a new opening that has everything of Exo's own ready before the library shows.

### Faster
- **A new opening.** Exo opens on black, its mark settles in at full size and its three bars sweep in one after another, then it holds while Exo loads Store, Devices, Settings, the GPU sheet with every tab and Exo Shaders' page, and the first screen of covers, and sinks back as the library comes up. It always plays in full, once, at your screen's own frame rate; no text or flicker before it, and no loading line in the top bar. Nothing of Exo's own builds on first open: the GPU sheet opens in a frame. Discord and music still start with their rooms. _Verified: on screen._
- **Exo Shaders is half the size.** Its engine and command-line tools share one copy of .NET instead of two: about 119 MB on disk from 234 MB. _Verified: end-to-end._
- **Exo Shaders is ready when you are.** It starts with Exo again, like Exo Capture and Exo Mouse, so its page opens at once and it sees every game you play. _Verified: on screen._

### Fixes
- **Exo's menus show over Discord and music apps.** The Capture menu (and every other menu) opened over Discord's or a music app's window was hidden behind it; that window now gives way under any open menu. _Verified: on screen._
- **Store pages open whole.** Games on screen in the Store are read ahead in the background, a few at a time, with their art and other stores, so a page opens with its tags, description and buttons already there. _Verified: on screen._

### Changes
- **The NVIDIA driver, driver only.** GPU > Profile shows your driver and NVIDIA's newest Game Ready driver for your card. Update (or Reinstall) downloads it from NVIDIA, checks NVIDIA's signature, and clean-installs the driver alone: no NVIDIA app, telemetry, ShadowPlay, PhysX or HDMI/DisplayPort audio. Exo puts your GPU profile and desktop color back afterwards and waits while a game runs. Your display settings (scaling, override, output color) stay as they were. _Verified: on screen._
- **Display has NVIDIA Control Panel's settings.** Output color is Default (the driver picks) or NVIDIA settings with format, depth and range; Scaling is Aspect ratio, Full-screen, No scaling or Integer scaling, done on the GPU or the display, with Override the scaling set by games; brightness, contrast, gamma, digital vibrance and hue are sliders (moving one hands color to NVIDIA, Restore hands it back); RTX Video Super Resolution (Off, Auto, Quality 1-4) and RTX Video HDR where the card has them. Each change to the picture still waits 15 seconds for you to keep it. The GPU sheet is larger and Display is in two columns, so nothing scrolls. _Verified: on screen._
- **Exo's own Discord client (preview, off by default).** Turn it on in Settings > Music and Discord and the Discord room becomes Exo's own client: your servers and DMs down the left, unread and mention marks, channels in Discord's order, and messages with formatting, emoji, images, videos, link previews and stickers. Send, reply, edit and delete your own messages; Exo marks what you read and shows who is typing. Sign in by scanning a QR code with Discord's phone app, so Exo never sees your password; the sign-in stays on this PC, encrypted for your Windows account, and Sign out ends it. Only channels your roles let you see are shown, and nothing is saved. Calls, video and screen share stay in the Discord app for now. Discord's terms don't allow other clients, so it's at your own risk. _Checked in code only._
- **SDR looks right in HDR.** While a screen is in HDR, Exo gives it a color profile that draws the desktop, overlays and SDR windows with gamma 2.2 like an SDR screen, so blacks stay black instead of gray; HDR content above SDR white is untouched. The screen's own profile comes back when HDR goes off. On by default in GPU > HDR. _Checked in code only._
- **One kind of control.** Every setting with choices is a dropdown, however many it has; on and off is a switch, an amount is a slider. _Verified: on screen._
- **Everything on one line.** Settings' section headings and their buttons line up with the rows under them, and every row is the same height. _Verified: on screen._
- **About lights up** in Settings' list when you reach the bottom. _Verified: on screen._
- **Exo Shaders' info window opens whole,** never on a spinner first. _Verified: on screen._
- **No scrollbars anywhere,** including the GPU and Captures sheets, where one flashed while switching tabs. Everything still scrolls. _Checked in code only._
- **Exo Shaders' cache and about** are behind an "i" button in its toolbar, in one window; the gray line at the bottom of the page is gone. _Verified: on screen._
- **"Color"** everywhere, in US spelling. _Checked in code only._
- **Time played is just the time.** No "From tracker.gg" or other source note under it. _Checked in code only._

[Download ExoLauncher-Setup.exe](https://github.com/ImAvgErix/ExoLauncher/releases/download/v3.8.0/ExoLauncher-Setup.exe) · [Full changelog](https://github.com/ImAvgErix/ExoLauncher/blob/main/CHANGELOG.md) · [Privacy policy](https://github.com/ImAvgErix/ExoLauncher/blob/main/PRIVACY.md)
