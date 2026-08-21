# exo-id HTTP and presence contract

This is the source-of-truth protocol for Exo Launcher's optional online identity/social module. The native host calls it; React receives sanitized DTOs and browser-safe media URLs. Raw Exo/store tokens, raw response bodies, and filesystem paths do not cross into React or logs.

The base URL is the configured Worker origin (`BETTER_AUTH_URL`). Production must be HTTPS and exactly match the origin pinned in the Windows build. Local development uses `http://127.0.0.1:8787`. Configuration is not evidence of a live deployment.

Library scan, local settings, install, update, launch, Play, and Remove do not call this API. Signed out, offline, unconfigured, rate-limited, or unavailable: those paths remain usable. There is no chat API or tray presence agent.

## Transport and errors

JSON requests use `Content-Type: application/json`. Authenticated HTTP requests and the WebSocket upgrade use:

```http
Authorization: Bearer <accessToken>
```

Normal API errors are:

```json
{ "error": { "code": "HANDLE_TAKEN", "message": "That handle is taken." } }
```

Custom `assertRateLimit`/`ApiError` `429` responses include standard `Retry-After`. The password boundary translates Better Auth's limiter to the same error envelope and standard header. The ninth concurrent presence socket is a plain-text `429` without that header, and Better Auth's browser-only callbacks retain their own response format. Native clients surface stable codes and bounded messages, never raw bodies. `401 UNAUTHENTICATED` deletes the local session blob but does not block the launcher.

The native module may return a DPAPI-protected last-good DTO cache or a bounded, validated media cache (hashed filenames, browser-safe virtual URL) only for retryable transport failures, `408`, `429`, `5xx`, or an invalid transient response, and labels diagnostics `source=cache` with the last successful timestamp. It does not use cache fallback for authoritative non-401 `4xx` such as `404` after a privacy/block/clear change. Exact-profile cache scope includes the current viewer session; signed-out callers do not inherit a prior friend's private cache.

