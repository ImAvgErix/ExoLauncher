# Steam and Discord profile customization

_Checked 2026-08-20. Primary/first-party sources only._

## Scope

This note covers profile features that Steam or Discord either describes as popular/user-requested or continues to foreground, expand, and monetize. “Users value” is not treated as independent market research: direct evidence is called out; the remaining product lessons are explicitly inferences from platform behavior and feature placement.

## Steam

### What users can express and feature

- Steam Community profiles support custom avatars, showcases, badges, and owned-game information ([Steam Support: Community Profiles](https://help.steampowered.com/en/wizard/HelpWithSteamIssue/?issueid=1002)).
- A showcase is explicitly a user-curated way to feature what the owner finds interesting, including favorite games, achievements, screenshots, and virtual items. The official FAQ says the first showcase unlocks at Steam Level 10 and another slot unlocks every ten levels ([Steam Trading Cards FAQ](https://steamcommunity.com/tradingcards/faq/Badges)).
- The available proof/collection surfaces are broad: Game Collector, Item Collector, Items to Trade, and Achievement showcases are all named in Steamworks documentation ([Profile Features](https://partner.steamgames.com/doc/marketing/profile?l=english)). Valve later added Community Awards, Game Completionist, Featured Artwork, and Video showcases, plus Points-funded upgrades and additional copies ([2020 Steam Winter Sale announcement](https://steamcommunity.com/games/593110/announcements/detail/2904223191309839604)).
- Badges make progress and investment legible. A user can feature one at the top of the full profile and mini-profile; badges grant XP, and Steam describes level as a quick indication of investment in an account ([Steam Trading Cards FAQ](https://steamcommunity.com/tradingcards/faq/Badges)).

### Cosmetics and the strongest value signal

- Steam’s Points Shop spans avatar frames, animated avatars, full and mini-profile backgrounds, stickers, and themed Game Profile bundles. Game Profiles combine an animated avatar, frame, mini-profile background, full background, and a five-color theme ([Points Shop Items](https://partner.steamgames.com/doc/marketing/pointsshopitems?language=english)).
- Valve says **millions of players** have exchanged Steam Points for animated game-themed collectibles. The same partner documentation labels its item list “in order of popularity” and places animated avatar frames, animated avatars, and animated profile backgrounds first ([Points Shop Items](https://partner.steamgames.com/doc/marketing/pointsshopitems?language=english)). This is the clearest first-party evidence here of relative user demand.
- Profile expression is tied to fandom and collection: crafting a game badge can yield a game-themed profile background and emoticon, while cards, backgrounds, and some other items can be traded or sold ([Steam Trading Cards FAQ](https://steamcommunity.com/tradingcards/faq/Badges)).

### Privacy

- Steam exposes three overall profile states—Public, Friends Only, and Private—with additional subcategories ([Steam Profile Privacy](https://help.steampowered.com/en/faqs/view/588C-C67D-0251-C276)).
- “Game details” is separately controllable and covers owned/wishlisted games, achievements, playtime, in-game presence, and the current title; total playtime has an additional hide control. Valve said these controls came directly from user feedback ([New Profile Privacy Settings](https://steamcommunity.com/games/593110/announcements/detail/1667896941884942467)).
- Per-game privacy can suppress a title's ownership, activity, status, and playtime, and omits that game's title and achievements from profile showcases ([Steam Private Games](https://help.steampowered.com/en/faqs/view/1150-C06F-4D62-4966)). Steam's Web API correspondingly returns owned games only when game details are visible to the viewer, so inaccessible data must not be interpreted as an empty library ([IPlayerService](https://partner.steamgames.com/doc/webapi/IPlayerService?l=english)).
- Inventory visibility is separately selectable as public, friends-only, or private ([Steam Trading](https://help.steampowered.com/en/faqs/view/46A2-2B3C-95CC-8878)).

**Steam signal:** users are given both visual identity and curated evidence of taste, progress, rarity, and contribution. Showcases are not just decoration; they let the owner decide which part of their gaming identity deserves prominence.

## Discord

### What users can express

- The base profile supports an avatar, display name, About Me (up to 190 characters with Markdown, links, emoji, and Unicode), and pronouns. Nitro adds animated avatars, uploaded image/GIF banners, two-color profile themes, and custom emoji in About Me ([Custom Profiles](https://support.discord.com/hc/en-us/articles/4403147417623-Custom-Profiles)).
- Discord's newer Profile Board converges on Steam-like curation: users can reorder widgets for a favorite game, up to five currently played games, up to twenty wanted or previously played games, and—where supported—game stats, achievements, and progress. These lists are manually authored rather than inferred from live activity ([Profile Widgets FAQ](https://support.discord.com/hc/en-us/articles/35344672307607-Profile-Widgets-FAQ)).
- Purchased avatar decorations and profile effects are permanent collection items that can be previewed, equipped, and mixed together ([Avatar Decorations & Profile Effects](https://discord.com/blog/avatar-decorations-collect-and-keep-the-newest-styles)). This makes identity cosmetics collectible without making the core identity fields paid.
- Cosmetic identity can extend beyond the profile card: purchased Nameplates decorate names in DMs, group chats, and member lists, while Nitro Display Name Styles add fonts, colors, and effects. Discord provides reduced-motion or disable controls for these effects ([Nameplates FAQ](https://support.discord.com/hc/en-us/articles/30408457944215-Nameplates-FAQ), [Display Name Styles FAQ](https://support.discord.com/hc/en-us/articles/33833879643927-Discord-Display-Name-Styles-FAQ)).
- Nitro per-server profiles let a user present a different avatar, banner, and About Me in each community while leaving the primary profile unchanged ([Per-Server Profiles](https://support.discord.com/hc/en-us/articles/4409388345495-Per-Server-Profiles)). This supports contextual self-presentation, not merely more visual polish.
- Connected external accounts can be shown on the profile and can share what the user is playing, listening to, or otherwise doing ([Account Connections](https://support.discord.com/hc/en-us/articles/32330173689623-Account-Connections-on-Discord-FAQ)). Badges add non-authored signals such as Nitro tenure, boosting, quests, or account history ([Profile Badges 101](https://support.discord.com/hc/en-us/articles/360035962891-Profile-Badges-101)).

### Direct value signal

Discord says it “keep[s] hearing that users love” perks that customize how they appear, and cites that demand when explaining its Shop expansion into collectible avatar decorations and profile effects ([Discord product update](https://discord.com/blog/best-place-to-hang-out-with-friends)). This is first-party qualitative feedback, not a published survey or usage breakdown.

### Privacy

- Discord’s current profile-privacy control offers Friends & All Servers, Friends & Small Servers (200 members or fewer), and Friends Only. Restricting the full profile hides custom status, pronouns, badges, activity, connected accounts, bio, widgets, and wishlists, but still leaves avatar, banner, display name, username, server tag, account age, and mutual friends/servers visible ([Profile Privacy Setting](https://support.discord.com/hc/en-us/articles/38859942749463-Profile-Privacy-Setting-on-Discord)).
- Profile privacy and activity sharing are separate. Activity can be disabled globally, defaulted to friends/all servers, friends/small servers, or friends only, and adjusted per server; recent activity can appear on profiles for up to 30 days ([Activity Sharing FAQ](https://support.discord.com/hc/en-us/articles/7931156448919-Activity-Sharing-on-Discord-FAQ)).
- Direct-message and friend-request permissions are also separate from profile visibility. Users can limit requests to everyone, friends-of-friends, server members, any combination, or nobody ([Blocking & Privacy Settings](https://support.discord.com/hc/en-us/articles/217916488-Blocking-Privacy-Settings)).

**Discord signal:** Discord's direct statement supports demand for customization generally; its investment in global identity, paid expressive polish, and per-server identity shows the product directions it has chosen, not a quantified preference ranking. Privacy is modeled as several independent decisions—profile detail, activity, messages, and requests—rather than one “private account” switch.

## Cross-platform product lessons

These are inferences from the first-party evidence above, not measured Exo user preferences.

1. **Protect a free identity core.** Avatar, handle/display name, short bio, and a readable static visual treatment are the baseline. Animation, frames, effects, and themed bundles are secondary polish.
2. **Let users curate proof, not just decorate.** Favorite games, achievements, collections, artwork/media, and a featured badge/item make a gaming profile feel authored. Steam establishes this pattern; Discord's newer Profile Board independently converges on it.
3. **Keep visibility controls granular.** Profile fields, game/activity presence, social contact, and discoverability reveal different things and should not be silently coupled.
4. **Preview before publishing.** Both platforms repeatedly provide edit/preview/apply flows; this matters more as banners, themes, animation, and contextual variants accumulate.
5. **Do not copy progression gates blindly.** Steam uses account levels and Points to expand showcases, while Discord uses Nitro for animation and contextual profiles. Those systems demonstrate willingness to invest, but do not prove that the same gates would fit Exo.

## Evidence limits

- No third-party surveys, community guides, Reddit posts, or forum anecdotes were used. These sources describe platform capabilities and the platforms’ own interpretation of feedback; they do not establish a representative ranking across all users.
- Steam provides the strongest quantitative signal (“millions” of Points exchanges) and a relative popularity order, but does not publish counts by item type. Discord provides a qualitative feedback statement but no sample, methodology, or conversion data.
- Steam’s showcase FAQ has no visible revision date. Its current public text was checked on 2026-08-20, but entitlement thresholds should be rechecked before claiming exact parity.
- Discord’s Help Center reflects rapidly changing entitlements and privacy defaults. The cited profile-privacy and activity articles were updated in July 2026; region- and age-specific defaults can differ.
- Discord Profile Boards are desktop/browser-only in the cited documentation, and the Game Stats widget supports only select games and users. Display Name Styles also has rollout-sensitive fonts and effects.
- Storefront/Points inventory varies over time and by account or locale. This note records durable categories and rules, not the current catalog.
