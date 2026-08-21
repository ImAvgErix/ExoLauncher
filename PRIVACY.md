# Exo Launcher privacy statement

**Exo Launcher is local-first. An Exo account is optional. There are no ads or behavioral analytics.**

Library, install, update, launch, and local settings stay usable while signed out or offline. Online identity and social failures do not block those paths.

## What stays on this PC

- Settings, library data, cover cache, local profile images, and fallback play sessions live under `%LocalAppData%\ExoLauncher`.
- Custom game covers are content-addressed local copies. Their filenames stay in local settings and are excluded from portable account sync. **Report artwork** copies at most 4 KiB of sanitized, path-free diagnostics to the clipboard; Exo opens only an empty GitHub issue page when requested and never submits those details itself.
- Library discovery reads local paths, manifests, registry keys, and store-client data. Exo does not upload library paths, install roots, launch overrides, or historical playtime to exo-id. A password entered for optional account creation/sign-in is sent only to the bounded HTTPS authentication endpoint described below. While signed in with Launcher open, presence connects and the current game id/title may be shared with connected friends under activity privacy.
- Store credentials stay native. The one exception is an explicit Epic or GOG link: the existing Legendary or gogdl access token is sent once over HTTPS to exo-id, used for one store identity lookup, and discarded. It is not put in React, D1, R2, or application logs.
- The Exo session is stored in a current-Windows-user DPAPI blob, not `settings.json` or WebView2 storage. Normal sign-out removes the session and clears the online DTO/media disk caches and browser mapping. Other processes running as the same Windows user remain inside that trust boundary.

## Optional Exo account

Email/password account creation and sign-in are implemented through two bounded Better Auth routes. Passwords must be 12–128 characters. Better Auth stores a salted Scrypt hash in the D1 account record, not the plaintext password. React holds the user's current form input only long enough to call the native host; neither server nor native code returns the password to React, and native password requests are excluded from caches and credential-bearing logs. On success, only the Exo bearer session is saved in the current-user DPAPI blob.

Password-account email addresses are currently marked unverified. Password recovery is unavailable because no reset route or real mail provider is configured. Google and email magic-link sign-in remain separate capability-gated providers.

exo-id also stores the account id, email, provider record, session records, reserved handle, portable profile, allowlisted preferences, privacy choices, and the social data described below. A small server-managed staff-role table controls badge administration; fixed profile badges store only an allowlisted key, grant time, and optional grantor. Public profile responses may show a badge's server-defined label, description, and tone, but never expose roles, permissions, grantors, email, or arbitrary markup/colors. Better Auth may keep Google provider credentials server-side when that provider is enabled; Exo Launcher never receives them. Session APIs and account export omit password hashes, bearer/provider tokens, and IP addresses.

The portable allowlists exclude machine paths, install roots, launch overrides, library history, local image filenames, window coordinates, and unknown settings keys. Profile game references are opaque ids; another PC drops ids it cannot resolve instead of creating a library row.

### Profile, search, share, and media

Privacy-safe defaults are:

- profile visible to connected friends;
- not searchable;
- friend requests allowed;
- game activity visible to connected friends.

The owner may choose public, friends-only, or private profile visibility; enable or disable prefix search; allow or reject requests; and hide game activity. Inaccessible or blocked profiles return the same not-found result. Search returns only opted-in profiles the viewer is allowed to see. The `/p/<handle>` share page is anonymous and therefore works only for public profiles; it contains no script and is sent with no-store and restrictive browser headers.

Uploaded avatars, banners, and up to six profile-gallery pictures are stored in Cloudflare R2 under account-owned, versioned keys. exo-id accepts only bounded PNG, JPEG, WebP, or GIF images, validates their structure and dimensions, reconstructs safe bytes without identifying/unsafe metadata, bounds GIF frames and decoded pixels, and rejects animated WebP. Origin reads re-check the current D1 ownership/version record and profile visibility. Anonymous public media uses `max-age=0, must-revalidate`; authenticated media is private/no-store. Replacement or clear makes the old origin version inaccessible and attempts to delete its object; an R2 delete failure may leave an inaccessible orphan until account-prefix cleanup. Account deletion enumerates that prefix and aborts if cleanup fails.

### Friends, blocks, and verified store discovery

Direct friend requests, accepted friendships, outgoing blocks, removals, and suppression records are stored in D1. A block in either direction hides the relationship and profile. Removing or blocking someone writes a suppression so store discovery cannot silently recreate the connection; accepting a later direct request clears that suppression.

