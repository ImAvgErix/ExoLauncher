# Launcher API options: first-party support matrix

_Checked 2026-08-20. Primary/first-party documentation and source only._

## Scope and decision rule

This note asks a narrow question: what may a third-party Windows launcher build on as a **documented, supported integration surface**? A game SDK that can inspect entitlements for the developer's own title is not the same thing as an API that can enumerate a player's store-wide library. A client-internal endpoint, local database, command-line switch, or reverse-engineered protocol is not called supported merely because it works today.

“No public API found” below means the reviewed first-party material does not publish a suitable surface. It does not rule out a private partner agreement. Local discovery also proves only that something is installed or registered on this PC; it does not prove account ownership.

## Recommendation

1. **P0 — ship the supported aggregators:** add a consent-driven Windows local/manual import, an optional Playnite export bridge, and itch.io through `butlerd`. These are the clearest documented paths and cover more games without collecting publisher credentials.
2. **P1 — use limited official surfaces where they are honest:** Steam Web API for visible account library metadata; Windows package enumeration and activation for locally installed Microsoft Store/Xbox titles. Keep “not visible” distinct from “empty,” and “installed” distinct from “owned.”
3. **P2 — pursue partner access, not private endpoints:** Epic and GOG have useful first-party SDKs, but the documented SDKs are product/game-scoped. Ask for written launcher/aggregation access before treating either as a cloud-library source.
4. **P3 — remain local/client-authoritative:** Riot, EA, Ubisoft, Battle.net, and Amazon Games expose no reviewed first-party store-wide PC library/install API. Detect proven local installs and hand launch/license/update authority to the official client. Do not build new account scraping or private-protocol dependencies.

## Capability matrix

| Provider / surface | Owned-library metadata | Install / update | Launch / status | Authorization boundary | Exo priority |
| --- | --- | --- | --- | --- | --- |
| Windows local/manual | Installed/selected entries only | Exo may manage only user-selected portable files; vendor titles stay vendor-owned | Shell links, executables, and packaged-app activation | Local user consent; no store credentials | **P0** |
| Playnite bridge | Full Playnite library through an in-process extension | SDK can ask Playnite to install/uninstall, but import/export should begin read-only | SDK can start a Playnite game | Extension runs inside Playnite; raw DB files are not the public contract | **P0** |
| itch.io | Yes, with `profile:owned` or `butlerd` | Yes through `butlerd` | Yes through `butlerd` | itch login/OAuth; keep daemon current | **P0** |
| Steam | `GetOwnedGames` when visible to the caller | Documented URI actions can delegate to Steam; no public third-party download API | Documented Steam URI actions; local manifests remain local evidence | Web API key, privacy policy, user-requested access, 100,000 calls/day | **P1** |
| Xbox / Microsoft Store | No store-wide cloud collection for an unrelated launcher | No supported cross-publisher install/update API | Enumerate installed packages and activate an AUMID | Store APIs are current-app/publisher scoped; Xbox services are title/sandbox scoped | **P1 local; P3 cloud** |
| Epic Games Store / EOS | No documented store-wide EGS library export | Ecom is product/sandbox commerce; documented Launcher URI actions are narrow handoffs | Known-artifact launch/update/install handoff; no result/progress API | EOS organization/product/sandbox/deployment/client credentials; Brand Review for external EAS users | **P2 identity/partner; P3 library** |
| GOG | No documented store-wide GOG account API | Galaxy SDK is for a registered game; Galaxy integration API is inbound to Galaxy | Same product/plugin boundary | Per-game client ID/secret, or a plugin hosted by Galaxy | **P2 partner track** |
| Riot | No; public APIs expose game/account/match data | No documented launcher API | Local client APIs are explicitly unsupported | Register player-facing products; production key approval; secrets may not ship in binaries | **P3 local only** |
| Battle.net | No; APIs expose Blizzard game/profile data | No documented launcher API | No documented desktop-client control API | OAuth client; user authorization for protected profile data | **P3 local only** |
| EA app | No reviewed public surface | No reviewed public surface | Official client remains required for applicable PC licenses | No public launcher authorization flow located | **P3 local only** |
| Ubisoft Connect | No reviewed public surface | No reviewed public surface | Official client may be required to install/launch/unlock content | No public launcher authorization flow located | **P3 local only** |
| Amazon Games | No reviewed public PC-games surface | No reviewed public PC-games surface | No documented Amazon Games App control API | Public Amazon SDKs are for an app's own sign-in/IAP/DRM, chiefly Appstore products | **P3 local only** |

