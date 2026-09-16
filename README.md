<p align="center"><img src="docs/media/logo.png" alt="Exo" width="88"></p>
<h1 align="center">Exo Launcher 3.2.6</h1>
<p align="center"><strong>Your games. Your music. Your space.</strong></p>
<p align="center">A home for your PC gaming life. Bring your libraries together, keep Discord close, and take your music from browsing to playing.</p>
<p align="center"><a href="https://github.com/ImAvgErix/ExoLauncher/releases/latest/download/ExoLauncher-Setup.exe"><strong>Download for Windows 11</strong></a> · <a href="https://github.com/ImAvgErix/ExoLauncher/releases/tag/v3.2.6">3.2.6 release notes</a></p>

![Exo Launcher library](docs/media/home.jpg)

## Meet your next launcher

**Your whole session in one place.** A focused library, a full music room, Discord, and supported mouse profiles share one desktop app. The interface stays compact; your games get the space.

- **One library, across stores.** Find installed games, pin favorites, search, and open a game for its available Play, Install, or Update action. Steam, Epic, GOG, Riot, and portable games have provider-specific support; additional stores support detected local games.
- **Keep the music going.** Open your selected music service, use the mini player, and keep playback running while moving between rooms. Provider accounts and subscriptions still apply.
- **Discord, within reach.** The Friends room hosts your installed Discord desktop app inside Exo. Voice, video, and screen sharing are Discord's own.
- **Make it yours.** Replace game artwork, save games to your wishlist, and configure supported mouse settings and game-linked profiles.
- **A stronger foundation.** Clearer transfer failures, more reliable embedded views, keyboard focus improvements, validated host messages, and safer installer staging and rollback.

![Game details with artwork and available tools](docs/media/game.jpg)

## Five rooms. One session.

| Room | What you can do |
| --- | --- |
| Library | Browse your games, pin favorites, and use supported store actions. |
| Discover | Browse game offers and open legitimate store pages. |
| Friends | Use the embedded Discord website. |
| Devices | Inspect supported mice and manage available settings and profiles. |
| Music | Open a music provider and control supported playback. |

Settings keeps store connections, appearance, account management, and runtime installers together.



## Built around your PC

Your local library and launching work without an Exo account. Settings, artwork overrides, paths, and local wishlist data stay on your PC. Optional online account and provider features require their respective services.

Exo coordinates supported store clients and helpers. Vendors still handle ownership, authentication, downloads, DRM, and anti-cheat. Support varies by store; see the [feature matrix](docs/SUPPORT_MATRIX.md) and [vendor notes](docs/VENDORS.md).

**Upscalers:** tools are optional and act only on supported files already shipped by a game. Explicit swaps use validation and backups, including `.exo-bak`; compatibility with a particular game or anti-cheat system is not guaranteed. Devices requires supported hardware.

**Account availability:** email/password Exo accounts are available with a 12–128-character password. Google sign-in, email magic links, password recovery, and email verification are not currently enabled on the hosted service. Account features are optional; Discord and music services use their own accounts.

Read the [privacy policy](PRIVACY.md).

The application source is private. Public downloads and documentation are maintained in [ImAvgErix/ExoLauncher](https://github.com/ImAvgErix/ExoLauncher). Future original changes are proprietary under [LICENSE](LICENSE); previously MIT-licensed releases retain their applicable permissions.

## Get Exo 3.2

1. Download **[ExoLauncher-Setup.exe](https://github.com/ImAvgErix/ExoLauncher/releases/latest/download/ExoLauncher-Setup.exe)**.
2. Run the installer, then open Exo.
3. Let Exo find the games and clients on your PC.

**Windows 11 x64.** Installs per user to `%LOCALAPPDATA%\ExoLauncher\app`. The installer is unsigned, so Windows SmartScreen may display a warning.

## Build locally (private source)

WinUI 3 + React/WebView2, with the .NET SDK pinned in [global.json](global.json) and Node 24. `VERSION` supplies the native app version; the private UI package mirrors it.

```powershell
cd ui
npm ci
cd ..
dotnet test ExoLauncher.sln -c Debug -p:Platform=x64
pwsh -File Run-ExoLauncher.ps1
```

See [architecture](ARCHITECTURE.md), [supported features and validation limits](docs/SUPPORT_MATRIX.md), [cache policy](docs/CACHE_POLICY.md), and the optional [account service](services/exo-id/README.md).

[Report an issue](https://github.com/ImAvgErix/ExoLauncher/issues) · [Support development](https://www.buymeacoffee.com/UhhErix) · [Changelog](CHANGELOG.md)

Exo Launcher, [Exo OS](https://github.com/ImAvgErix/ExoOS), and [Exo Browser](https://github.com/ImAvgErix/ExoBrowser) are separate products.

© 2026 Erix ([ImAvgErix](https://github.com/ImAvgErix)).
