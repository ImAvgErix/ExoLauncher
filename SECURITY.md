# Exo Launcher security model

## Trust boundaries

- The unpackaged WinUI 3 desktop process runs as the signed-in Windows user (`asInvoker`). Store backends remain vendor processes.
- The product UI is React in WebView2, served from shipped `wwwroot` assets through the native host. `WebHostBridge` exposes bounded operations; raw Exo bearer tokens, Epic/GOG verification tokens, and filesystem paths for online profile/media requests stay in native code.
- The optional exo-id Worker is a separate HTTPS boundary backed by D1, R2, and one hibernating Durable Object per presence user. It is not in the library, install, update, or launch call graph.
- Dependency installs and purchases require user action. Exo does not silently force system changes.

## Identity and transport

- Email/password account creation and sign-in use only the guarded `POST /api/auth/sign-up/email` and `POST /api/auth/sign-in/email` routes. Bodies are strict JSON capped at 2 KiB; passwords are 12–128 characters and are stored by Better Auth as salted Scrypt hashes in D1. Email is not currently verified and there is no password-recovery route.
- Google, email magic-link, and Steam browser flows use the system browser plus an ephemeral `127.0.0.1`/`::1` callback. Auth uses one-time codes, state, and PKCE S256; Steam uses its one-time OpenID assertion flow.
- Password responses drop cookies and body tokens. The native host accepts the single bounded bearer-session header, stores it in a current-user DPAPI blob, and best-effort restricts that file to the current user. It never stores the password in `settings.json`, online caches, WebView2 storage, or a URL. The DPAPI user boundary remains the protection if ACL hardening is unavailable; same-user malware can still access the user's authority, because DPAPI is not an application sandbox.
- Release builds pin one exact HTTPS exo-id origin in `ExoIdContract.ProductionOrigin`. Environment/explicit origins are accepted only for loopback development or when they match that pin. A configured pin is a trust decision, not proof that the service is deployed or healthy.
- HTTP responses, WebSocket frames, cursors, ids, media, and activity strings are bounded and validated. Cleartext presence WebSockets are loopback-only.
- Missing Google/Resend configuration or a missing local Epic/GOG token source disables only that provider action. Email/password remains separately capability-reported. The client and server return a capability error; they do not invent credentials, store ids, users, or success.

## Data handling

- Application logs redact authorization/session material, passwords, email, provider/store tokens, store-native ids, friend-id lists, and account/user ids. React supplies the current password form value to the native RPC but never receives a password, hash, bearer token, or raw server body in response; password fields are cleared around submission.
- Verified store ids are AES-GCM encrypted at rest with account/store-bound additional data and indexed by a keyed HMAC. Epic/GOG tokens are used for one non-redirecting verification request and then discarded.
- Public profile, search, share, and media reads apply profile visibility plus block/suppression checks. Social transitions apply request, block, and suppression rules; presence requires a connected friend and applies activity visibility. An authorization denial is returned as not found where revealing existence would create an oracle.
- Uploaded PNG/JPEG/WebP/GIF avatar, banner, and gallery bytes are structurally parsed and sanitized before R2 storage. Identifying/unsafe metadata is removed, GIF frames/decoded pixels are bounded, animated WebP is rejected, and D1 ownership/version plus R2 size/type/hash metadata must agree before a read succeeds.
- Staff roles are assigned only through D1 operations; there is no HTTP role-mutation path. Badge management reauthorizes on every request, accepts only fixed keys/projections, hides target ids/roles/email, and cannot turn a visual badge into authority. Founder is database-exclusive.
- Presence is friend-only and per-user. Activity privacy strips game id/title. A backend miss is `unknown`/`unavailable`; only an authoritative reachable state is `offline`. Every heartbeat/status revalidates the session; revoked sessions close with code `4003`.
- Account deletion requires a session created within 15 minutes and is cleanup-first: related peers' cached presence rows, the owner's presence state/sockets, and R2 media must be removed before the D1/Better Auth account is deleted. Cross-store cleanup is ordered but not one atomic transaction.

## Anti-cheat safe (always)

- No kernel hacks or bypass for Vanguard, EAC, BattlEye, or Steam DRM.
- Cleanup after launch only soft-closes store **UI** processes — never anti-cheat services (`vgk`, `vgc`, EasyAntiCheat, BattlEye).
- Optional upscaler updates for DLSS / FSR / XeSS replace only files the game already ships, keep `.exo-bak` originals, and refuse anti-cheat titles. They are user-requested file swaps, not silent patches.

## Reporting

Open a GitHub issue on [ImAvgErix/ExoLauncher](https://github.com/ImAvgErix/ExoLauncher) for security-relevant bugs. Do not include tokens, account exports, store ids, local paths, or other personal data in a report. Bypass requests will be closed.
