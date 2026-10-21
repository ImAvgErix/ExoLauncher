# Music engines

Exo's Music room is Exo's own player. Behind it, each service has one hidden engine, chosen for what
the service can do there, started with Exo (once the library is usable) and never reloaded while
Exo runs. An idle engine is frozen, so waiting costs memory, not processor time.

| Service | Engine | Why |
|---|---|---|
| Spotify | The Spotify desktop app, hidden | Jam and Lossless exist only in Spotify's apps, not its web player. Exo reads and drives it through the app's own platform APIs (`Helpers/SpotifyNative.cs`), including Settings: audio quality (0 Automatic, 1 Low, 2 Normal, 3 High, 4 Very high, 5 Lossless, read from Spotify's own Settings dropdown), crossfade, gapless, AutoMix, normalization, mono, exclusive mode (`ExclusiveModeAPI`) and the six-band equalizer (`EqualizerAPI.setFilterGain(bandKey, dB)`). |
| Apple Music | music.apple.com, hidden (`Helpers/AppleMusicNative.cs`) | The Windows Apple Music app (AppleInc.AppleMusicWin, checked at 1.1540) is a native WinUI app on Apple's media stack with no reachable API for the library, queue or quality. Its link protocols (`music://`, `itmss://`, music.apple.com links) didn't even start it from outside, and it publishes no media session until something plays. So Exo can't drive it reliably, and the web engine stays: standard-quality AAC, no lossless. Revisit if Apple adds automation to the Windows app. |
| YouTube Music | music.youtube.com, hidden (`Helpers/YouTubeMusicNative.cs`) | There is no desktop app and no lossless; the web page is the best engine. |

Shared on top, for every service: one queue, Now Playing, library views, the bar's mini player, and
Exo's own sleep timer (Sound panel).
