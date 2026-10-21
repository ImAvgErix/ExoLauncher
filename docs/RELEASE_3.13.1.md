# Exo Launcher 3.13.1

_October 21, 2026_

This update fixes calls with noise suppression and makes Go Live work on more graphics cards.

- **Noise suppression never silences a call.** With Noise Suppression for Others on, you heard no one and your own voice stopped going out after a minute, and stayed off until Exo restarted. Every voice cleaned at once shared one copy of DeepFilterNet's model, and they wrote over each other. Now each voice gets its own copy, so your microphone, each person you hear, and Voice & Video's meter run side by side. Three voices cleaned at once now come out exactly as each does alone. _Checked in code only._
- **Go Live on more graphics cards.** When a graphics card's video encoder refuses a stream's settings, Exo Capture tries again at 60, then 30 fps, then at 1080p, instead of failing. When it can't start at all, Exo now says why (the window, the screen, or your graphics card's encoder) instead of always blaming Windows. _Checked in code only._
- **Streams play on any graphics card.** A stream that a graphics card can't decode now plays through software decoding instead of staying black. _Checked in code only._
- **A clearer streaming icon.** Share Your Screen, LIVE in voice and friends who are streaming now show a screen with an arrow going up. _Checked in code only._

[Download ExoLauncher-Setup.exe](https://github.com/ImAvgErix/ExoLauncher/releases/download/v3.13.1/ExoLauncher-Setup.exe) · [Full changelog](https://github.com/ImAvgErix/ExoLauncher/blob/main/CHANGELOG.md) · [Privacy policy](https://github.com/ImAvgErix/ExoLauncher/blob/main/PRIVACY.md)
