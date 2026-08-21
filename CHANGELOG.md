# Changelog

## 2.0.2 - 2026-08-21

**Issue #13: Roblox posters, Epic hours, home avatar, and the signed-in onboarding flash.**

- Applied Epic playtime on the library scan so Sugar/Fortnite hours show without waiting for a later refresh
- Preferred Microsoft Store posters for Roblox and Minecraft instead of a Steam title match
- Painted the titlebar chip with the chosen game-cover avatar, and retried a cover-host image once instead of wiping it
- Marked account setup complete when a handle or session already exists, so a signed-in 2.0 install does not flash onboarding
- Stopped Exo friend rows from toggling closed, and kept last-played across a small Epic/Riot clock skew

## 2.0.1 - 2026-08-21

**The 2.0 library people actually hit.**

- Loaded Minecraft and Roblox posters from the Microsoft Store catalog instead of a missing Steam match
- Matched Epic last-played to Legendary/EGL ids and titles, and accepted Riot product aliases
- Opened Exo friend profiles with the reserved handle, kept roster avatars when media arrived without Content-Length, and stopped unverified installed Steam titles from becoming **Unavailable**
- Offered Exo account setup after a 1.x → 2.0 upgrade instead of skipping it
- Sent live download byte counts to the UI so install percent can move
- Stopped the achievement preview from restarting mid-hold

## 2.0.0 - 2026-08-21

**Online identity, truthful libraries, and a launcher that stays out of the way.**

- Added production Exo accounts with remote email/password authentication, DPAPI-protected desktop sessions, reserved handles, expressive profiles, avatar/banner/gallery media, privacy controls, direct friends, blocking, and presence that becomes unknown when providers are unavailable
- Added server-authoritative profile badges and owner-only badge management while keeping roles and management capability out of public profile responses
- Made store identity and entitlement state explicit so linked Steam, Epic, and GOG accounts cannot be reused by another Exo profile, refunds become **Buy again**, and unknown ownership never becomes a false Play action
- Rebuilt artwork discovery and caching across library, profile, friends, and game detail surfaces with validated local overrides, refetch/report controls, high-resolution friend art, collision-safe cache keys, and resilient fallbacks
- Expanded achievements with account-scoped Steam/Epic providers, retries, honest unavailable states, secure icon origins, expressive Exo notification themes, and a compact profile trophy cabinet
- Hardened optional DLSS, FSR, and XeSS updates with signed-vendor validation, correct x64 PE export parsing, official-source preference, semantic version truth, persistent caches, safe restore, and neutral states for unusable destinations
- Added a compact active Now dock, responsive virtualized libraries, smoother preloading, faster cached startup, cleaner search, game-detail title actions, controller navigation, and extensive accessibility/reduced-motion contracts
- Deployed the Cloudflare exo-id Worker with D1, R2, per-user Durable Object presence, strict privacy/rate limits, and offline fail-open behavior for the local launcher
- Replaced GitHub page and README screenshots with the current library, game, profile, and settings surfaces

Google sign-in and email magic links remain unavailable until real provider credentials are configured. Email/password accounts are live.

## 1.0.103 - 2026-08-21

**A useful Now dock instead of a decorative hero.**

- Replaced the oversized landscape banner with a compact 112px active-state dock using a cached cover thumbnail
- Added store, total playtime, transfer phase and speed where available while keeping one clear state action
- Hid recent-only Now state so Pinned moves up whenever nothing is playing, transferring, or awaiting an update

## 1.0.102 - 2026-08-21

**Title-owned favorite and visible close control.**

- Moved the game-detail favorite action beside the game title and removed its floating artwork overlay position
- Increased close affordance size and icon contrast with a solid surface, stronger border/shadow, and clear hover/focus feedback

## 1.0.101 - 2026-08-21

**Full-bleed heroes without boxed-in details.**

- Restored full-bleed Steam library heroes with a top-biased focal crop instead of visible side pillars
- Removed the heavy stats card and outer utility container while retaining readable text shadows and compact individual button surfaces

## 1.0.100 - 2026-08-21

**Uncropped heroes and readable controls.**

- Contained standard Steam library heroes over a blurred edge-fill in the ultra-wide home banner so faces and focal artwork stay visible
- Reduced search to 96px resting and 184px focused, with its caret delayed until the 200ms expansion completes
- Made detected but ineligible or unsupported upscalers neutral grey while preserving red only for usable outdated files
- Added stable contrast plates to game stats and utility controls so bright banner artwork cannot wash out labels or buttons

## 1.0.99 - 2026-08-21

