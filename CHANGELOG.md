# Changelog

## 1.0.24 - 2026-08-11

**Exact source control · session-true trophies · broader honest client support.**

- Kept exact same-title Steam and Epic copies on one card while retaining source-specific ownership, installation, updates, playtime, account scope, achievement lookups, and Play/Install/Update/Stop/Uninstall routing
- Isolated Steam and Epic cache state by active account, and kept machine-proven installs visible without turning unknown account identity into a false ownership claim
- Sent Steam install/update requests directly to the selected app ID instead of letting missing generic runtimes divert the action, and added a bounded Epic cold-start readiness handoff for exact game URIs
- Prepared the achievement baseline before vendor handoff, activated attribution only after a successful launch, and prevented manual/detail refreshes or failed launches from fabricating unlock notifications
- Rebuilt the achievement cue as an original 1.18-second 48 kHz stereo Exo sound and aligned the in-app preview with the native 432×122 AMOLED plate, real achievement art, rarity accents, scale-and-fade motion, and exact nine-point anchoring
- Expanded quiet-mode client catalogs to Xbox, EA app, Ubisoft Connect, Battle.net, Amazon Games, and Rockstar Games Launcher while explicitly protecting services, overlays, anti-cheat, game processes, and user-opened client windows
- Added honest Installed/Not installed and Open-only integrations for those official clients without inventing libraries, account state, or unsupported per-game actions
- Reworked Settings into a compact two-pane workspace with support links, restored card hover lift and clear store wording, constrained pinned-game overflow, upgraded undersized cover art, and made remaining app surfaces pure AMOLED black
- Removed idle Core Audio enumeration and endpoint rebinding while preserving fast 250 ms suppression during active Exo-driven operations
- Moved WebView2 state outside the replaceable application tree and bounded shutdown to Exo-owned WebView children so atomic updates do not strand cache-only rollback folders

## 1.0.23 - 2026-08-11

**Reliable trophies · honest store status · faster first paint.**

- Rebuilt the native achievement notification as one coherent 432×122 trophy plate with exact nine-point screen anchoring, directional entrance/exit motion, reduced-motion support, and Bronze, Silver, Gold, and Platinum accents derived from trusted provider data
- Added validated local caching for real Steam and Epic achievement artwork, including corrupt-cache repair, so the provider's unlocked image is ready when the notification appears
- Added a durable, account-scoped presentation outbox: unlock transitions are saved before dispatch, acknowledged only after the native window opens, and safely replayed after a matching account refresh if Exo closes between those steps
- Replaced the rejected generated cue with Kenney's compact CC0 glass achievement sound, shipped with its source and license notice and validated before playback
- Separated visible vendor clients from Exo's headless backends so bundled gogdl or Legendary can no longer make absent GOG Galaxy or Epic Games Launcher installations appear Ready, Connected, or Openable
- Removed remote Epic playtime from the startup critical path, retained last-good values across transient service errors, deferred background cover networking until after first paint, reduced cold-start cover contention, and added startup milestone logging
- Kept Steam partial achievement summaries tied to Steam's authoritative totals while accepting a newly exposed unlocked row only when an exact one-count provider delta identifies it unambiguously

## 1.0.22 - 2026-08-11

**A sharper Exo achievement signal.**

- Replaced the simple tone stack with an original 0.78-second stereo glass-and-air cue that adds a restrained cinematic lift without reusing or redistributing third-party audio
- Added a deterministic source generator, strict PCM validation with a built-in fallback, and single-flight playback so rapid unlocks cannot stack the reward sound

## 1.0.21 - 2026-08-10

**Quiet game sessions · verified achievements · smarter discovery.**

- Added a safe Stop action for running games with exact executable, install-root, process-instance, and launcher/overlay/anti-cheat protections
- Kept Steam, Epic, GOG, and Riot client chrome and client audio quiet while Exo drives installs, updates, launches, and active game sessions, then restored only audio sessions Exo muted
- Rebuilt trophy notifications as a compact branded Exo signal with an authored reward cue, a fixed 3.5-second display, and nine exact preview-matched screen anchors
- Made Steam zero-achievement results require independent official-catalog confirmation, removed stale Community XML progress, revalidated details on open, and made contradictory Epic totals fail closed
- Added typo-tolerant, punctuation-insensitive, word-order-aware library and store search, including safe accidental sequel-number matching such as `Mortal Shell 2`
- Removed playtime from dashboard cards, redundant detail status, account/profile/sync surfaces and retired local profile data, notification timing/sound choices, and explanatory Settings copy while enlarging the remaining Settings workspace
- Increased update-badge contrast and kept update actions scoped to the exact selected title without surfacing a vendor launcher

