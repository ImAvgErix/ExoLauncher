# Deep launcher performance and integration audit

_Checked 2026-08-20 against the live dirty working tree atop `43bf37d622e4`. Exa was used to locate and fetch primary vendor documentation, specifications, and upstream source. No store client, game, checkout, refund, or anti-cheat title was launched. Function names are used instead of line numbers because the checkout changed concurrently during the audit._

## Decision

Exo already has several strong foundations: last-good disk paint, concurrent bounded adapter scans, account-scope checks around scans, a virtualized grid, cancellable search generations, DPAPI storage for the optional Steam user key, local-first cover files, present-only upscaler detection, and an explicit rule against anti-cheat bypasses.

The next work should not be another provider. The highest-value work is to make four existing boundaries honest and safe:

1. Separate **installed**, **entitled**, **free to acquire**, **last verified**, and **unknown**. A single `Owned` Boolean currently turns several kinds of weak local evidence into an install right.
2. Preserve the now-removed automatic-agreement boundary and stop force-terminating store clients Exo did not start.
3. Treat downloaded upscaler DLLs as executable supply-chain inputs: vendor-authenticated, ABI-coherent, transactional, build-bound, and legally cleared—or keep the feature read-only.
4. Move network/account enrichment, hidden React rooms, speculative artwork, and the active WebView's low-memory mode off the startup and Play critical paths.

## Closed during this audit

The live tree initially contained `SteamEulaAcceptance.Accept` (fabricated `*_eula_* = "99"` state) and `StoreAgreementPromptAutomator.Arm` (automatic Agree/Accept UI Automation). A final re-scan found both files removed, no references in `SteamAdapter`, and regression assertions in `HiddenStoreContractTests` / `StoreActionReliabilityTests`. Keep that removal; required agreements must remain user-reviewed official UI.

The same final re-scan found Riot's `lion` mapping corrected from TFT to 2XKO, its local probe updated for 2XKO/Lion executables, and `RiotAdapter.GetLibraryAsync` changed so client/bootstrap presence no longer marks the entire catalog Owned. Preserve those truth fixes. The remaining free-acquisition/action conflict is described below.

## Ranked opportunities

| Rank | Priority | Opportunity | Main implementation seams |
| ---: | :---: | --- | --- |
| 1 | **P0** | Replace `Owned` with evidence-bearing access state; stop promoting Steam manifests and represent free Riot acquisition without faking ownership | `GameEntry`, `SteamOwnershipCatalog`, `SteamAdapter.GetLibraryAsync`, `RiotAdapter.GetLibraryAsync`, `LaunchOrchestrator.InstallAsync`, `LibraryService.ScanLibraryAsync` |
| 2 | **P0** | Quarantine Epic owned caches and Steam achievements by the exact active account | `EpicAdapter.ReadOwnedCache` / `WriteOwnedCache`, `StoreSearchService`, `AchievementService.GetLatestSnapshot` / `GetSummary`, UI achievement cache |
| 3 | **P0** | Disable upscaler writes until artifacts are authenticated and provider families update atomically | `DlssSwapService.DownloadManifestDllAsync`, `IsAllowedDownloadUrl`, `ApplyPackToGame`, backup/restore code |
| 4 | **P0** | Never kill a pre-existing vendor client; scope hide/close to an Exo-owned lease and every store mutation | `LaunchOrchestrator.CloseUnusedStoreClientsAsync`, `StoreClientCleanup.ExitUnusedAsync` / `TerminateRemainingUnused`, `HiddenStoreRuntime` |
| 5 | **P0** | Put Riot behind a written approval/support boundary; do not call its private local patch/client APIs a supported launcher contract | `RiotAdapter`, `RiotClientApi`, `RiotCli.FixedCatalog`, `RiotInstallProbe` |
| 6 | **P0** | Run the visible main WebView at normal priority; remove unconditional anti-backgrounding and defer the trophy controller | `MainWindow.EnsureWebCoreAsync`, `WebViewHostProfile`, `TrophyNotificationPresenter` |
| 7 | **P1** | Replace unsupported Epic `install` / `uninstall` actions with documented handoffs and explicit user-action states | `EpicAdapter.BuildEpicActionUris`, `UpdateAsync`, `WatchEpicLauncherJobAsync` |
| 8 | **P1** | Separate dispatched, needs-user-action, and process-confirmed launch; overlap or defer achievement enrichment | `WebHostBridge.GameLaunchAsync`, `LaunchOrchestrator.LaunchAsync`, store `LaunchAsync` implementations |
| 9 | **P1** | Make manual Refresh bypass provider entitlement TTLs so buys, refunds, account switches, and subscription changes reconcile now | `LibraryService.GetLibraryAsync(force)`, `SteamWebApi.LoadOwnedGamesAsync`, `EpicAdapter.ScheduleOwnedLibraryRefresh` |
| 10 | **P1** | Split portrait and hero work; cancel abandoned search art; shrink derivatives and use one shared scheduler | `WebHostBridge.WarmSearchCovers`, `CoverArtService.WarmCacheAsync`, `CoverArt` / `HeroWash` |
| 11 | **P1** | Lazy-load and first-visit mount Settings, Friends, Profile, and detail code; stop committing grid state on every scroll frame | `LauncherApp`, route components, `WindowedGameGrid` |
| 12 | **P1** | Replace undocumented Steam search/store endpoints with an opt-in local index from documented `IStoreService.GetAppList` | `StoreSearchService`, `StoreMetadataService`, `CoverArtService` Steam lookups |
| 13 | **P1** | Make Steam achievement cache reads account-specific and schema-complete; move keys to headers | `SteamLibraryCacheAchievementProvider`, `SteamWebApiAchievementParser`, `SteamWebApi`, settings disclosure |
| 14 | **P2** | Verify Open Store by executable/PID/main HWND and report late failure; do not close siblings during an explicit Open | `WebHostBridge.OpenVendorClient`, `OpenSteamProtocol`, `StoreWindowHider.RestoreStoreWindows` |
| 15 | **P2** | Make local imports explicit and stable: user-confirmed executable/shortcut/AUMID plus file identity, not folder heuristics alone | `LocalAdapter.GetLibraryAsync`, `FindPlayableExe`, `StableLocalId` |

