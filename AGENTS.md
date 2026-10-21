# Exo Launcher — working rules

Exo is a Windows 11 launcher: one UI over the official store clients, which stay installed where the vendor put them and become invisible backends. Rooms: Library, Store (Steam-ranked shelves, official stores, wishlist), Discord (Exo's own Discord client), Music (Spotify desktop, Apple Music and YouTube Music web), System (GPU, display, HDR, upscalers, Exo Shaders), Devices (Exo Mouse). Settings is the gear. `CONTEXT.md` has the domain, `ARCHITECTURE.md` the shape, `DESIGN.md` the look. Read the one you need, not all three.

## Hard stops

- Never fake store replacement, edit game binaries, or touch anti-cheat. Never kill anti-cheat (Vanguard, EAC, BattlEye, FACEIT, PunkBuster, XIGNCODE, GameGuard, ACE, a game's `_BE`/`_EAC` launcher), at the root or anywhere in a tree. Riot titles are never modified.
- Steam install/update/uninstall goes through `ExoLauncher.SteamIpc`. Epic = Legendary, GOG = gogdl. No OCR, DepotDownloader or CDN scraping. Commands, progress parsing and Steam helper internals: `docs/architecture/vendors.md`.
- Consent before any download, install or elevated action. No ads, tray agents or analytics.
- Secrets and filesystem paths stay in native code. React gets bounded DTOs.
- Do not Play VALORANT/League or uninstall real games from an agent session.
- No emoji in chrome.

## Enforced rules

These fail the build or `check`. Fix the code, not the check; an exception goes in the check's own config with a reason.

| Rule | Enforced by |
| --- | --- |
| No screen capture (`PrintWindow`, `BitBlt`, `Graphics.CopyFromScreen`, Windows.Graphics.Capture) or synthetic input (`SendInput`, `mouse_event`, `keybd_event`, `SendKeys`) in Exo itself; capture lives only in the separate Exo Capture add-on (`capture/`) | `BannedSymbols.txt` (RS0030, an error) for .NET APIs; `ExoLauncher.Tests/BannedNativeImportsTests` for P/Invokes |
| No `new HttpClient(...)`: use `ExoHttp.Create` | `BannedSymbols.txt` (RS0030) |
| No `Process.Kill`: close normally; last resort `ProcessTermination.Kill`, which refuses anti-cheat at the root and in every tree member and returns false when it refuses | `BannedSymbols.txt` (RS0030), `ProcessTerminationTests` |
| Every host native code contacts is in `NetworkDestinations.cs`, mapped to its PRIVACY.md "Other network requests" row (WebView pages are out of scope) | `ExoLauncher.Tests/NetworkDestinationsTests` |
| Hover background is `--hover-fill` (`#1a1a1a`), never see-through white (on Liquid Glass, `glass.css` redefines it as a lift of the glass) | `exo/hover-fill` in `scripts/css-design.mjs` (`ui/`, `mouse/`); ESLint for `hover:bg-white/*` |
| A control's fill is a token (DESIGN.md › Controls), never a literal gray | `exo/control-fill` in `scripts/css-design.mjs` (`ui/`, `mouse/`) |
| One slider: a range is drawn only in `ui/src/styles/chrome.css` (`mouse/src/mouse.css` for Exo Mouse); use `components/ui/Slider.tsx` | `exo/one-slider` in `scripts/css-design.mjs` (`sliderFiles` in each `css-design.config.mjs`) |
| Art has no ring, border or inset shadow on its edge; rings are an outline outside it | `exo/art-edge` in `scripts/css-design.mjs` (`ui/`, `mouse/`) |
| No opacity fade on hover | `exo/no-hover-fade` in `scripts/css-design.mjs`; ESLint for `hover:opacity-*` |
| Font sizes 20/13/12 as `calc(Npx * var(--exo-scale, 1))`; the few title sizes are listed exceptions | `exo/type-scale` in `scripts/css-design.mjs` (exceptions in `ui/css-design.config.mjs`); ESLint for Tailwind `text-*` sizes and inline `fontSize` |
| No native `title=` tooltips | ESLint `no-restricted-syntax` on lowercase JSX elements (`ui/`, `mouse/`); `Button` drops `title` from its props |

## Stack (do not drift)

WinUI 3 + Windows App SDK 2 + .NET 11 host, one WebView2, React 19 + Tailwind 4 + Tabler icons (through `ui/src/brand/icons.tsx`, data written by `npm run icons`) in `ui/`, with native controls in `ui/src/components/ui/` (no component library). `WebHostBridge.cs` dispatches JSON-RPC named in `ui/src/lib/host.ts`; the manifest is `ui/contracts/host-v1.json`. Windows 11 x64 only.

## How to work

1. **Evidence before opinion.** Run `tools\e2e\Invoke-ExoE2E.ps1` (screenshots, timings and checks of the installed app) or screenshot the running app, read `%LOCALAPPDATA%\ExoLauncher\logs\app.log`, run the analyzers. Do not diagnose from memory of the code.
2. **Size it, say it.** One line before starting: `small | medium | large — why`. Small (copy, one-file fix): touch it, build, ship. Large (contract, cross-layer, design): plan first, then build.
3. **Deterministic work is a script.** Counting, diffing, dead-code detection, contract checks, CSS pruning: write or run the tool, never reason it out. The tools that exist: `npm run dead`, `npm run css:prune`, `npm run contracts`, `tools/find-dead-types.ps1`.
4. **Delete, don't flag.** If it is not reachable in the shipped app, remove it: the code, its CSS, its RPC, its mock, its doc line. No feature flags for retired code, no "keep for later".
5. **Simplest thing that is correct.** No new abstraction until the third caller. Inline one-branch wrappers. Prefer a hairline to a card, a row to a dropdown, a constant to a setting nobody can change.
6. **Fix the cause.** A misleading label is renamed to what it does. An error the user waited on reaches them as one status line and reaches `app.log` via `report()`/`AppLog`.
7. **Verify on the real thing.** A clean build is not done. Done is the installed exe running the change with the room on screen.

Hosted Chromium windows need `DWMWA_SYSTEMBACKDROP_TYPE=DWMSBT_NONE` while cropped; otherwise DWM fills clipped-out regions and covers Exo chrome. Verify with a desktop screenshot plus a real click on Exo navigation, not only `GetWindowRgn` — a WebView (CDP) screenshot shows only Exo's own page and never an owned client window; `Invoke-ExoE2E.ps1` checks the owned window's visible rectangle against the room instead (`-CaptureHostedWindows` adds desktop shots). Every embedded client session is Exo's: the music service you chose starts with Exo, hidden, and the others start when their room first opens; each is adopted hidden at Exo start if already running, and closes with Exo, on a provider switch, or (music Exo started) after a long idle. System > Shaders hosts no window: it is Exo Shaders, a headless engine (`Services/ShadersAddon`) and its page.

Devices is Exo Mouse, a separate AGPL-3.0-or-later program in `mouse/` built on `@openmouse/protocol` (pinned). It runs in an iframe on its own origin (mouse.exo-launcher.local) and talks to Exo only by postMessage; `DevicesRoom.tsx` relays HID calls and events, the installed games list (id and title) and game start/stop, and nothing else. Never put Exo code into `mouse/` or AGPL code into `ui/`; Exo Launcher is not under the AGPL. Run `npm ci` in `mouse/` as well as `ui/`. `mouse/scripts/patch-protocol.mjs` patches the library before each build and stops the build when the library version changes. The AGPL obliges its source to be public: any release that ships a changed `mouse/` pushes the same tree to github.com/ImAvgErix/exo-mouse first (copy without `node_modules`/`dist`; it builds standalone into `dist/`). The GPL add-ons are the same: a release that ships a changed `capture/` pushes it to github.com/ImAvgErix/exo-capture (without `build`/`out`; keep that repo's own README and .gitignore), and a changed Exo Shaders (C:\Users\Erix\exo-shaders-work) to github.com/ImAvgErix/exo-shaders, before the release.

Switching music providers must exit the previous hosted music session, including a borrowed one; leaving Music for another room only hides it.

Code style is ponytail: the smallest complete change, reuse before adding, delete what a change replaces (never leave it flagged); review with the ponytail-review and ponytail-audit skills. Reverse engineering uses the leanrea skill and plain CLI tools, not the REA MCP.

## Design

Void black `#000`, white ink, Inter (the official 4.1 variable fonts in `ui/public/fonts`; Google Fonts' and Fontsource's builds strip the features Exo sets). One chrome pill. What floats over content and acts on it is Liquid Glass (DESIGN.md › Glass, `docs/architecture/liquid-glass.md`): legible by its band, never on content, never on glass, measured by the `glass.legible` E2E check. Structure by hairlines and a shared right-aligned control column, not nested cards. Sentence case everywhere; no tracked capitals. Section names are the only bold type. Fill the window; a page that fits without scrolling does not get a sidebar or a disclosure. Discord is Exo's own client (`ui/src/components/discord/`, `Services/DiscordClient/`); Exo never hosts or patches the Discord app. Music for Spotify, YouTube Music and Apple Music is Exo's own player (`ui/src/components/music/`, one adapter per service in `ui/src/lib/musicAdapters.ts`): the service runs hidden as the engine and Exo draws home, search, library, pages, queue, lyrics, and for Spotify devices, Jam and Friend activity, through the service's own APIs (`Helpers/SpotifyNative.cs`, `Helpers/YouTubeMusicNative.cs`, `Helpers/AppleMusicNative.cs`: music.apple.com's own MusicKit instance, never its tokens); its own UI shows only to install or sign in (never Spotify's: it signs in through the default browser). Exo always optimizes every hosted app (no setting): it themes and declutters Spotify's xpui.spa and YouTube Music. Use the `frontend-design` skill for any visual change and run the Impeccable detector on the CSS you touched. Full tokens in `DESIGN.md`.

## Gates

From `ui/`: `npm run check` (contracts, ESLint, CSS design check), `npm run dead`, `npm run build` (builds `mouse/` too). From `mouse/`: `npm run check` (tsc, ESLint, CSS design check), `npm run build`. Intentional RPC changes: `npm run contracts:write`.
Host: `dotnet build ExoLauncher\ExoLauncher.csproj -c Release -p:Platform=x64`, then `dotnet test ExoLauncher.Tests -c Release -p:Platform=x64` (pure logic only; verification is still the running app).
CI (`.github/workflows/ci.yaml`) runs all of the above.

## Testing

- `ExoLauncher.Tests` (xUnit) is for pure logic whose failure is silent in the app: parsers, process ownership, update and network policy. Each test class opens with the ways the code could fail.
- Highly prefer E2E tests as the sole testing mechanism. Use them to verify complex features work. At the end of E2E tests, produce a verifiable and repeatable artifact.
- If you must test a system in isolation, first write down all the ways it could fail, then write the code.
- The E2E run is `pwsh -File tools\e2e\Invoke-ExoE2E.ps1` (`-Onboarding` adds the first-run walk). It closes Exo normally, relaunches the installed exe with a loopback-only CDP port in the child's environment, drives the real UI (`tools/e2e/exo-e2e.mjs`, Node 22+, no packages), checks the host natively (window, owned client windows, `app.log`, Steam cache, idle CPU), then closes Exo, restores every file it touched byte-for-byte and starts Exo normally. It never kills a process, plays music, sends messages or installs anything. `window.drag` moves the real mouse over Exo's own title band (keep hands off the mouse while it runs); `music.volume.linked` nudges Spotify's volume while paused and puts it back; `music.spotify.native` reads Spotify's home, library, a search, the queue and the player through Exo's Spotify room and checks Spotify's own window stays off screen; `music.apple.native` reads the same views through Exo's Apple Music room when Apple Music is the service. `music.miniplayer.sync` and `music.miniplayer.volume` only read: the top-bar mini player against the provider page's own title, clock and volume, on the shell the run just started. `-UpscalerCycle` restores one game's upscalers on its page and brings them back with System > Graphics > Upscalers > Update all. `-RealGame steam:<app>` starts that game outside Exo (never Riot or kernel anti-cheat), checks game.runState and its linked mouse profile, and closes it with Exo's Stop. The default run also adds and removes a temporary portable game (a copy of ping.exe) to check run-state for games started outside Exo, and holds the page's `game.launch`/`store.open` requests where it follows a click to them.
- The artifact is `e2e-reports/<yyyyMMdd-HHmmss>/` (gitignored): `report.json` (build, commit, machine without identifiers, every check with status, timing, evidence and screenshots), `summary.md`, `screenshots/`, `app-log-excerpt.txt` (this run's lines, redacted) and `SHA256SUMS.txt`; `Invoke-ExoE2E.ps1 -Verify <folder>` re-checks it. Exit code 1 means a check failed; a FAIL must be an app bug, so fix the check if it is not. New features add their check to the script.

## Ship

Iteration only: `pwsh -File Run-ExoLauncher.ps1` (Debug; `-DevUi` hot-reloads the UI from Vite).
Shipping: delete the Release `win-x64\publish` and `win-x64\R2R` folders under `ExoLauncher\bin` and `ExoLauncher\obj` first (an incremental publish once shipped plain IL instead of ReadyToRun; the E2E `build.precompiled` check catches it), then `dotnet publish` (Release, x64, self-contained), then `pwsh -File tools\swap-dev.ps1` (stages into a unique `app.incoming-dev-*`, closes Exo normally, and swaps over `app` while retaining a unique `app.previous-dev-*` backup; never force-kills), launch `%LOCALAPPDATA%\ExoLauncher\app\ExoLauncher.exe`, then run `pwsh -File tools\e2e\Invoke-ExoE2E.ps1` (it checks `app.log` for `ERROR`/`Exception`/`unknown method` among everything else). Never run it while the user is in a game; it restarts Exo and drives the window. Memory: `pwsh -File tools\measure-memory.ps1` on the installed exe, 3 minutes after launch, sitting on Library.
CHANGELOG entry under the current version, in product voice. Every entry ends with how it was checked, in italics: `_Verified: on screen._` (seen working in the installed app), `_Verified: end-to-end._` (an `Invoke-ExoE2E.ps1` check covers it), or `_Checked in code only._` (anything not seen on this PC: other stores, services, hardware, sign-in flows). Unsure means code only. Release headings read `## X.Y.Z — Month D, YYYY`. Commit in small steps; never force-push.
Repo and license: github.com/ImAvgErix/ExoLauncher is the one repo, public and source-available under `LICENSE` (read and check only; no copying, forking outside GitHub or changed versions without written permission). Its history starts at the source-available snapshot on `main`; never push commits from before it (the earliest were MIT). Everything pushed is public at once, so never commit secrets, tokens, personal data or unblurred screenshots. Reader docs: README, CHANGELOG, PRIVACY, LICENSE, THIRD_PARTY_NOTICES, `docs/HELP.md`, `docs/SUPPORT_MATRIX.md`, `docs/VENDORS.md`, `docs/RELEASE_<v>.md` and `docs/media` are for users; developer detail goes under `docs/architecture/`. The Docs workflow lints and link-checks only those. At release, after VERSION and the CHANGELOG entry: `pwsh -File tools\Publish-Docs.ps1` writes `docs/RELEASE_<v>.md` from the top CHANGELOG section (never edit it by hand), removes older release files, and checks VERSION, README title, CHANGELOG and release file agree, every top entry carries its verification, every `docs/media` image is used by README and every relative link resolves.

## Talk

Direct and short. File and line, not "somewhere in the bridge". Say what was verified and how; say plainly what was not. End with the next action or the open decision, not a recap.