Steam, Epic, and GOG links are optional and must be proven. Steam uses Valve OpenID in the system browser. Epic and GOG use the one-shot verification described above. The proven store-native id is AES-GCM encrypted in D1 and also indexed by a keyed HMAC for matching. It is returned only to its owner through link state or account export.

Store discovery defaults on and can be turned off. While on, Exo may send up to 200 store-friend ids per match request. exo-id sees those ids for that request, but does not store unmatched ids or log the list. A possible match becomes an Exo-user-id claim; a connection is created only after both verified, opted-in accounts submit each other as a mutual store friendship. Claims expire after 30 days. The current Windows host can supply a proven mutual list only for Steam. Epic and GOG linking work, but neither currently exposes a verified automatic mutual-match source to Exo, so no list is fabricated. One-sided or plugin-derived lists never create a claim. Turning discovery off deletes pending claims but leaves already completed connections until removal.

### Presence

Presence starts when the WebView host attaches for a signed-in session and runs only while Launcher is open; there is no separate presence-enable switch or tray agent. A per-user hibernating Durable Object stores active connection/session ids, status, optional game id/title, last-seen time, and cached friend presence. Connections expire after 90 seconds without a heartbeat, every heartbeat/status revalidates the session against D1, and cached peer rows expire after 24 hours as a safety bound. Account deletion also purges related peer copies immediately or fails for retry.

Only connected, non-blocked, non-suppressed friends receive presence. Activity privacy hides game id/title; it does not turn a reachable online person into offline. **Offline** means the service authoritatively has no live connection. **Unknown** means presence could not be determined because the service or a peer object was unavailable. Exo does not convert unknown to offline.

There is no chat or message-content service.

## Export, deletion, and retention

Account export includes account/provider names, handle, portable profile/preferences, staff roles and fixed profile badges, privacy, media metadata, session ids/timestamps, discovery state, the owner's decrypted verified store links, discovered/direct friends, requests, outgoing blocks, owner-created suppressions, and the owner's presence snapshot. It excludes bearer/provider tokens, friend store ids, machine paths, and media bytes.

Account deletion requires a session created within the last 15 minutes. It removes cached copies from related peers' Durable Objects (broadcasting unknown/unavailable), closes presence sockets, and deletes the user's own Durable Object state, R2 profile media, sessions/provider account, live handle, profile/preferences/privacy, verified links, match claims, discovered/direct friendships, requests, blocks, and suppressions. If presence cleanup cannot be confirmed, deletion fails so the user can retry instead of reporting partial success. Cleanup spans multiple stores and is retry-oriented, not one atomic transaction. The deleted normalized handle remains tombstoned for 365 days to prevent immediate impersonation; an abuse hold may remain indefinitely.

Exo's custom application rate-limit identifiers are scoped hashes. Better Auth's separate database limiter stores its own IP-plus-route key. Application logging redacts email, authorization/session data, provider/store tokens, store-native ids, friend-id lists, and account/user ids.

## Other network requests

| Purpose | When |
| --- | --- |
| Store library, metadata, cover, achievement, friend, and playtime services | While refreshing the corresponding supported feature |
| GitHub release service | When update checking is enabled or you press **Check** |
| Official dependency releases | When a supported action needs Legendary or gogdl; Exo verifies the official asset digest |
| Upscaler version catalog | During bounded prewarm/status checks for installed non-protected games, or when you explicitly check/update |
| Vendor upscaler DLL assets | Only when you explicitly ask Exo to update existing DLSS / FSR / XeSS files |
| exo-id | Public profile/search/share reads, or optional signed-in identity, social, media, and presence actions |

For upscaler status, Exo reads the beeradmoore DLSS Swapper version manifest and reuses a bounded in-memory cache; protected/anti-cheat titles are rejected before that network step. An explicit update may then retrieve an approved NVIDIA, NVIDIA-RTX/Streamline, GPUOpen-LibrariesAndSDKs/FidelityFX-SDK, or Intel XeSS asset. Extracted DLLs are accepted only after vendor signature, PE/export/version, and local SHA-256 integrity checks. Exo never downloads or swaps an upscaler merely because a game was opened or launched.

Network requests use bounded timeouts. Store-authenticated verification requests do not follow redirects, so a credential is not forwarded to another host.

## What Exo Launcher does not do

- No advertising, behavioral analytics, telemetry SDK, or silent crash upload.
- No Exo account requirement and no online dependency for the library or game actions.
- No chat, Discord linking, or Sign in with Apple in the current service.
- No password recovery or verified-email claim in the current email/password flow.
- No upload of machine paths, library/play history, or unrequested store credentials.
- No kernel driver, anti-cheat bypass, or silent approval of Windows security prompts.
