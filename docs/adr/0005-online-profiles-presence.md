# ADR-0005: Optional online profiles and presence

## Status

Accepted — 2026-08-19.

Supersedes the option-A rejection in [ADR-0003](0003-exo-accounts.md). Retains the verified mutual-discovery decision in [ADR-0004](0004-cross-store-friend-linking.md).

## Context

Exo Launcher now has implemented exo-id and native transport modules for an online identity/social stack. Repository source and tests do not by themselves prove that every module is wired into the product UI, shipped, deployed, or live-provider verified. The earlier account rejection correctly protected Exo's local-first library, but it no longer describes the authorized product direction.

An account must add identity and social capability without making Exo a cloud launcher. It also creates privacy and security boundaries that a local profile did not have: public discovery, uploaded media, social relationships, store identifiers, and live activity.

## Decision

Accept an **optional** Exo account in Exo Launcher. Signed-out and offline use remains complete for library scan, local settings, install, update, launch, Play, and Remove. Those paths do not call exo-id and do not wait for presence.

The accepted online scope is:

- email/password account creation and sign-in through guarded Better Auth routes, with 12–128-character passwords stored as salted Scrypt hashes in D1;
- email magic-link and Google sign-in through the system browser, loopback callback, state, and PKCE S256 when those providers are configured;
- globally reserved handles, portable profile fields, and a deny-by-default portable preference subset;
- privacy-controlled exact profile reads, opt-in prefix search, and an anonymous public share page;
- sanitized, versioned avatar, banner, and six-slot picture/GIF gallery uploads in R2;
- direct friend requests, accept/decline/remove, directional blocks, and suppression that prevents silent rediscovery;
- [ADR-0004](0004-cross-store-friend-linking.md)'s verified Steam/Epic/GOG links and double-submit mutual discovery;
- friend-only online/away/in-game/offline presence over a per-user hibernating Durable Object, with a bounded REST roster fallback;
- complete account export and cleanup-first deletion.

Onboarding presents account creation/sign-in as the normal identity path, but an explicit Continue offline escape keeps the account outside the local launcher's critical path. No chat, direct messages, Discord link, Sign in with Apple, tray agent, ads, or analytics are part of this decision. Email/password accounts are currently unverified and have no password-recovery flow.

## Architecture and boundaries

- `services/exo-id` is a Worker with D1 for identity/social metadata, R2 for current profile media, and one SQLite-backed Durable Object per presence owner. The exact protocol is [CONTRACT.md](../../services/exo-id/CONTRACT.md).
- Native C# owns the Exo bearer token, system-browser/loopback flows, Epic/GOG token sourcing, file selection, media caching, response bounds, and WebSocket transport. React receives sanitized DTOs and browser-safe media URLs, never raw tokens, password hashes, store credentials, server bodies, or filesystem paths for online operations. Its password form value is cleared around the native call; native/server responses never return it.
- Password capability is implemented directly. Google and Resend are optional provider capabilities exposed as configured booleans by health. A missing provider returns a stable unavailable error. A missing Legendary/gogdl token source leaves Epic/GOG linking unavailable. Credentials, store ids, and successful results are never fabricated.
- Production trusts one compiled HTTPS exo-id origin. Loopback HTTP/WS is development-only. An empty production origin means the client is unconfigured, not that it may trust an arbitrary server.
- Application logging redacts auth/provider/store/session/account identifiers. Exo's custom application rate-limit keys are scoped hashes; Better Auth's own database limiter retains its IP-plus-route key.

## Privacy rules

Profile defaults are friends-only, unsearchable, requests allowed, and friend-visible activity. Exact reads, search, the share page, and media apply profile visibility plus block/suppression policy. Social transitions apply request, block, and suppression rules; presence requires a connected friend and applies activity visibility. Inaccessible resources return not found where revealing existence would create an oracle.

Store-native ids are encrypted at rest and HMAC-indexed. Unmatched friend ids exist only during a match request; a match claim stores Exo user ids and expires after 30 days. A discovered connection needs reverse submission from the other verified, opted-in account.

Media is structurally parsed and sanitized before R2 storage. D1 owns the current version, and reads require the D1 record, owned object key, R2 size/type/hash metadata, and viewer authorization to agree.

Presence exposes activity only to connected friends. `activityVisibility: private` removes game id/title. **Offline** is an authoritative reachable state with no live connection. **Unknown/unavailable** means the service could not determine state and must never be presented as offline. The host starts presence when its WebView attaches to an existing signed-in session and stops it on sign-out/shutdown; there is no separate opt-in switch or tray process. A 30-second client heartbeat maintains a 90-second server lease; every heartbeat/status revalidates its session. Each account is capped at eight sockets, and cached peer rows expire after 24 hours.

## Operations

Source and tests alone do not prove a public deployment. The repository contains production-shaped D1, R2, SQLite-backed Durable Object, cleanup-trigger, secret, and pinned-origin configuration, but this revision does not claim live-service verification. Google and email magic-link remain disabled without real credentials/mail delivery. Email/password is implemented, but account emails remain unverified and password recovery is unavailable until a real mail-backed recovery design exists.

Account deletion requires a session created within 15 minutes. It first removes cached peer-presence copies, the owner's presence sockets/state, and R2 objects, then deletes D1/Better Auth identity, profile, privacy, social, link, and session rows. A handle tombstone remains for 365 days. Cross-store cleanup is ordered and retry-oriented, not atomic; failure returns an error instead of claiming success.

## Consequences

- Exo now owns an optional identity/social service and its abuse, uptime, deletion, secret-rotation, storage, and privacy obligations.
- A service outage removes online identity/social capability but does not remove the local launcher.
- Presence exists only while authenticated Launcher is open; there is no always-on agent outside the app.
- Public profiles and media are deliberately discoverable only under the owner's privacy choices.
- The current source must not be described as deployed or live-provider verified until those operations are actually completed.