**Favorite controls belong in details.**

- Removed the favorite star, hit target, hover behavior, keyboard focus path, and related styling from every library and pinned card
- Kept the single favorite action in the game detail overlay

## 1.0.98 - 2026-08-21

**Unboxed favorites and visible hero art.**

- Replaced the persistent black favorite badge with one floating, shadowed star and a transparent accessible hit target
- Restored full-strength home hero artwork and brightened game, profile, and showcase banners while retaining localized text contrast

## 1.0.97 - 2026-08-20

**Trusted upscaler recovery and final visual fit.**

- Fixed x64 PE export validation so genuine signed NVIDIA, AMD, and Intel upscaler DLLs pass provenance checks, with official-source preference and bounded persistent catalog and per-game status caches
- Fixed real Legendary ownership caches containing null install sizes so Rocket League and other Epic entitlements remain playable, and made successful same-account background refreshes republish the library immediately
- Refined the pinned shelf, card corners, single-layer favorite control, and title spacing; restored a visibly oval search capsule with a smooth reduced-motion-aware expansion
- Removed the Profile Studio divider, tightened its spacing, and kept failed upscaler downloads retryable with readable error wrapping

## 1.0.96 - 2026-08-20

**Online authority, repaired artwork, and entitlement truth.**

- Deployed server-authoritative owner/developer roles and fixed profile badges; Erix now has the exclusive Founder plus CEO and Developer badges, with a bounded owner-only community badge manager
- Repaired the retained pinned-shelf offset, broken Friends preloader layout, cover-corner wedges, profile density, and the oval titlebar search with a smooth reduced-motion-aware reveal
- Hardened profile/library art with exact origins, streamed byte limits, full decode validation, atomic promotion, collision-safe native keys, legacy-cache compatibility, and post-write pressure control
- Made upscaler status compact and honest while requiring signed vendor identity, expected PE exports, embedded version agreement, byte equality, stale-restore protection, and fail-closed anti-cheat checks
- Scoped achievements to the active provider account, required a complete matching Steam schema, redacted locked hidden data, and removed arbitrary remote icon loading
- Added typed entitlement propagation across variants and disk cache so refunded/revoked games show **Buy again**, unknown ownership stays unavailable, and native launch/install/update paths fail closed
- Deferred background store observers and trophy-WebView warming until the shell is visible, and restricted WebView local/image access to the minimum required origins

## 1.0.95 - 2026-08-20

**Readiness-driven startup and a complete profile viewport.**

- Made the opening Exo mark represent real library readiness by decoding first-screen covers and prewarming installed-game upscaler status before revealing the shell
- Cached DLSS, FSR, and XeSS status so game details open with their tool rows already populated
- Added restrained search expansion without the white focus box, with an instant reduced-motion state
- Moved the trophy cabinet beside the showcase and compacted game rows so profile achievements remain visible without scrolling at the standard window size

## 1.0.94 - 2026-08-20

**Reliable profile art and cleaner identity.**

- Fixed lazy covers in hidden profile rooms exhausting their fallbacks before the browser requested them; all library art now waits until its tile approaches the viewport
- Removed the redundant public handle line and its obsolete visibility control from the profile surface
- Let profile-picker titles wrap fully and simplified the nested Activity statistics surface

## 1.0.93 - 2026-08-20

**A cleaner favorite and quieter game details.**

- Removed catalog descriptions from the opened game surface while retaining store, genre, and year
- Replaced the oversized layered favorite control with one compact 30 px surface and a clearer 15 px state glyph

## 1.0.92 - 2026-08-20

**Complete titles, expressive profiles, and stricter provider truth.**

- Enlarged library cards and made the home surface cover-and-title only, with complete wrapped names and detail-owned store, genre, year, and description
- Rebuilt the profile showcase around featured artwork, real achievement progress, a provider-backed trophy cabinet, and safer account-scoped achievement reads
- Added Epic/Riot/Deadlock metadata fallbacks, corrected Riot's 2XKO mapping, and stopped install history or another account's cache from becoming ownership
- Hardened upscaler downloads with vendor signature checks, improved WebView memory behavior, and removed forced termination of pre-existing store clients

## 1.0.91 - 2026-08-20

**Reachable shelves and warmer navigation.**

- Fixed the pinned shelf starting offset that clipped the first game; added explicit previous/next controls so every pinned title is reachable
- Kept card metadata on the cards, removed snap-induced horizontal clipping, and preserved consistent single-line title geometry
- Preloaded Settings, Friends, Profile, and likely-next artwork behind the first shell paint so navigation does not start cold network work