## Provider findings

### Windows local import and packaged apps

- Windows provides a durable, vendor-neutral baseline. Shell links can be resolved with `IShellLink`; a link records the target, working directory, arguments, icon, and description ([Microsoft: Shell Links](https://learn.microsoft.com/en-us/windows/win32/shell/links)). WinUI also has user-facing file/folder pickers intended for desktop apps ([Microsoft: manage files and folders](https://learn.microsoft.com/en-us/windows/apps/develop/files/)).
- `PackageManager.FindPackagesForUser` enumerates packages installed for a user, while `IApplicationActivationManager::ActivateApplication` starts a packaged app by Application User Model ID ([FindPackagesForUser](https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.packagemanager.findpackagesforuser), [ActivateApplication](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-iapplicationactivationmanager-activateapplication)). These are suitable for **local installed-state and launch**, not ownership claims.
- Recommended boundary: import only after an explicit picker/scan action; preserve the resolved path/AUMID and source; never infer a cloud entitlement from a shortcut or package; never modify a game's binaries to make it launch.

### Playnite

- Playnite's supported extension SDK exposes `IPlayniteAPI.Database`; the library tutorial explicitly iterates `PlayniteApi.Database.Games` and provides database change events ([library tutorial](https://api.playnite.link/docs/tutorials/extensions/library.html), [`IPlayniteAPI`](https://api.playnite.link/docs/api/Playnite.SDK.IPlayniteAPI.html)). The same API has `StartGame`, `InstallGame`, and `UninstallGame` methods.
- The API is supplied to a plugin running inside Playnite. Playnite warns not to reference its non-SDK assemblies and says SDK versions within a major branch are backward compatible ([plugin guidance](https://api.playnite.link/docs/tutorials/extensions/plugins.html)). Its changelog also calls direct library-file modification unsupported ([Playnite changelog](https://api.playnite.link/docs/changelog.html)). Therefore, parsing Playnite's private database files is not the supported contract.
- Best Exo design: an optional, open-source Playnite generic extension exports a bounded snapshot (IDs, title, platform/source, installed flag, play action, playtime, artwork references) to a user-chosen file or authenticated local IPC. Start read-only. If the user chooses Playnite as action owner, use its documented `--start <gameId>` or `playnite://playnite/start/{gameId}` surface ([command-line and URI commands](https://api.playnite.link/docs/manual/advanced/cmdlineArguments.html)).

### itch.io

- This is the strongest first-party launcher opportunity. itch.io says that if an app needs to log users in, browse their library, install games, and run them, **`butlerd` is the supported way**. It exposes JSON-RPC 2.0, owns saved login/install state in its own SQLite database, and has an official TypeScript client. itch also stresses that the bundled daemon must be updated for live-API compatibility ([building a launcher with `butlerd`](https://itch.io/docs/butler/launcher-integration.html)).
- OAuth explicitly offers `profile:owned` for games a user purchased or claimed. For a public launcher, the current `butlerd` guide uses browser authorization-code flow with PKCE and `state`, then lets the daemon retain the profile credential; the user approves the app ([launcher authentication](https://itch.io/docs/butler/launcher-integration.html#4-log-the-user-in), [OAuth applications](https://itch.io/docs/api/oauth)). The server API separately distinguishes the user's own uploaded games from purchases ([server API](https://itch.io/docs/api/serverside)).
- Recommended implementation: supervise a pinned/verified `butler` binary, follow the documented daemon handshake and update channel, request the smallest scopes, and let `butlerd` own credentials and cave/install state. Do not read the daemon's SQLite database directly.

### Steam

- `IPlayerService.GetOwnedGames` returns owned games only when owned-game/game-detail visibility allows the caller to see them. It can include name/icon and playtime; inaccessible data must not be presented as an empty library ([IPlayerService](https://partner.steamgames.com/doc/webapi/IPlayerService)).
- Steam OpenID can establish a verified SteamID, but it does not grant a private-library scope ([Steam authentication and ownership](https://partner.steamgames.com/doc/features/auth)). Standard Web API keys require a Steam account and associated domain; Valve says Web API keys must remain confidential ([Web API key authentication](https://partner.steamgames.com/doc/webapi_overview/auth)). The terms additionally require a named application, privacy policy, retrieval only at the user's request, disclosure of stored Steam data, no Steam-password interception, and a default limit of 100,000 calls per day ([Steam Web API terms](https://steamcommunity.com/dev/apiterms)).
- Valve documents `steam://` browser-protocol actions for handing launch/install back to the client, but that surface supplies no completion/progress callback ([Steam browser protocol](https://developer.valvesoftware.com/wiki/Steam_browser_protocol)). This is a narrower support claim than treating Steam client IPC, local ACF files, or `steamclient64.dll` as a general third-party SDK contract.
- Recommended implementation: use local evidence for installed-state, the URI surface for client-owned launch/install prompts where it fits, and a backend-proxied Web API key for optional metadata enrichment. Never embed the application key in the distributed client or ask for/store a Steam password.

### Xbox / Microsoft Store

- `StoreContext` is documented for listing/license/purchase/update data for the **current app and its add-ons**, not for an unrelated user's complete Store library ([StoreContext](https://learn.microsoft.com/en-us/uwp/api/windows.services.store.storecontext)). The server-side Store collection query similarly returns products for apps associated with the publisher's Microsoft Entra client ID ([collection query](https://learn.microsoft.com/en-us/windows/uwp/monetize/query-for-products)).
- Xbox services require a Title ID, Service Configuration ID, and sandbox; Microsoft describes the Title ID as the identity used for a title's content, statistics, achievements, and multiplayer ([Xbox sandbox setup](https://learn.microsoft.com/en-us/gaming/gdk/docs/services/fundamentals/sandboxes/live-setting-up-sandboxes)). This is a title developer surface, not a global Xbox/PC Game Pass library grant.
- Recommended implementation: enumerate and launch installed packages using the Windows APIs above, and optionally open documented Store product/library/download pages with `ms-windows-store:` URIs ([Store URI scheme](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-store-app)). Label subscription/ownership as unknown. Do not use `AppInstallManager`: Microsoft documents that it requires a private capability unavailable to third parties ([AppInstallManager restriction](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.store.preview.installcontrol.appinstallmanager)). Partner outreach is required before promising cloud Xbox library, install, achievements, or Game Pass state.

### Epic Games Store / Epic Online Services

- EOS must be configured as an Epic Developer Portal **product** with Product ID, Sandbox ID, Deployment ID, Client ID, and Client Secret ([EOS configuration](https://dev.epicgames.com/documentation/unreal-engine/enable-and-configure-online-services-eos-in-unreal-engine)).
- Epic Account Services can provide authentication plus approved Basic Profile, friends, and presence scopes, but documents no owned-library permission. External users require Brand Review, see the requested data, and may deny or revoke it ([EAS Auth Interface](https://dev.epicgames.com/docs/epic-online-services/accounts-and-social/eos-epic-account-services/auth-interface), [Brand Review](https://dev.epicgames.com/docs/epic-online-services/accounts-and-social/eos-epic-account-services/brand-review), [consent management](https://dev.epicgames.com/docs/epic-online-services/accounts-and-social/eos-epic-account-services/consent-management)). This makes Epic identity a possible separate feature, not a library grant.
- Epic's commerce documentation describes offers, checkout, and entitlements in an in-game/product flow: after the user launches a game, that game queries its entitlements and compares them to its registered content ([Commerce Interface](https://dev.epicgames.com/documentation/unreal-engine/commerce-interface-in-unreal-engine)). The Ecom API can query ownership by sandbox IDs, reinforcing that the documented boundary is configured product/sandbox commerce ([Ecom callback reference](https://dev.epicgames.com/docs/api-ref/callbacks/eos-ecom-on-query-ownership-by-sandbox-ids-callback)).
- Epic documents `com.epicgames.launcher://` handoffs for known-artifact launch, update-check, and installer actions using Sandbox/Catalog/Artifact identifiers; it documents no progress/result channel ([Epic Games Launcher protocol](https://assets-unreal2-epic-prod-us2.s3.dualstack.us-east-1.amazonaws.com/original/4X/f/c/4/fc4342c50e1a0abf34943e019ee90bacccc62950.pdf)). This does not grant enumeration of every EGS purchase, installed game, or another publisher's catalog entitlements.
- Recommendation: retain a clean abstraction around whatever local/current operational path Exo uses, but do not call EOS a replacement library API. Ask Epic for written partner access and exact permitted scopes before adding Epic cloud-library or client-control promises.

### GOG

- The GOG GALAXY SDK is a game integration SDK. GOG requires special client credentials **for each game**, and only peers with the same Client ID interact ([GALAXY SDK introduction](https://docs.gog.com/galaxyapi/)). Its documented features include achievements, multiplayer, friends, storage, and DLC discovery for that game ([SDK overview](https://docs.gog.com/sdk/)).
- GOG separately publishes the open-source GALAXY Integrations Python API. It lets a plugin hosted by GALAXY authenticate to another platform, import owned/installed games, and implement install/launch methods **into GOG GALAXY** ([official `gogcom` source](https://github.com/gogcom/galaxy-integrations-python-api)). That direction is useful if Exo wants to appear inside GALAXY; it is not an export API for a user's GOG library into Exo.
- No first-party public GOG account-library REST contract was located. Recommendation: partner inquiry for a supported Exo-facing library surface; otherwise keep GOG client/helper authority explicit and do not depend on undocumented web endpoints.

### Riot

- Riot frames its supported public API around game data such as active games, match history, ranked statistics, and account lookup—not purchased-library or patch management ([Riot Developer Portal](https://developer.riotgames.com/)).
- Player-facing products must be registered even if they use no official documented APIs. Development keys expire every 24 hours; public products require production-key approval; production keys start at documented regional limits; API secrets may not be embedded in a distributed binary ([portal/key guidance](https://developer.riotgames.com/docs/portal), [League developer policy](https://developer.riotgames.com/docs/lol)).
- Riot explicitly says the League Client API is local client/CEF communication and “not officially supported for use with third party applications,” with no documentation, uptime, or change guarantees ([League Client API section](https://developer.riotgames.com/docs/lol#league-client-api)).
- Riot's general policy also says products cannot create alternatives to the game client ([Riot General Policies](https://support-developer.riotgames.com/hc/en-us/articles/22698591841939-General-Policies)). Recommendation: do not add LCU/private Riot client endpoints as a launcher foundation. Detect a proven installation, defer patch/license/anti-cheat handling to Riot's client, and use a production Riot API only for a separately approved game-data feature.

### Battle.net

- Blizzard's official OAuth samples require a registered Battle.net API client and demonstrate authorization-code login for accessing user/profile data ([Blizzard OAuth sample](https://github.com/Blizzard/oauth-client-sample)). The documented scopes are game/profile oriented (`wow.profile`, `sc2.profile`, `d3.profile`, and `openid`), not an owned-games scope; the public limits are 36,000 requests/hour and 100/second ([Battle.net OAuth](https://community.developer.battle.net/documentation/guides/using-oauth), [getting started](https://community.developer.battle.net/documentation/guides/getting-started)).
- No reviewed first-party API enumerates Battle.net desktop licenses or controls installs/updates/launches. Recommendation: keep the Battle.net adapter local and official-client-authoritative. A Battle.net OAuth link may later enrich supported game/profile data, but must not be represented as library ownership.

### EA app

- No reviewed EA first-party documentation publishes a general PC library, entitlement, install, update, or launch API for third-party launchers. EA documents the default `C:\Program Files\EA Games` location and EA-app workflows for recognizing existing files, which can inform a user-confirmed local scan but not an ownership claim ([EA install troubleshooting](https://help.ea.com/en/articles/orders-and-rewards/ea-app-game-download-installation-loading-not-working/)). EA's current User Agreement says the EA app may be required, requires an EA Account and internet connection to authenticate applicable PC licenses, and prohibits extracting/using EA service code or data by reverse engineering unless expressly authorized or permitted by law ([EA User Agreement](https://www.ea.com/legal/user-agreement)).
- Recommendation: local installed-title discovery and ordinary official-client launch only. Do not parse private EA account endpoints or claim cloud ownership. Seek a written publisher agreement before anything deeper.

### Ubisoft Connect

- No reviewed Ubisoft first-party documentation publishes a general Connect library, entitlement, install, update, or launch API for third-party launchers. Ubisoft's current terms say Connect may be required to install, launch, access, and unlock content, and prohibit extracting/automating service information without prior permission ([Ubisoft Terms of Use](https://www.ubisoft.com/legal/documents/termsofuse/en-CA)).
- Recommendation: local installed-title discovery and official-client launch only. Avoid private service automation; request written access if Ubisoft coverage becomes strategically important.

### Amazon Games

- Amazon's public developer SDK catalog covers an app developer's own Login with Amazon, Appstore IAP/DRM, and Fire-device services. Login with Amazon's documented scopes expose identity/profile fields, not game ownership; the Appstore SDK uses a per-app public key and is aimed at authorizing users of that app ([Login with Amazon scopes](https://www.developer.amazon.com/docs/login-with-amazon/requesting-scopes-as-essential-voluntary.html), [Amazon SDK catalog](https://www.developer.amazon.com/apps-and-games/sdks), [Appstore SDK integration](https://www.developer.amazon.com/docs/appstore-sdk/integrate-appstore-sdk.html)). These are not Amazon Games App PC-library APIs.
- No reviewed first-party source documents a PC Amazon Games library export or desktop-client install/update/launch API. Recommendation: local installed-title detection and official-client launch only; treat any non-Amazon CLI/helper as an explicitly non-first-party dependency and do not store Amazon credentials.

## Authorization and privacy guardrails

- Prefer browser/daemon/official-client authorization. Never collect a store password in Exo.
- Request the narrowest documented scope, make linking optional, expose unlink/revoke, and keep tokens out of React/browser storage and logs.
- Keep server credentials server-side. Steam requires API-key confidentiality; Riot forbids shipping a key in a binary; EOS, GOG, and Blizzard client secrets identify a registered product/application rather than an end user's generic library grant.
- Model three independent states: `owned`, `installed`, and `action-capable`. A provider can truthfully supply one without the others.
- Treat privacy denial, missing scope, API outage, and unsupported provider as `unknown`/`unavailable`, never as an empty library or zero achievements.
- Require confirmation before download, install, update, uninstall, elevation, or allowing another launcher to act on the user's behalf.

## Suggested sequence

1. Define a provider-capability contract with independent owned/installed/install/update/launch/metadata flags and provenance.
2. Implement manual local import and packaged-app discovery first.
3. Prototype `butlerd` behind a feature flag and verify login, owned library, install, update, cancel, launch, and daemon upgrade on a disposable itch account.
4. Build the read-only Playnite export extension and version its snapshot schema.
5. Harden Steam's optional Web API path around visibility, key custody, caching, daily budget, deletion, and unlink.
6. Keep other vendors local-only while opening Epic/GOG/Microsoft partnership conversations. Promote a capability only after written scope and a live end-to-end proof.

## Evidence limits

- Public documentation can omit private partner programs. “No public API found” is therefore a product-scope conclusion, not proof that no contractual integration exists.
- This review did not log into publisher developer portals, accept SDK agreements, create production applications, or test account-specific entitlements.
- Local manifests, registries, SQLite databases, proprietary URI schemes, and command-line switches were excluded unless the provider documents them as an integration surface.
- Terms and approval programs change. Recheck the cited pages before shipping authentication, distribution, or a new store capability.