## 1. Discovery and account/entitlement truth

### Adopt one evidence model

`ExoLauncher/Models/GameEntry.cs` defaults `Owned = true`, and `PrimaryAction` maps either `CanInstall` or `Owned` to Install. That collapses at least five different facts:

- a current account API verified durable ownership;
- a local cache last observed ownership for that account;
- a machine manifest proves files are or were present;
- a free product is available to acquire;
- access is unknown because the provider is signed out, private, offline, or unsupported.

Use a model such as:

```text
access = verified-owned | verified-not-owned | cached-owned | free | unknown
install = complete | incomplete | absent | unknown
evidence = provider + opaque account scope + observed time + source kind
```

Only `verified-owned`, deliberately permitted `cached-owned` offline behavior, or `free` should enable an install request. `verified-not-owned + installed` should show **Buy / renew access**, not Play; `unknown + installed` may offer Play and let the official client adjudicate, but must not claim ownership. This also fixes refunds, Family Sharing, free weekends, subscription expiry, shared PCs, and locally retained files without inventing a separate rule for each.

Valve's supported public surface reinforces this distinction: `IPlayerService.GetOwnedGames` returns games only when owned-game details are visible, while publisher ownership checks are app/publisher scoped and server-side ([IPlayerService](https://partner.steamgames.com/doc/webapi/IPlayerService), [authentication and ownership](https://partner.steamgames.com/doc/features/auth)). Epic Ecom ownership is likewise scoped to the integrating product/sandbox, not a global EGS library ([Epic Ecom](https://dev.epicgames.com/docs/web-api-ref/ecom-web-apis)).

### Steam: keep machine discovery, stop elevating it

Good current behavior:

- `SteamAdapter.GetLibraryAsync` parses ACF state flags rather than comparing one magic value, keeps incomplete/update state distinct, and uses the active account's local data.
- A successful `GetOwnedGames` response is treated as authoritative only when a real games array exists; unavailable/private remains unknown.
- `LibraryService.InstalledWithoutAccountClaims` strips account data while retaining verified local installs when no account scope is available.

Release blocker:

- `LibraryService.ScanLibraryAsync` calls `SteamOwnershipCatalog.RememberInstalled` for all manifest-backed rows.
- `SteamOwnershipCatalog.TryCreateEntry` requires Installed but not Owned.
- `ToUninstalledGame` later emits `Owned=true` and `CanInstall=true`.

An install left by account A, Family Sharing, a free weekend, or an expired license can therefore become account B's remembered entitlement whenever the optional online snapshot is unavailable. Persist install history as install history; never promote it to ownership.