## 1.0.90 - 2026-08-20

**Entitlement-safe installs and a bounded edge-case sweep.**

- Steam owned-game snapshots now distinguish an authoritative empty library from a private/unavailable response, so refunded titles return to Buy instead of inheriting stale Download state
- Cached manifest proof is pruned only after an authoritative account snapshot, and direct install bridge requests reject unowned titles
- Hardened Steam JSON reads with bounded response bodies and tightened store-search ownership semantics so install capability never masquerades as entitlement
- Friend activity cards now use warmed wide art with a portrait fallback, so Steam/Epic/Riot games do not collapse into blank banner slots

## 1.0.89 - 2026-08-20

**A skeptical social refresh and provider-ready identity.**

- Kept the pinned shelf in one horizontal row, shared catalog metadata across shelves, and exposed full card metadata to assistive technology
- Made social refreshes capability-isolated, removed the initial false outage flash, and added Steam portrait fallback for friend activity
- Added honest Google/email-link readiness messaging and operator setup documentation without fabricating credentials
- Added bounded, idempotent Resend delivery when email credentials are configured

## 1.0.88 - 2026-08-20

**Sharper cards, expressive profiles, and honest upscaler status.**

- Cards now keep a single-line title and compact `Store · Genre · Year` metadata when official catalog data is available
- Removed the redundant achievement progress bar and added Exo-specific flare/eclipse and nova/black-hole unlock effects
- Moved dependency checks into About, reset search when leaving the library, and improved favorites and profile editor controls
- Added drag reordering, eye visibility toggles, handle display privacy, richer showcase presentation, and corrected FSR 3.1 semantic version selection

## 1.0.87 - 2026-08-20

**Production Exo identity, reliable artwork, and a cleaner social surface.**

- Deployed the production Exo ID Worker with D1 migrations for bounded profile GIF/gallery media and provider-scoped account uniqueness
- Fixed wide cached hero/banner art being rejected on wide surfaces while keeping portrait cards poster-safe; stale lower-revision covers no longer overwrite refreshed art
- Made profile identity changes debounce-save to the signed-in Exo account, with resilient avatar/banner fallbacks and high-quality friend/Steam artwork
- Kept Steam `personastate=0` as authoritative Offline and unknown/private responses unavailable rather than falsely online
- Widened and symmetrically padded title-bar search so the full placeholder remains visible

## 1.0.86 - 2026-08-19

**Faster large libraries, repairable artwork, and one calmer interaction system.**

- Added native per-game Replace cover, Reset cover, Refetch artwork, and Report wrong art controls with strict PNG/JPEG validation, local content-addressed storage, grouped-card ownership, authoritative reset, and cache-version busting
- Replaced paint-only containment with zero-dependency row virtualization: the main grid preserves exact height and keyed focus while mounting at most 99 cards at 1920×1080, even for 5,000-game libraries
- Added honest local-only store diagnostics and retry: Amazon/Nile sessions are recognized, Steam ownership requires a readable account, and Epic/GOG download capability requires the actual helper instead of optimistic labels
- Fixed stale global keyboard activation, added column-preserving grid navigation and F2 Pin/Unpin, and added conservative gamepad navigation that can open only explicitly safe views—never Play, Install, Update, Stop, Remove, dependency, purchase, account, or artwork actions
- Scaled the native minimum window for display DPI, added Windows High Contrast and stronger focus rings, repaired radio/step focus behavior, announced real trophy overlays, moved progressbar semantics outside buttons, and added proper main landmarks
- Made the Exo account form a real single-column flow with a quiet Sign in/Create account switch and one primary submit action; shared shell chrome now owns the final titlebar/button cascade
- Reused parsed Steam playtime, kept the synchronous cover handler only as a virtual-host failure fallback, and added privacy-safe process-to-window startup timing plus a non-capturing restore callback
- Kept Friends game art portrait-shaped, Steam `personastate=0` authoritative Offline, unknown/private Steam states Unknown, and avatar status textual rather than a presence circle

## 1.0.85 - 2026-08-19

**Optional email/password accounts, profiles, and friends—without putting the library behind an account.**