## 1.0.20 - 2026-08-10

**Exact game updates · honest achievements · Launcher-only focus.**

- Removed Exo account, profile, profile-sync, and cloud playtime code from Launcher; Exo Link remains the owner of Exo social identity while Steam, Epic, GOG, and Riot authentication stays available
- Replaced Steam's global Downloads action with an exact-app request plus a hidden, fail-closed scheduled-row promotion that requires the selected app ID, manifest title, unique OCR title row, and final queued-state check to agree before it can click
- Stopped treating Steam's empty local `0 / 0` cache as proof that a game has no achievements; stale placeholders now refresh through Steam's own Community catalog while positive local progress remains preferred
- Made the Update badge a high-contrast amber marker that remains readable over light or busy cover art
- Removed the redundant Status row from game details while keeping Playtime, Achievements, and Size there and keeping playtime off dashboard cards
- Removed obsolete profile RPCs, persistence, automatic sync workers, settings, UI modules, and tests without changing store credentials or local launcher data

## 1.0.19 - 2026-08-10

**Quieter UI · reliable updates · corrected lifetime stats.**

- Reworked the Launcher profile surface for a clearer identity and showcase layout
- Reworked Settings into a readable divided two-column workspace with larger type, controls, store connections, and notification configuration
- Moved playtime off dashboard tiles and kept it in the selected game's detail view, where the extra context belongs
- Unified game and launcher update progress inside the original action button with stable, transform-driven fills and restrained state crossfades
- Rebuilt trophy notifications as one compact near-black Exo surface with matching web preview, safe achievement artwork, free positioning, and graceful reduced-motion behavior
- Fixed Steam update nudges to target the exact numeric app through a hidden client command plan and explicitly queue stalled downloads before checking progress
- Reused the cached library during startup so opening Exo and launching store-backed games no longer repeats full adapter scans unnecessarily
- Kept distinct Steam, Epic, and GOG totals additive without double-counting local fallback sessions
- Reconciled provider achievement corrections and retractions while retaining a durable one-time notification ledger, including valid supported zero-progress catalogs

## 1.0.18 - 2026-08-10

**Refined profiles and trophies · automatic sync · quiet-play hardening.**

- Rebuilt Exo Profile into four fixed, keyboard-accessible Identity, Style, Games, and Privacy views with no scrolling cards, a compact six-slot shelf, and a cleaner live canvas
- Replaced the generic Profile titlebar button with a compact Exo identity bar showing the signed-in display name, profile picture, and connection state
- Unified trophies around one DPI-sharp Exo notification surface, improved sound cues, and a freely positionable preview instead of visual-style presets
- Removed the detail-panel trophy cabinet and manual achievement refresh; missing local coverage now refreshes automatically while unsupported sources stay explicit
- Kept an installed game's Update badge, status, and primary action consistent, including Steam manifests that report an update-required state
- Restored the last successful Riot lifetime totals from the former local sync cache as a one-way, account-scoped Exo Tracker observation so valid VALORANT and League hours do not disappear
- Preserved proven Steam ownership across a successful uninstall, including atomic recovery, so Exo can still offer the same owned game for one-click reinstall
- Kept install, update, and uninstall actions behind one serialized job gate, and made an in-app update close through the normal shutdown path so sessions and settings flush first
- Made GOG and portable-game registrations durable across restart; in-place portable removal now unregisters without deleting user files, while only catalog-proven managed copies can be removed recursively
- Upgraded Quiet Game Mode to hide unused launcher shells and request graceful exit while protecting the active store, games, services, overlays, and anti-cheat; Exo never force-terminates a busy client
- Kept the authenticated account display name when an older editable profile name is empty instead of showing the generic Exo Player fallback
- Replaced manual account sync with a native single-flight scheduler that coalesces sign-in, startup, profile/settings, library, achievement, and completed-session changes with offline backoff
- Kept Exo sign-in and last-good profile data usable when one stats endpoint is unavailable, and only advances the sync checkpoint after every playtime and achievement upload succeeds
- Made portable account settings thread-safe and revision-aware, and added a per-device three-way favorite baseline so an unfavorite made on one PC is not resurrected by another
- Hardened on-demand Legendary and gogdl installation with exact official GitHub assets, mandatory published SHA-256 digests, bounded downloads, redirect allowlists, and atomic promotion
- Fixed Riot cold starts that briefly reported an installed League game as missing, including transient product-registry/eligibility reads, slow accepted handoffs, and idempotent already-running sessions
- Made new GOG and managed portable installs stage into unique private folders, promote atomically, and roll back only paths created by that exact operation on cancel or failure
- Restored a hidden Exo notification-area icon automatically after Explorer restarts, and bounded verified in-app updater artifacts with cancellation/failure cleanup

