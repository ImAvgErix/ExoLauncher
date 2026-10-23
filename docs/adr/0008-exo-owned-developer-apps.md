# ADR-0008: Users authorize; Exo owns developer apps

## Status

Accepted — 2026-09-02. Still in force for Exo's own developer apps, which today is only Twitch for IGDB: its paste rows show in Debug or with `EXO_SHOW_DEV_KEYS=1`, and no client id ships (`Services/ExoAppCredentials.cs`, `settings/KeysSection.tsx`).

Since then: the MusicKit, Spotify SDK and Discord Social SDK paths were deleted (CHANGELOG 3.2.4) and the GG.deals key with Discover (3.2.7). Music drives each service's own app or page and Discord signs in by QR code, so neither needs a developer app. Your own Steam Web API, itch.io and SteamGridDB keys are personal keys, shown in shipped Settings › Optional keys since 3.3.0, not behind Your own app.

## Context

Settings and first-run Music asked people to paste MusicKit, Spotify, Twitch, Discord, Steam Web API, SteamGridDB, and GG.deals credentials. Those are developer-app secrets, not a person’s login. Most people will not create Apple / Spotify / Twitch / Discord developer apps. Library, install, update, and launch already work without them.

Vendor APIs treat “this program” and “this person” as two credentials. A user OAuth code cannot mint a MusicKit developer JWT or register a Spotify Web Playback SDK client. Shipping Exo’s `.p8` / client secrets inside the exe is how those keys get stolen and revoked.

## Decision

- **Default UX is Sign in / Connect.** Discord stays Connect in Friends. Spotify and Twitch Connect live under Settings → Accounts. Apple Music sign-in stays in the Music catalog pane once a developer token exists.
- **Exo owns the developer apps.** Public client ids may ship or come from `EXO_*` environment variables. Secrets stay on this PC (env or a later token service), never in React, never in `settings.json`.
- **Your own app** is Debug only (or Release with `EXO_SHOW_DEV_KEYS=1`). Shipped Settings never shows developer-app paste. It is not the first-run path.
- **Optional enrichment is not required to run Exo:** Steam Web API (live friend status), SteamGridDB (extra covers), GG.deals (keyshop prices). Those stay in Your own app only.
- Store Sign in (Steam / Epic / GOG / …) is unchanged: official clients, not Web API keys.

## Consequences

- Until official Exo apps exist, Connect is disabled unless this PC has env vars or a Your own app override. Spotify Catalog stays on the in-Exo player either way. Apple Music uses the official site until MusicKit is ready.
- A public token service (MusicKit JWT, Spotify / Twitch client-credentials) is the path that lets other people Sign in with no paste. Not in this revision.
- Clearing a Your own app field does not clear an `EXO_*` env credential.
