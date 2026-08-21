# Exo Launcher comparative audit — Exa, August 2026

This is a bounded implementation audit, not a claim that every third-party
launcher API is stable or available to a client. Exa was used to locate the
primary references; the conclusions below are constrained by Exo's local-first
and anti-cheat rules.

## Findings that changed this pass

| Surface | Reference pattern | Exo decision |
| --- | --- | --- |
| Steam achievements | Steam's `ISteamUserStats` separates account progress (`GetPlayerAchievements`) from the game catalog (`GetSchemaForGame`). Privacy and HTTP failures are meaningful unavailable states. | Keep the local cache as a progress fallback, retry in-place Steam cache writes, and never turn a catalog-only response into a fake 0/N snapshot. |
| Artwork | Playnite's library plugins treat artwork as a metadata provider and cache files by stable game identity; itch's butlerd launcher flow fetches owned keys/caves early and checks updates in the background. | Keep native source selection authoritative, mount Friends/Profile ahead of navigation, preload bounded above-the-fold and friend-playing art, and refresh cache revisions without blocking local launch. |
| Grid performance | Playnite batches database changes and exposes virtualized views; a desktop library should measure its real viewport rather than use a fixed poster width. | The virtual grid now derives card width from the actual container, targets twelve columns where readable, and keeps a bounded overscan window. |
| Upscaler versions | Vendor DLL file resources and SDK/product release names are different version domains. FSR 3.1 releases expose 1.0.x Windows resources while the meaningful SDK line is 3.1.x. | Compare semantic display families for FSR 2.x → 3.1.x transitions and raw build numbers within a family; restore and Newest share the same native decision path. |

## Primary references

- Steamworks `ISteamUserStats`: https://partner.steamgames.com/doc/webapi/ISteamUserStats
- Steamworks Web API overview and response semantics: https://partner.steamgames.com/doc/webapi_overview
- Playnite plugin API: https://api.playnite.link/docs/tutorials/extensions/plugins.html
- Playnite library and artwork model: https://api.playnite.link/docs/master/tutorials/extensions/library.html
- itch.io launcher integration / butlerd: https://itch.io/docs/butler/launcher-integration.html
- Epic third-party launcher integration: https://dev.epicgames.com/docs/epic-online-services/accounts-and-social/eos-epic-account-services/auth-interface/integrate-a-third-party-launcher-with-egs
- Current DLSS Swapper manifest used by the native updater: https://beeradmoore.github.io/dlss-swapper/manifest.json

## Explicit non-adoptions

- No game binary edits, anti-cheat bypass, or hidden store-client automation.
- No public Steam catalog is treated as account unlock evidence.
- No provider credential or store identity is inferred when the provider is unavailable.