- Deployed the optional exo-id stack with Better Auth, D1, R2 profile media, per-user hibernating Durable Object presence, hourly metadata cleanup, and a pinned HTTPS origin; disposable production smoke accounts were removed afterward
- Added email/password account creation and sign-in: strict 12–128-character passwords, salted Scrypt hashes in D1, native-only DPAPI bearer sessions, exact 2 KiB auth bodies, and no password/hash/token returned to React
- Added privacy-aware public profiles and sharing, reserved handles, direct requests, accepted friendships, removal suppression, blocking, verified Steam/Epic/GOG link contracts, and Steam/Epic mutual discovery
- Kept bearer sessions native in DPAPI-protected storage; added viewer-scoped offline caches, bounded sanitized avatar/banner media, session-revalidated presence, provider capability detection, export, and fresh-auth account deletion
- Wired online diagnostics, privacy, sessions, store links, requests, profiles, media, and presence into the existing React/WebView shell while preserving signed-out local/store fallbacks and never putting identity on Play, install, launch, or library scan paths
- Restored portrait Friends art, preserved selected avatar/banner art through edits, made the profile rail/showcase responsive with an opaque editor header and readable activity stats, and fixed compact-titlebar search clipping
- Removed avatar presence-dot overlays while retaining textual status; used Steam `lastlogoff` as evidence for Offline while keeping indeterminate/private rows unknown, session-revalidated, friend-only, and bounded
- Made keyboard actions immediate, gated hover to fine pointers, honored reduced motion on continuous spinners, and removed redundant anti-cheat warning copy without weakening native refusal
- Kept Google and email magic-link sign-in visibly unavailable without real provider credentials. Password-account emails are currently unverified, and password recovery is unavailable until real mail delivery exists

## 1.0.82 - 2026-08-17

**Native WinUI 3 shell. No React, no WebView2 UI.**

- Home, Now, tiles, the 400px plate, settings, and first-run are XAML + C#. `ShellController` replaces JSON-RPC `WebHostBridge`.
- `dotnet build` is the UI build. WebView2 remains for GOG login only.

## 1.0.81 - 2026-08-15

**One chrome house. Play and Apply actually click.**

- First-run is for a stranger. Steam-only names Steam. Nothing names the next move (add a folder). Missing stores are not listed. Empty library does not send them to search.
- Titlebar is the mark. No Exo / Launcher wordmark. Play lives on the game (hero, card, detail), not the chrome.
- Settings is one column, not a catalog. Stores on this PC only. Achievement toast is 360x80, waits to be on screen before it animates, and Preview no longer lies when nothing showed.
- tokens.css no longer forks winbtn size or hover. Cover virtualization matches the CSS grid. Empty-library and banner CTAs keep chrome height.
- Settings and first-run wait for the real store list. They do not paint four fake Not installed rows.
- Official-store Update/Install/Play that only opens the vendor client is a handoff, not Update complete.
- Library grid no longer skip-jumps from content-visibility or a measure loop.
- Search is not a store shelf. Unowned catalog hits stay off the live screen. Owned-not-installed sit under On your account, not Install. Public Steam catalog is not queried when Steam is missing. Settings says so when no store apps are on the PC.

## 1.0.80 - 2026-08-15

**Apply swaps what the title already ships. Play just launches.**

- One Apply on the armed game fetches and swaps only DLSS / FSR / XeSS files that title already has. A DLSS-only install is not told to download the rest.
- Play does not wait on the upscaler pack. Apply can fetch. XeSS and other latest labels only claim a real version.

## 1.0.79 - 2026-08-13

**Closing a game no longer flashes Home.**

- The card stays until the dim fades. A CSS enter animation was holding opacity at 1, so the fade popped instead.

## 1.0.78 - 2026-08-13

**Library tiles open in place.**

- Clicking a title in the grid no longer scrolls Home under the game page. Close details stays put. The Now plate and pinned row stay where they were.

## 1.0.77 - 2026-08-13

**Opening a library tile no longer breaks Close.**

- Game cards and the game page do not share a poster morph. That projection was stealing clicks after the Now plate fix, so regular titles did the same in-and-out mess.

## 1.0.76 - 2026-08-13

**Install percent is visible. Uninstall does not skip the library. Now opens and closes once.**

- Steam’s ACF often sits at `0 / total` while a leftover `downloading/` folder is already larger than this job. That is unknown, not 0% and not 100%. The bar is indeterminate until `BytesDownloaded` moves, then it shows the live percent on the button.
- Uninstall no longer publishes install Preparing/Completed, so Now does not flip to Downloading and the library does not reload twice.
- The Now plate is a real open control. It does not share a poster morph with the game page, and Close details sits above the overlay.

## 1.0.75 - 2026-08-13

**Steam percent matches the live job. Home has one Now plate. Steam chrome stays gone.**

