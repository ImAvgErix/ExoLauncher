# exo-id

Optional Exo identity/social API for Exo Launcher. It provides D1-backed email/password accounts, optional email-magic-link/Google sign-in, reserved handles, portable profile/preferences, privacy-aware public profiles and search, profile media, direct friends and blocks, verified mutual store discovery, and friend presence.

Exo stays complete while signed out or offline. Library, scan, install, update, launch, Play, Remove, and local settings must not call this service. There is no chat or tray agent.

The native Windows modules are the service/network boundary. React briefly holds only the password a person types, sends it once through the native bridge, and clears the form; it never calls these routes directly or stores/logs/caches that value. Native code alone receives raw service responses, owns bearer/store tokens, writes the DPAPI-protected session, and handles upload paths. The current service is live at `https://exo-id.exo-erix.workers.dev` on D1 `exo-id`, R2 `exo-id-media`, and SQLite-backed `PresenceDurableObject`. Password sign-up/sign-in, bearer handoff, duplicate and invalid-credential normalization, and bounded auth/session bodies were production-smoke-tested with disposable accounts and cleaned afterward; the database is empty again. Google and magic-link login remain unproven and disabled because no real provider credentials are configured. See [CONTRACT.md](CONTRACT.md), [ADR-0004](../../docs/adr/0004-cross-store-friend-linking.md), and [ADR-0005](../../docs/adr/0005-online-profiles-presence.md).

## Runtime

| Layer | Binding | Purpose |
| --- | --- | --- |
| Cloudflare Worker | `src/index.ts` | Hono routes and Better Auth browser callbacks |
| D1 | `DB` | Accounts, sessions, handles, portable fields, privacy, social graph, verified-link ciphertext/indexes, media ownership |
| R2 | `PROFILE_MEDIA` | Sanitized current avatar/banner objects |
| Durable Object | `PRESENCE` / `PresenceDurableObject` | Per-user SQLite state and hibernating WebSockets |

Pinned packages are Better Auth 1.7.1, Hono 4.13.3, Wrangler 4.124.0, and `@cloudflare/vitest-pool-workers` 0.22.0. Better Auth requires `nodejs_compat`; D1 is passed directly as its database.

Provider capabilities are independent:

The operator checklist for enabling the optional providers is in
[docs/provider-setup.md](../../docs/provider-setup.md). It contains callback,
sender, secret, deploy, and redacted health-check steps without embedding any
credential.

- `BETTER_AUTH_SECRET` is required for the service.
- `POST /api/auth/sign-up/email` and `POST /api/auth/sign-in/email` use Better Auth's D1 `user`, `account`, and `session` rows and need no mail credential. The sign-up response is normalized to `200 { "ok": true }` and creates no session; native sign-in alone receives `set-auth-token` and persists it with DPAPI. The combined create-and-sign-in flow does not prove email ownership.
- Google needs `GOOGLE_CLIENT_ID` plus secret `GOOGLE_CLIENT_SECRET`.
- Email magic links need `RESEND_FROM` plus secret `RESEND_API_KEY` in production. Password email ownership is unverified today, and verification, reset, and recovery routes are not implemented; adding a sender alone does not expose them.
- Steam linking uses OpenID 2.0 and needs no Web API key or Steamworks app.
- Epic/GOG linking accepts only the existing Legendary/gogdl token supplied by the native host, verifies it once against the store, and discards it.

The server protocol can match all three linked stores. The current Windows host has native mutual-friend proof sources for Steam's active local account and Epic's authenticated provider friend list. GOG does not expose a supported mutual list on this build, so GOG automatic match returns local `MATCH_SOURCE_UNAVAILABLE`; Exo does not convert missing provider proof into an empty or successful match.

If Google, Resend, or a local store token/proof source is absent, only that operation is unavailable. Password accounts remain available independently, but they do not imply verified email or recovery. Missing Google credentials and missing production Resend key/from have stable capability errors. The deployed `/v1/health` advertises `providers.password=true` separately from the magic-link `providers.email` flag; clients still handle start-time and transport failure. Neither side may fabricate a credential, provider identity, store id, friend proof, successful email send, or successful link.

## Local development

Requires Node 22 or newer.

```powershell
cd services/exo-id
npm ci
if (-not (Test-Path .dev.vars)) { Copy-Item .dev.vars.example .dev.vars }
npm run migrate:local
npm test
npm run dev
```

Fill at least `BETTER_AUTH_SECRET` in `.dev.vars`; keep `ENVIRONMENT=development` and `BETTER_AUTH_URL=http://127.0.0.1:8787`. Google and Resend may stay blank. Magic-link routes stay disabled until both `RESEND_API_KEY` and `RESEND_FROM` are set; local/test still writes `email_outbox` instead of calling Resend.

Useful checks:

```powershell
npm run typecheck
npm run check
npm run deploy:dry
```

`.dev.vars` and Wrangler local state are uncommitted. The setup helper writes local variables only; it does not create remote resources, set production secrets, or deploy.

## Production preparation

These steps describe the required operator work. Repository configuration and passing tests do **not** prove that a resource exists, a secret was set, a provider works, or a Worker is live.

