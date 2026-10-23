# Music engines

Exo's Music room is Exo's own player (`ui/src/components/music/`, one adapter per service in
`ui/src/lib/musicAdapters.ts`). Behind it, the chosen service has one hidden engine, chosen for what
the service can do there. It starts with Exo (the room is preloaded behind the splash); switching
services closes the previous engine. An idle engine is frozen (the desktop app) or suspended (the web
player), so waiting costs memory, not processor time, and after 15 idle minutes it is closed (a desktop
app only if Exo started it); opening Music starts it again (`MainWindow.Music.cs`).

| Service | Engine | Why |
|---|---|---|
| Spotify | The Spotify desktop app, hidden | Jam and Lossless exist only in Spotify's apps, not its web player. Exo reads and drives it through the app's own platform APIs (`Helpers/SpotifyNative.cs`), including Settings: audio quality (0 Automatic, 1 Low, 2 Normal, 3 High, 4 Very high, 5 Lossless), crossfade, gapless, AutoMix, normalization, mono, exclusive mode (`ExclusiveModeAPI`) and the equalizer (`EqualizerAPI.setFilterGain(band, dB)`, ±12 dB). |
| Apple Music | music.apple.com, hidden (`Helpers/AppleMusicNative.cs`, through the page's own MusicKit instance) | The Windows Apple Music app (AppleInc.AppleMusicWin, checked at 1.1540) is a native WinUI app on Apple's media stack with no reachable API for the library, queue or quality. Its link protocols (`music://`, `itmss://`, music.apple.com links) didn't even start it from outside, and it publishes no media session until something plays. So Exo can't drive it reliably, and the web engine stays: AAC at 256 kbps (High) or 64 kbps (Standard), no lossless or Dolby Atmos. Revisit if Apple adds automation to the Windows app. |
| YouTube Music | music.youtube.com, hidden (`Helpers/YouTubeMusicNative.cs`) | There is no desktop app and no lossless; the web page is the best engine. |

Shared on top, for every service: one queue, Now Playing, library views, the bar's mini player, and
Exo's own sleep timer (the player's Sound panel, `SoundPanel.tsx`).