The online path also underuses its strongest evidence. `SteamWebApi.LoadOwnedGamesAsync` asks for `include_appinfo=0`; `SteamAdapter` uses the returned IDs only to intersect locally known IDs. A never-installed, API-visible owned game therefore never becomes a candidate. Ask for app info and materialize the API rows, or union authoritative IDs into a provider-qualified name lookup. Keep the visibility caveat and do not describe the result as a complete license ledger. Valve also documents a paged, game-filtered catalog through `IStoreService.GetAppList`; the old `ISteamApps.GetAppList` is deprecated ([IStoreService](https://partner.steamgames.com/doc/webapi/IStoreService), [ISteamApps](https://partner.steamgames.com/doc/webapi/ISteamApps)).

Manual refresh is not currently forceful enough. `LibraryService.GetLibraryAsync(force:true)` still reaches a five-minute static Steam ownership cache. Add a refresh reason/context and propagate `ForceProviderRefresh` so a buy, refund, privacy change, or account switch can invalidate the provider cache without making ordinary view changes expensive.

### Epic: scope the cache; keep Legendary as the boundary

`EpicAdapter.GetLibraryAsync` correctly treats EGL/Legendary installed files as machine evidence (`Owned=false`) and merges owned rows separately. The problem is that `%AppData%/epic-owned.json`, `ReadOwnedCache`, `WriteOwnedCache`, and the search service's Epic cache contain no account scope. The adapter can report account B's scope while returning account A's still-fresh rows.

Persist an envelope `{ schemaVersion, opaqueAccountScope, verifiedAtUtc, source, rows }`, reject any mismatch before returning a row, write atomically, and invalidate both library and search caches when the scope changes. `force:true` must bypass the six-hour owned-cache TTL.

Keep Legendary as the credential-owning agent. Do not widen the trust boundary by reading its/EGL's bearer and sending it to exo-id or private Epic endpoints. Epic's published third-party identity path is an application-owned EAS authorization flow with configured permissions, consent, and Brand Review; its documented third-party-launcher exchange-code flow is for a game's own launcher, not a general account-library grant ([EAS getting started](https://dev.epicgames.com/docs/epic-online-services/accounts-and-social/eos-epic-account-services/getting-started), [consent management](https://dev.epicgames.com/docs/epic-online-services/accounts-and-social/eos-epic-account-services/consent-management), [third-party game launcher](https://dev.epicgames.com/docs/epic-online-services/accounts-and-social/eos-epic-account-services/auth-interface/integrate-a-third-party-launcher-with-egs)).

### Riot: model free acquisition separately

The final live-tree re-scan confirms that Riot Client/bootstrap presence no longer sets every fixed product Owned. Uninstalled rows now have `Owned=false` and `CanInstall=true`. That is more truthful, but the current Boolean model cannot complete the action: `GameEntry.PrimaryAction` offers Install from `CanInstall`, while `WebHostBridge.GameInstallAsync` and `LaunchOrchestrator.InstallAsync` reject every unowned title. Add the explicit `free` access state (or a separate acquisition permission) rather than restoring false ownership.

The final live-tree re-scan also confirms `lion` is now labeled 2XKO and its probe includes 2XKO/Lion executables, matching Riot's current paths; TFT remains League-shared. Add Vanguard to 2XKO's dependency/honesty text where Riot requires it—the current `Deps` special case still covers VALORANT only ([2XKO connection guide](https://support-2xko.riotgames.com/hc/en-us/articles/45690805021715-Connection-Troubleshooting-Guide-PC), [TFT connection guide](https://support-teamfighttactics.riotgames.com/hc/en-us/articles/45690758080403-Connection-Troubleshooting-Guide-PC)).

More importantly, Riot states that the League Client API is unsupported for third parties, with no uptime or change guarantees, and its general policy says products cannot create alternatives to the game client ([League Client API](https://developer.riotgames.com/docs/lol), [general policy](https://support-developer.riotgames.com/hc/en-us/articles/22698591841939-General-Policies)). Exo needs written review/approval before treating local patch, eligibility, install, or client-hiding endpoints as a shippable integration. RSO is approved-production identity, not launcher/library/commerce authority.

### Local: install provenance, not a license oracle

`LocalAdapter` has good path guards, avoids reparse-point roots, stages managed copies as siblings, and atomically promotes the staging directory. Its automatic scan still selects a top-level or one-level executable by filename/size heuristic and marks every hit Owned. Keep explicit user registrations as strong local install evidence; label root scans heuristic and require the user to confirm the chosen executable.

Add supported Windows import surfaces instead of deeper filesystem guessing: resolve `.lnk` files with `IShellLink`, enumerate installed packages with package query APIs, and activate known AUMIDs with `IApplicationActivationManager` ([Shell Links](https://learn.microsoft.com/en-us/windows/win32/shell/links), [package query API](https://learn.microsoft.com/en-us/windows/win32/appxpkg/functions), [application activation](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-iapplicationactivationmanager)). For moved portable titles, preserve a file identity (volume serial plus file index) rather than deriving the only stable ID from the path ([GetFileInformationByHandle](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfileinformationbyhandle)).

## 2. Store actions, client suppression, buying, and refunds

### Resolved guard: never accept an agreement for the user

The initial audited tree wrote fabricated `*_eula_* = "99"` records into Steam `localconfig.vdf` and automatically invoked Agree/Accept controls. The final live-tree re-scan confirms that implementation and its call sites are gone. This remains a permanent non-adoption because it recorded legal acceptance without presenting the agreement or obtaining informed consent.

If an agreement blocks install or Play:

1. suspend suppression for Steam;
2. reveal only the verified Steam agreement window;
3. tell the user why official UI is required;
4. resume only after the user acts or cancels.

Valve describes activation as conditional on the user accepting the applicable agreement ([Steam Subscriber Agreement guidance](https://partner.steamgames.com/doc/marketing/branding/ssa)). An earlier click on Exo's Install button is not acceptance of an unseen third-party EULA.

### Use only documented Epic actions

`EpicAdapter.BuildEpicActionUris`, `WatchEpicLauncherJobAsync`, and the update fallback emit `action=install` and `action=uninstall`. Epic currently documents only `launch`, `updatecheck`, and `installer`. `silent=true` applies to launch and is explicitly suggestive: Epic may surface required UI ([Epic protocol activation](https://dev.epicgames.com/docs/epic-games-store/protocol-activation)).

- Use `installer` only as a user-visible installation-options handoff.
- Use `updatecheck` for an update check/handoff.
- Do not issue a protocol uninstall; use Legendary or reveal the official client.
- Do not run a two-hour invisible watcher after an unsupported URI.

The current triple `SandboxId:CatalogId:ArtifactId` launch form is the right documented shape when those values came from a trusted local manifest. The bare artifact fallback is legacy and should be labeled compatibility-only.

### Track who owns a client process before closing it

`LaunchOrchestrator.CloseUnusedStoreClientsAsync` unconditionally starts sibling cleanup. `StoreClientCleanup.ExitUnusedAsync` sends close requests twice and then `TerminateRemainingUnused` kills exact process names. Activity detection covers only a subset of real vendor transfers, and install/update/remove jobs do not all register provider ownership, so stale cleanup can race a later job.

Introduce a `StoreClientLease`:

- snapshot PID, executable path, start time, and whether the process pre-existed;
- hide only the leased provider's verified chrome during the scoped operation;
- never terminate a pre-existing process;
- never terminate on unknown transfer/game activity;
- register the provider for launch, install, update, repair, and remove;
- version/cancel cleanup tasks so an old cleanup cannot act after the next operation begins;
- after exit, close only a client Exo started, only when the user enabled that setting, and prefer a documented graceful command.

This preserves the product's quiet-client goal without destroying downloads, chat, cloud sync, or a client the user opened independently.

### Dispatch is not success

Several adapters return success after Shell/URI dispatch with no game-process proof. `WebHostBridge.GameLaunchAsync` hides Exo after 450 ms and awaits potentially long client warmups. The user can see neither required vendor UI nor a launch-cancel affordance.

Use explicit states:

```text
preparing -> dispatched -> needs-user-action | process-confirmed | failed | cancelled
```

Only process-confirmed is Running. An undocumented EA/Ubisoft/Battle.net/Wargaming protocol must be `HandoffOnly`, never confirmed. Keep Exo visible until a process is confirmed or a documented silent client accepts the handoff; if official UI is required, suspend suppression and say so.

### Open Store must prove it opened

`WebHostBridge.OpenVendorClient` returns `{ ok:true }` before the background `Process.Start` and reveal loop runs. Late failure is only logged. It also begins closing sibling clients even though the user merely asked to open one store.

Centralize Open into an async operation that validates the installed executable or registered URI handler, suspends suppression, starts/re-invokes the client, waits for an expected executable and primary HWND, then publishes success/failure. Do not close siblings for an explicit Open or Buy action. Steam's `steam:` registration is provisional, so validate numeric AppIDs and invoke the OS URI handler rather than building a shell command ([IANA `steam` registration](https://www.iana.org/assignments/uri-schemes/prov/steam), [Steam browser protocol](https://developer.valvesoftware.com/wiki/Steam_browser_protocol)).

There is also an end-to-end scheme mismatch: `Storefront.XboxDestination` can return `ms-windows-store:`, and Ubisoft can return `uplay:`, while `WebHostBridge.OpenUrl` accepts only HTTP(S) and Steam. Route Microsoft Store ProductId/search URIs through a narrowly typed dispatcher; use Ubisoft HTTPS because no supported public `uplay://store` contract was found ([Microsoft Store URI scheme](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-store-app)).

### Buying and refunds remain vendor-controlled

Exo is correct not to host checkout. Keep Buy as a PDP handoff, never a successful purchase claim. After returning, offer **Refresh access** and bypass the entitlement TTL. A refunded but still-installed title must not remain Play-only merely because `Storefront.BuyUrl` hides Buy whenever Installed.

A secondary **Manage purchase / request refund** action may open official account UI without claiming eligibility. Do not automate checkout, account pages, cookies, or refund requests. Steam, Epic, GOG, and Microsoft publish user-controlled flows ([Steam refunds](https://store.steampowered.com/steam_refunds/), [Epic refunds](https://www.epicgames.com/help/en-US/c-Category_BillingSupport/c-EpicGamesStore/how-to-refund-an-epic-games-store-purchase-a000084827), [GOG orders](https://www.gog.com/account/settings/orders), [Microsoft/Xbox refunds](https://support.microsoft.com/en-US/accounts-billing/subscriptions/get-a-refund-for-apps-and-games-purchased-from-microsoft-store)). Publisher refund/commerce APIs are scoped to the publisher's own products and secure services, not a general launcher.

## 3. Launch latency and state

### Current diagnostic snapshot

The last ten complete but uncontrolled startup log samples showed:

| Interval | min | median | p95 |
| --- | ---: | ---: | ---: |
| managed entry to native window ready | 306 ms | 410 ms | 523 ms |
| `MainWindow` construction to WebView visible | 458 ms | 596 ms | 785 ms |
| correlated managed entry to WebView visible | 672 ms | 877 ms | 1,077 ms |

These are useful diagnostics, not a benchmark: UDF/cache state, hardware, power mode, and cold/warm status were uncontrolled. The current `MainWindow` stopwatch also starts after synchronous `AppServices.Initialize`, so its WebView number excludes some real startup cost.

The Play path contains much larger potential waits:

- `LaunchOrchestrator` allows two seconds for a pre-launch achievement baseline.
- Cold Steam may wait up to about 15.75 seconds for process-based listener readiness, then up to 45 seconds for a verified game process.
- Epic can spend 30 seconds on Legendary, then give several URI forms 12 seconds each.
- Riot can spend 45 seconds connecting, additional warm-up retries, then 15 seconds waiting for a launch-ready process.

Most healthy launches return early, but the failure tail is exactly when Exo hides its own window and suppresses the vendor UI. That makes latency a state-design problem, not only a timeout-tuning problem.

### Recommended launch pipeline

1. Validate local game/action state synchronously.
2. Start the required client/agent warm-up and a **local/cached** achievement baseline concurrently.
3. Never block Play on a network achievement refresh. Enrich the already captured baseline later.
4. Emit `dispatched` as soon as the documented client handoff succeeds.
5. Keep observing for a verified game PID and emit `process-confirmed` separately.
6. Keep Cancel/Show client available until process confirmation.
7. Attribute each phase to one monotonic launch correlation ID.

Measure: RPC received, dependency check, account/cache read, baseline, client start, client-ready signal, protocol/agent dispatch, first candidate PID, confirmed PID, window hide, and failure/required-UI reveal. Record cold/warm and p50/p95. Do not reduce confirmation/debounce guards until traces show they dominate healthy launches.

## 4. WebView2 and React startup/navigation/rendering

### P0: active WebViews should not be Low

`MainWindow.EnsureWebCoreAsync` sets the visible main shell to `CoreWebView2MemoryUsageTargetLevel.Low` before navigation. Microsoft says Low is for inactive WebViews and may swap memory to disk; active views should return to Normal because performance can be affected ([WebView2 performance guidance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/performance), [`MemoryUsageTargetLevel`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.memoryusagetargetlevel)).

At the same time, `WebViewHostProfile` unconditionally adds `--disable-backgrounding-occluded-windows`, so the shell can continue full-rate browser work while hidden for gameplay. Remove that undocumented global switch. Keep the main shell Normal while visible; when it moves to the notification area, either use Low or `TrySuspendAsync`, then restore/Resume before showing it.

`TrophyNotificationPresenter` constructs and navigates a second controller during main startup, leaves `IsVisible=true` after hiding its HWND, and also selects Low. Defer its warm until the main shell/library-ready milestone. Explicitly set controller visibility false while hidden and Normal only while the banner is visible. Microsoft recommends sharing an environment—which Exo already does—and avoiding/reducing inactive WebView work ([WebView2 performance guidance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/performance), [`CoreWebView2Controller.IsVisible`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2controller.isvisible)).

### Mount rooms on demand

`LauncherApp` statically imports and immediately mounts Settings, Friends, Profile, and the library/detail surfaces, then hides inactive rooms with the HTML `hidden` attribute. Their effects still run: account/privacy/dependency reads, profile health, roster/library reads, and media work all compete with first paint.

Use a first-visit gate plus `lazy` / `Suspense` for Settings, Friends, Profile, and heavy detail code. After the first visit, React 19's `<Activity mode="hidden">` can preserve state while cleaning up effects; Activity alone may pre-render hidden content, so it does not replace the first-visit gate ([React `lazy`](https://react.dev/reference/react/lazy), [React Activity](https://react.dev/reference/react/Activity)). Keep StrictMode; its extra effect/render checks are development-only.

Defer `Library.StartWatchers`, `HiddenStores.Start`, orphan-window enumeration, and other nonessential services until after native activation or the first shell milestone, but instrument each phase before moving it. Keep settings and the minimum bridge/security state synchronous.

### Reveal on application readiness, not only navigation completion

WebView2 fires DOMContentLoaded before all images and other content complete; NavigationCompleted is later ([navigation event sequence](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/navigation-events)). Add a React `shell.ready` message after the first committed skeleton/cached-library frame, correlate it with DOMContentLoaded and NavigationCompleted, and reveal on the explicit application-ready signal. Retain the native boot panel as the fast initial UI, which aligns with Microsoft's recommendation not to use WebView2 for a splash screen.

Keep the shared `CoreWebView2Environment`, LocalAppData UDF, and virtual-host mapping. Microsoft notes that `WebResourceRequested` is slower because each request crosses to the host UI thread; Exo's mapped-cover fast path is correct ([local content guidance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/working-with-local-content)).

### Stop scroll-only state commits

`WindowedGameGrid` correctly coalesces scroll measurement through `requestAnimationFrame`, but its equivalence includes `originTop/originLeft`. The bounding rectangle moves on every scroll, so React receives a new measurement even when the rendered row window did not change. Keep transient origin data in refs; commit state only when start/end row, column count, row stride, or fixed geometry changes. Profile state commits, ResizeObserver callbacks, mounted cards, and dropped/long frames before and after.

The current custom virtualizer is otherwise appropriate. Do not replace it with a dependency without production traces showing a real gap.

## 5. Search performance and supported catalog sources

The current local scorer is not the immediate bottleneck. An isolated diagnostic run measured roughly 0.75 ms median for 154 titles, 4.63 ms for 1,000, and 22.64 ms for 5,000. Keep the current cancellation generation, 140 ms network debounce, partial results, title-only matching, and bounded typo logic.

The higher-value fixes are provider and render work:

- `StoreSearchService.SearchAsync` waits for Epic/GOG warming before final completion, even after useful partials; Epic can hold that final response for 20 seconds.
- Search spawns another Legendary-owned load instead of consuming the adapter's account-scoped durable owned cache.
- Steam search calls `steamcommunity.com/actions/SearchApps` and Store search JSON endpoints that Valve does not document as Web APIs.
- `WebHostBridge.WarmSearchCovers` turns every result into a full portrait+hero requested warm.

Adopt a provider-qualified search index:

1. Reuse the exact account-scoped library/owned snapshots.
2. With explicit user-key consent, incrementally page the documented `IStoreService.GetAppList` into a local name/AppID index using `if_modified_since`; the default result type is games ([Valve `IStoreService`](https://partner.steamgames.com/doc/webapi/IStoreService)).
3. Without a key, search only current library/owned caches, direct validated AppIDs, and official storefront handoffs; do not silently scrape web endpoints.
4. Precompute normalized title/tokens once per library/index generation.
5. Keep the input urgent and defer only the memoized result subtree with `useDeferredValue` if profiling shows typing jank. React explicitly notes that deferred values do not prevent network requests, so keep cancellation/debounce too ([React `useDeferredValue`](https://react.dev/reference/react/useDeferredValue)).

At the current library size a worker is unjustified. Reconsider near several thousand locally scored rows or after a production trace, not by default.

## 6. Cover, hero, metadata, and media caching

### The cache is already under pressure

The inspected machine had 1,685 cover-cache images using 437.5 MiB—about 85% of the 512 MiB high-water mark. Of those, 577 hero files used 307.3 MiB. The active cached library had 98 visual cards, 25 installed and eight favorites, so hero acquisition is far broader than the intended visible/pinned set.

`WebHostBridge.WarmSearchCovers` calls `WarmCacheAsync(requested:true)`. In `CoverArtService`, requested work bypasses `ShouldWarmWideArt`, uses concurrency 16, and does not receive the abandoned query's cancellation token. Search can therefore download a full hero for every transient result. Split the Boolean into explicit intent:

```text
SearchPortrait | LibraryPortrait | VisibleHero | UserRefetch
```

Search should fetch portrait only and honor cancellation. Use one global prioritized scheduler with a small measured concurrency (start around 2–4, then trace), provider backoff, and one in-flight key per exact source. Run cache pressure accounting after promotions as well as before the first background warm.

### Preload only likely visible art

`LauncherApp` renders a hidden preload tree for up to 16 games; each contains `CoverArt preload` and `HeroWash`. Covers become eager/high and heroes are default eager. Sixteen common 3840×1240 heroes can decode to roughly 291 MiB of RGBA before the visible cards are counted.

Give high fetch priority only to one likely LCP hero and, at most, the first visible covers. Keep offscreen grid art lazy/low, and warm the next detail hero on pointer/focus intent or a bounded idle task. The HTML fetch-priority model makes high a scarce relative hint; marking many resources high removes its value ([WHATWG fetch-priority attributes](https://html.spec.whatwg.org/multipage/urls-and-fetching.html#fetch-priority-attributes), [Chrome fetch-priority guidance](https://web.dev/articles/fetch-priority)).

Store bounded derivatives rather than always retaining source-size heroes: a portrait sized for the largest tile and a hero near the maximum rendered width. `decoding="async"` changes scheduling, not decoded pixel memory. Stream remote responses with `ResponseHeadersRead`, enforce a read-time byte and pixel cap, fully decode with WIC, transcode to a canonical format, flush a temporary file, then atomically promote it ([`HttpCompletionOption`](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption), [WIC decoder workflow](https://learn.microsoft.com/en-us/windows/win32/wic/-wic-decoder-howto-createusingfilename)). Apply the same full-decode rule to custom/profile imports; a plausible PNG/JPEG header is not proof of a decodable safe image.

### Tighten origin and cache identity

Native and React Steam URL checks use substring tests, and `ui/index.html` includes the broad CSP source `https:`. Parse each URL, require HTTPS/default port, compare exact IDN hosts or approved subdomains, and revalidate every redirect/final URI. Remove broad `https:` from `img-src` and retain only explicit vendor hosts.

Map cover/image folders with `CoreWebView2HostResourceAccessKind.DenyCors`, not Allow, and remove wildcard `Access-Control-Allow-Origin`; DenyCors still allows `<img src>` while rejecting arbitrary cross-origin fetch. Use a non-`.local` virtual hostname, as Microsoft's local-content guidance recommends ([host mapping API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.setvirtualhostnametofoldermapping), [resource access kind](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2hostresourceaccesskind), [local content guidance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/working-with-local-content)).

`SanitizeId` can collide (`epic:foo-bar` and `epic:foo_bar`). Use a hash of exact provider+source ID for filenames and persist a sidecar with provider, source ID, original/final URL, fetched time, dimensions, content SHA-256, ETag/Last-Modified, and schema version. Carry provenance with each candidate instead of inferring `CoverSource` from a mapped Steam ID.

### Metadata and online profile media

`StoreMetadataService` permanently caches Steam `/api/appdetails` text without an expiry and can map non-Steam games to a Steam ID, making edition/platform descriptions wrong. That endpoint is not documented in Valve's Web API reference. Keep it optional/unknown, key by exact provider source, add fetched time/TTL and atomic persistence, or obtain a supported catalog/partner source. Public CDN reachability is not an artwork license; review rights before cross-store reuse ([Steam store asset definitions](https://partner.steamgames.com/doc/store/assets), [Steam Web API terms](https://steamcommunity.com/dev/apiterms)).

The online profile client also re-downloads immutable version/SHA-addressed media before checking its validated local entry, even though exo-id supports HTTP 304. Check the exact local cache first, send `If-None-Match`, keep authorization revalidation, and handle 304. Return profile/friend text immediately; prioritize the visible avatar/banner and progressively fetch visible gallery/friend media. Invalidate one denied/corrupt entry rather than clearing every offline image. HTTP conditional semantics are defined by [RFC 9110](https://www.rfc-editor.org/rfc/rfc9110#name-if-none-match).

## 7. Steam achievements

### Keep the fail-closed pieces

The provider already does several important things correctly: it resolves only the active Steam userdata cache, rechecks the active account after reads, treats local UI-cache data as partial, uses `achieved` rather than timestamp alone, rejects community XML as account progress, keeps notification deliveries account-separated, and stores the optional user key with CurrentUser DPAPI.

### P0: never select another account's newest snapshot

`AchievementService.GetLatestSnapshot` and `GetSummary` choose the newest persisted row by game ID across every hashed coverage key. `WebHostBridge` can return that fallback without resolving the current account, and `ui/src/lib/achievements.ts` caches only by game ID. On a shared PC, account B can see account A's counts, rows, unlock timestamps, and showcase totals when B's refresh is pending or unavailable.

Resolve the provider/current account coverage key before every cached read; query persistence by `(provider, sourceGameId, coverageKey)`; return no cached account data when resolution is ambiguous; and key/clear the React cache on account-scope change. Apply the same filter to profile showcase summaries.

### P0: require schema truth before Complete

`SteamWebApiAchievementParser.ParsePlayerAchievements` currently treats `success:true` without an achievements array as confirmed zero, treats schema as optional metadata, and defaults unknown hidden state to false. That can fabricate 0/0/perfection or expose a hidden API name/art.

Valve defines `GetSchemaForGame` as the complete stats/achievement list and `GetPlayerAchievements` as account unlock data ([`ISteamUserStats`](https://partner.steamgames.com/doc/webapi/ISteamUserStats)). Require a valid schema and matching IDs before setting Complete/CompleteCatalog. Missing or contradictory schema remains partial/unavailable. Validate the echoed SteamID, model hidden as unknown/true/false, redact locked hidden rows, and use locked—not unlocked—art for locked visible rows.

Remove the keyless `store.steampowered.com/api/appdetails` category heuristic that upgrades local 0/0 to confirmed zero. Without a valid documented schema, zero stays unknown.

### Consent, transport, latency, and rarity

Settings currently says the Steam key is sent only while Friends is open. It is also used during Steam library scans, achievement detail refreshes, launch baselines, and session polling. Correct the disclosure, call it a **standard user Web API key**, prohibit publisher keys, expose feature-specific opt-ins, and link Valve's terms. The terms require user-requested retrieval, key confidentiality, stored-data disclosure, a privacy policy, and a 100,000-call/day limit ([Steam Web API terms](https://steamcommunity.com/dev/apiterms), [key authentication](https://partner.steamgames.com/doc/webapi_overview/auth)).

Use `x-webapi-key` rather than query-string keys and disable redirects for authenticated requests. Never ship a publisher key or call the partner-only host from the desktop client.

Let a completed local snapshot win the two-second launch budget; cancel/continue online enrichment independently. Add per-endpoint 401/403/429/network backoff. For optional rarity, Valve's documented `GetGlobalAchievementPercentagesForApp` needs no user key; cache it by AppID and never treat aggregate data as unlock evidence ([`ISteamUserStats`](https://partner.steamgames.com/doc/webapi/ISteamUserStats)).

## 8. DLSS / FSR / XeSS replacement and version truth

### P0: downloaded DLLs are unauthenticated executable input

`DlssSwapService` selects artifacts from a community DLSS Swapper manifest. `DownloadManifestDllAsync` accepts an optional MD5 supplied by that same manifest; `IsAllowedDownloadUrl` accepts any `*.githubusercontent.com` and some non-default vendor ports; cache hits require only an x64 PE shape and size. A compromised manifest/cache can become code execution when a game loads the copied DLL.

Until fixed, keep detection/status/restore diagnostics but disable Newest writes. The write path needs all of:

- exact official owner/repository/tag/asset allowlisting;
- size and release SHA-256/digest binding ([GitHub release asset digest](https://docs.github.com/en/rest/releases/assets));
- `WinVerifyTrust` plus expected publisher where the vendor ships signed DLLs ([Microsoft `WinVerifyTrust`](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/nf-wintrust-winverifytrust));
- expected filename, architecture, effect/family, and export-set validation;
- an atomic verified cache promotion;
- no generic raw/gist host or non-443 exception.

Reuse the repository's `VerifiedGitHubReleaseDownloader` design. AMD says official FSR SDK DLLs are signed; NVIDIA's Streamline production guidance says to use NVIDIA-signed DLLs or enforce an application signing system ([AMD FSR SDK](https://gpuopen.com/amd-fsr-sdk/), [NVIDIA Streamline](https://github.com/NVIDIA-RTX/Streamline/blob/main/README.md)). HTTPS, MD5, PE shape, filename, and version resources are not provenance.

### Update a compatible provider cohort or nothing

The current per-file loop can update part of a family and leave other DLLs old after a failure. That is not a safe “newest” operation:

- AMD FSR SDK 2.3 includes component/API changes and says upgradability depends on the individual game release ([AMD 2.3 release](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/releases/tag/v2.3.0), [AMD FSR SDK](https://gpuopen.com/amd-fsr-sdk/)).
- Intel XeSS 3 directs developers to replace `libxess.dll`, `libxell.dll`, and `libxess_fg.dll` together and update the game's UI; Intel says a major version can break functionality ([XeSS 3.0](https://github.com/intel/xess/releases/tag/v3.0.0), [XeSS developer guide](https://github.com/intel/xess/blob/main/doc/xess_sr_developer_guide_english.md)).
- NVIDIA's license warns later SDK versions may introduce incompatibilities ([DLSS license](https://github.com/NVIDIA/DLSS/blob/main/LICENSE.txt)).

Build an explicit filename/API/provider compatibility matrix. Preflight every destination, stage and authenticate the entire required cohort, create validated backups, replace all files under one per-install lock and journal, and roll back the whole cohort if any member fails. Never auto-upgrade an SDK major from numeric comparison alone.

### Restore must be tied to the current game build

`EnsureFactoryBackup` preserves the first `.dlsss` forever. If a store later patches the live DLL, `InvalidateForeignWrite` removes only Exo's written marker; Restore remains enabled and can roll the new game build back to an incompatible pre-update DLL.

After any foreign/store write, quarantine the old baseline and disable Restore. On the next explicit swap, capture the current live file only after store/build evidence and validation, using content hash, file identity, and store build/manifest metadata. Create the backup through temp + flush + hash + atomic move; reject partial or pre-planted backups. Do not silently “adopt” an unverified foreign file.

### Version is a tuple of facts, not one string

Keep separate fields for fixed file version, SDK release, provider/effect version, SHA-256, signer, source release, and compatibility family. Only attach a semantic SDK label when the local bytes match the authenticated catalog artifact. Same length + same version is not byte identity, and a five-part version must not be silently truncated to four.

FSR capability is already stale: the registry-name regex deliberately rejects RX 7900, while current FSR 4.1 adds RX 7000 discrete support and analytical fallbacks on older architectures. Make capability release-specific and use DXGI vendor/device IDs; also distinguish “a capable adapter exists” from “this game runs on it” ([AMD FSR SDK](https://gpuopen.com/amd-fsr-sdk/), [`DXGI_ADAPTER_DESC3`](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_6/ns-dxgi1_6-dxgi_adapter_desc3)).

Do not load an arbitrary game DLL into Exo merely to call a version export; DLL load executes code. Use offline PE inspection/hash-to-release mapping or a tightly isolated helper.

### Anti-cheat and licensing remain hard stops

`UpdateAllAsync` downloads before the later anti-cheat denial. The denylist checks a few titles/path markers and only direct-child anti-cheat folders, so unknown/nested installs fail open. Evaluate protection before any network or writable scan. Given Exo's hard rule, unknown anti-cheat status must refuse; there is no “continue anyway.” Never inject missing files, rename FSR components, patch protected binaries, kill services, or work around store ACLs.

NVIDIA permits SDK distribution only under its application/pass-through conditions and prohibits stand-alone redistribution. Copying its SDK DLL into arbitrary third-party games is not plainly covered and needs legal clearance before release. AMD and Intel notice/redistribution obligations must also ship with the product. Do not bundle, mirror, or call community-manifest artifacts “official” until license and notice review is complete ([NVIDIA license](https://github.com/NVIDIA/DLSS/blob/main/LICENSE.txt), [AMD license](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/main/docs/license.md), [Intel license](https://github.com/intel/xess/blob/main/LICENSE.txt)).

## Explicit non-adoptions

- No automatic EULA/subscriber-agreement acceptance, account-state stamping, checkout, purchase, or refund automation.
- No game binary edits, missing-DLL injection, wrapper/injector, anti-cheat override, kernel/process bypass, or termination of Vanguard/EAC/BattlEye/Steam DRM components.
- No publisher key, client secret, or provider bearer in the desktop UI/binary/log. Publisher/product-scoped Steam, Epic Ecom, Microsoft/XStore, and Riot APIs are not global library APIs.
- No Riot local patch/eligibility/client API as a claimed supported integration without written Riot approval; no LCU credential reuse.
- No Epic bearer reuse or private account endpoints; Legendary remains the bounded operational agent and owns its credentials.
- No undocumented `origin2://`, `uplay://`, `battlenet://`, Rockstar/Wargaming flags, or Galaxy CLI behavior presented as a stable contract.
- Steam client IPC remains a repo-mandated compatibility implementation, not a public Steamworks contract. Keep it isolated, feature-detected, version-tested, and backed by documented URI/client handoff; never use it as entitlement proof.
- No force-kill of a pre-existing store client, unknown transfer, cloud sync, or user-opened window.
- No Steam/Epic storefront scrape promoted to a supported API, no public CDN URL treated as an artwork license, and no account data presented as current after scope/visibility becomes unknown.
- No upscaler “Newest” based on a third-party manifest, HTTPS, MD5, PE shape, filename, or file-version metadata alone.

## Verification plan for follow-up implementation

Focused checks observed during the audit were green: the DLSS/GPU/version filter passed 90/90 and `npm run test:grid-window` passed 12/12 after its concurrent layout-contract update. Those tests confirm current intent; several findings above are unsafe behaviors encoded by that intent, so green tests are not disposition evidence.

1. **Truth:** shared-PC account switches; private/offline APIs; never-installed Steam ownership; refunded/expired/family/free-weekend installs; Epic cache mismatch; Riot free catalog; local moved executable.
2. **Actions:** agreement required, sign-in required, client update required, cold/warm protocol, repeated Open, missing URI handler, unsupported Epic install/uninstall, user-opened sibling client downloading.
3. **Latency:** 30 cold + 30 warm controlled runs per major provider; p50/p95 for every launch/startup phase; cancellation and required-UI recovery.
4. **Artwork:** portrait-only search, query cancellation, high-water crossed in one run, huge-pixel small-byte images, corrupt/truncated input, redirect host change, cache-key collision, offline restart.
5. **Achievements:** account B never sees A; missing/mismatched schema; hidden locked rows; wrong SteamID; private profile; key refusal/rate limit; local baseline wins launch budget.
6. **Upscalers:** attacker raw/gist URL, wrong signer/digest/export set, same version/different bytes, five-part version, nested anti-cheat causes zero HTTP and zero writes, partial family rolls back all, store patch disables stale Restore, junction/reparse escape, concurrent Apply/Restore.

Static/unit checks are not live proof. Store handoffs and installed-app behavior must be validated separately on disposable/non-anti-cheat titles with the exact built executable; no VALORANT, League, 2XKO, Fortnite, or other protected title should be used as an automation target.

## Primary source index

### Stores and accounts

- Valve: [IPlayerService](https://partner.steamgames.com/doc/webapi/IPlayerService), [IStoreService](https://partner.steamgames.com/doc/webapi/IStoreService), [ISteamUserStats](https://partner.steamgames.com/doc/webapi/ISteamUserStats), [Web API authentication](https://partner.steamgames.com/doc/webapi_overview/auth), [Web API terms](https://steamcommunity.com/dev/apiterms), [refunds](https://store.steampowered.com/steam_refunds/), [browser protocol](https://developer.valvesoftware.com/wiki/Steam_browser_protocol).
- Epic: [protocol activation](https://dev.epicgames.com/docs/epic-games-store/protocol-activation), [Ecom quick start](https://dev.epicgames.com/docs/epic-games-store/services/ecom/ecom-quick-start), [Ecom Web APIs](https://dev.epicgames.com/docs/web-api-ref/ecom-web-apis), [third-party game launcher flow](https://dev.epicgames.com/docs/epic-online-services/accounts-and-social/eos-epic-account-services/auth-interface/integrate-a-third-party-launcher-with-egs).
- Riot: [League/LCU documentation](https://developer.riotgames.com/docs/lol), [Developer Portal keys](https://developer.riotgames.com/docs/portal), [general policies](https://support-developer.riotgames.com/hc/en-us/articles/22698591841939-General-Policies).
- Upstream agents: [Legendary](https://github.com/derrod/legendary), [gogdl](https://github.com/Heroic-Games-Launcher/heroic-gogdl).
- Windows: [Shell Links](https://learn.microsoft.com/en-us/windows/win32/shell/links), [package query APIs](https://learn.microsoft.com/en-us/windows/win32/appxpkg/functions), [`IApplicationActivationManager`](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-iapplicationactivationmanager), [Microsoft Store URI scheme](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-store-app).

### WebView, React, and media

- Microsoft: [WebView2 performance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/performance), [navigation events](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/navigation-events), [local content](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/working-with-local-content).
- React: [`lazy`](https://react.dev/reference/react/lazy), [Activity](https://react.dev/reference/react/Activity), [`useDeferredValue`](https://react.dev/reference/react/useDeferredValue), [Profiler](https://react.dev/reference/react/Profiler).
- Standards: [HTML fetch-priority attributes](https://html.spec.whatwg.org/multipage/urls-and-fetching.html#fetch-priority-attributes), [HTTP conditional requests](https://www.rfc-editor.org/rfc/rfc9110#name-if-none-match).

### Upscalers and executable trust

- NVIDIA: [DLSS releases](https://github.com/NVIDIA/DLSS/releases), [DLSS license](https://github.com/NVIDIA/DLSS/blob/main/LICENSE.txt), [Streamline security guidance](https://github.com/NVIDIA-RTX/Streamline/blob/main/README.md).
- AMD: [FSR SDK](https://gpuopen.com/amd-fsr-sdk/), [FidelityFX releases](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/releases), [license](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/main/docs/license.md).
- Intel: [XeSS releases](https://github.com/intel/xess/releases), [XeSS developer guide](https://github.com/intel/xess/blob/main/doc/xess_sr_developer_guide_english.md), [license](https://github.com/intel/xess/blob/main/LICENSE.txt).
- Microsoft/GitHub: [`WinVerifyTrust`](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/nf-wintrust-winverifytrust), [release-asset digest](https://docs.github.com/en/rest/releases/assets).
