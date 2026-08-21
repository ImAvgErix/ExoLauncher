# ADR-0004: Cross-store friend linking

## Status

Accepted — 2026-08-18. Retained by [ADR-0005](0005-online-profiles-presence.md) on 2026-08-19.

This decision established verified, double-submit store discovery. ADR-0005 accepts the broader optional online identity/social stack and leaves this privacy design in force. The Windows client now consumes the contract; neither ADR claims a production deployment or live two-account proof.

## Context

Two people are Steam friends. Both have Exo accounts with Steam linked. Exo should connect their Exo profiles without either of them searching for a handle. Same for Epic, GOG, and any other store Exo can read friends from.

That is the thing that makes an Exo account worth having. A naive version is a privacy incident:

1. If a user can assert "my SteamID is X", they inherit that person's friend graph.
2. `POST { ids: [/* 67 SteamIDs */] }` → "which of these are Exo users?" is an enumeration oracle. Anyone can map who uses Exo. A scraper can walk the user base.
3. Adding a stranger to an Exo friend list without agreement is not acceptable. Steam and Epic friendships are already mutual — both people accepted — so reflecting that on Exo is not inventing a relationship. A one-directional or plugin-derived row (Galaxy's mixed friends tables) is weaker evidence.
4. Sending a friend's store id to exo-id discloses part of the user's social graph to Exo. That cost did not exist before. Say it.

Library, launch, and install must not wait on any of this. Signed out, no verified link, discovery off, or the server unreachable: today's local friends behaviour.

## Options

### Verification

**A — Trust the client.** Host posts `steamId` from `localconfig.vdf` / Legendary / gogdl. Cheap. Anyone who can mint an Exo session can claim any store id. Rejected.

**B — Steam OpenID 2.0; Epic/GOG token verify (chosen).**

Steam still publishes OpenID 2.0 as "Sign in through Steam". Official docs (checked 2026-08-18):

- [steamcommunity.com/dev](https://steamcommunity.com/dev) — provider `https://steamcommunity.com/openid`, claimed id `https://steamcommunity.com/openid/id/<SteamID64>`. Free. No API key. Entering a Steam password on a third-party site is a ToS violation; OpenID is the allowed path.
- [Steamworks: User Authentication and Ownership](https://partner.steamgames.com/doc/features/auth) — "Web Browser based authentication with OpenID". OP endpoint `https://steamcommunity.com/openid/`. Same claimed-id format (the Steamworks page writes `http://`; live claimed ids are `https://`; accept both).
- Session tickets and encrypted app tickets need a Steamworks AppID and a publisher/Web API key. Exo is not a Steam game. Skip them.
- OpenID **Connect** is not offered. `/.well-known/openid-configuration` on that host is XRDS for OpenID 2.0.

Verification is OpenID 2.0 §11.4.2: POST the assertion back to `https://steamcommunity.com/openid/login` with `openid.mode=check_authentication`. `is_valid:true` once; replays fail.

Epic has no equivalent "Sign in with Epic" that Exo can register for without Epic Account Services (a game-developer product). Exo already holds a Legendary user token that Epic issued. The server `GET`s `https://account-public-service-prod.ol.epicgames.com/account/api/oauth/verify` with that access token, reads `account_id`, discards the token. Same host family Legendary uses for OAuth.

GOG Galaxy SDK OpenID Connect is for games: DevPortal client, a support ticket to enable the scope ([docs.gog.com/sdk-openid](https://docs.gog.com/sdk-openid/)). Wrong product. gogdl already holds a bearer token. The server `GET`s `https://embed.gog.com/userData.json`, reads `userId`, discards the token.

The uncomfortable part of B: an Epic or GOG access token briefly touches exo-id. It is not stored. It is not logged. HTTPS only. Steam OpenID never sends a Steam credential to Exo.

**C — Steam Web API key + GetFriendList.** Proves nothing about the caller. Anyone with a key can read public friend lists. Rejected as a verification mechanism.

### Enumeration

**A — Batch membership oracle.** Return which ids are Exo users. Rejected.

**B — Double-submit, silence on miss, verified caller, discovery opt-in, rate limit (chosen).**

- Caller must hold a verified link of the **same** store.
- Request body is friend ids. Server HMAC-SHA256s them with the auth secret and looks up verified, discovery-on links. Unmatched hashes are discarded. They are not stored.
- A hit writes a `match_claim` (Exo user → Exo user, not a store id). The response still omits that person until the reverse claim exists.
- Only then is a `discovered_connection` written and returned.
- Opted-out, unverified, and unknown ids are indistinguishable in the response.
- 8 match calls / 10 min / user, 20 / 10 min / IP, 200 ids / call. Rate-limit keys are hashed. Logs do not carry store ids or id lists.

A stolen D1 without the secret is not a cleartext Steam directory. Each owner's proven id is AES-GCM encrypted with user/store-bound additional data and separately HMAC-indexed for matching; owner link/export reads decrypt it with the auth secret.

**C — Blinded PSI / OPRF so the server never sees friend ids.** Would address the social-graph disclosure. Not in v1: Worker CPU, still need rate limits, still need verified links. Say "not yet" rather than ship a toy.

### Consent

**A — Auto-add when one side posts a friends list.** Seamless. Lets a malicious client attach anyone who has discovery on, once it learns or guesses a store id. Rejected.

**B — Mutual store friendship, both verified, both discovery on, both sides submit (chosen).** Steam/Epic friendship is already mutual in the real world. The server still waits for both Exo clients to present each other, because it cannot see the store list except as presented. Galaxy plugin rows and any other one-directional evidence are `relationship: "onesided"` and never auto-link. The user can still add by handle.

**C — Friend-request UI for every overlap.** Honest. Not the ask. People who already accepted each other on Steam should not have to accept again on Exo.

Discovery defaults **on**. Off is a real off: future matching skips them; pending claims involving them are deleted. Existing Exo connections are not silently unfriended (that relationship was completed). Disclose in plain language next to the switch.

## Decision

Build B+B+B on exo-id. Contract: `services/exo-id/CONTRACT.md`. Schema: verified `store_link`, account-level `user_discovery` (missing row = on), `match_claim`, `discovered_connection`. Steam via OpenID 2.0 in the system browser (same RFC 8252 loopback handoff as Google). Epic/GOG via one-shot token verify.

Do not put this on the library, launch, or install path. Deployment and Cloudflare resources are separate operational steps; see `services/exo-id/README.md`.

Riot and the list-only stores have no friend source Exo can prove. Not yet.

## Consequences

- Two people who already accepted each other on a supported store, both on Exo, both with discovery on, can connect after each client posts a mutual match. The current Windows host supplies that mutual source for Steam only; Epic/GOG linking works, but their automatic match provider remains unavailable rather than fabricating a list.
- GOG auto-link is empty until the client has a **mutual** GOG-native friends list. Galaxy's database is not that. Sending those rows as `mutual` would be a lie.
- exo-id learns each verified store id, and briefly sees friend store ids during match. `PRIVACY.md` says so.
- Epic/GOG access tokens transit exo-id once. A compromised Worker in that window is as bad as holding the token. Keep the window to one `GET` and drop the body.
- ADR-0003's "must not upload `friend-links.json`" still holds for the local file. Matching is a different, consented upload of friend store ids while discovery is on.
- Handle squatting and account takeover remain the bigger identity risks. Linking a stolen Exo session to your Steam via OpenID would bind Steam to the thief until you unlink; OpenID is interactive, which is the remaining brake.
- Nothing here is proven against live Steam/Epic/GOG or two real accounts. Unit and Wrangler-local tests cover the contract. Cross-account match on production needs a deploy, which this change does not do.

## Sources (checked 2026-08-18)

- Steam OpenID: [steamcommunity.com/dev](https://steamcommunity.com/dev), [Steamworks auth](https://partner.steamgames.com/doc/features/auth), [OpenID 2.0](https://openid.net/specs/openid-authentication-2_0.html) §11.4.2
- Epic OAuth verify: Legendary `account-public-service-prod` `/account/api/oauth/verify` (same account service the EGL token lives on)
- GOG: [gogapidocs auth](https://gogapidocs.readthedocs.io/en/latest/auth.html) (token includes `user_id`); [Galaxy OpenID](https://docs.gog.com/sdk-openid/) (games, not this)