## 1.0.17 - 2026-08-10

**Exo Profile showcase · universal trophies · full-session Quiet Game Mode.**

- Added a standalone, blurred Exo Profile studio outside Settings with a live canvas, first-party profile-picture and banner uploads, themes, frames, nameplates, effects, bio, badges, and privacy controls
- Added a six-game profile shelf with combined lifetime playtime, achievement progress, recent unlocks, per-game visibility, and ordering controls
- Added private achievement discovery and sync for existing Epic and Steam data, with explicit partial-coverage labels and no game injection or raw account identifiers
- Added native, no-focus trophy notifications with four visual presets, position, duration, sound, rarity/perfect variants, and a Settings preview
- Kept vendor windows hidden for the complete detected game session while Exo moves to the notification area; unused clients still close softly and required services remain untouched
- Fixed explicit Open actions so orphan helper processes can no longer prevent the real Steam, Epic, GOG, or Riot client from surfacing
- Extended silent sign-in sync to refresh supported installed/recent game achievements before uploading settings, combined playtime, and trophy progress

## 1.0.16 - 2026-08-10

**Exo Profile and Exo Tracker · faster launches · quieter store control.**

- Replaced the fragile Tracker.gg browser flow with an optional private Exo account, customizable ecosystem profile, portable settings sync, and first-party lifetime playtime aggregation
- Combined distinct Steam, Epic, and GOG histories; retained Exo sessions only as a fallback when no native lifetime total exists
- Scoped Steam observations and GOG owned-library caches to the active store user so shared PCs never merge or expose another user's history
- Added Epic's authenticated lifetime playtime source and restored Rocket League totals across Epic and legacy Steam history
- Fixed GOG connection and owned-library sync with token refresh, pagination, cached metadata, and immediate post-connect refresh
- Reduced launcher handoff delays and kept Steam, Epic, GOG, and Riot windows out of the way while games start
- Moved both game-start and ordinary minimize behavior to the Windows notification area
- Made uninstall direct and quiet while restricting prompt automation to the exact selected game; destructive local removal is guarded to managed game roots
- Expanded Settings into a two-column account, profile, playtime, stores, update, gameplay, and portable-game workspace
- Hardened WebView navigation, settings recovery, updater origin/digest checks, single-instance startup, session recovery, and atomic installer rollback

## 1.0.15 - 2026-08-09

**Tracker.gg sign-in + sync fixed.**

- Sign in opens your Valorant profile for **Sign In with Riot** (old `/auth/login` 404 removed)
- Sync keeps the Tracker.gg window visible (Cloudflare) and reads hours from the page/API
- No extra Riot copy on detail cards; Epic still has no Tracker.gg lifetime hours (Exo sessions only)

## 1.0.14 - 2026-08-09

**Full detail poster · Tracker.gg playtime (opt-in).**

- Detail rail: full 2:3 poster again (no max-height crop / fade)
- Settings → Tracker.gg: Riot ID, Sign in, Sync for Valorant / League

## 1.0.13 - 2026-08-09

**Steam openable again · quieter UI · pin/covers.**

- Playtime: Steam localconfig (as before); Epic/Riot/Local track the real game process through launcher handoff (not bootstrap PIDs); Riot last-played from client settings; GOG Galaxy JSON when present
- Covers: larger 172px poster tiles; search + library paint official Steam CDN posters immediately; faster warm (capsule+classic race, no cancel-on-search, mapped Steam art for Epic titles)
- Buy on Steam / Open Steam: reveal main Steam window only (no steamwebhelper duplicates)
- Covers: portrait posters only — never wide heroes / letterboxing; monogram until a real 2:3 cover exists
- Covers: Epic `catcache.bin` + Legendary metadata for tall art when GraphQL is blocked
- Settings: Open Steam / Epic / GOG / Riot; Add portable folder
- Epic install auto-fetches Legendary; Riot install uses patch API progress when available
- Search: Buy on Steam (browser) when a catalog hit is not owned
- Store hide no longer sets TOOLWINDOW (Steam was stuck unopenable); restore on stop + Settings → Open Steam
- Install progress shown once (detail rail when selected — not library + CTA + detail)
- Pin: layout motion + CoverArt no longer remounts on favorite
- Softer motion; sibling store close only when those clients are actually running
- Cover match threshold raised (fewer wrong/soft posters); Epic/GOG copy clarifies Legendary/gogdl CLI