- Install percent uses the `steamapps/downloading` folder when content_log is only the start snapshot (`download 0/total`). Exo no longer freezes at 0% while Steam’s Downloads row climbs.
- Uninstall calls Steam IPC once. A failed confirm is not retried, and `steam://uninstall` only runs if the helper never reached the client. Steam stays hidden.
- Search no longer flashes the same owned title in Library and Install. Dead Steam OCR/capture/click paths are gone.
- Home’s featured row is one game with one reason (downloading, playing, update, last launched). Wide Steam `library_hero` only. Not a carousel.

## 1.0.74 - 2026-08-13

**Install percent follows Steam’s download. Uninstall stays hidden.**

- The install bar uses Steam’s live `download done/total` from content_log when the appmanifest still holds leftover totals, and it no longer jumps from 99% download to a 40% staging bar.
- Uninstall no longer restores the Steam window. Steam stays a hidden backend.

## 1.0.73 - 2026-08-12


**Home is installed games. Steam uninstall reports the real result.**

- The library grid is downloaded titles only. Owned-but-not-installed Steam games stay in search as Download.
- Steam uninstall no longer treats every helper exit as success. A hidden Steam confirm was swallowing the removal; Exo now leaves Steam visible for that step and fails in seconds if files do not start going.

## 1.0.72 - 2026-08-12

**Steam percent, Download for owned titles, smoother motion.**

- Install/update percent ignores leftover ACF totals from a previous job, uses staging when the download counters are already finished, and polls every 400ms. A leftover 37 GB counter next to a 36 MB patch is no longer shown as 100%.
- Steam titles in this account’s librarycache that are not installed appear in Exo with Download. Search uses tickets, localconfig Apps, and that cache — not Buy — when Steam already knows the game.
- Library tiles, the progress fill, and the busy mark use short compositor tweens. Springs and perpetual `willChange` layers are gone.

## 1.0.71 - 2026-08-12

**Progress, Open store, one client at a time.**

- Steam install/update percent is `downloaded / toDownload` from the appmanifest. Exo no longer maps 0–100 onto 10–95 or invents a climbing number while queued.
- Settings → Open shows any installed official client, including during an update. Steam gets `steam://open/main` so a silent instance actually appears. Store hiders skip a client the user just opened.
- Minimize-while-playing is always on. The Settings checkbox is gone.
- Idle titlebar mark is the original E (short middle bar), optically tighter in the square. The three equal bars only run while Exo is busy.
- Launching or opening one store asks the others to exit. Steam gets `-shutdown`, Riot gets its client kill API, then a graceful thread quit. Unused shells that ignore that are terminated. Vanguard is never touched.

## 1.0.70 - 2026-08-12

**Steam updates actually start.**