The server sends `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `X-Frame-Options: DENY`, and `Cache-Control: no-store` unless a media response sets a stricter/specific policy.

## Lifetimes, limits, and pagination

| Item | Current value |
| --- | --- |
| Pending login / Steam link | 10 minutes |
| One-time auth code | 60 seconds |
| Email magic link | 5 minutes |
| Password | 12..128 characters |
| Password request JSON | 2 KiB |
| Exo session | 7 days; sliding update after 1 day of use |
| Handle-change cooldown | 30 days |
| Deleted-handle tombstone | 365 days; abuse hold may be permanent |
| Mutual match claim | 30 days |
| Account-delete recent-auth window | session created within 15 minutes |
| Presence heartbeat / lease | client 30 seconds / server 90 seconds |
| Presence peer-cache safety retention | 24 hours |
| Concurrent presence sockets | 8 per account |
| JSON response bound in native client | 512 KiB |
| WebSocket message | 4 KiB UTF-8 text JSON |
| Generic native JSON/Presence REST response bound | 512 KiB |
| Optional `ExoPresenceClient.ParseRestFallback` helper bound | 64 KiB |

Cursor endpoints default to `limit=20`, accept `1..50`, and return an opaque request-scoped `nextCursor` (or direction-specific cursors for requests). A cursor copied to a different query/user/scope is `INVALID_REQUEST`. Presence defaults to 50 and accepts `1..50`; it does not paginate beyond the selected first page. Store matching accepts at most 200 ids per call.

Additional route limits (fixed windows reset from the first request in that window):

| Route/action | Limit |
| --- | --- |
| `POST /v1/auth/start` | 5 / 10 min / IP; email also 3 / hour / normalized address |
| `POST /v1/auth/token` | 20 / 10 min / IP |
| Password sign-up or sign-in | 5 / 1 min / IP per route |
| `PUT /v1/handle` | 10 / 10 min / user and 20 / 10 min / IP |
| `PUT /v1/profile/privacy` | 20 / 10 min / user |
| `GET /v1/profiles/search` | 60 / 10 min / IP; signed-in callers also 30 / 10 min / user |
| Friend/block mutations | shared 40 / 10 min / user |
| Avatar/banner/gallery PUT or DELETE | 10 / 10 min / user **per kind** |
| Presence WebSocket upgrade | 30 / 10 min / user and 60 / 10 min / IP; also 8 concurrent/account |
| Discovery toggle | 10 / 10 min / user |
| Steam link start | 5 / 10 min / user and 10 / 10 min / IP |
| Steam callback | 20 / 10 min / IP |
| Epic or GOG link | 10 / 10 min / user |
| Store match | 8 / 10 min / user and 20 / 10 min / IP |
| Badge administration (list/grant/revoke, shared) | 40 / 10 min / authorized user |

Exo's custom `app_rate_limit` keys are scoped hashes. Better Auth's enabled database limiter separately persists its own IP-plus-route key in `rateLimit`. Application code does not log raw IPs, emails, tokens, account/store ids, or friend-id lists.

## `GET /v1/health`

Unauthenticated response:

```json
{
  "ok": true,
  "service": "exo-id",
  "capabilities": {
    "providers": { "google": false, "email": false, "password": true },
    "profiles": true,
    "friends": true,
    "media": true,
    "presence": true
  }
}
```

`google` reflects its configured id+secret. `email` means magic-link delivery and reflects the Resend key+from. `password` is the independent D1-backed email/password capability and needs no mail provider. Email verification and password reset are not implemented; configuring a sender alone does not expose those routes. Feature booleans describe route capability, not D1/R2/Durable Object health, migration state, deployment proof, or live-provider verification. Never gate Play on health.

## Desktop sign-in

Implemented account entry points are D1-backed email/password, Google, and email magic link. Google and magic link may be disabled independently by missing server configuration. Password authentication stays available without pretending that email verification or password reset can send mail. There is no Sign in with Apple.

### Email and password

Only these Better Auth password routes are exposed:

- `POST /api/auth/sign-up/email` with exactly `{ "name", "email", "password" }`;
- `POST /api/auth/sign-in/email` with exactly `{ "email", "password" }`.

Both accept bounded `application/json` only. Names are trimmed, contain 1..80 Unicode characters, and reject control characters. Addresses are trimmed, normalized to lowercase, and limited to 254 characters. Passwords are not trimmed and must contain 12..128 characters. Image, callback, remember-me, confirmation, and unknown fields are rejected. Passwords never appear in responses, logs, caches, browser storage, or React DTOs.

The sign-up route normalizes a new address, an already-registered address, and a duplicate-write race to the same `200 { "ok": true }` response, with no `set-auth-token` and no cookie. The native host then calls sign-in with the same submitted credentials. A wrong password and an unknown address both return `401 INVALID_CREDENTIALS` with the same body. A successful sign-in returns the seven-day session only in Better Auth's official `set-auth-token` header; the boundary removes `Set-Cookie`, and native code stores the token only in DPAPI-protected `auth.bin`. This keeps the sign-up response generic, but the combined create-and-sign-in flow is not proof that the email address belongs to the caller; password-account emails remain unverified.

`ACCOUNT_CONFLICT` is retained as a generic legacy client fallback and never identifies an existing account; the current public sign-up boundary normalizes duplicate conflicts to `{ "ok": true }`. `INVALID_PASSWORD` reports only the 12..128 policy. `RATE_LIMITED` includes standard `Retry-After`. Direct verification, reset, change-password, session, and alternate-method routes under `/api/auth/*` return `404`.

### Loopback and PKCE

The host binds an OS-assigned port before starting sign-in. `redirectUri` must be exactly `http://127.0.0.1:<1..65535>/callback` or `http://[::1]:<1..65535>/callback`: scheme `http`, literal loopback host, exact `/callback`, and no query, fragment, or userinfo. `localhost` is rejected.

The host generates 43–128-character PKCE material and state. `codeChallengeMethod` is `S256`; `plain` is rejected. The browser eventually receives:

```text
http://127.0.0.1:<port>/callback?code=<one-time>&state=<client-state>
```

The host compares state, exchanges the code with the original verifier and exact redirect URI, returns a local close-window page, then closes the listener. The session token never appears in the loopback URL.

Google has a separate fixed server callback:

```text
{BETTER_AUTH_URL}/api/auth/callback/google
```

Google uses a **Web application** OAuth client. The Google secret stays in Wrangler. Google never receives the loopback URI.

### Auth endpoints

#### `POST /v1/auth/start`

Unauthenticated request:

```json
{
  "provider": "google",
  "redirectUri": "http://127.0.0.1:55123/callback",
  "codeChallenge": "<S256>",
  "codeChallengeMethod": "S256",
  "state": "<1-128 chars>",
  "email": "user@example.com"
}
```

`email` is required only for email. Google returns `200 { loginId, expiresIn: 600, authorizationUrl }`; open that URL in the system browser. Email returns `202 { loginId, expiresIn: 600, authorizationUrl: null }`; ask the user to open the message. A well-formed address does not reveal whether an account exists.

The request must be `application/json`, is stream-bounded to 2 KiB before parsing, and accepts only the fields shown above (`state` is the only optional extra). Unknown fields, wrong types, and oversized bodies return `400 INVALID_REQUEST`.

Missing Google id/secret returns `503 GOOGLE_NOT_CONFIGURED`. Missing `RESEND_API_KEY` or `RESEND_FROM` in production returns `503 EMAIL_NOT_CONFIGURED`. These failures never authorize fabricating a provider identity or successful send.

#### Browser-only completion routes

- `GET /v1/auth/continue/:loginId` redirects Google to its provider; email shows a check-mail page. Expired/spent ids return HTML `410`.
- `GET /v1/auth/complete?login=<id>` is the Better Auth completion target. It creates the desktop session, deletes the browser session, creates a one-time code, and redirects to loopback.
- Only the two password POST routes above plus `GET /api/auth/callback/google` are always exposed under `/api/auth/*`. `GET /api/auth/magic-link/verify` is exposed only when magic-link email is configured (`RESEND_API_KEY` and `RESEND_FROM`). Every other direct Better Auth route or method returns `404`. A magic-link verify for an existing unverified password account is refused and does not revoke the password or inherit that user's handle, store links, friends, or media.

#### `POST /v1/auth/token`

Unauthenticated body:

```json
{
  "code": "<one-time>",
  "codeVerifier": "<original verifier>",
  "redirectUri": "http://127.0.0.1:55123/callback"
}
```

The request must be exact `application/json`, is stream-bounded to 2 KiB before parsing, and accepts only the three fields shown. `code` is exactly 64 lowercase hexadecimal characters.

Success:

```json
{
  "tokenType": "Bearer",
  "accessToken": "<session token>",
  "expiresIn": 604800,
  "expiresAt": "<ISO-8601>",
  "user": {
    "id": "<user id>",
    "name": "",
    "email": "user@example.com",
    "handle": null
  }
}
```

After claim, `handle` is `{ display, normalized }`. `INVALID_GRANT` covers missing/spent/expired code or redirect mismatch; `INVALID_PKCE` covers verifier mismatch. Store the token only in the native current-user DPAPI/ACL blob (`auth.bin`).

#### Session endpoints

- `POST /v1/auth/sign-out` revokes the current session and returns `{ "ok": true }`. Treat `401` as locally signed out; delete `auth.bin` and clear native online DTO/media caches plus the host's mapped-media registry.
- `GET /v1/sessions` returns `{ sessions: [{ id, current, createdAt, updatedAt, expiresAt, userAgent }] }`; tokens and IP addresses are omitted.
- `POST /v1/sessions/revoke` accepts an exact `application/json` body `{ "sessionId": "..." }`, stream-bounded to 2 KiB with a 128-character session-id maximum. Only the caller's session ids are addressable. Missing/already-gone is `404`.
- `POST /v1/sessions/revoke-all` revokes all sessions including the caller. Delete `auth.bin` immediately.

An established presence WebSocket revalidates its session id/owner/expiry against D1 before accepting each heartbeat or status message. Revoked/expired sessions close with code `4003`; a native sign-out integration must also stop its presence client promptly.

## Account, handle, portable profile, and preferences

### `GET /v1/me`

Returns `{ id, name, email, handle, profile, badges, roles, canManageBadges, session: { id, expiresAt } }`. `handle` includes `display`, `normalized`, `claimedAt`, and `changedAt`, or is null. `profile` is the current value projection. `roles` is the caller's server-stored subset of `owner`, `admin`, and `developer`; `canManageBadges` is true only for `owner`. `admin` remains a server-stored self-visible role but currently grants no badge-management capability. Those authority fields appear only in this authenticated self projection (and the caller's explicit export), never in public profile/search DTOs. A client profile field, badge, handle, or display name cannot create a role.

### `GET /v1/handle` / `PUT /v1/handle`

`GET` returns `{ "handle": null | { display, normalized, claimedAt, changedAt } }`. `PUT` body is `{ "handle": "Erix" }`.

Rules: 3–24 ASCII letters/digits/underscore, at least one letter; display casing is retained; lowercase and the repeated confusable skeleton (`0→o`, `1→l`, `rn→m`) are each unique. Reserved values and their skeletons are refused. There is no availability endpoint. First claim is immediate; later normalized changes use the 30-day cooldown and tombstone the old value for 365 days. A casing-only update does not consume the cooldown.

### `GET /v1/profile` / `PUT /v1/profile`

Authenticated `GET` returns:

```json
{
  "values": { "displayName": "Erix" },
  "fields": {
    "displayName": { "value": "Erix", "updatedAt": "<ISO-8601>", "deviceId": "<id>" }
  },
  "media": { "avatar": null, "banner": null },
  "badges": []
}
```

`badges` uses the same fixed, safe public projection described below. It contains no authority or grantor metadata.

`PUT` body is `{ "deviceId": "<1-80 chars>", "fields": { "key": { "value": ..., "updatedAt": "<ISO-8601>" } } }`. Response returns `values`, `fields`, `applied`, and `discarded`; media is read separately/from the next GET.

Conflict resolution is per field: later `updatedAt` wins, then lexicographically higher `deviceId`. Lists replace as a whole. Losing, denied, and invalid fields appear in `discarded`; they are not merged or queued server-side. Preserve the original timestamp on reconnect. Pull on sign-in and push explicit saves; no background sync worker.

Profile allowlist:

| Key | Accepted value |
| --- | --- |
| `displayName`, `pronouns`, `statusText`, `bio` | trimmed strings, max 40 / 24 / 80 / 400 |
| `accent` | `ash`, `steel`, `sand`, `clay`, `sage`, `rose` |
| `layout` | `left`, `center` |
| `bannerHeight` | `short`, `standard`, `tall` |
| `showcaseStyle` | `grid`, `rows` |
| `sections` | unique allowed values, then every missing canonical section (`facts`, `about`, `showcase`, `stores`) is appended in canonical order; `[]` becomes all four |
| `hiddenSections` | unique values from `facts`, `about`, `showcase`, `stores` |
| `showcase` | up to 10 opaque ids, each max 80 and not path-like |
| `avatarGameId`, `bannerGameId` | null or one opaque non-path id, max 80 |

Unknown profile keys are denied. Handle, local roster, and local avatar/banner filenames are not profile fields. An unresolved opaque game id is dropped locally; it never creates a library row.

### `GET /v1/sync` / `PUT /v1/sync`

Authentication and a claimed handle are required. The field-vector/conflict shape matches profile. Allowed keys are `sortMode` (`name|recent|size|store`), `trophyNotificationsEnabled`, `trophyNotificationPosition` (named anchor, not pixels), `trophyNotificationPreset`, `trophyNotificationSound`, and `trophyNotificationSoundCue`.

Default is deny. Paths/directories/CWD/window keys, install roots, launch overrides, app version, local image filenames, onboarding, product-enforced flags, favorites/recents/history/showcase/roster/handle, pixel coordinates, unknown keys, and machine-specific values are returned as `discarded: { reason: "denied" }` with `deniedCode: "SYNC_DENIED_KEY"`.

## Profile privacy, public reads, search, and share

Privacy defaults when no row exists:

```json
{
  "profileVisibility": "friends",
  "searchable": false,
  "requestPolicy": "anyone",
  "activityVisibility": "friends",
  "updatedAt": null
}
```

### `GET /v1/profile/privacy` / `PUT /v1/profile/privacy`

Authenticated. `GET` returns `{ privacy }`. `PUT` requires exactly the four setting fields: visibility `public|friends|private`, boolean `searchable`, request policy `anyone|none`, and activity visibility `friends|private`; response returns `{ privacy }` with server `updatedAt`. Unknown fields are invalid.

### `GET /v1/profiles/:handle`

Bearer is optional. Returns:

```json
{
  "userId": "...",
  "handle": { "display": "Erix", "normalized": "erix" },
  "profile": { "<allowlisted key>": "..." },
  "media": { "avatar": null, "banner": null },
  "badges": [
    { "key": "founder", "label": "Founder", "description": "Founder of Exo", "tone": "founder" }
  ]
}
```

Present gallery media appears under its stable `gallery0`..`gallery5` key; empty gallery slots are omitted.

Profile badges are independent from staff authority. The only stored value is an allowlisted key; the Worker supplies fixed plain-text `label`, `description`, and `tone`. There are no caller-provided labels, markup, icons, colors, or CSS values. Current keys are `founder`, `developer`, `moderator`, `contributor`, and `early_supporter`. Seeing a Developer or Founder badge does not grant or prove API permissions; only the server-side role table authorizes an operation.

Owner always sees self. Anonymous viewers see only `public`; authenticated connected friends may see `friends`; nobody else sees `private`. Either-direction block always denies. Suppression removes the connected-friend grant, but does not hide an otherwise public profile. Invalid, missing, and inaccessible profiles all return `404 NOT_FOUND`.

For exact profile, search, and media reads, an absent bearer is anonymous. Any present malformed, invalid, or expired bearer returns `401 UNAUTHENTICATED`; it never silently downgrades to anonymous. Search applies the signed-in per-user limit after successful bearer validation.

### `GET /v1/profiles/search?q=<prefix>&limit=<n>&cursor=<opaque>`

Bearer is optional. `q` is 1–24 ASCII letters/digits/underscore and matches the normalized-handle **prefix**. Only `searchable=true` profiles visible to the current viewer are returned. Blocks always remove a result; suppression removes friends-only access but not an opted-in public result. Response is `{ profiles, nextCursor }`.

Search entries deliberately contain only handle/user id plus any present `displayName`, `statusText`, `accent`, and `avatarGameId`; uploaded media, badges, staff roles, and capability booleans are omitted from search results. Fetch the exact profile for its full allowed avatar, banner, gallery media, and safe visual badges.

## Staff roles and profile-badge administration

Staff roles are operational state in D1, not user-editable profile data. There is deliberately no HTTP endpoint to grant, revoke, or enumerate another user's roles. Only `owner` may administer visual badges. `admin` and `developer` confer no badge-management capability. Unauthorized callers receive the same `404 NOT_FOUND` surface as an absent route, and target responses never include account ids, email, roles, or grantor ids.

All badge-admin routes require a bearer session, server-side `owner` authority, and share the 40-per-10-minute caller limit. An authenticated `admin`, `developer`, or ordinary user receives the same `404 NOT_FOUND` body before target lookup or body parsing. Target handles use the normal exact normalized-handle rules. Invalid/missing targets return `404` without identifying a role or account. Mutation JSON is stream-bounded to 2 KiB, must be `application/json`, and accepts exactly `handle` and `badge`.

### `GET /v1/admin/badges?handle=<exact-handle>`

Returns only `{ "handle": { "display", "normalized" }, "badges": [...] }`. Extra/missing query parameters are invalid. The badge array uses the public fixed projection and includes no grant timestamp or grantor.

### `POST /v1/admin/badges`

Exact body `{ "handle": "Erix", "badge": "contributor" }` grants one allowlisted visual badge and returns the same target projection as GET. Repeating the same grant is idempotent. Self-mutation is refused. Owners may grant CEO as a display title without granting the target any role. Founder is database-exclusive to one account; an exclusivity conflict is a generic `409 INVALID_REQUEST` that does not identify the holder. Badge operations never branch on or return the target's staff roles.

### `DELETE /v1/admin/badges`

Uses the same exact body and response. Removing an absent badge is idempotent. Self-mutation is refused. Every badge grant or removal requires `owner`; `admin` receives the same `404` surface as any other unauthorized caller.

### `GET /p/:handle`

Anonymous HTML share page. Because there is no viewer session, only public profiles resolve. It contains escaped title/description, absolute canonical/Open Graph page URLs, an optional absolute public-media `og:image`, no scripts, `Cache-Control: no-store`, `Referrer-Policy: no-referrer`, and a CSP of `default-src 'none'` plus deny rules.

## Profile media

Kinds are `avatar`, `banner`, and six stable gallery slots (`gallery0`..`gallery5`). Uploads are raw binary bodies, not JSON or multipart.

### `PUT /v1/profile/media/:kind`

Authenticated. `Content-Type` must be `image/png`, `image/jpeg`, `image/webp`, or `image/gif` (parameters are ignored after MIME normalization).

| Kind | Byte limit | Dimensions |
| --- | --- | --- |
| avatar | 4 MiB | width and height each 256..4096 for new uploads |
| banner | 8 MiB | width 320..8192, height 120..4096, aspect ratio 1.5..8 |
| gallery0..gallery5 | 8 MiB each | width and height each 128..4096, aspect ratio 0.25..4 |

The server bounds the stream even without `Content-Length`, validates signatures/chunks/frames, and strips unsafe or identifying metadata while reconstructing the accepted image. GIF is limited to 120 frames and 80 million decoded frame pixels; comments, plain text, and unknown application extensions are removed while the safe loop extension is retained. Animated WebP remains rejected. The sanitized bytes are hashed, written to a new versioned R2 object, atomically promoted in D1, then the old object is deleted. A concurrent change returns `409 MEDIA_CONFLICT`; a failed D1 write deletes the new object.

Success is `{ "media": { kind, version, url, contentType, size, width, height, sha256, updatedAt } }`. Media URLs are relative same-origin `/v1/media/...` paths in mutation, owner-profile, public-profile, and export projections; native code combines them only with its pinned origin.

### `DELETE /v1/profile/media/:kind`

Authenticated and rate-limited like PUT. Deletes the current D1 record, then its object, returning `{ "ok": true }`; an empty slot is idempotent success. A concurrent version change is `MEDIA_CONFLICT`. If R2 deletion fails after the D1 delete, the response fails but the version is already inaccessible and a retry sees an empty slot; the orphan remains until account-prefix cleanup. Replacement has the same possible orphan if deleting the superseded object fails.

### `GET|HEAD /v1/media/:userId/:kind/:version`

Bearer is optional. `version` is exactly 64 lowercase hex characters. The route serves only the D1 current version whose account-owned object key and R2 size/content-type/cache-control/kind/hash metadata all match. Profile visibility and blocks are re-evaluated on every read. Any invalid path, stale version, missing/mismatched object, or authorization denial returns the same `404`.

Responses use ETag `"sha256-<digest>"`, `Accept-Ranges: bytes`, `nosniff`, conditional `If-Match`/`If-None-Match`, and one byte range (`206`, or `416` with `Content-Range`). Anonymous reads are possible only for public profiles and use `public, max-age=0, must-revalidate` with `Vary: Authorization`, so privacy, block, replacement, and delete are re-checked on the next request. Authenticated reads use `private, no-store` and `Vary: Authorization`.

Media errors are `MEDIA_UNSUPPORTED` (415), `MEDIA_TOO_LARGE` (413), `MEDIA_INVALID` (400), `MEDIA_DIMENSIONS_INVALID` (400), and `MEDIA_CONFLICT` (409).

## Direct friends, requests, removals, and blocks

All endpoints require auth. Friend listing unions accepted direct friendships and completed store discoveries, excludes either-direction blocks and suppressions, and preserves sources.

### Read endpoints

- `GET /v1/friends?limit&cursor` → `{ friends: [{ userId, handle, sources, connectedAt }], nextCursor }`. `sources` contains `direct` first, then store names.
- `GET /v1/friends/requests?limit&incomingCursor&outgoingCursor` → `{ incoming, outgoing, nextIncomingCursor, nextOutgoingCursor }`. Each request has `id`, `direction`, peer `{ userId, handle }`, `status`, `createdAt`, `updatedAt`.
- `GET /v1/blocks?limit&cursor` → `{ blocks: [{ userId, handle, createdAt }], nextCursor }`. Only the caller's outgoing blocks are exposed.

### Mutation endpoints

- `POST /v1/friends/requests` body `{ "handle": "Erix" }` → `{ request }`.
- `POST /v1/friends/requests/:id/accept` or `POST /v1/friends/requests/:id/decline` → `{ request }`; only the recipient may transition it.
- `DELETE /v1/friends/:userId` → `{ "ok": true }`.
- `PUT /v1/blocks/:userId` → `{ block }`; `DELETE` → `{ "ok": true }`.

Self-targeting is invalid. A missing, blocked, non-requestable, or otherwise inaccessible target returns `404` where possible. `requestPolicy:none` prevents new requests. A reverse pending request auto-accepts; repeated accepted operations are idempotent.

Removing deletes direct/discovered edges and pending claims/requests, then writes a `removed` suppression so mutual store matching cannot silently recreate the person. Blocking is directional storage but an either-direction authorization deny; it also removes relationships and writes a `blocked` suppression. Unblock deletes the directional block but intentionally leaves suppression. A later accepted direct request clears suppression.

There is no message body, chat, typing, read-receipt, or inbox endpoint.

## Verified store links and mutual discovery

Supported stores are exactly `steam`, `epic`, and `gog`. Riot and list/launch-only stores return `LINK_STORE_UNSUPPORTED`. A typed id is never proof.

The owner's verified store id is stored as versioned AES-GCM ciphertext bound to user+store and indexed by HMAC-SHA256 keyed with `BETTER_AUTH_SECRET`. Owner link/export reads decrypt it. Friend ids in match bodies exist in request memory only; unmatched ids are neither persisted nor logged.

Each Exo user may own at most one linked account per store, and each verified external account may belong to exactly one Exo user globally for that store. D1 enforces both rules with the `(user_id, store)` primary key and the unique `(store, id_hash)` fingerprint, so concurrent claims cannot create two owners. Re-verifying the same owner/account is idempotent and preserves existing discovery state. Replacing a link releases the old account only after the replacement succeeds; a `409 LINK_TAKEN` leaves the caller's current link and connections unchanged. Explicit unlink atomically removes that store's link, claims, and discovered connections, then makes the external account available for a later verified link. Conflict responses never reveal the current owner.

### `GET /v1/links`

Returns:

```json
{
  "discovery": { "enabled": true, "updatedAt": null },
  "links": [{ "store": "steam", "externalId": "...", "verified": true, "verifiedAt": "..." }],
  "connections": [{ "userId": "...", "handle": null, "store": "steam", "createdAt": "..." }]
}
```

Discovery defaults on (missing row). A connection handle may be null before claim. Blocks/suppressions filter connections.

### `PATCH /v1/links/discovery`

Body `{ "enabled": false }`; response `{ discovery }`. Turning off prevents new matches and deletes pending claims involving the user. Existing completed connections remain until removed.

### Steam OpenID

`POST /v1/links/steam/start` body `{ redirectUri, state }` uses the same literal-loopback rules and returns `{ linkId, expiresIn: 600, authorizationUrl }`. Open only a valid HTTPS `steamcommunity.com/openid/login` URL in the system browser.

Steam returns to browser-only `GET /v1/links/steam/callback?link=<id>`. exo-id posts the assertion back to Valve with `openid.mode=check_authentication`; a valid claimed id is a SteamID64. Success writes the link and redirects to loopback with `state` and `link=ok`; failure redirects with a stable error. Steam id is never put in the loopback URL. The host then calls `GET /v1/links`.

### Epic and GOG token verification

`POST /v1/links/epic` and `POST /v1/links/gog` take `{ "accessToken": "<8..8192 chars>" }`. The native host supplies only a real token from Legendary/gogdl. If that capability is absent it returns local `LINK_TOKEN_UNAVAILABLE` and sends no request; it must not fabricate a token.

exo-id makes one 8-second, no-redirect HTTPS identity request to the fixed official store endpoint, reads/canonicalizes the id, and drops the token. Failure is `LINK_VERIFY_FAILED`. `LINK_TAKEN` does not disclose the other owner. Relinking the same user/store to a different verified account replaces its id and removes that store's old claims/connections; re-verifying the same account is an idempotent success.

### `DELETE /v1/links/:store`

Deletes the verified link plus that store's claims and discovered connections involving the caller. An absent link is `404`.

### `POST /v1/links/match`

Request:

```json
{
  "store": "steam",
  "relationship": "mutual",
  "ids": ["7656119..."]
}
```

Caller always needs a verified link for the same store. Invalid/duplicate/self ids are ignored after validation; over 200 is `MATCH_TOO_LARGE`. `onesided` returns `{ "matches": [] }` and writes no claim without consulting discovery. `mutual` additionally requires discovery enabled; when it is off, matching returns an empty list.

For `mutual`, the server HMACs valid ids, silently ignores unknown/unverified/opted-out/blocked/suppressed peers, and stores only Exo-user-id claims for hits. A reverse claim within 30 days creates a discovered connection. Response `{ matches }` contains all completed, currently visible connections for that store; it is never a membership bit vector aligned with input ids.

The current Windows host can automatically supply a proven mutual list only for Steam. Epic/GOG link verification is implemented, but attempting automatic match for either returns local `MATCH_SOURCE_UNAVAILABLE`; the provider absence is not converted to an empty fabricated list.

## Presence WebSocket and REST fallback

Presence requires a signed-in user. The current host starts it when the WebView attaches to an existing signed-in session and stops it on sign-out/shutdown; there is no separate user-facing presence-enable gate, global presence process, or tray agent. Each account is routed by user id to its own SQLite-backed, hibernating `PresenceDurableObject`.

### `GET /v1/presence/socket`

Requires `Upgrade: websocket`; otherwise `426 INVALID_REQUEST`. The outer Worker authenticates the bearer and forwards only internal owner/session headers to the caller's object. Cleartext `ws://` is allowed by the native client only on loopback; production is `wss://` at the pinned origin.

Each account is capped at eight accepted sockets. A ninth upgrade currently returns plain-text HTTP `429` without the normal JSON error body or `Retry-After`; platform limits also apply.

On connect, that connection starts as `online`, gets a 90-second lease, and receives:

```json
{ "type": "ready", "self": { "userId": "...", "status": "online", "gameId": null, "gameTitle": null, "lastSeen": "..." } }
```

Client text messages:

```json
{ "type": "heartbeat" }
{ "type": "status", "status": "online", "gameId": null, "gameTitle": null }
{ "type": "status", "status": "away", "gameId": null, "gameTitle": null }
{ "type": "status", "status": "in_game", "gameId": "<max 128>", "gameTitle": "<max 160>" }
```

Messages allow no extra keys. Strings are trimmed and reject control characters. Game fields are null/absent unless `in_game`. Valid heartbeat/status extends the lease and returns `{ "type": "ack", "self": ... }`. Invalid/binary/oversize messages return `type:error`, code `INVALID_MESSAGE`, then close (`1003`, `1008`, or `1009`). Expiry closes with `4000`; account deletion closes with `4001`.

Multiple sockets aggregate to one owner state: `in_game` outranks `online`, which outranks `away`; ties use latest heartbeat then stable connection id. The owner becomes authoritative `offline` only after the final connection closes/expires. The object fans revisions to all connected, non-blocked, non-suppressed friends and broadcasts:

```json
{
  "type": "presence",
  "presence": {
    "userId": "...",
    "status": "online|away|in_game|offline|unknown",
    "gameId": null,
    "gameTitle": null,
    "lastSeen": "<ISO-8601|null>",
    "availability": "available|unavailable"
  }
}
```

`activityVisibility:private` strips only `gameId` and `gameTitle`; it does not fabricate offline. **Offline + available** is authoritative. **Unknown + unavailable** means policy/D1/peer-object/backend state could not be determined. Clients preserve that distinction.

The native transport heartbeats every 30 seconds, reconnects with bounded exponential jitter (up to 30 seconds), validates/redacts every frame, and is lifecycle-owned by the open signed-in Launcher rather than a tray process.

Authentication is checked at upgrade and revalidated against the D1 session before every accepted client heartbeat/status. A revoked, wrong-owner, expired, or unverifiable session closes with `4003` and removes the connection.

### `GET /v1/presence?limit=1..50`

Authenticated bounded fallback for the first connected-friend page. Success:

```json
{
  "friends": [{ "userId": "...", "status": "offline", "gameId": null, "gameTitle": null, "lastSeen": "...", "availability": "available" }],
  "unavailable": false
}
```

`unavailable` is true if any returned row is unavailable. A D1 roster failure returns `{ friends: [], unavailable: true }`. A Durable Object failure preserves known friend ids as `unknown/unavailable` rows. The typed generic native REST client accepts up to 512 KiB and preserves mixed available/unavailable rows; the optional standalone presence parser has a 64 KiB bound. Neither converts service failure into an empty authoritative offline roster.

## Export and deletion

### `GET /v1/me/export`

Returns a user-requested JSON export with:

- `exportedAt`;
- `account` (id, name, email, verified flag, timestamps, provider names);
- live `handle`, profile values, safe badge records, the caller's staff roles, portable preferences, and privacy;
- media metadata/URLs, not object bytes;
- session ids/timestamps/current/user-agent, not tokens or IP addresses;
- discovery, decrypted owner store links, and completed discovered connections;
- direct-friend rows, pending/accepted/declined request rows, outgoing blocks, and suppressions created by the owner;
- owner presence snapshot including revision, or null if unavailable. Export uses a non-mutating presence peek; an account with no presence state remains without presence state.

It excludes bearer/provider/store access tokens, friends' store ids, machine paths, library/play history, local filenames, and R2 bytes.

### `DELETE /v1/me`

Requires a valid bearer whose server session was created no more than 15 minutes ago. An older session returns `403 REAUTHENTICATION_REQUIRED`; sign in again, then retry.

Current cleanup order is:

1. Route account deletion to the user's presence object. It enumerates related peers, deletes this user from each peer object's cache, broadcasts `unknown/unavailable` to those peers, closes owner sockets with `4001`, and deletes the owner's alarm and SQLite state. If any peer/owner cleanup cannot be confirmed, return `503 INTERNAL` and leave the account for retry.
2. Delete every R2 object under the account-owned prefix, then its D1 media rows. R2 failure aborts the remaining deletion.
3. Delete privacy, links/discovery/claims/connections, direct friends/requests, blocks/suppressions, and pending store links. The final user delete cascades the caller's staff roles and badges; badges the caller granted to other users remain and have their private grantor reference cleared.
4. Insert the 365-day handle tombstone and delete auth codes, profile/preferences, and the live handle.
5. Delete the Better Auth user, which cascades sessions and provider account rows.

This sequence is retry-oriented but is not one transaction across Durable Object storage, R2, D1 batches, and Better Auth. A later failure can leave earlier cleanup applied while the route returns an error; no rollback is claimed. The endpoint requires a valid bearer session but no separate recent-auth challenge.

Success is `{ "ok": true, "handleHeldUntil": "<ISO-8601|null>" }`. The bearer is dead; delete `auth.bin`. Local `settings.json`, library, covers, and play data remain the user's unless separately cleared.

## Stable error catalog

| Code | Meaning/client behavior |
| --- | --- |
| `UNAUTHENTICATED` | Delete session blob; keep launcher usable |
| `REAUTHENTICATION_REQUIRED` | Sign in again before destructive account deletion |
| `RATE_LIMITED` | Honor `Retry-After` |
| `INVALID_REQUEST` | Invalid JSON/query/path/protocol shape |
| `ACCOUNT_CONFLICT` | Generic legacy client fallback; the current sign-up boundary normalizes duplicate conflicts to `200 { "ok": true }` |
| `INVALID_CREDENTIALS` | Unknown email and wrong password use the same response |
| `INVALID_PASSWORD` | Password is outside the 12..128-character policy |
| `INVALID_REDIRECT_URI`, `INVALID_PKCE`, `INVALID_GRANT`, `LOGIN_EXPIRED` | Restart/fix browser handoff |
| `INVALID_PROVIDER` | Only `google` and `email` |
| `GOOGLE_NOT_CONFIGURED`, `EMAIL_NOT_CONFIGURED` | Provider capability absent; do not fake success |
| `HANDLE_INVALID`, `HANDLE_RESERVED`, `HANDLE_TAKEN`, `HANDLE_CONFUSABLE`, `HANDLE_COOLDOWN`, `HANDLE_REQUIRED` | Handle policy/ownership |
| `SYNC_DENIED_KEY` | Stop sending the denied machine/unknown key |
| `LINK_UNVERIFIED`, `LINK_TAKEN`, `LINK_INVALID`, `LINK_VERIFY_FAILED`, `LINK_STORE_UNSUPPORTED`, `MATCH_TOO_LARGE` | Verified-link/matching contract |
| `MEDIA_UNSUPPORTED`, `MEDIA_TOO_LARGE`, `MEDIA_INVALID`, `MEDIA_DIMENSIONS_INVALID`, `MEDIA_CONFLICT` | Media validation/concurrency |
| `NOT_FOUND` | Missing or deliberately existence-hiding denial |
| `INTERNAL` | Optional online failure; retry later and never block Play |

`LINK_TOKEN_UNAVAILABLE`, `NOT_CONFIGURED`, transport-unavailable diagnostics, and cancellation codes may be produced locally by the native client; they are capability/transport results, not server success.

## Logging and native boundary

Server logs redact authorization/cookies, tokens, passwords and password-confirmation fields, codes/verifiers/challenges, email, session/account/user/store ids, claimed ids, and friend-id arrays. Epic/GOG verification uses fixed HTTPS endpoints, an 8-second timeout, and `redirect: error`. R2 receives only sanitized bytes plus kind/dimensions/SHA-256 metadata.

Native code owns the DPAPI session, provider token access, loopback listener, system browser, file picker/upload stream, bounded media cache, REST client, and WebSocket. React gets neither a raw bearer/store token nor a local upload/cache path. Do not add such values to RPC payloads, exception text, logs, browser storage, query strings, or share HTML.

`BETTER_AUTH_SECRET` also derives verified-store encryption and HMAC keys. Rotating it without migrating existing store-link ciphertext and indexes makes those links unreadable/unmatchable; secret rotation therefore needs an explicit data migration, not a blind replacement.
