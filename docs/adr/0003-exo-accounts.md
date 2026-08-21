# ADR-0003: Exo accounts

## Status

Superseded by [ADR-0005](0005-online-profiles-presence.md) — 2026-08-19.

The 2026-08-18 decision below rejected an account requirement and chose option A. On 2026-08-19 that decision was explicitly reversed to option B: an **optional**, offline-first account whose identity/social features never gate library, install, update, launch, or local settings. ADR-0005 is the accepted current decision; the rest of this file is retained as the historical analysis behind the reversal.

## Context

The ask is an **Exo account**: create one during onboarding; email, Apple, and Google sign-in; a unique handle nobody else can take; settings and profile synced to that account; “free if possible.” Better Auth was suggested ([better-auth.com](https://better-auth.com/)).

At the time, that overturned hard stop 4 in `AGENTS.md`: “No account, no ads, no tray agent, no analytics by default.” It also contradicted the then-current privacy statement on 2026-08-18. Both documents changed when [ADR-0005](0005-online-profiles-presence.md) accepted the optional account.

This is not a green field. Changelog **1.0.20** (2026-08-10) already removed Exo account, profile-sync, and cloud playtime code from Launcher. The local profile that exists today is authored on this PC and stored in `%LOCALAPPDATA%\ExoLauncher\settings.json`. `SocialService` says so in as many words: no account, no server, no directory. The roster is handles the user typed. Store friends stay per-store.

Exo’s value is that it commands Steam, Epic, GOG, and Riot without owning those credentials. An Exo account is a different product: Exo becomes an identity provider. A half-built one is worse than none — users will treat a reserved handle as property, then the backend will be the thing that can lose it.

Better Auth cannot live inside the WinUI process. It is a TypeScript library that runs on a server, talks HTTP, and stores users in a database. Shipping it means hosting a public HTTPS API, a database, OAuth client secrets, transactional email, backups, and a deletion process. The React UI in WebView2 is the wrong place to put Google or Apple sign-in.

## Options

### A — No account (recommended)

Keep hard stop 4. Keep `PRIVACY.md`. Profiles stay local. Friends stay per-store plus the local roster. Costs nothing. Ships nothing new. “Add other Exo people” stays impossible, because there is no directory to look them up in.

This is the current product. It is also the thing 1.0.20 already chose.

### B — Account optional

Everything that launches, installs, and lists games keeps working with no account and no network. Signing in adds a reserved handle, a profile that is theirs, later Exo-to-Exo friends, and a portable subset of settings.

This costs money every month, operational ownership forever, and amendments to `AGENTS.md` and `PRIVACY.md`. Onboarding must not require it.

### C — Handle registry without a full account

A unique human-readable handle is a globally unique string. That needs a server-side uniqueness constraint. Without authentication, the registry is a squatters’ market: whoever hits the endpoint first owns `erik`. There is no way to prove the handle is theirs, recover it, or delete the person behind it.

A public-key identity without a handle (a device-generated key, something like SSH) can prove “this is the same app install” and does not need a central name service. It does not give the user a handle nobody else can take.

C does not satisfy the ask. It is not cheaper in the ways that matter: you still run a public API, still take GDPR requests, still get abused. Skip it.

## Superseded decision

**Do not build an Exo account in Launcher.** Choose option A.

The unique-handle and Exo-to-Exo friends request is real. It is also a different product from a local-first Windows library UI. Putting it in onboarding would train people that Exo needs an account to be Exo. The launch, install, and library paths would then have a cloud dependency hanging over them even if the code tries not to call it.

If this is reversed later, the only honest shape is **B**: optional, offline-first, no Apple in the first phase, no settings sync in the first phase. The rest of this document is the costed plan for that reversal — not permission to start it.

### Why not Better Auth-in-the-app, and why not “free”

Better Auth is MIT-licensed ([LICENSE.md](https://github.com/better-auth/better-auth/blob/main/LICENSE.md), copyright Bereket Engida). It is not a hosted auth service you point the desktop app at. Official installation requires a Node or edge runtime, a database (SQLite / Postgres / MySQL / Mongo, or D1 as of [Better Auth 1.5](https://www.better-auth.com/blog/1-5)), a `BETTER_AUTH_SECRET`, and a public `BETTER_AUTH_URL` with a catch-all handler at `/api/auth/*` ([Installation](https://www.better-auth.com/docs/installation)). Stateless mode exists; most plugins, including username, need a database.

Hono on Cloudflare Workers is a documented path ([Hono integration](https://www.better-auth.com/docs/integrations/hono)), with `nodejs_compat` / `nodejs_als` because Better Auth uses `AsyncLocalStorage`. D1 can be passed as `database: env.DB`. That is still **Exo’s** Worker, **Exo’s** D1, **Exo’s** domain, **Exo’s** uptime.

Apple Sign In is not free. Sign in with Apple is a paid Apple Developer Program capability ([What’s included](https://developer.apple.com/programs/whats-included/)). Enrollment is **99 USD per membership year**, or local currency where available ([Compare memberships](https://developer.apple.com/support/compare-memberships/), [Enrollment](https://developer.apple.com/support/enrollment)). Better Auth’s Apple provider also needs Team ID, Key ID, a `.p8` key, and HTTPS return URLs — Apple rejects `localhost` and non-TLS ([Better Auth Apple](https://www.better-auth.com/docs/authentication/apple)).

## Desktop OAuth (the hard part)

### What is not allowed

Google blocks OAuth in embedded webviews (`disallowed_useragent`). An application that embeds a webview can intercept the login form, cookies, and keystrokes; IETF already forbade this in RFC 8252. Google’s cutover for embedded webviews was 2021 ([Google Developers Blog](https://developers.googleblog.com/upcoming-security-changes-to-googles-oauth-20-authorization-endpoint-in-embedded-webviews/)). Exo’s UI is React inside **WebView2**. A Google button that loads accounts.google.com in that WebView2 will fail. GOG login already uses WebView2; that path must not be reused for Google.

Apple’s return URLs must be HTTPS on a real domain. Loopback `http://127.0.0.1` is not an Apple return URL.

### What is allowed

[RFC 8252](https://www.rfc-editor.org/rfc/rfc8252.html) §7.3: desktop native apps open the **system browser**, listen on the loopback interface, and redeem the authorization code with **PKCE** (RFC 7636). Redirects look like `http://127.0.0.1:{port}/…`. Native apps are public clients; they cannot keep a client secret.

Google still supports loopback for **desktop** OAuth clients. It removed loopback for iOS, Android, and Chrome app client types, not desktop ([Loopback migration](https://developers.google.com/identity/protocols/oauth2/resources/loopback-migration)). Register a Desktop app client, not a Web application client, for the loopback hop ([OAuth 2.0 for Mobile & Desktop Apps](https://developers.google.com/identity/protocols/oauth2/native-app)).

Google consent-screen **Testing** is capped at 100 listed test users; test authorizations expire in seven days **except** when the only scopes are `openid` / `email` / `profile` (or Sign in with Google) ([Manage App Audience](https://support.google.com/cloud/answer/15549945)). Sign-in-only can publish without a sensitive-scope security assessment. A privacy-policy URL is still required to look like a real app. Stay on Testing and Google sign-in is a 100-person toy.

### How it would fit this WinUI 3 + WebView2 host

Better Auth’s React client is a browser client. It expects cookies on the auth origin. The system browser’s cookie jar is not WebView2’s cookie jar. Completing OAuth in Edge/Chrome does not sign the WebView2 shell in.

Better Auth’s [Electron integration](https://www.better-auth.com/docs/integrations/electron) is the closest documented native pattern: main process opens the system browser, PKCE lives off-renderer, a deep link or pasted code is exchanged for a session, tokens never go to the UI. That package is Electron IPC. It does not run in WinUI. A reversal of this ADR would mean a C# equivalent in the host (not in `ui/`): `HttpListener` on `127.0.0.1`, `Process.Start` the system browser, PKCE in memory, POST the code to Exo’s auth server, store the session in the host.

Two provider shapes:

| Provider | Browser | Redirect | Then |
| --- | --- | --- | --- |
| Google | System browser | Loopback `127.0.0.1` (desktop client) **or** HTTPS callback on Exo’s domain | Host exchanges code / id token with Exo’s server |
| Apple | System browser | **Only** HTTPS on Exo’s domain (no loopback) | Server creates session, then redirects to loopback or shows a one-time paste code |
| Email | System browser or a thin HTTPS page | Magic link hits Exo’s domain | Same handoff as Apple |

Do not put Google’s or Apple’s client secret in the Exo binary. Desktop is a public client. Secrets stay on the server.

### Token storage on Windows

Exo is an unpackaged WinUI 3 app under `%LOCALAPPDATA%\ExoLauncher`. That matters.

**Credential Locker (`PasswordVault`)** is documented per-user, and AppContainer apps get an isolated locker. Unpackaged full-trust desktop apps write a **shared** locker: other full-trust processes running as the same Windows user can read it. WinAppSDK maintainers have said PasswordVault does not behave as UWP developers expect for WinUI 3 ([WindowsAppSDK #1840](https://github.com/microsoft/WindowsAppSDK/discussions/1840)).

**DPAPI** (`ProtectedData.Protect` / `Unprotect`, `DataProtectionScope.CurrentUser`) encrypts to the Windows user profile ([ProtectedData.Protect](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.protecteddata.protect)). Any process as that user can decrypt. That is the real desktop ceiling, not a bug.

Honest store if B is ever built:

- Session (and refresh, if any) in a DPAPI-CurrentUser blob, e.g. `%LOCALAPPDATA%\ExoLauncher\auth.bin`, ACL’d to the current user. Not `settings.json`. Not WebView2 `localStorage`. Not logs.
- `WebHostBridge` attaches the session only on account RPCs. React never sees the raw token.
- Sign-out deletes the blob. Account deletion deletes the blob and the server row.
- Refresh tokens, if Google issues one, stay on the **server** (Better Auth `account` table) where possible. The desktop app holds an Exo session, not a Google refresh token. Google refresh tokens for Testing apps expire in seven days except on the basic profile scopes above.

Malware as the signed-in Windows user can steal the session. Say that. Do not pretend the locker makes it app-private.

## Cost

Prices checked 2026-08-18. USD. They move.

### What is never free

| Item | Cost | Notes |
| --- | --- | --- |
| Sign in with Apple | **$99 / year** (~$8.25 / month) | Apple Developer Program. No free-tier Sign in with Apple. |
| A public hostname + TLS | Domain ~$10–15 / year | Apple requires HTTPS. Google production needs a privacy-policy URL. |
| Operational ownership | Human time | Uptime, key rotation, abuse, deletion requests. This is the real bill. |

Google OAuth client IDs are free. Better Auth the library is free (MIT). Hosting, email, and Apple are not.

### Assumptions for the scale table

Low-chat identity: sign-in, session check, handle claim, occasional profile write. No presence, no chat, no library upload. ~2–5 API calls per open, not a poller. Avatars either stay local or stay tiny.

If settings sync or friends-online is added, multiply request volume; the Free-tier cliffs below are why production should not sit on Free.

### Better Auth on Cloudflare Workers + D1

[Workers pricing](https://developers.cloudflare.com/workers/platform/pricing/): Free = 100,000 requests / day, 10 ms CPU / invocation. Paid = **$5 / month** minimum, 10 million requests and 30 million CPU-ms included, then $0.30 / million requests. Hitting Free’s daily cap returns **error 1027** and the Worker stops ([Workers limits](https://developers.cloudflare.com/workers/platform/limits/)). For an auth API, fail-open is wrong (requests would bypass auth). Free means sign-in dies at the cap until 00:00 UTC.

[D1 pricing](https://developers.cloudflare.com/d1/platform/pricing/) (updated 2026-04-21): Free = 5 million rows read / day, 100,000 rows written / day, 5 GB storage. Exceed Free reads/writes: queries **error** until the next UTC day. Paid included: 25 billion reads / month, 50 million writes / month, 5 GB, then $0.001 / million reads, $1 / million writes, $0.75 / GB-month.

Email is separate. [Resend](https://resend.com/pricing): Free = 3,000 emails / month, **100 / day**. Pro from $20 / month (50,000). A launch-day spike of magic links burns the daily cap first.

| Users | Honest monthly | Why |
| --- | --- | --- |
| 100 | **$5** | Workers Paid. Do not run production auth on Free 1027. D1 included. Resend Free if Google is the common path. |
| 1,000 | **$5** | Still inside 10M requests / 50M D1 writes if the client stays quiet. |
| 10,000 | **$5–25** | Compute likely still $5. Email is the overage: a verification wave needs Resend Pro (~$20). |

Apple on top: +$8.25 / month amortized.

### Better Auth on a Node host + Postgres (Neon) or libSQL (Turso)

Better Auth’s first-class databases are SQLite, Postgres, MySQL, Mongo, and now D1. Turso is libSQL: works through Kysely/`@libsql/kysely-libsql` or Drizzle’s SQLite provider, not an official Turso adapter. Extra moving parts.

You still need a **Node (or Bun) process** plus the database. Neon Free does not include that process. Fly / Render / Railway are typically another $5–7 / month once free spin-down is refused (an auth API that sleeps for minutes is a bad sign-in).

[Neon plans](https://neon.com/docs/introduction/plans): Free = $0, 0.5 GB / project, 100 CU-hours / project / month, scale-to-zero after 5 minutes (cannot disable), 5 GB egress. Hitting CU-hours or egress **suspends compute until the next month**. Hitting 0.5 GB: writes fail. Launch is pay-as-you-go: $0.106 / CU-hour, $0.35 / GB-month, no monthly minimum. Always-on 0.25 CU is on the order of **$19 / month** compute before storage.

[Turso pricing](https://turso.tech/pricing): Free = 100 databases, 5 GB, 500 million row reads / month, 10 million row writes / month. Developer = **$4.99 / month**. Overages optional; if disabled, that metric blocks.

| Users | Neon path | Turso path |
| --- | --- | --- |
| 100 | **$5–7** host + $0 Neon Free, or **~$25** if you refuse scale-to-zero | **$5–7** host + $0 Turso Free |
| 1,000 | Same, until CU-hours or 0.5 GB bite → Launch ~$10–20 DB + host | Still Free DB + host, or $4.99 Developer for DPA / PITR |
| 10,000 | **$15–40**: host + Launch compute/storage. 0.5 GB Free is the first cliff (avatars, sessions). | **$10–15**: host + Free or Developer. 10M writes / month is fine for this workload. |

Neon Free also advertises Managed Better Auth MAU on their plans page. That is Neon’s product, not a reason to put Better Auth inside ExoLauncher.exe.

### Supabase Auth (auth + Postgres + RLS)

[Supabase pricing](https://supabase.com/pricing.md): Free = $0, 50,000 MAU, 500 MB database, 5 GB egress, 1 GB file storage, **paused after 1 week of inactivity**, 2 active projects. Pro = **$25 / month** (includes $10 compute credit covering one Micro), 100,000 MAU then $0.00325 / MAU, 8 GB disk, no pause.

Free pausing is disqualifying for an identity service. Spend cap is on by default on Pro; exceeding quota with the cap on means a grace period, not silent unbounded bills ([MAU](https://supabase.com/docs/guides/platform/manage-your-usage/monthly-active-users)).

| Users | Honest monthly |
| --- | --- |
| 100 | **$25** Pro. $0 Free only if you accept pause-kills-sign-in. |
| 1,000 | **$25** |
| 10,000 | **$25** (MAU still under 100k). Avatars in Storage still inside 100 GB included. |

Supabase is more product per dollar at small scale: hosted Postgres, Auth, RLS, Storage, dashboard. Less TypeScript to own. You still implement RFC 8252 in C#; Supabase’s JS client in WebView2 has the same Google-webview problem.

### Comparison

| | Workers + D1 + Better Auth | Node + Neon/Turso + Better Auth | Supabase Auth |
| --- | --- | --- | --- |
| Floor (production) | $5 / month | $5–25 / month | $25 / month |
| 10k users | $5–25 | $10–40 | $25 |
| Pause / hard stop | Free: 1027 / D1 daily errors | Neon Free: suspend; Turso Free: block metric | Free: pause after 1 week |
| Own the auth code | Yes | Yes | No (you own RLS policies) |
| Apple | +$99 / year on all three | same | same |

“Free if possible” is true for a private prototype on Cloudflare Free or Supabase Free. It is not true for something people depend on for a handle. The honest production floor without Apple is **$5 / month** (Workers Paid) or **$25 / month** (Supabase Pro). With Apple it is that plus **$99 / year**.

## Handles

Local handles today: lowercase letters, digits, underscore; max 24 (`SocialService.HandleMax`). They are not unique across humans.

Minimum honest global handle:

1. **Charset:** keep ASCII `[a-z0-9_]`, length 3–24. Do not allow Unicode. That is the homoglyph policy: refuse `еrix` (Cyrillic ie) instead of mapping confusables ([UTS #39](https://unicode.org/reports/tr39/) exists; you should not need it if the alphabet is ASCII).
2. **Normalize:** lowercase before compare. Unique index on the normalized value. Better Auth’s [username plugin](https://www.better-auth.com/docs/plugins/username) already lowercases; it also allows dots by default — turn that off to match Exo. Use `immutableUsername` or a one-change cooldown.
3. **Reserve list:** `exo`, `official`, `admin`, `support`, `help`, `steam`, `epic`, `gog`, `riot`, `system`, plus short numeric traps. Validator runs server-side, not only in the UI.
4. **Claim:** authenticated, in a transaction, on the unique index. “Is it free?” is optional and must be rate-limited; Better Auth can disable `/is-username-available` to cut enumeration (`disabledPaths`).
5. **Abandon:** on delete, drop PII immediately; keep a tombstone of the normalized handle for 12 months so a squatter cannot instantly become yesterday’s `@erik`. After 12 months, release. GDPR and “nobody else can take it” pull in opposite directions; the tombstone is the compromise. Do not recycle handles of banned-for-abuse accounts.
6. **Not Better Auth username-as-login** unless email/password is the authenticator. The handle is a public name. Sign-in is email / Google / (later) Apple.

## Sync

`settings.json` is `AppSettings` in `ExoLauncher/Models/GameEntry.cs`, loaded by `SettingsService`. There is also `friend-links.json` next to it (store-account links the user asserted). Profile images are filenames in Exo’s cover cache, not URLs.

### Must not sync

Machine-specific. Syncing these across PCs breaks installs and windows.

- `DefaultInstallRoot`
- `LaunchOverrides` (especially `WorkingDirectory`, extra args)
- `AppVersion`
- `CopyPortableIntoLibrary`, `AllowResize` (legacy / unused)
- Trophy **pixel** anchors (`TrophyNotificationPositionX` / `Y`) — monitor layout
- `ProfileAvatarImage`, `ProfileBannerImage` — local filenames
- `OnboardingComplete` — per machine
- Product-enforced flags (`CloseStoreClientsAfterLaunch`, `AntiCheatSafeMode`, …)

### Must not upload

Sensitive or not Exo’s to hold.

- Store credentials (already not in this file)
- `friend-links.json` (Steam/Epic/etc. account ids)
- Playtime databases, library cache, cover cache
- Auth blob
- Absolute paths of any kind

### May sync (portable identity)

Only after a reserved handle exists:

- `ProfileName`, `ProfilePronouns`, `ProfileStatusText`, `ProfileBio`
- `ProfileAccent`, `ProfileLayout`, `ProfileBannerHeight`, `ProfileShowcaseStyle`, `ProfileShowLevel`
- `ProfileSections`, `ProfileHiddenSections`
- Handle is **server-owned**, not a settings field that can be edited into a collision

`ProfileShowcase`, `Favorites`, `Recent`, `LastPlayed` are **library ids on this PC**. The other machine may not have those games. Do not sync them in a first version. If they sync later, sync as opaque ids and drop unknown ones locally — never create fake library rows.

`SortMode`, `TrophyNotificationsEnabled`, and the named trophy preset (`top-right`, etc.) are portable preferences. They are still not phase 1.

### Conflict rule

Last-write-wins **per field**, with a server timestamp. Lists (`ProfileSections`) replace as a whole, never union: unioning resurrects a section the other PC hid.

Two machines offline: each writes locally; on reconnect the newer field timestamp wins. Show a single “signed-in profile from another PC replaced this one” notice, not a merge UI.

Do not auto-push. Push on explicit save of the portable profile, and pull on sign-in. A background sync worker is how you corrupt a quiet PC.

### Export and delete

- **Export:** a JSON file the user asked for: account id, handle, email-or-provider, portable profile fields, timestamps. No tokens, no store ids, no paths.
- **Delete:** server drops user, sessions, profile, and email; writes the handle tombstone; the app deletes `auth.bin` and leaves local `settings.json` alone unless the user also ticks “clear the local profile.” GDPR erasure is the server row, not a factory reset of their library.

## Offline promise

With or without an account:

| Path | May depend on auth? |
| --- | --- |
| Library scan, covers from disk, Play / Install / Update / Remove | **Never** |
| Steam IPC, Legendary, gogdl, Riot | **Never** |
| Local settings read/write | **Never** |
| Store metadata / GitHub updates / owned-library refresh | Network, not auth |
| Claim handle, sync profile, Exo friends directory | Requires account + network |

Signed in but offline: local app is full Exo; the handle stays reserved on the server; profile edits queue locally and push when the user is back, or they don’t — they must not block Play.

No auth call on the launch, install, or library path. No timeout that sits in front of Play. No “re-sign-in to continue” for the library. If the session is dead, the profile badge says so; the rest of the app does not care.

Onboarding: account is a skippable step, or it is not in onboarding at all (Settings). Putting it on the first-run critical path is how optional becomes required.

## Historical phased plan

Phase 1 is the smallest thing that delivers a handle that is actually theirs. Each phase lists what it does **not** do.

### Phase 1 — Reserved handle

Stand up a tiny auth API (Workers Paid + D1, or Supabase Pro). Email magic link + Google, RFC 8252 in the C# host, DPAPI session blob, unique ASCII handle, skippable from Settings (not a wall in onboarding). Amend `AGENTS.md` hard stop 4 and `PRIVACY.md` in the same change.

Does **not:** Apple, password database, settings sync, friends graph, presence, onboarding requirement, WebView2 OAuth, analytics, linking Steam/Discord, storing store credentials, touching launch/install/library.

### Phase 2 — Portable profile

Sync the portable identity fields in “May sync.” Per-field last-write-wins. Export and delete. Avatar upload only if it is a bounded blob on the account, not a path from disk.

Does **not:** favorites/recents/showcase-as-library-ids, launch overrides, install roots, trophy pixel position, friend-links, playtime upload.

### Phase 3 — Exo-to-Exo people

The roster becomes lookups against reserved handles, not free-typed strings. Friend request, accept, remove. Still no chat, no presence, no store-account merge into the Exo graph.

Does **not:** chat, presence, “who is in-game,” store identity linking beyond the local `friend-links.json` the user already made on that PC.

### Phase 4 — Apple, and maybe portable prefs

Apple only after the $99 membership, HTTPS callbacks, and a working Google+email path. Optional sync of `SortMode` / trophy enabled / named trophy anchor. Still no machine paths.

Does **not:** become required, become a launch blocker, grow into a social network.

Ship nothing between phases that people will treat as durable identity unless phase 1’s deletion path already works.

## Risks

**Account takeover.** A WebView2 Google flow, a loopback listener without PKCE, a session in `settings.json`, or a token logged by the bridge, and someone else is `@erik`. Unpackaged DPAPI is stealable by same-user malware; the remaining duty is not making it stealable by everyone else. This is the single biggest technical risk.

**Handle squatting.** The day the registry opens, bots will take short names. Reserved-word list, rate limits, no public unauthenticated availability oracle, delayed recycle. Still expect to referee `exo` lookalikes by hand.

**GDPR / deletion.** An account is personal data (email, handle, provider subject). Need a real delete, a privacy statement that matches, and a processor DPA with whoever hosts (Cloudflare, Neon, Turso Developer+, Supabase). Tombstoning handles for a year is a retention decision that must be written down. “We’ll add export later” is not a policy.

**Sync corrupts settings.** A last-write-wins blob of the whole `AppSettings` would copy `D:\Games` onto a laptop and then Install would aim at a drive that does not exist. That is a reputation hit Exo would deserve. Phase 1 must not sync settings. If a later phase does, the denylist above is the product.

**Backend shutdown.** If the API dies, signed-in users keep launching games (offline promise). They lose the guarantee that the handle is uniquely theirs. Say that in the privacy statement up front: the library does not depend on us; the name does. Provide export before any shutdown. There is no ethical way to “hand handles to another vendor” without the users.

**Scope creep.** Friends imply presence. Presence implies always-on. Always-on implies a tray agent, which hard stop 4 also forbids. Phase 3 stops at a directory.

**Reversal cost.** 1.0.20 already deleted this once. Building a partial second copy in `ui/` while the host still has no loopback listener would be the half-built system this ADR exists to prevent.

## Superseded consequences

- **If A (this decision):** no new hosting, no Apple bill, no GDPR surface for Exo identity, no change to onboarding. Exo-to-Exo “add people” remains not yet. Local profile and per-store friends stay as they are. Hard stop 4 and `PRIVACY.md` stay true.
- **If reversed to B:** budget at least $5/month (Workers) or $25/month (Supabase) before the first reserved handle, $99/year before Apple, a public HTTPS origin, and a C# RFC 8252 client. Better Auth is a server library, not a shortcut around that. `AGENTS.md` and `PRIVACY.md` change in the same commit as the first sign-in button.
- **What hurts:** you become on-call for other people’s names. You will get handle disputes. You will get deletion mail. You will be tempted to sync all of `settings.json`. Don’t.

## Sources (checked 2026-08-18)

- Better Auth: [installation](https://www.better-auth.com/docs/installation), [database](https://www.better-auth.com/docs/concepts/database), [Hono / Workers](https://www.better-auth.com/docs/integrations/hono), [Electron](https://www.better-auth.com/docs/integrations/electron), [Google](https://www.better-auth.com/docs/authentication/google), [Apple](https://www.better-auth.com/docs/authentication/apple), [username](https://www.better-auth.com/docs/plugins/username), [1.5 / D1](https://www.better-auth.com/blog/1-5), [MIT license](https://raw.githubusercontent.com/better-auth/better-auth/main/LICENSE.md)
- OAuth: [RFC 8252](https://www.rfc-editor.org/rfc/rfc8252.html), [Google native apps](https://developers.google.com/identity/protocols/oauth2/native-app), [loopback migration](https://developers.google.com/identity/protocols/oauth2/resources/loopback-migration), [embedded webview block](https://developers.googleblog.com/upcoming-security-changes-to-googles-oauth-20-authorization-endpoint-in-embedded-webviews/), [consent audience](https://support.google.com/cloud/answer/15549945)
- Apple: [membership](https://developer.apple.com/support/compare-memberships/), [enrollment](https://developer.apple.com/support/enrollment), [what’s included](https://developer.apple.com/programs/whats-included/)
- Hosting: [Workers pricing](https://developers.cloudflare.com/workers/platform/pricing/), [Workers limits](https://developers.cloudflare.com/workers/platform/limits/), [D1 pricing](https://developers.cloudflare.com/d1/platform/pricing/), [Neon plans](https://neon.com/docs/introduction/plans), [Turso pricing](https://turso.tech/pricing), [Supabase pricing](https://supabase.com/pricing.md), [Resend](https://resend.com/pricing)
- Windows: [ProtectedData.Protect](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.protecteddata.protect), [PasswordVault](https://learn.microsoft.com/en-us/uwp/api/windows.security.credentials.passwordvault), [WinAppSDK #1840](https://github.com/microsoft/WindowsAppSDK/discussions/1840)
