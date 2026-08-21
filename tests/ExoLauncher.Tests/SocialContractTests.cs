using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// The profile is Exo's own: the user authors it and the host keeps it on this
/// PC. These tests hold two lines. First, nothing in the profile or either
/// people list may be invented — no sample cast, no presence, no hours Exo did
/// not read. Second, a store is five separate backends behind one login, and
/// Exo reports each layer honestly.
/// </summary>
public sealed class SocialContractTests
{
    [Fact]
    public void StoreLayers_NeverClaimSocialForStoresWithoutASocialCall()
    {
        var matrix = ReadRepoFile("ExoLauncher", "Services", "StoreLayerMatrix.cs");

        // Steam's friends cache is names only unless the user pastes a key, so
        // social stays partial even then — private profiles stay unknown.
        var steam = Between(matrix, "\"steam\" =>", "\"epic\" =>");
        Assert.Contains("context.ClientPresent ? Partial : None", steam, StringComparison.Ordinal);
        Assert.Contains("Steam owns the session", steam, StringComparison.Ordinal);
        Assert.Contains("context.WebApiKeyPresent", steam, StringComparison.Ordinal);
        Assert.Contains("A Steam Web API key in Settings", steam, StringComparison.Ordinal);
        Assert.DoesNotContain("Social: Wired", steam, StringComparison.Ordinal);

        // Epic's token reaches the list, the names, and last-seen — but Epic
        // serves live presence over its chat service, so social stays partial
        // and the note may not promise more than that.
        var epic = Between(matrix, "\"epic\" =>", "\"gog\" =>");
        Assert.Contains("context.BackendPresent && context.SessionPresent ? Partial : None", epic, StringComparison.Ordinal);
        Assert.Contains("it does not give live presence", epic, StringComparison.Ordinal);

        // GOG social is Galaxy's local database, and only when that file exists.
        var gog = Between(matrix, "\"gog\" =>", "\"riot\" =>");
        Assert.Contains("context.LocalDatabasePresent", gog, StringComparison.Ordinal);
        Assert.Contains("Friends need GOG Galaxy's local database", gog, StringComparison.Ordinal);
        Assert.DoesNotContain("Social: Wired", gog, StringComparison.Ordinal);

        // Riot must never claim it can patch around the anti-cheat patcher.
        var riot = Between(matrix, "\"riot\" =>", "\"xbox\"");
        Assert.Contains("never patches around anti-cheat", riot, StringComparison.Ordinal);
        Assert.Contains("context.ClientPresent && context.BackendPresent ? Wired : None", riot, StringComparison.Ordinal);

        var amazon = Between(matrix, "\"amazon\" =>", "\"itch\" =>");
        Assert.Contains("context.BackendPresent && context.SessionPresent", amazon, StringComparison.Ordinal);
        Assert.Contains("context.SessionPresent ? Partial : None", amazon, StringComparison.Ordinal);
        Assert.Contains("Nile keeps the Amazon token", amazon, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreLayers_ReportEveryLayerAndDefaultToNotWired()
    {
        var matrix = ReadRepoFile("ExoLauncher", "Services", "StoreLayerMatrix.cs");

        foreach (var layer in new[] { "Login", "Owned", "Covers", "Downloads", "Social" })
            Assert.Contains(layer, matrix, StringComparison.Ordinal);

        Assert.Contains("_ => new Layers(None, None, None, None, None,", matrix, StringComparison.Ordinal);

        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");
        Assert.Contains("StoreMatrixWithLayers()", bridge, StringComparison.Ordinal);
        Assert.Contains("\"stores.matrix\" => StoreMatrixWithLayers()", bridge, StringComparison.Ordinal);
        Assert.Contains("login = layers.Login", bridge, StringComparison.Ordinal);
        Assert.Contains("social = layers.Social", bridge, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreFriends_FromTheSteamCacheStayUnknownAndSayWhy()
    {
        var social = ReadRepoFile("ExoLauncher", "Services", "SocialService.cs");

        // Cached names carry no presence. They must not be dressed up as offline.
        Assert.Contains("\"unknown\"", social, StringComparison.Ordinal);
        Assert.Contains("Live: false", social, StringComparison.Ordinal);
        Assert.Contains("Live presence needs a Steam Web API key in Settings", social, StringComparison.Ordinal);
        Assert.Contains("SteamWebApi.LoadSummariesAsync", social, StringComparison.Ordinal);
        Assert.Contains("if (live) steam = await OverlaySteamAsync", social, StringComparison.Ordinal);
        Assert.Contains("GogGalaxyFriends.Load()", social, StringComparison.Ordinal);
        Assert.Contains("FoldGalaxy(", social, StringComparison.Ordinal);
        Assert.Contains("SamePerson", social, StringComparison.Ordinal);
        Assert.DoesNotContain("levenshtein", social, StringComparison.OrdinalIgnoreCase);
        var diskOnly = Between(social, "public FriendsSnapshot StoreFriends()", "public async Task<FriendsSnapshot> StoreFriendsAsync(");
        Assert.DoesNotContain("LoadSummariesAsync", diskOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("Random", social, StringComparison.Ordinal);

        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");
        Assert.Contains("live = snapshot.Live", bridge, StringComparison.Ordinal);
        Assert.Contains("note = snapshot.Note", bridge, StringComparison.Ordinal);
    }

    [Fact]
    public void Profile_IsAuthoredByTheUser_NotMirroredFromAStore()
    {
        var social = ReadRepoFile("ExoLauncher", "Services", "SocialService.cs");
        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");
        var all = social + bridge;

        // The local profile module owns no account credential; native identity does.
        Assert.DoesNotContain("exo.signIn", all, StringComparison.Ordinal);
        Assert.DoesNotContain("accessToken", social, StringComparison.Ordinal);
        Assert.DoesNotContain("password", social, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Exo has no account of its own", social, StringComparison.Ordinal);

        // The identity is read from settings the user wrote. A store persona may
        // only appear in the separately labelled store-account list.
        var profile = Between(social, "public ExoProfile Profile(", "public ExoProfile SetProfile(");
        Assert.Contains("settings.ProfileName", profile, StringComparison.Ordinal);
        Assert.Contains("settings.ProfileHandle", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadSelf", profile, StringComparison.Ordinal);
        var accounts = Between(
            social,
            "private static IReadOnlyList<StoreAccount> StoreAccounts(",
            "private static IReadOnlyList<string> Showcase(");
        Assert.Contains("LoadSelf", accounts, StringComparison.Ordinal);
        Assert.Contains("storeAccounts = profile.StoreAccounts", bridge, StringComparison.Ordinal);

        // Playtime comes from the local library, and an unknown unlock total
        // stays null instead of rendering a confident zero.
        Assert.DoesNotContain("MinutesPerLevel", social, StringComparison.Ordinal);
        Assert.DoesNotContain("LevelPercent", all, StringComparison.Ordinal);
        Assert.DoesNotContain("LevelAtCap", all, StringComparison.Ordinal);
        Assert.Contains("UnlockedCount: null", social, StringComparison.Ordinal);
    }

    [Fact]
    public void Profile_CapsAndValidatesEverythingItPersists()
    {
        var social = ReadRepoFile("ExoLauncher", "Services", "SocialService.cs");
        var settings = ReadRepoFile("ExoLauncher", "Models", "GameEntry.cs");
        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");

        foreach (var field in new[]
                 {
                     "ProfileName", "ProfileHandle", "ProfilePronouns", "ProfileStatusText",
                     "ProfileBio", "ProfileAvatarGameId", "ProfileBannerGameId", "ProfileAccent",
                     "ProfileShowcase", "ProfileRoster",
                 })
        {
            Assert.Contains(field, settings, StringComparison.Ordinal);
        }

        // A client string never reaches settings.json as it arrived.
        Assert.Contains("NormalizeHandle", social, StringComparison.Ordinal);
        Assert.Contains("NormalizeAccent", social, StringComparison.Ordinal);
        Assert.Contains("private static string? Cap(", social, StringComparison.Ordinal);
        Assert.Contains("AccentKeys.Contains(key) ? key : DefaultAccent", social, StringComparison.Ordinal);
        Assert.Contains("KnownId(avatarGameId, known)", social, StringComparison.Ordinal);
        Assert.Contains("\"profile.set\" => ProfileSet", bridge, StringComparison.Ordinal);
    }

    [Fact]
    public void Level_DoesNotPaintProgressItCannotMake()
    {
        var profile = ReadRepoFile("ui", "src", "components", "ProfileRoom.tsx");

        // Exo has no progression system, so local playtime must not be painted
        // as a level or XP bar.
        Assert.DoesNotContain("levelFromMinutes", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-level", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("exo-xp", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("progressbar", profile, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Roster_MergesOnlineFriendsWithoutLosingTheLocalFallback()
    {
        var social = ReadRepoFile("ExoLauncher", "Services", "SocialService.cs");
        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");
        var friends = ReadRepoFile("ui", "src", "components", "FriendsRoom.tsx");
        var host = ReadRepoFile("ui", "src", "lib", "host.ts");
        var all = social + bridge + friends + host;

        // The local roster remains available while accepted online friends and
        // requests use their own typed namespace.
        Assert.Contains("RosterMax", social, StringComparison.Ordinal);
        Assert.Contains("\"friends.roster\" => FriendsRoster()", bridge, StringComparison.Ordinal);
        Assert.Contains("\"friends.add\" => FriendsAdd", bridge, StringComparison.Ordinal);
        Assert.Contains("\"friends.remove\" => FriendsRemove", bridge, StringComparison.Ordinal);
        Assert.Contains("\"online.friends.list\"", bridge, StringComparison.Ordinal);
        Assert.Contains("\"online.friends.requests\"", bridge, StringComparison.Ordinal);
        Assert.Contains("mergeOnlinePeople", friends, StringComparison.Ordinal);
        Assert.Contains("Local fallback", friends, StringComparison.Ordinal);
        Assert.Contains("onHostEvent('online.presence'", friends, StringComparison.Ordinal);
        Assert.Contains("sign in to use Exo friend requests and presence", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("service Exo does not run", all, StringComparison.OrdinalIgnoreCase);

        // Removing someone stays two clicks, and nothing may imply a graph Exo
        // cannot reach.
        Assert.Contains("Confirm remove", friends, StringComparison.Ordinal);
        foreach (var fiction in new[] { "send message", "onlineNow" })
            Assert.DoesNotContain(fiction, all, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Epic is the one store that answers Exo over the network, so every URL in
    /// the adapter has to be one that was verified against a live launcher
    /// token. A guessed endpoint is a fabricated friend waiting to happen.
    /// </summary>
    [Fact]
    public void EpicFriends_UseOnlyVerifiedEndpointsAndNeverInventPresence()
    {
        var epic = ReadRepoFile("ExoLauncher", "Adapters", "EpicFriends.cs");
        var social = ReadRepoFile("ExoLauncher", "Services", "SocialService.cs");

        var urls = Regex.Matches(epic, "https://[^\"]+")
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(
            [
                "https://friends-public-service-prod.ol.epicgames.com/friends/api/v1/{0}/summary",
                "https://account-public-service-prod.ol.epicgames.com/account/api/public/account?",
                "https://presence-public-service-prod.ol.epicgames.com/presence/api/v1/_/{0}/last-online",
            ],
            urls);

        // There is one Epic session in the app and this adapter borrows it.
        Assert.Contains("EpicPlaytime.ResolveSessionAsync", epic, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh_token", epic, StringComparison.Ordinal);
        Assert.DoesNotContain("user.json", epic, StringComparison.Ordinal);
        Assert.DoesNotContain("Random", epic, StringComparison.Ordinal);

        // Epic rows are names and a last-seen timestamp. Never a live state.
        var rows = Between(
            social,
            "private static (IReadOnlyList<FriendPresence> Rows, FriendSource? Source) EpicRows(",
            "private static FriendsSnapshot Merge(");
        Assert.Contains("\"unknown\"", rows, StringComparison.Ordinal);
        Assert.Contains("Live: false", rows, StringComparison.Ordinal);
    }

    /// <summary>
    /// The full count spans every store Exo can read; the badge count is only
    /// the people a store says are around.
    /// </summary>
    [Fact]
    public void Counts_SpanEveryStore_ButOnlyPresenceAStoreReportedIsCalledActive()
    {
        var social = ReadRepoFile("ExoLauncher", "Services", "SocialService.cs");
        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");
        var lib = ReadRepoFile("ui", "src", "lib", "social.ts");
        var friends = ReadRepoFile("ui", "src", "components", "FriendsRoom.tsx");

        Assert.Contains("ActivePresence = [\"ingame\", \"online\", \"away\", \"dnd\"]", social, StringComparison.Ordinal);
        Assert.Contains("const ACTIVE_PRESENCE: readonly Presence[] = ['ingame', 'online', 'away', 'dnd']", lib, StringComparison.Ordinal);

        // A cached name is not a person Exo can see, so unknown counts as nobody.
        var active = Between(social, "ActivePresence = [", "];");
        Assert.DoesNotContain("unknown", active, StringComparison.Ordinal);
        Assert.DoesNotContain("offline", active, StringComparison.Ordinal);

        Assert.Contains("count = snapshot.Count", bridge, StringComparison.Ordinal);
        Assert.Contains("activeCount = snapshot.ActiveCount", bridge, StringComparison.Ordinal);
        Assert.Contains("active now", friends, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not offline", friends, StringComparison.OrdinalIgnoreCase);

        // Every store that gave up nothing still has to say why.
        Assert.Contains("public sealed record FriendSource(", social, StringComparison.Ordinal);
        Assert.Contains("sources = snapshot.Sources", bridge, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two accounts with the same name are still two accounts. Only the user
    /// may say they are one person, and doing so moves the row.
    /// </summary>
    [Fact]
    public void Linking_IsTheUsersClaim_AndIsNeverInferred()
    {
        var social = ReadRepoFile("ExoLauncher", "Services", "SocialService.cs");
        var links = ReadRepoFile("ExoLauncher", "Services", "FriendLinks.cs");
        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");
        var friends = ReadRepoFile("ui", "src", "components", "FriendsRoom.tsx");

        Assert.Contains("\"friends.link\" => FriendsLink", bridge, StringComparison.Ordinal);
        Assert.Contains("\"friends.unlink\" => FriendsUnlink", bridge, StringComparison.Ordinal);
        Assert.Contains("Exo cannot work this out", links, StringComparison.Ordinal);
        Assert.Contains("Link to someone on Exo", friends, StringComparison.Ordinal);

        // A claimed row leaves the store list instead of being shown twice.
        Assert.Contains("!linked.Contains(friend.Id)", social, StringComparison.Ordinal);

        // And nothing may guess the match from a name, an avatar, or a library.
        foreach (var guess in new[] { "autoLink", "fuzzy", "levenshtein", "probableMatch", "sameNameAs" })
            Assert.DoesNotContain(guess, links + social + friends, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Opening a game from a friend's row uses the one launch path, and never
    /// points it at a title the rest of Exo keeps its hands off.
    /// </summary>
    [Fact]
    public void OpeningWhatAFriendIsPlaying_LabelsWhatItWillActuallyDo()
    {
        var lib = ReadRepoFile("ui", "src", "lib", "social.ts");
        var friends = ReadRepoFile("ui", "src", "components", "FriendsRoom.tsx");

        foreach (var title in new[] { "valorant", "league of legends", "fortnite", "teamfight tactics" })
            Assert.Contains(title, lib, StringComparison.Ordinal);
        Assert.Contains("store === 'riot'", lib, StringComparison.Ordinal);
        Assert.Contains("isAntiCheatTitle(game)", lib, StringComparison.Ordinal);
        Assert.Contains("export function friendPlayingAction", lib, StringComparison.Ordinal);
        Assert.Contains("kind: 'play'", lib, StringComparison.Ordinal);
        Assert.Contains("label: 'Play'", lib, StringComparison.Ordinal);
        Assert.Contains("label: 'Install'", lib, StringComparison.Ordinal);
        Assert.Contains("kind: 'buy'", lib, StringComparison.Ordinal);
        Assert.Contains("Buy on Steam", lib, StringComparison.Ordinal);

        // Play is only the installed, non-anti-cheat case. The other three
        // ownership states must not reuse that word.
        var playable = Between(lib, "export function openableGame", "export type FriendPlayingKind");
        Assert.Contains("!game.installed", playable, StringComparison.Ordinal);
        Assert.Contains("isAntiCheatTitle(game)", playable, StringComparison.Ordinal);
        Assert.DoesNotContain("label: 'Play'", playable, StringComparison.Ordinal);

        // A playing title without live presence must not be shown as in-game.
        Assert.Contains("live ? friendPlayingAction(", friends, StringComparison.Ordinal);
        Assert.Contains("live ? playingTitle(", friends, StringComparison.Ordinal);
        Assert.Contains("host.launch(", friends, StringComparison.Ordinal);
        Assert.Contains("host.install(", friends, StringComparison.Ordinal);
        Assert.Contains("host.openUrl(", friends, StringComparison.Ordinal);
        Assert.Contains("does not open anti-cheat titles", lib, StringComparison.Ordinal);
        Assert.DoesNotContain("Open game", friends, StringComparison.Ordinal);
        Assert.DoesNotContain("export function storeBuyUrl", lib, StringComparison.Ordinal);
        Assert.Contains("game.buyUrl", lib, StringComparison.Ordinal);
    }

    [Fact]
    public void FriendPlayingAction_PreservesStopAndUpdateState()
    {
        var lib = ReadRepoFile("ui", "src", "lib", "social.ts");
        var friends = ReadRepoFile("ui", "src", "components", "FriendsRoom.tsx");
        var action = Between(lib, "export function friendPlayingAction", "export function buyLabel");
        var run = Between(friends, "async function run()", "async function openDeals()");

        Assert.Contains("canStop?: boolean", lib, StringComparison.Ordinal);
        Assert.Contains("updateAvailable?: boolean", lib, StringComparison.Ordinal);
        Assert.Contains("'stop'", lib, StringComparison.Ordinal);
        Assert.Contains("'update'", lib, StringComparison.Ordinal);
        Assert.Contains("if (game.canStop)", action, StringComparison.Ordinal);
        Assert.Contains("kind: 'stop'", action, StringComparison.Ordinal);
        Assert.Contains("label: 'Stop'", action, StringComparison.Ordinal);
        Assert.Contains("game.updateAvailable", action, StringComparison.Ordinal);
        Assert.Contains("kind: 'update'", action, StringComparison.Ordinal);
        Assert.Contains("label: 'Update'", action, StringComparison.Ordinal);

        // Runtime state wins over the generic installed => Play fallback.
        Assert.True(
            action.IndexOf("if (game.canStop)", StringComparison.Ordinal) <
            action.IndexOf("const playable", StringComparison.Ordinal));
        Assert.True(
            action.IndexOf("game.updateAvailable", StringComparison.Ordinal) <
            action.IndexOf("const playable", StringComparison.Ordinal));

        Assert.Contains("case 'stop'", run, StringComparison.Ordinal);
        Assert.Contains("host.stop(game.id)", run, StringComparison.Ordinal);
        Assert.Contains("case 'update'", run, StringComparison.Ordinal);
        Assert.Contains("host.update(game.id)", run, StringComparison.Ordinal);
    }

    [Fact]
    public void FriendPlayingAction_ResolvesTheExactGroupedVariantBeforeChoosingAnAction()
    {
        var lib = ReadRepoFile("ui", "src", "lib", "social.ts");
        var resolver = Between(lib, "function gameForPlayingId", "export function openableGame");
        var action = Between(lib, "export function friendPlayingAction", "export function buyLabel");

        // Presence reports an exact store id. A grouped card may expose that id
        // only through variants, so the variant's installed/owned/runtime state
        // must replace the card projection before Play/Install/Stop is chosen.
        Assert.Contains("card.id === playingId", resolver, StringComparison.Ordinal);
        Assert.Contains("card.variants?.find", resolver, StringComparison.Ordinal);
        Assert.Contains("variant.id === playingId", resolver, StringComparison.Ordinal);
        Assert.Contains("Object.assign({}, card, variant)", resolver, StringComparison.Ordinal);
        Assert.Contains("gameForPlayingId(playingId, games)", action, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "games.find((candidate) => candidate.id === playingId)",
            action,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A friend's title that is not in this library must not look like Install.
    /// A Steam app id still buys. A bare name says why nothing will happen.
    /// </summary>
    [Fact]
    public void UnmatchedPlayingTitle_NeverLooksLikeInstall()
    {
        var lib = ReadRepoFile("ui", "src", "lib", "social.ts");
        var action = Between(lib, "function steamStoreUrlFromPlayingId", "export function buyLabel");

        Assert.Contains("steamStoreUrlFromPlayingId", action, StringComparison.Ordinal);
        Assert.Contains("label: 'Buy on Steam'", action, StringComparison.Ordinal);
        Assert.Contains("steam://store/", action, StringComparison.Ordinal);
        Assert.Contains("Exo cannot match this title, so it cannot install it.", action, StringComparison.Ordinal);
        Assert.Contains("Exo cannot open a store page for this title.", action, StringComparison.Ordinal);
        Assert.Contains("game.buyUrl", action, StringComparison.Ordinal);

        // Unmatched is not Install. A Steam app id buys; a bare name says why.
        var unmatched = Between(action, "if (!game)", "if (isAntiCheatTitle(game))");
        Assert.DoesNotContain("label: 'Install'", unmatched, StringComparison.Ordinal);
        Assert.DoesNotContain("kind: 'install'", unmatched, StringComparison.Ordinal);
        Assert.Contains("Exo cannot match this title, so it cannot install it.", unmatched, StringComparison.Ordinal);
    }

    [Fact]
    public void FriendProfile_LeavesWithoutAPeopleBackButton()
    {
        var friends = ReadRepoFile("ui", "src", "components", "FriendsRoom.tsx");
        var tokens = ReadRepoFile("ui", "src", "tokens.css");

        Assert.DoesNotContain("exo-friend-back", friends, StringComparison.Ordinal);
        Assert.DoesNotContain("is-hidden-mobile", friends, StringComparison.Ordinal);
        Assert.Contains("event.key !== 'Escape'", friends, StringComparison.Ordinal);
        Assert.Contains("cur === id ? null : id", friends, StringComparison.Ordinal);

        var sticky = Between(tokens, ".exo-roster-sticky {", ".exo-roster-steam-note");
        Assert.DoesNotContain("position: sticky", sticky, StringComparison.Ordinal);
        Assert.DoesNotContain("position:sticky", sticky, StringComparison.Ordinal);
    }

    [Fact]
    public void FriendsSearch_ClosesDetailsForRowsItHides()
    {
        var friends = ReadRepoFile("ui", "src", "components", "FriendsRoom.tsx");
        var visibility = Between(friends, "const visibleFriends = useMemo", "const selectedPerson =");

        Assert.Contains("source === 'exo'", visibility, StringComparison.Ordinal);
        Assert.Contains("!visiblePeople.some((person) => person.id === selectedPersonId)", visibility, StringComparison.Ordinal);
        Assert.Contains("setSelectedPersonId(null)", visibility, StringComparison.Ordinal);
        Assert.Contains("source === 'stores'", visibility, StringComparison.Ordinal);
        Assert.Contains("!visibleFriends.some((friend) => friend.id === selectedFriendId)", visibility, StringComparison.Ordinal);
        Assert.Contains("setSelectedFriendId(null)", visibility, StringComparison.Ordinal);
    }

    [Fact]
    public void FriendsPanel_RoutesUnknownSteamToTheWebApiKey()
    {
        var friends = ReadRepoFile("ui", "src", "components", "FriendsRoom.tsx");

        Assert.Contains("Steam status needs a Web API key.", friends, StringComparison.Ordinal);
        Assert.Contains("Open Steam Web API key in Settings", friends, StringComparison.Ordinal);
        Assert.Contains("set-tab-stores", friends, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Steam Web API key\"", friends, StringComparison.Ordinal);
        Assert.Contains("steamKeySet === false", friends, StringComparison.Ordinal);
        Assert.DoesNotContain("offline from absence", friends, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FriendsPresence_IsPerRow_AndUnavailableRowsStayExplained()
    {
        var friends = ReadRepoFile("ui", "src", "components", "FriendsRoom.tsx");
        var lib = ReadRepoFile("ui", "src", "lib", "social.ts");

        // A successful source request does not make every row live. Missing
        // summaries and private profiles stay unknown and keep their source note.
        Assert.DoesNotContain("?.live === true ||", friends, StringComparison.Ordinal);
        Assert.Contains("live={friend.live === true}", friends, StringComparison.Ordinal);
        Assert.Contains("live={selectedFriend.live === true}", friends, StringComparison.Ordinal);
        Assert.Contains("friendPresence(friend) === 'unknown'", friends, StringComparison.Ordinal);
        Assert.Contains("presence === 'unknown' || !live", friends, StringComparison.Ordinal);

        // Unknown is still the row's explicit status and must not be presented
        // in the same group as a confirmed offline persona state.
        var grouping = Between(lib, "const PRESENCE_GROUPS", "export function sortFriends");
        Assert.Contains("key: 'unknown', label: 'Presence unavailable'", grouping, StringComparison.Ordinal);
        Assert.Contains("statuses: ['unknown']", grouping, StringComparison.Ordinal);
        Assert.Contains("key: 'offline', label: PRESENCE_LABEL.offline", grouping, StringComparison.Ordinal);
        Assert.Contains("statuses: ['offline']", grouping, StringComparison.Ordinal);
        Assert.DoesNotContain("statuses: ['unknown', 'offline']", grouping, StringComparison.Ordinal);
        Assert.Contains("PRESENCE_LABEL[presence]", friends, StringComparison.Ordinal);
    }

    [Fact]
    public void FriendsGrouping_RequiresPerRowLiveAuthorityAndKeepsLastSeenHonest()
    {
        var friends = ReadRepoFile("ui", "src", "components", "FriendsRoom.tsx");
        var lib = ReadRepoFile("ui", "src", "lib", "social.ts");
        var authority = Between(lib, "export function friendPresence", "const PRESENCE_RANK");
        var grouping = Between(lib, "export function sortFriends", "/** Empty groups are dropped.");
        var groups = Between(lib, "export function groupFriends", "const ANTI_CHEAT_TITLES");
        var row = Between(friends, "function FriendRow", "/** What Exo can actually say right now");

        Assert.Contains("live?: boolean", lib, StringComparison.Ordinal);
        Assert.Contains("friend.live === true ? presenceOf(friend.status) : 'unknown'", authority, StringComparison.Ordinal);
        Assert.Contains("friendPresence(a)", grouping, StringComparison.Ordinal);
        Assert.Contains("friendPresence(b)", grouping, StringComparison.Ordinal);
        Assert.Contains("group.statuses.includes(friendPresence(friend))", groups, StringComparison.Ordinal);
        Assert.Contains("const seen = lastSeenLabel(friend.lastSeenUtc)", row, StringComparison.Ordinal);
        Assert.Contains("presence === 'offline'", row, StringComparison.Ordinal);
        Assert.Contains(": seen", row, StringComparison.Ordinal);
    }

    [Fact]
    public void FriendsRoom_RefreshesOnActivation_SurfacesAddFailures_AndResetsFriendState()
    {
        var friends = ReadRepoFile("ui", "src", "components", "FriendsRoom.tsx");
        var activation = Between(
            friends,
            "// Steam's Web API is requested only while this room is on screen.",
            "useEffect(\n    () =>");
        var addPerson = Between(friends, "async function addPerson", "async function removePerson");

        Assert.Matches(@"if \(!active\) return\s+let cancelled = false\s+void loadFriendsCacheFirst", activation);
        Assert.Contains("catch (error)", addPerson, StringComparison.Ordinal);
        Assert.Contains("return false", addPerson, StringComparison.Ordinal);
        Assert.Contains("key={selectedFriend.id}", friends, StringComparison.Ordinal);
    }

    [Fact]
    public void FriendsRoom_SourceTabsAndSelectedRowsExposeTheirInteractiveState()
    {
        var friends = ReadRepoFile("ui", "src", "components", "FriendsRoom.tsx");
        var tabs = Between(friends, "<div className=\"exo-roster-tabs\"", "{source === 'stores' && steamNeedsKey");

        Assert.Contains("role=\"tablist\"", tabs, StringComparison.Ordinal);
        Assert.Contains("id={SOURCE_TAB_IDS.exo}", tabs, StringComparison.Ordinal);
        Assert.Contains("id={SOURCE_TAB_IDS.stores}", tabs, StringComparison.Ordinal);
        Assert.Contains("aria-controls={SOURCE_PANEL_IDS.exo}", tabs, StringComparison.Ordinal);
        Assert.Contains("aria-controls={SOURCE_PANEL_IDS.stores}", tabs, StringComparison.Ordinal);
        Assert.Contains("tabIndex={source === 'exo' ? 0 : -1}", tabs, StringComparison.Ordinal);
        Assert.Contains("tabIndex={source === 'stores' ? 0 : -1}", tabs, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(tabs, Regex.Escape("onKeyDown={handleSourceTabKeyDown}")).Count);

        var keyboard = Between(friends, "function handleSourceTabKeyDown", "async function addPerson");
        Assert.Contains("event.key === 'ArrowLeft') next = source === 'exo' ? 'stores' : 'exo'", keyboard, StringComparison.Ordinal);
        Assert.Contains("event.key === 'ArrowRight') next = source === 'stores' ? 'exo' : 'stores'", keyboard, StringComparison.Ordinal);
        Assert.Contains("event.preventDefault()", keyboard, StringComparison.Ordinal);
        Assert.Contains("document.getElementById(SOURCE_TAB_IDS[next])?.focus()", keyboard, StringComparison.Ordinal);

        Assert.Contains("id={SOURCE_PANEL_IDS.exo}", friends, StringComparison.Ordinal);
        Assert.Contains("aria-labelledby={SOURCE_TAB_IDS.exo}", friends, StringComparison.Ordinal);
        Assert.Contains("id={SOURCE_PANEL_IDS.stores}", friends, StringComparison.Ordinal);
        Assert.Contains("aria-labelledby={SOURCE_TAB_IDS.stores}", friends, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(friends, "role=\"tabpanel\"").Count);
        Assert.Contains("selected={person.id === selectedPersonId}", friends, StringComparison.Ordinal);
        Assert.Contains("selected={friend.id === selectedId}", friends, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(friends, Regex.Escape("aria-pressed={selected}")).Count);
    }

    [Fact]
    public void FriendsCache_DowngradesPresenceBeforePersistingOrRestoring()
    {
        var friends = ReadRepoFile("ui", "src", "components", "FriendsRoom.tsx");
        var sanitize = Between(friends, "function staleFriendsResponse(", "export function FriendsRoom");

        Assert.Contains("live: false", sanitize, StringComparison.Ordinal);
        Assert.Contains("status: 'unknown'", sanitize, StringComparison.Ordinal);
        Assert.Contains("statusText: null", sanitize, StringComparison.Ordinal);
        Assert.Contains("playingId: null", sanitize, StringComparison.Ordinal);
        Assert.Contains("playingTitle: null", sanitize, StringComparison.Ordinal);
        Assert.Contains("presenceFrom: null", sanitize, StringComparison.Ordinal);
        Assert.Contains("activeCount: 0", sanitize, StringComparison.Ordinal);
        Assert.Contains("sources:", sanitize, StringComparison.Ordinal);

        var load = Between(friends, "const loadFriends = useCallback", "useEffect(() =>");
        var paint = load.IndexOf("applyFriends(result)", StringComparison.Ordinal);
        var persist = load.IndexOf(
            "writeCache(CACHE_KEYS.friends, staleFriendsResponse(result))",
            StringComparison.Ordinal);
        Assert.True(paint >= 0 && persist > paint, "fresh presence must paint before the downgraded cache write");

        Assert.Contains("applyFriends(staleFriendsResponse(cachedFriends))", friends, StringComparison.Ordinal);
        Assert.Equal(
            2,
            Regex.Matches(
                friends,
                Regex.Escape("void loadFriends(true).catch(applyFriendsFailure)"),
                RegexOptions.CultureInvariant).Count);
        Assert.DoesNotContain("if (peekCache<FriendsResponse>(CACHE_KEYS.friends)) return", friends, StringComparison.Ordinal);
    }

    [Fact]
    public void FriendsColdLoad_PaintsDiskSnapshotBeforeStartingLiveRefresh()
    {
        var friends = ReadRepoFile("ui", "src", "components", "FriendsRoom.tsx");
        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");
        var social = ReadRepoFile("ExoLauncher", "Services", "SocialService.cs");
        var rpc = Between(bridge, "private async Task<object> FriendsListAsync", "private object FriendsRoster");
        var diskOnly = Between(
            social,
            "public FriendsSnapshot StoreFriends()",
            "public async Task<FriendsSnapshot> StoreFriendsAsync");

        Assert.Contains("live\n            ? await _social.StoreFriendsAsync", rpc, StringComparison.Ordinal);
        Assert.Contains(": _social.StoreFriends()", rpc, StringComparison.Ordinal);
        Assert.Contains("EpicFriends.Cached()", diskOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("EpicFriends.LoadAsync", diskOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadSummariesAsync", diskOnly, StringComparison.Ordinal);

        var cacheFirst = Between(
            friends,
            "const loadFriendsCacheFirst = useCallback",
            "useEffect(() =>");
        var disk = cacheFirst.IndexOf("await loadFriends(false)", StringComparison.Ordinal);
        var live = cacheFirst.IndexOf(
            "void loadFriends(true).catch(applyFriendsFailure)",
            StringComparison.Ordinal);
        Assert.True(disk >= 0 && live > disk, "disk friends must paint before the live request starts");
        Assert.Contains("if (isCancelled()) return", cacheFirst, StringComparison.Ordinal);

        var activation = Between(
            friends,
            "// Steam's Web API is requested only while this room is on screen.",
            "useEffect(\n    () =>");
        Assert.Contains("void loadFriendsCacheFirst(() => cancelled)", activation, StringComparison.Ordinal);
        Assert.Contains("cancelled = true", activation, StringComparison.Ordinal);
    }

    [Fact]
    public void FriendsAndProfileUi_ShipNoSamplePeople()
    {
        var friends = ReadRepoFile("ui", "src", "components", "FriendsRoom.tsx");
        var profile = ReadRepoFile("ui", "src", "components", "ProfileRoom.tsx");
        var lib = ReadRepoFile("ui", "src", "lib", "social.ts");
        var epic = ReadRepoFile("ExoLauncher", "Adapters", "EpicFriends.cs");
        var ui = friends + profile + lib + epic;

        // The reference designs shipped with a mock cast. None of it may land here.
        foreach (var ghost in new[]
                 {
                     "Mira Chen", "Jules Okonkwo", "Ada Voss", "Sable Quinn", "Kasim Hale",
                     "Alex Rivera", "dana.okafor", "priya.nair",
                 })
        {
            Assert.DoesNotContain(ghost, ui, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("Math.random", ui, StringComparison.Ordinal);
        Assert.DoesNotContain("tickPresence", ui, StringComparison.Ordinal);

        // Both rooms read the real host bridge, and the profile face is the
        // user's own pick — never a store avatar URL.
        Assert.Contains("friendsList(", friends, StringComparison.Ordinal);
        Assert.Contains("friendsRoster(", friends, StringComparison.Ordinal);
        Assert.Contains("profileGet(", profile, StringComparison.Ordinal);
        Assert.Contains("profileSet(", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("profile.avatarUrl", profile, StringComparison.Ordinal);
    }

    [Fact]
    public void Showcase_PicksArePersistedLibraryIdsOnly()
    {
        var social = ReadRepoFile("ExoLauncher", "Services", "SocialService.cs");
        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");
        var settings = ReadRepoFile("ExoLauncher", "Models", "GameEntry.cs");

        Assert.Contains("ProfileShowcase", settings, StringComparison.Ordinal);
        Assert.Contains("known.Contains(id)", social, StringComparison.Ordinal);
        Assert.Contains("Take(ShowcaseMax)", social, StringComparison.Ordinal);
        Assert.Contains("private const int ShowcaseMax = 10;", social, StringComparison.Ordinal);
        Assert.Contains("\"profile.setShowcase\" => ProfileSetShowcase", bridge, StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="SettingsService.Current"/> hands out a detached snapshot, so a
    /// profile the caller cannot read back is a profile that was never saved.
    /// </summary>
    [Fact]
    public void SettingsSnapshot_CarriesTheAuthoredProfile()
    {
        var service = new SettingsService(new AppSettings
        {
            ProfileName = "Nine",
            ProfileHandle = "nine",
            ProfileAccent = "steel",
            ProfileShowcase = ["steam:1", "epic:2"],
            ProfileRoster = [new ProfilePerson { Handle = "pal", Name = "Pal", Note = "co-op" }],
        });

        var snapshot = service.Current;

        Assert.Equal("Nine", snapshot.ProfileName);
        Assert.Equal("nine", snapshot.ProfileHandle);
        Assert.Equal("steel", snapshot.ProfileAccent);
        Assert.Equal(new[] { "steam:1", "epic:2" }, snapshot.ProfileShowcase);
        Assert.Equal("pal", Assert.Single(snapshot.ProfileRoster).Handle);

        // And the snapshot must not be a handle back into the service.
        snapshot.ProfileShowcase.Clear();
        snapshot.ProfileRoster.Clear();
        Assert.Equal(2, service.Current.ProfileShowcase.Count);
        Assert.Single(service.Current.ProfileRoster);
    }

    [Fact]
    public async Task UpdateProfile_PersistsTheAuthoredFieldsAndRoster()
    {
        await InIsolatedDataDirectory(async () =>
        {
            var service = new SettingsService();
            service.UpdateProfile(settings =>
            {
                settings.ProfileName = "Nine";
                settings.ProfileHandle = "nine";
                settings.ProfileStatusText = "Playing slow games";
                settings.ProfileAccent = "sage";
                settings.ProfileShowcase = ["steam:620"];
                settings.ProfileRoster.Add(new ProfilePerson { Handle = "pal", Note = "co-op" });
            });

            using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(PathHelper.SettingsPath));
            var root = persisted.RootElement;
            Assert.Equal("Nine", root.GetProperty("profileName").GetString());
            Assert.Equal("nine", root.GetProperty("profileHandle").GetString());
            Assert.Equal("sage", root.GetProperty("profileAccent").GetString());
            Assert.Equal("steam:620", root.GetProperty("profileShowcase")[0].GetString());
            Assert.Equal("pal", root.GetProperty("profileRoster")[0].GetProperty("handle").GetString());

            // A reload has to see the same profile, not a default one.
            var reloaded = new SettingsService();
            reloaded.Load();
            Assert.Equal("Nine", reloaded.Current.ProfileName);
            Assert.Equal("Playing slow games", reloaded.Current.ProfileStatusText);
            Assert.Single(reloaded.Current.ProfileRoster);
        });
    }

    [Fact]
    public void SteamWebApi_IsOptInHttps_AndNeverLogsASecret()
    {
        var api = ReadRepoFile("ExoLauncher", "Adapters", "SteamWebApi.cs");
        var store = ReadRepoFile("ExoLauncher", "Services", "SteamWebApiKeyStore.cs");
        var social = ReadRepoFile("ExoLauncher", "Services", "SocialService.cs");
        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");
        var friends = ReadRepoFile("ui", "src", "components", "FriendsRoom.tsx");
        var settings = ReadRepoFile("ui", "src", "components", "SettingsPanel.tsx");
        var host = ReadRepoFile("ui", "src", "lib", "host.ts");

        Assert.Contains("https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/", api, StringComparison.Ordinal);
        Assert.Contains("BatchSize = 100", api, StringComparison.Ordinal);
        Assert.DoesNotContain("ISteamUser/GetFriendList", api, StringComparison.Ordinal);
        Assert.DoesNotContain("partner.steam-api.com", api, StringComparison.Ordinal);

        Assert.Contains("if (live) steam = await OverlaySteamAsync", social, StringComparison.Ordinal);
        Assert.Contains("SteamWebApiKeyStore.TryRead()", social, StringComparison.Ordinal);
        Assert.Contains("live = hasParams &&", bridge, StringComparison.Ordinal);
        Assert.Contains("steamWebApiKeySet = SteamWebApiKeyStore.HasKey()", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("steamWebApiKey =", Between(bridge, "private object BuildSettings()", "private object SetSettings("), StringComparison.Ordinal);

        Assert.Contains("30_000", friends, StringComparison.Ordinal);
        Assert.Contains("steamWebApiKeySet", friends, StringComparison.Ordinal);
        Assert.Contains("host.friendsList(live)", friends, StringComparison.Ordinal);
        Assert.DoesNotContain("?.live === true ||", friends, StringComparison.Ordinal);
        Assert.Contains("live={friend.live === true}", friends, StringComparison.Ordinal);
        Assert.Contains("friend.live === true", friends, StringComparison.Ordinal);
        Assert.Contains("Epic is last-seen only", friends, StringComparison.Ordinal);
        Assert.Contains("Presence from", friends, StringComparison.Ordinal);

        Assert.Contains("Steam Web API key", settings, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Steam Web API key\"", settings, StringComparison.Ordinal);
        Assert.Contains("https://steamcommunity.com/dev/apikey", settings, StringComparison.Ordinal);
        Assert.Contains("steamWebApiKeySet?: boolean", host, StringComparison.Ordinal);
        Assert.Contains("delete next.steamWebApiKey", host, StringComparison.Ordinal);

        Assert.DoesNotContain("AppLog.Debug(\"Steam presence query unavailable: \" + ex.Message)", api, StringComparison.Ordinal);
        Assert.Contains("AppLog.Debug(\"Steam presence query unavailable: \" + ex.GetType().Name)", api, StringComparison.Ordinal);
        Assert.Contains("AppLog.Debug(\"Steam Web API key could not be stored: \" + ex.GetType().Name)", store, StringComparison.Ordinal);
        var get = Between(api, "private static async Task<Payload?> GetAsync", "private static void HoldOff");
        Assert.DoesNotContain("AppLog.Debug(url", get, StringComparison.Ordinal);
        Assert.DoesNotContain("AppLog.Debug(\"Steam presence query \" + url", get, StringComparison.Ordinal);
    }

    private static async Task InIsolatedDataDirectory(Func<Task> test)
    {
        var previous = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);
        var root = Path.Combine(
            Path.GetTempPath(),
            "ExoLauncherSocialContractTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, root);
            await test();
        }
        finally
        {
            Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, previous);
            try { Directory.Delete(root, recursive: true); }
            catch { /* temporary test cleanup is best effort */ }
        }
    }

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"missing '{start}'");
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"missing '{end}' after '{start}'");
        return text[from..to];
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ExoLauncher.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ReadRepoFile(params string[] relative)
    {
        return File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(relative).ToArray()));
    }
}
