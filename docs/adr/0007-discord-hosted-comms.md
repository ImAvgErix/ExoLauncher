# ADR-0007: Discord-hosted comms in Friends, Exo-native later

## Status

Accepted — 2026-09-01

Reopens the “no chat, Discord linking” clause of [ADR-0005](0005-online-profiles-presence.md). Does not reopen store discovery (ADR-0004) or Optimize (ADR-0006).

## Context

Friends is roster + presence. Chat and voice are not on exo-id yet. Discord’s official Social SDK (Windows x64 C++) can host DMs and lobby voice inside a game/launcher without a banned third-party Discord client.

## Decision

- Host messages and 1:1 voice through the **Discord Social SDK**. Friends is the Discord roster. Store names and playing titles overlay a Discord person when the user already linked that identity; they are not a second friends list.
- Expose the host as provider-agnostic `comms.*`. Discord is an implementation. A later `ExoCommsProvider` on exo-id can replace it without rebuilding the room.
- Linking Discord does **not** create an exo-id friendship (ADR-0004 double-submit still applies).
- Library, install, update, and launch never wait on Discord. Missing SDK or app id is `unavailable`, not offline.
- No user-token client, no self-bot, no Discord guild/server list in this revision.

## Consequences

- DMs and voice stay capped until Discord grants communication features for the application.
- `comms.*` fails closed when the SDK, application id, or comms grant is missing. The host does not invent friends, messages, or a live call.
- The native bind lives in `ExoLauncher.DiscordSocial` and talks to `discordpp::Client` (PKCE authorize, `GetToken` / `RefreshToken`, `GetRelationships`, `SendUserMessage`, lobby `StartCall`). Refresh tokens are DPAPI on this PC. Access tokens never enter the WebView.
- exo-id still has no message store. Account export does not include Discord DM bodies.