1. Choose the exact public HTTPS origin and keep it consistent in `vars.BETTER_AUTH_URL`, Google callbacks, Steam return URLs, and the native `ExoIdContract.ProductionOrigin` pin. Do not document or guess an origin before it is owned and verified.
2. Create or identify one D1 database. If creating it once:

   ```powershell
   npx wrangler d1 create exo-id
   ```

   Put the returned id in `d1_databases[0].database_id`; this checkout already contains the verified production id. A `00000000-...` placeholder must never be deployed. Confirm `binding=DB`, `database_name`, and `migrations_dir=migrations` describe the intended resource.
3. Create or identify the R2 bucket named by `r2_buckets[0].bucket_name`. If creating the configured name once:

   ```powershell
   npx wrangler r2 bucket create exo-id-media
   ```

   Confirm the `PROFILE_MEDIA` binding points to that bucket. Never repoint a live deployment casually; account deletion and replacement cleanup rely on the binding.
4. Keep the Durable Object declaration in configuration:

   - binding `PRESENCE` → class `PresenceDurableObject`;
   - declarative export `PresenceDurableObject` with `type: durable-object` and `storage: sqlite`.

   There is no separate Durable Object create command. Wrangler provisions the namespace/storage from those declarations during deployment.
   Keep the hourly `0 * * * *` cron: it removes expired auth codes/logins/store-link attempts, match claims, custom rate-limit rows, and releasable handle tombstones.
5. Set production variables in configuration: `ENVIRONMENT=production` and the exact HTTPS `BETTER_AUTH_URL`. Add `GOOGLE_CLIENT_ID` and `RESEND_FROM` only when enabling those providers.
6. Put secrets through Wrangler, never in git:

   ```powershell
   npx wrangler secret put BETTER_AUTH_SECRET
   ```

   Generate a strong independent secret. The same value derives store-id encryption and HMAC keys; rotation requires migrating existing store links before replacing it. Add these only for enabled providers:

   ```powershell
   npx wrangler secret put GOOGLE_CLIENT_SECRET
   npx wrangler secret put RESEND_API_KEY
   ```

   `GOOGLE_CLIENT_ID`, `BETTER_AUTH_URL`, and `RESEND_FROM` are configuration variables, not secret values.
7. Apply every D1 migration to the selected remote database, then dry-run and deploy:

   ```powershell
   npx wrangler d1 migrations apply exo-id --remote
   npm run deploy:dry
   npx wrangler deploy
   ```

8. After an authorized deployment, verify health, browser callbacks, media upload/read/delete, WebSocket plus REST presence, account export, and cleanup deletion against disposable test accounts. The current deployment passed the non-provider core checks on 2026-08-19 and the Windows client pins its exact HTTPS origin. Re-run these checks after any binding, secret, migration, or origin change.

### Google (optional)

Create a Google OAuth client of type **Web application**. Authorized redirect URI:

```text
{BETTER_AUTH_URL}/api/auth/callback/google
```

Use only `openid email profile`. The client secret stays in Wrangler. Google never sees the desktop loopback URI; exo-id completes Google first, then hands a one-time code to the native loopback callback.

### Resend email (optional)

Configure a sending domain, `RESEND_FROM`, and `RESEND_API_KEY`. A well-formed email start remains enumeration-safe; when Resend is absent in production the API returns `EMAIL_NOT_CONFIGURED` rather than pretending an email was sent.

Apple is not implemented. Do not add it to the UI or provider list without a new decision and a supported HTTPS handoff.

## Data and availability rules

- Store-native ids are AES-GCM encrypted at rest and HMAC-indexed. Unmatched friend ids are request-only and are not persisted.
- D1 permits one linked account per user/provider and one Exo owner per verified provider account. Same-owner verification is idempotent; ownership moves only after replacement or explicit unlink succeeds.
- Media accepts bounded PNG/JPEG/WebP/GIF (including gallery slots), strips identifying/unsafe metadata, stores sanitized bytes under versioned account-owned keys, and authorizes every read through D1 profile policy.
- Presence uses 30-second client heartbeats, a 90-second server lease, at most eight sockets per account, and 24-hour peer-cache retention. `unknown/unavailable` is a backend miss; `offline` is authoritative. Activity privacy hides game fields.
- Account deletion removes presence state/sockets and R2 objects before the D1/Better Auth account. If presence cleanup cannot be confirmed, deletion returns an error for retry.
- Exo's custom rate-limit keys are scoped hashes; Better Auth's database limiter retains its IP-plus-route key. Application logs still redact passwords, tokens, emails, session/account/store ids, and friend-id lists.

## Scripts

| Script | Purpose |
| --- | --- |
| `npm test` | Unit and Wrangler-local Worker tests |
| `npm run typecheck` | TypeScript check without emit |
| `npm run check` | Typecheck, then all tests |
| `npm run migrate:local` | Apply migrations to local D1 |
| `npm run dev` | Run `wrangler dev` |
| `npm run deploy:dry` | Build a deployment bundle without publishing |

There is intentionally no publish script.