- This Steam build’s `GetIClientAppManager` is engine slot 43, not 36. Slot 36 was the network-device manager, so installs returned a fake error and bytes never moved.
- `InstallApp` takes app id, folder index, and a legacy flag. Deadlock’s queued 36.6 MB patch downloaded through that call.
- The Steam helper is a self-contained process in `steam-ipc\`, so it does not share WinUI’s runtime.

## 1.0.69 - 2026-08-12

**Steam is commanded as a backend. The mark only moves when Exo is actually doing work.**

- Install, update, and uninstall talk to the running Steam client through a helper. This Steam build’s app-manager layout does not yet match the known slots, so Exo fails honestly if bytes never move instead of clicking Steam chrome.
- Library tiles use 600×900 posters instead of 1200×1800, so WebView does not decode a 2x bitmap for every card.
- The three bars sit on one diagonal. The wave runs during boot, install, update, search, and launch — not while idle.

## 1.0.68 - 2026-08-12

**Steam’s Downloads row is read by a helper that never loads WinUI.**

- Capturing from ExoLauncher.exe stayed black even in a child process. A separate capture host snapshots the painted desktop, then Exo clicks the queued title

## 1.0.67 - 2026-08-12

**Steam’s Downloads row is read by a short helper process, not the WinUI compositor.**

- Capturing from inside Exo kept returning a black Steam window. Exo now snapshots the painted desktop from a child process, then clicks the queued title

## 1.0.66 - 2026-08-12

**A bad GPU texture create no longer blocks reading Steam’s Downloads row.**

- Desktop capture now creates an empty staging surface. If that path still fails, Exo keeps waiting for a painted compositor frame and clicks the queued title

## 1.0.65 - 2026-08-12

**Steam’s Downloads row is copied from the painted desktop, not the first black GPU frame.**

- The compositor often hands Exo an empty first frame. Exo now waits for a real desktop image, crops the Steam window, and clicks the queued title

## 1.0.64 - 2026-08-12

**Steam’s Downloads row is read from the monitor compositor, then cropped.**

- Capturing Steam’s HWND stayed black. Exo now copies the painted desktop the same way a screenshot does, keeps the Steam rectangle, and clicks the queued title

## 1.0.63 - 2026-08-12

**Steam’s GPU frame is captured the same way a desktop screenshot is.**

- GDI still copies a black Downloads window even when Steam is on top of Exo. Windows Graphics Capture reads the compositor, then Exo clicks the queued row

## 1.0.62 - 2026-08-12

**Steam has to activate for a moment or its Downloads UI stays black.**

- A no-activate overlay covers Exo with an empty GPU surface. Exo now shows Steam for the OCR click, then hides it

## 1.0.61 - 2026-08-12

**Steam’s layered UI is copied with CaptureBlt, not a black GDI blit.**

- Default CopyFromScreen skips DirectComposition windows. Exo now includes layered pixels, reads the leased Steam frame, then clicks the queued row

## 1.0.60 - 2026-08-12

**Steam’s GPU UI is read from the screen, not PrintWindow.**

- Modern Steam captures black via PrintWindow. Exo copies the visible Downloads frame from the desktop, clicks the queued row, then hides Steam

## 1.0.59 - 2026-08-12

**Steam has to be on top for a queued update to start.**

- Chromium captures black when the Downloads window is covered or off-screen. Exo shows it over the shell for the OCR click, then hides it again

## 1.0.58 - 2026-08-12

**Steam’s child windows stay painted during a hidden update.**

- The hide loop no longer covers Chrome children of a leased Downloads window. A capture dump lands in logs when OCR is empty

## 1.0.57 - 2026-08-12

**Steam paints behind Exo during a hidden update.**

- The Downloads window sits under the Exo shell instead of off the desktop. Chromium actually draws, so OCR can read the queued row

## 1.0.56 - 2026-08-12

**Hidden Steam still paints for OCR.**

- The offscreen Downloads window keeps a 2px sliver on the desktop so DWM composites it, and OCR reads the Chrome child instead of a blank SDL frame

## 1.0.55 - 2026-08-12

**Steam OCR can actually read the Downloads row.**

- Captures are written with DataWriter and DetachStream. The previous flush wrapper disposed the buffer, so every frame died as ObjectDisposedException

## 1.0.54 - 2026-08-12

**Steam OCR no longer aborts a queued update.**

- The hidden Downloads screenshot is flushed into WinRT before decode. A bad frame is skipped instead of killing the whole update

## 1.0.53 - 2026-08-12

**Steam queued updates actually start.**

- A patch sitting at 0 bytes with Steam’s UpdateStarted bit still gets the hidden Downloads-row click. Previously Exo treated that as already busy and never promoted it

## 1.0.52 - 2026-08-12

**Search placeholder clears on focus.**

- Clicking the search pill hides “Search” so the caret isn’t sitting on the word

## 1.0.51 - 2026-08-12

**The mark actually moves.**

- Titlebar, boot, and Settings logos run the three-bar wave — draw in, then scale like Grok’s thinking mark. The old 2px wobble is gone

## 1.0.50 - 2026-08-12

**Home is just the library.**

- The Continue Playing film strip is gone. Pinned and the grid are the home screen

## 1.0.49 - 2026-08-12

**Chrome icons are one family, one size.**

- Titlebar, search, Play/Stop, pin, folder, and Settings use Phosphor at 16px — not Amicons sharp
- Outline for chrome. Fill for Play, Stop, and pin-on. Lucide and Tabler stay out

## 1.0.48 - 2026-08-12

**The titlebar is easier to grab.**

- Empty space beside search, and the strip above and below the pill, drags the window. Logo, search, and window buttons stay clickable

## 1.0.47 - 2026-08-12

**Continue Playing stays put.**

- The spotlight film no longer auto-rotates through titles. Click a poster if you want a different one

## 1.0.46 - 2026-08-12

**Chrome uses Amicons.**

- Titlebar, search, Play/Stop, pin, folder, and Settings glyphs are Amicons free sharp — not the homemade 1.5-stroke set
- Line for chrome, solid for pin-on and Stop. Lucide and Tabler stay out

## 1.0.45 - 2026-08-12

**One launcher at a time.**

- Opening Steam, Epic, GOG, Riot, Xbox, EA, Ubisoft, Battle.net, Amazon, or Rockstar from Settings closes the others
- Play, install, and update do the same for unused store clients
- Steam as a hidden backend starts without Friends or chat toasts. Settings → Open Steam still brings the full client
- Rockstar close/hide is path-qualified so an unrelated `Launcher.exe` is left alone

## 1.0.44 - 2026-08-12

**Steam Uninstall confirms the prompt.**

- Uninstall sends a hidden `steam://uninstall/<appid>` for the selected title, then clicks only an offscreen OCR-verified Uninstall/Remove control that names that game — not Cancel, not a sequel
- Failure stays on screen instead of vanishing after a few seconds

