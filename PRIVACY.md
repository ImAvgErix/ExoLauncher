# Exo Launcher privacy statement

**Exo Launcher is local-first. It does not require an Exo account or profile sync. There are no ads or analytics.**

## What stays on your machine

- Settings, library cache, cover cache, and local fallback play sessions live under `%LocalAppData%\ExoLauncher`.
- Library discovery reads local install paths, manifests, registry keys, and store-client data.
- Existing Epic/Legendary credentials are read only when requesting your Epic lifetime playtime from Epic; Exo does not copy them into its settings or logs.

## Exo Link owns social identity

Exo Link is the only Exo product that needs an Exo account for profiles, friends, chat, presence, or cloud-backed social features. Exo Launcher has no Exo account sign-in, profile editor, profile sync, or portable-setting sync.

Machine paths, store credentials, friends, chat, payment data, and Launcher settings stay out of Exo Launcher network requests. Native store totals take priority; local-session totals are fallback data and are excluded whenever a native source exists for the same game.

GOG's owned-library cache is bound to a one-way tag for the currently authenticated GOG user. A stale cache from another local GOG user is ignored, including while offline or after a failed account switch.

## Other network requests

| Purpose | When |
| --- | --- |
| Store library, metadata, cover, achievement, and playtime services | While refreshing supported libraries, achievements, or artwork |
| GitHub release service | When update checking is enabled or you press **Check** |
| Official GitHub dependency releases | Only when a supported action needs Legendary or gogdl; Exo requires the exact official asset and its published SHA-256 digest |

Network requests use bounded timeouts. Store-authenticated requests do not follow redirects, preventing credentials from being forwarded to another host.

## What Exo Launcher does not do

- No advertising, behavioral analytics, or telemetry SDK.
- No upload of crash reports, library paths, store tokens, or passwords.
- No Exo account sign-in, profile, or cross-device Launcher sync.
- No account linking to Steam or Discord.
- No kernel driver, anti-cheat bypass, or silent approval of Windows security prompts.