## 1.0.12 - 2026-08-09

**League Play click + Steam scheduled updates.**

- Riot: auto-click product-page Play (UIA + relative CTA) when warm client ignores `--launch-product`
- Steam: stop treating every “Steam” window as an install dialog (false Enter accept stalled updates at 0 bytes)
- Steam update: click Downloads → **Download now** when Steam parks updates as Scheduled (overnight window)
- Steam update: keep re-nudging `steam://install` until BytesDownloaded moves

## 1.0.11 - 2026-08-09

**League launch + Steam update/shutdown.**

- Riot: warm client only opens the product page — restart Riot then cold `--launch-product`
- Riot: never hide LeagueClient; close unused stores at launch start
- Steam unused clients: official `-shutdown` / `steam://exit` (WM_CLOSE was a no-op)
- Steam update/install: keep Steam visible until bytes actually download (hide-on-click was stalling)

## 1.0.10 - 2026-08-09

**Steam update kick + close unused store clients.**

- Steam update: click Update, keep Steam visible until download starts, re-nudge stuck queues
- On launch, soft-close other store clients (never Vanguard / anti-cheat)
- Search: ignore cancelled RPC so results do not wipe
- CoverArt remount key preserves art across pin

## 1.0.3 - 2026-08-09

**Library polish, Store search, locked defaults.**

- App update: close + relaunch after quiet install
- Hide Steamworks / redistributables / tools from library
- Not-installed tiles fully greyscale; hover shows large library poster
- Store tab: live search (Steam + owned Epic/GOG) with install path picker
- Settings: remove resize / updates / redist / close-store toggles (locked on)
- Fixed window size; Local / portable removed
- Epic/GOG Sign in opens visible console (import Epic session first when possible)
- Cover seed for MECCHA CHAMELEON

## 1.0.2 - 2026-08-09

**Cover art quality + library layout.**

- Prefer Steam CDN `library_600x900_2x` posters (multi-CDN fallback)
- Title → Steam app id search (community SearchApps + storesearch + seed map)
- Larger 2:3 library cards (`minmax(168px)`); portrait detail cover
- Allowlisted Steam/GOG/Riot CDNs in CSP so art shows while cache warms
- Upgrade disk cache from low-res 1x to hi-res 2x on warm

## 1.0.1

**Distinct Exo Launcher brand mark.**

- New app icon and logo: mint launch chevron (separate from Hub).
- Title-bar logo asset; README brand mark; favicon update.

## 1.0.0 - 2026-08-07

Phase 1 public release + polish pass.

### Product

- Fixed 1400×900 AMOLED shell (WinUI 3 + React + WebView2); optional resize
- Library UI: covers (cached + monogram fallback), title, store dot, favorites, sort, recents
- Detail rail: **Play | Install | Update**, secondary actions (folder, uninstall, favorite), install speed
- Install progress in Exo (percent, speed when known, cancel)
- Shared `IStoreAdapter`: auth, library, install, update, launch, uninstall, progress, cleanup
- **Local:** portable register in place (optional copy) + direct launch
- **Epic:** Legendary CLI install/update/launch with stdout progress (GUI optional) + Sign in
- **GOG:** gogdl download/repair/launch; Galaxy optional + Sign in
- **Riot:** fixed product tiles; official RiotClientServices; hide UI; no Vanguard kill
- **Steam:** appmanifest library; playtime best-effort; minimized install/launch
- Stubs: Xbox / EA / Ubisoft / Battle.net / Amazon (honest handoff messages)
- Dependencies panel + consent installers
- Settings: store agents matrix, default install root, portable copy, update check, sort
- Opt-in GitHub release update check
- Docs: honest store matrix, architecture, vendor pin strategy
- Tests: CLI helpers, LocalAdapter fixture, bridge parity

### Fixes

- Cover art virtual host + progressive warm
- Test fixture pollution filtered from Local library
- Version pipeline aligned to 1.0.0
- Phase-2 stubs no longer report false install/launch success