## 1.0.43 - 2026-08-12

**Steam Update actually starts the patch.**

- A scheduled Steam update no longer dies after one missed Downloads-row click. Exo clears that game's schedule, keeps requesting the exact app, and retries the hidden row until bytes move
- Downloads OCR still requires the selected title, but accepts Steam's size/percent/scheduled suffix and a split title line so Counter-Strike 2 matches its own row — not a sequel or a dedicated server

## 1.0.42 - 2026-08-12

**Update sits on the poster.**

- Games with an update get a white Update mark on the cover and a brighter frame — not a gray store-line footnote, not an orange brick

## 1.0.41 - 2026-08-12

**Search does not spin.**

- The titlebar search pill no longer grows a spinner while stores look up results; progress stays on the Install list

## 1.0.40 - 2026-08-12

**Shine only on the poster.**

- Cover sweep runs when the pointer is on the poster, not the title, caption, or plate around it

## 1.0.39 - 2026-08-12

**A game you already have is never Buy. The strip is a film of posters.**

- Search collapses Steam catalog Buy rows when the same title is already in the library from any store (Steam, Epic, GOG, Riot, Xbox, EA, Ubisoft, Battle.net, Amazon, Rockstar, local)
- Opening a leftover catalog hit still lands on the library card
- Spotlight is a film strip: current poster larger, neighbors peek, no dots — click a peek to switch

## 1.0.38 - 2026-08-12

**Splash waits for the library. One stroke icon set. The mark actually animates.**

- Cold start keeps the splash up until installed covers and playtime are on the rows (20s cap); owned-not-installed posters keep filling after
- Boot mark is three HTML bars (WebView2 would not animate SVG polygons), held at least 1.4s so the assemble is visible
- Chrome icons are one 24-grid, 1.5 stroke, square-cap set — not Tabler, not mixed sizes
- Spotlight resets when the slide set changes identity (same length, different games no longer desync); Update/Install on the strip uses the download mark, not Play

## 1.0.37 - 2026-08-12

**The Exo mark is the mascot. Chrome uses one icon pack.**

- Boot and titlebar use the slanted three-bar E; on boot the bars assemble and idle like a visor
- Copy `brand/exo-mark.svg` and `ui/src/brand/` into ExoOS / Exo Control for the same identity
- Window and in-app icons are Tabler (MIT, 6100+) at 1.75 stroke — not Lucide, not a paid pack we cannot ship

## 1.0.36 - 2026-08-12

**Covers for every store, cached on disk.**

- Library warm fills a poster for every real title (Steam, Epic, GOG, Riot, Xbox, EA, Ubisoft, Battle.net, Amazon, Rockstar, local) — not only Steam app ids and favorites
- Non-Steam games resolve official Steam library posters by title, then keep the file under `%LOCALAPPDATA%\ExoLauncher\covers`
- GOG uses the store’s own cover URL / Galaxy v2 art instead of a fake `{id}_product_tile` path
- Unreal plugins and the Add-portable row stay out of the cover crawl

## 1.0.35 - 2026-08-12

**Owned means Download. Covers paint without hover. The strip is a detached plate.**

- Search for a game you own says Download, not Buy on Steam, and opening it stays on the game plate
- Steam treats a mismatched TargetBuildID as an update; the caption stays quiet
- Covers decode immediately (no opacity-0), tile shine still sweeps on hover
- Startup is the logo splash while settings and the library load — not “Starting…”
- Rotating strip drops Download, skips live-service loops like Rocket League for Pick back up / Unplayed, and sits on a detached dark plate with a sharp inset poster

## 1.0.34 - 2026-08-12

**Search hugs the word. Spotlight is a tall poster panel.**

- Search sits in the gap between the logo and the window buttons, sized to “Search”, then grows when you type
- The rotating strip is a 280px plate with a full-height portrait on the left — not a small card, not a washed background

## 1.0.33 - 2026-08-12

**The strip is a plate again. Search is a small pill.**

- Spotlight is a dark plate with a sharp poster on the left — no veiled background wash
- Download slides prefer a title that actually has a poster
- Centered search starts at 118px and grows to 240px when you type

## 1.0.32 - 2026-08-12

**Update lives under the title. Continue is the game’s art, not a widget.**

- Library cards no longer stamp an Update pill on the poster — it sits in the caption with the store name
- The rotating strip fills with the same portrait, veiled, with a sharp poster and the lane label on the art
- Search sits in the middle of the titlebar as a small pill and grows when you type

## 1.0.31 - 2026-08-12

**Covers show up. Download means download. Labels say what they mean.**

- Library posters load without waiting for hover; Steam capsules that are a hair under 450px tall still count as covers
- Cold start is a quiet logo and bar under the titlebar — not “Scanning libraries…”
- Tile shine is a clipped sweep inside the rounded frame
- Owned store titles say Download, not Buy; opening one from search keeps the game plate open
- Steam pending byte deltas count as updates; the Update badge matches the other quiet badges
- Spotlight is a detached dark plate with a poster: Continue playing, Update ready, Download, Been a while / Unplayed — no Last / Played / Almost done
- Game plate stats are Time played and Last launched
- Settings is tighter; achievement toast says Unlocked and the cue is a warmer chime

## 1.0.30 - 2026-08-12

**A compact spotlight that pages itself.**

- Continue Playing is a short strip again, not a tall hero
- The strip rotates Continue / Last played / Almost done (achievement 60–99%) / Update ready when those are real and distinct
- Arrows and dots to go through by hand; auto-advance pauses on hover. Almost done uses cached achievement snapshots only — no home-screen refresh flash

## 1.0.29 - 2026-08-12

**Click-in is a plate over the library. Continue is the hero, not a green field.**

- Opening a game keeps the library behind a blur and puts poster, stats, and Play on a dark plate
- Continue Playing is a full-bleed Steam hero with a black text veil — no hashed color wash
- Library and pinned cards are a step larger, with side inset so rounded edges are not sliced
- Tile and poster fallbacks stay `#050505` instead of a hue that changes per title

## 1.0.28 - 2026-08-12

**Readable stage, honest hours, a Continue banner that isn’t a cropped slice.**

- Continue Playing uses a wide Steam hero on the right of a taller strip, faded into a color field — portraits are never sliced across the banner
- Library cards sit at Grok scale (larger than pins, not huge); cards meet the window lip
- Game page type sits on a dark plate over a colored wash so Rocket League stays readable
- Playtime keeps the highest known hours on a card; opening a title no longer flashes Checking / Updating
- Tile shine is an inside highlight so rounded edges stay clean

## 1.0.27 - 2026-08-12

**Home layout and art that match what you actually see.**

- Game page sits on a hue gradient with a blurred cover wash; Continue Playing stays a sharp 2x Steam hero (or 2x poster), not a stretched CSS background
- Pinned titles no longer repeat in Library; library cards are larger than pinned cards
- Pinned count and Recent / A-Z / Played chips are gone

## 1.0.26 - 2026-08-12

**Library polish: no clipped pins, no fake blur, a window that actually resizes.**

- Pinned games wrap in the same grid as the library instead of a dual-axis carousel with edge masks, and pinned titles stay in Library
- Continue Playing and the game page use the cover as a color wash with a sharp poster — portraits are never blurred to fake a hero
- Opening a game keeps last-good playtime and achievements on screen; background refresh no longer flashes Checking / Updating or overwrites known hours with zero
- Steam lifetime hours prefer `PlaytimeForever` when both keys exist
- Cards sit on the window lip, hover Play is gone, and shine is clipped inside the tile so rounded edges stay clean
- The window is resizable and maximizable, default 1400×900, floor 1100×700

## 1.0.25 - 2026-08-12

**Working stop, real store libraries, and the Grok library shell.**

- Stop kills verified game helpers without `Kill(entireProcessTree: true)`, so Easy Anti-Cheat / BattlEye / Vanguard children are never force-terminated
- Steam achievements stay on the active account; uncorroborated `0 / 0` no longer wipes last-good counts, and a confirmed empty catalog shows None instead of `0 / 0`
- Playing / Stop flags clear when the host says the process is gone
- EA, Ubisoft, Xbox PC, Battle.net, Amazon Games, and Rockstar titles appear from proven local installs and launch through the official protocol or executable
- Library home matches the intended shell: Continue Playing hero, titles under posters, Recent / A-Z / Played, and a full game page instead of a side rail

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

- Removed the then-experimental Exo account, profile-sync, and cloud playtime code from that Launcher release while Steam, Epic, GOG, and Riot authentication stayed available
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
