using ExoLauncher.Adapters;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// The Exo profile and the two people lists behind it.
///
/// The profile is the user's own: every field is authored by them and kept in
/// settings.json on this PC. Exo has no account of its own, no server, and no
/// directory — so the roster is a local list of handles the user typed, and it
/// never claims presence, requests, or messages.
///
/// Store friends are a separate source. Steam's on-disk cache carries names and
/// avatar hashes. Live status is an opt-in Steam Web API call, only while
/// Friends is open and only with a key the user pasted. Without that key the
/// rows stay "unknown" instead of being dressed up as offline.
/// </summary>
internal sealed class SocialService
{
    private const int ShowcaseMax = 10;
    private const int RosterMax = 100;
    private const int NameMax = 40;
    private const int HandleMax = 24;
    private const int PronounsMax = 24;
    private const int StatusMax = 80;
    private const int BioMax = 400;
    private const int NoteMax = 120;

    private const string DefaultAccent = "ash";

    /// <summary>The only accents that may be persisted. Anything else falls back.</summary>
    private static readonly string[] AccentKeys = ["ash", "steel", "sand", "clay", "sage", "rose"];

    /// <summary>
    /// The blocks the profile page is built from, in the order Exo ships them.
    /// The user reorders and hides these; an unknown key never reaches disk.
    /// </summary>
    private static readonly string[] SectionKeys = ["facts", "about", "showcase", "stores"];

    private static readonly string[] LayoutKeys = ["left", "center"];
    private static readonly string[] BannerHeightKeys = ["short", "standard", "tall"];
    private static readonly string[] ShowcaseStyleKeys = ["grid", "rows"];

    private readonly LibraryService _library;
    private readonly SettingsService _settings;

    public SocialService(LibraryService library, SettingsService settings)
    {
        _library = library;
        _settings = settings;
    }

    /// <summary>
    /// The presence states that count as someone being around. "offline" is not
    /// one, and neither is "unknown" — a name Exo cached is not a person Exo can
    /// see.
    /// </summary>
    private static readonly string[] ActivePresence = ["ingame", "online", "away", "dnd"];

    public sealed record FriendPresence(
        string Id,
        string Name,
        string? AvatarUrl,
        string Source,
        string Status,
        string? StatusText,
        string? PlayingId,
        string? PlayingTitle,
        string? LastSeenUtc = null,
        bool Live = false,
        string? PresenceFrom = null);

    /// <summary>One store's contribution, so a silent store can say why it is silent.</summary>
    public sealed record FriendSource(string Store, bool Live, int Count, string Note);

    public sealed record FriendsSnapshot(
        bool Live,
        string? Source,
        string? Note,
        IReadOnlyList<FriendPresence> Friends,
        IReadOnlyList<FriendSource> Sources,
        int Count,
        int ActiveCount);

    /// <summary>A store session Exo could read a name from. Labelled as the store's, not Exo's.</summary>
    public sealed record StoreAccount(string Store, string DisplayName, string? AccountName);

    /// <summary>A store row the user said is the same human as this Exo person.</summary>
    public sealed record PersonLink(string Id, string Store, string? Name);

    public sealed record ExoPerson(
        string Id,
        string Handle,
        string? Name,
        string? Note,
        string? AddedUtc,
        IReadOnlyList<PersonLink> Links);

    public sealed record RosterSnapshot(
        bool Live,
        string Note,
        IReadOnlyList<ExoPerson> People);

    public sealed record RosterResult(bool Ok, string? Message, RosterSnapshot Roster);

    public sealed record ProfileGalleryImage(string Slot, string Url);

    public sealed record ExoProfile(
        string? Name,
        string? Handle,
        string? Pronouns,
        string? StatusText,
        string? Bio,
        string Accent,
        string? AvatarGameId,
        string? BannerGameId,
        // Virtual-host URLs for pictures the user uploaded, null when there are none.
        string? AvatarImageUrl,
        string? BannerImageUrl,
        IReadOnlyList<ProfileGalleryImage> GalleryImages,
        string Layout,
        string BannerHeight,
        string ShowcaseStyle,
        bool ShowHandle,
        IReadOnlyList<string> Sections,
        IReadOnlyList<string> HiddenSections,
        string? PlayingId,
        string? PlayingTitle,
        int GameCount,
        int InstalledCount,
        int? PlaytimeMinutes,
        int? UnlockedCount,
        int StoresConnected,
        int RosterCount,
        IReadOnlyList<string> Showcase,
        IReadOnlyList<StoreAccount> StoreAccounts);

    /// <summary>How the page is arranged. A null field leaves that choice alone.</summary>
    public sealed record ProfileLook(
        string? Layout,
        string? BannerHeight,
        string? ShowcaseStyle,
        bool? ShowHandle,
        IReadOnlyList<string>? Sections,
        IReadOnlyList<string>? HiddenSections);

    public sealed record ProfileImageResult(bool Ok, string? Message, ExoProfile Profile);

    /// <summary>
    /// Everyone Exo can read from a store the user is signed in to, minus anyone
    /// they have already claimed on their Exo list. Instant: Steam comes off
    /// disk and Epic comes from its last verified snapshot, so no caller waits.
    /// </summary>
    public FriendsSnapshot StoreFriends()
    {
        var steam = ReadSteam();
        return Merge(steam, EpicFriends.Cached(), GogGalaxyFriends.Load());
    }

    /// <summary>
    /// The same list with Epic given a chance to answer first. Bounded by
    /// <paramref name="ct"/> and by the adapter's own timeout — a slow Epic
    /// degrades to the cached snapshot instead of holding the room shut.
    /// Steam's Web API runs only when <paramref name="live"/> is true — Friends
    /// on screen — never from a library scan, launch, or the badge read.
    /// </summary>
    public async Task<FriendsSnapshot> StoreFriendsAsync(CancellationToken ct = default, bool live = false)
    {
        var epic = await EpicFriends.LoadAsync(ct).ConfigureAwait(false);
        var steam = ReadSteam();
        if (live) steam = await OverlaySteamAsync(steam, ct).ConfigureAwait(false);
        return Merge(steam, epic, GogGalaxyFriends.Load());
    }

    private readonly record struct SteamPack(
        IReadOnlyList<FriendPresence> Rows,
        FriendSource? Source,
        IReadOnlyList<SteamFriends.Friend> Cached);

    /// <summary>
    /// localconfig.vdf is a name and avatar cache written by the Steam client.
    /// It carries no presence, so every row stays unknown rather than being
    /// dressed up as offline.
    /// </summary>
    private static SteamPack ReadSteam()
    {
        var steamRoot = SteamAdapter.TryResolveSteamRootPublic();
        if (steamRoot is null)
            return new SteamPack(Array.Empty<FriendPresence>(), null, Array.Empty<SteamFriends.Friend>());

        var cached = SteamFriends.LoadActiveAccount(steamRoot);
        var rows = cached
            .Select(friend => new FriendPresence(
                friend.AccountKey,
                friend.Name,
                friend.AvatarUrl,
                "steam",
                "unknown",
                null,
                null,
                null))
            .ToList();

        return new SteamPack(rows, new FriendSource(
            "steam",
            Live: false,
            rows.Count,
            cached.Count == 0
                ? "Sign in to Steam once so Exo can read the local friends list."
                : "Names come from the Steam client cache on this PC. Live presence needs a Steam Web API key in Settings — off by default."),
            cached);
    }

    /// <summary>
    /// Overlay live summaries onto the local name cache. No key, a refused
    /// key, a throttle, or a network miss all leave the names as they were.
    /// </summary>
    private static async Task<SteamPack> OverlaySteamAsync(SteamPack steam, CancellationToken ct)
    {
        if (steam.Source is null || steam.Cached.Count == 0) return steam;
        var key = SteamWebApiKeyStore.TryRead();
        if (key is null) return steam;

        var ids = steam.Cached
            .Select(friend => friend.SteamId64)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToList();
        if (ids.Count == 0) return steam;

        var result = await SteamWebApi.LoadSummariesAsync(key, ids, ct).ConfigureAwait(false);
        if (!result.Live)
            return steam with { Source = steam.Source with { Live = false, Note = result.Note } };

        var byKey = steam.Cached.ToDictionary(friend => friend.AccountKey, StringComparer.Ordinal);
        var rows = steam.Rows.Select(row =>
        {
            if (!byKey.TryGetValue(row.Id, out var cached) ||
                string.IsNullOrWhiteSpace(cached.SteamId64) ||
                !result.Players.TryGetValue(cached.SteamId64, out var summary))
                return row;
            return row with
            {
                Status = summary.Status,
                StatusText = summary.StatusText,
                PlayingId = summary.PlayingId,
                PlayingTitle = summary.PlayingTitle,
                LastSeenUtc = summary.LastSeenUtc,
                AvatarUrl = PreferFullAvatar(summary.AvatarUrl, row.AvatarUrl),
                Live = true,
                PresenceFrom = "steam",
            };
        }).ToList();

        return steam with
        {
            Rows = rows,
            Source = steam.Source with { Live = true, Note = result.Note },
        };
    }

    private static string? PreferFullAvatar(string? incoming, string? current)
    {
        if (!string.IsNullOrWhiteSpace(incoming) &&
            incoming.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return incoming.Replace("_medium.", "_full.", StringComparison.OrdinalIgnoreCase);
        return current;
    }

    private static (IReadOnlyList<FriendPresence> Rows, FriendSource? Source) EpicRows(
        EpicFriends.Snapshot snapshot)
    {
        // No Epic sign-in at all means Exo has nothing to say about Epic, the
        // same way it stays quiet about a Steam that was never installed.
        if (!snapshot.SessionPresent && snapshot.Friends.Count == 0)
            return (Array.Empty<FriendPresence>(), null);

        if (!snapshot.Reachable && snapshot.Friends.Count == 0)
            return (Array.Empty<FriendPresence>(), new FriendSource("epic", false, 0, snapshot.Note));

        var rows = snapshot.Friends
            .Select(friend => new FriendPresence(
                friend.Id,
                friend.Name,
                null,
                "epic",
                "unknown",
                null,
                null,
                null,
                friend.LastOnlineUtc))
            .ToList();

        return (rows, new FriendSource("epic", Live: false, rows.Count, snapshot.Note));
    }

    private static FriendsSnapshot Merge(
        SteamPack steam,
        EpicFriends.Snapshot epicSnapshot,
        GogGalaxyFriends.Snapshot galaxy)
    {
        var epic = EpicRows(epicSnapshot);
        var folded = FoldGalaxy(steam.Rows, epic.Rows, steam.Cached, galaxy);
        var linked = FriendLinks.LinkedIds();

        var friends = folded.Rows
            // A row the user claimed on their Exo list belongs to that person
            // now, not to a second copy of them under Stores.
            .Where(friend => !linked.Contains(friend.Id))
            .ToList();

        var steamSource = steam.Source;
        if (steamSource is not null && folded.GalaxyMatchedSteam)
        {
            steamSource = steamSource with
            {
                Note = steamSource.Note +
                       " Matching Steam IDs also pick up last-known presence from GOG Galaxy.",
            };
        }

        var sources = new List<FriendSource>();
        foreach (var source in new[] { steamSource, epic.Source })
        {
            if (source is null) continue;
            var visible = friends.Count(friend =>
                string.Equals(friend.Source, source.Store, StringComparison.Ordinal));
            sources.Add(source with { Count = visible });
        }

        foreach (var extra in folded.ExtraSources)
        {
            var visible = friends.Count(friend =>
                string.Equals(friend.Source, extra.Store, StringComparison.Ordinal));
            if (visible == 0 && extra.Count == 0) continue;
            sources.Add(extra with { Count = visible });
        }

        var live = sources.Any(source => source.Live);
        var contributing = sources.Where(source => source.Count > 0).ToList();

        return new FriendsSnapshot(
            Live: live,
            Source: contributing.Count == 1 ? contributing[0].Store : null,
            Note: sources.Count == 0
                ? "No store session on this PC yet. Sign in to a store client once and Exo can read its friends."
                : string.Join(" ", sources.Select(source => source.Note)),
            Friends: friends,
            Sources: sources,
            Count: friends.Count,
            ActiveCount: friends.Count(friend =>
                friend.Live && ActivePresence.Contains(friend.Status, StringComparer.Ordinal)));
    }

    private readonly record struct FoldedFriends(
        IReadOnlyList<FriendPresence> Rows,
        IReadOnlyList<FriendSource> ExtraSources,
        bool GalaxyMatchedSteam);

    /// <summary>
    /// One human, one row. A Galaxy SteamID or Epic id is a join key. A
    /// shared display name is not.
    /// </summary>
    private static FoldedFriends FoldGalaxy(
        IReadOnlyList<FriendPresence> steam,
        IReadOnlyList<FriendPresence> epic,
        IReadOnlyList<SteamFriends.Friend> cached,
        GogGalaxyFriends.Snapshot galaxy)
    {
        if (galaxy.Friends.Count == 0)
            return new FoldedFriends(steam.Concat(epic).ToList(), Array.Empty<FriendSource>(), false);

        var steamByKey = cached.ToDictionary(friend => friend.AccountKey, StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var steamRows = steam.Select(row =>
        {
            if (!steamByKey.TryGetValue(row.Id, out var local)) return row;
            var match = galaxy.Friends.FirstOrDefault(friend =>
                GogGalaxyFriends.SamePerson(friend, local.SteamId64, epicId: null));
            if (match is null) return row;
            used.Add(match.Id);
            return OverlayGalaxy(row, match);
        }).ToList();

        var epicRows = epic.Select(row =>
        {
            var match = galaxy.Friends.FirstOrDefault(friend =>
                friend.EpicId is not null &&
                (row.Id.Equals(friend.EpicId, StringComparison.Ordinal) ||
                 row.Id.Equals("epic:" + EpicFriends.HashAccount(friend.EpicId), StringComparison.Ordinal)));
            if (match is null) return row;
            used.Add(match.Id);
            return OverlayGalaxy(row, match);
        }).ToList();

        var leftover = galaxy.Friends
            .Where(friend => !used.Contains(friend.Id))
            .Select(ToPresence)
            .ToList();

        var extra = leftover
            .GroupBy(friend => friend.Source, StringComparer.Ordinal)
            .Select(group => new FriendSource(
                group.Key,
                galaxy.Live && group.Any(friend => friend.Live),
                group.Count(),
                galaxy.Note ?? GogGalaxyFriends.LastKnownNote))
            .ToList();

        return new FoldedFriends(steamRows.Concat(epicRows).Concat(leftover).ToList(), extra, used.Count > 0);
    }

    private static FriendPresence OverlayGalaxy(FriendPresence row, GogGalaxyFriends.Friend galaxy)
    {
        // A live Steam Web API answer already won. Galaxy may only add a stamp.
        if (row.Live) return row with { LastSeenUtc = row.LastSeenUtc ?? galaxy.LastSeenUtc };

        if (galaxy.Fresh)
        {
            return row with
            {
                Status = galaxy.Status,
                StatusText = galaxy.StatusText,
                PlayingId = galaxy.PlayingId,
                PlayingTitle = galaxy.PlayingTitle,
                LastSeenUtc = galaxy.LastSeenUtc,
                Live = true,
                PresenceFrom = "galaxy",
            };
        }

        return row with
        {
            StatusText = row.StatusText ?? galaxy.StatusText,
            LastSeenUtc = row.LastSeenUtc ?? galaxy.LastSeenUtc,
            PresenceFrom = row.PresenceFrom ?? "galaxy",
        };
    }

    private static FriendPresence ToPresence(GogGalaxyFriends.Friend friend) =>
        new(
            friend.Id,
            friend.Name,
            null,
            friend.Store,
            friend.Status,
            friend.StatusText,
            friend.PlayingId,
            friend.PlayingTitle,
            friend.LastSeenUtc,
            friend.Fresh,
            friend.Fresh ? "galaxy" : "galaxy");

    /// <summary>The people the user added on Exo. Live is always false: there is no service behind it.</summary>
    public RosterSnapshot Roster()
    {
        // Only pay for a store read when there is a link whose name needs finding.
        var names = FriendLinks.LinkedIds().Count == 0
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : LinkedRows()
                .GroupBy(friend => friend.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.Ordinal);

        var people = (_settings.Current.ProfileRoster ?? new List<ProfilePerson>())
            .Select(person => Map(person, names))
            .Where(person => person is not null)
            .Select(person => person!)
            .ToList();

        return new RosterSnapshot(
            Live: false,
            Note: "Signed out. Local handles stay on this PC; sign in to use Exo friend requests and presence.",
            People: people);
    }

    /// <summary>
    /// Store rows the user has already claimed. They are filtered out of the
    /// store list, so their names have to be found again to label the link.
    /// </summary>
    private static IReadOnlyList<FriendPresence> LinkedRows()
    {
        var steam = ReadSteam().Rows;
        var epic = EpicRows(EpicFriends.Cached()).Rows;
        var galaxy = GogGalaxyFriends.Load().Friends.Select(ToPresence);
        return steam.Concat(epic).Concat(galaxy).ToList();
    }

    /// <summary>
    /// Ties a store row to someone on the Exo list. The store is resolved from
    /// the row Exo can actually see — a client may not name it — and Exo never
    /// guesses that two accounts are one person.
    /// </summary>
    public RosterResult LinkPerson(string? id, string? friendId)
    {
        var handle = HandleFromId(id);
        if (handle.Length == 0) return new RosterResult(false, "Missing person.", Roster());
        if (!EnsureRosterPerson(handle, out var rosterFailure))
            return new RosterResult(false, rosterFailure ?? "That person is not on your list.", Roster());

        var row = LinkedRows().FirstOrDefault(friend =>
            string.Equals(friend.Id, friendId, StringComparison.Ordinal));
        if (row is null)
            return new RosterResult(false, "Exo cannot see that store account right now.", Roster());

        var failure = FriendLinks.Add(handle, row.Id, row.Source);
        return new RosterResult(failure is null, failure, Roster());
    }

    /// <summary>
    /// Online Exo friends are not on the local roster until the user claims a
    /// store account against them. Linking is still a user act; this only makes
    /// the handle persist on this PC so the claim has somewhere to live.
    /// </summary>
    private bool EnsureRosterPerson(string handle, out string? failure)
    {
        string? rosterFailure = null;
        _settings.UpdateProfile(settings =>
        {
            settings.ProfileRoster ??= new List<ProfilePerson>();
            if (settings.ProfileRoster.Any(person =>
                    string.Equals(NormalizeHandle(person.Handle), handle, StringComparison.Ordinal)))
                return;
            if (settings.ProfileRoster.Count >= RosterMax)
            {
                rosterFailure = $"The roster holds {RosterMax} people.";
                return;
            }

            settings.ProfileRoster.Add(new ProfilePerson
            {
                Handle = handle,
                AddedUtc = DateTimeOffset.UtcNow.ToString("O"),
            });
        });
        failure = rosterFailure;
        return rosterFailure is null;
    }

    public sealed record SteamOwnedGame(string Id, string Title, string AppId, int? PlaytimeMinutes);

    public sealed record SteamLibrarySnapshot(bool Ok, string? Note, IReadOnlyList<SteamOwnedGame> Games);

    /// <summary>
    /// Games a linked Steam friend has made public. The SteamID never leaves
    /// this process. A private profile is an empty shelf with a reason, not a
    /// guessed library.
    /// </summary>
    public async Task<SteamLibrarySnapshot> LinkedSteamLibraryAsync(
        string? id, CancellationToken ct = default)
    {
        var handle = HandleFromId(id);
        if (handle.Length == 0)
            return new SteamLibrarySnapshot(false, "Missing person.", Array.Empty<SteamOwnedGame>());

        var steamLink = FriendLinks.For(handle)
            .FirstOrDefault(link => string.Equals(link.Store, "steam", StringComparison.Ordinal));
        if (steamLink is null)
            return new SteamLibrarySnapshot(true, "No Steam account is linked to this person.", Array.Empty<SteamOwnedGame>());

        var key = SteamWebApiKeyStore.TryRead();
        if (key is null)
            return new SteamLibrarySnapshot(
                true,
                "A Steam Web API key in Settings is needed to read a friend's public Steam library.",
                Array.Empty<SteamOwnedGame>());

        var cached = ReadSteam().Cached.FirstOrDefault(friend =>
            string.Equals(friend.AccountKey, steamLink.Id, StringComparison.Ordinal));
        if (cached is null || string.IsNullOrWhiteSpace(cached.SteamId64))
            return new SteamLibrarySnapshot(
                false,
                "Exo cannot see that Steam account right now.",
                Array.Empty<SteamOwnedGame>());

        var result = await SteamWebApi.LoadFriendOwnedGamesAsync(key, cached.SteamId64, ct)
            .ConfigureAwait(false);
        if (!result.Authoritative)
            return new SteamLibrarySnapshot(
                true,
                "That Steam library is private, or Steam did not answer just now.",
                Array.Empty<SteamOwnedGame>());

        var games = result.Games
            .OrderByDescending(game => game.PlaytimeMinutes)
            .ThenBy(game => game.Name, StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .Select(game => new SteamOwnedGame(
                "steam:" + game.AppId,
                string.IsNullOrWhiteSpace(game.Name) ? "Steam app " + game.AppId : game.Name,
                game.AppId,
                game.PlaytimeMinutes > 0 ? game.PlaytimeMinutes : null))
            .ToList();

        return new SteamLibrarySnapshot(
            true,
            games.Count == 0 ? "No public Steam games on that account." : null,
            games);
    }

    public RosterResult UnlinkPerson(string? id, string? friendId)
    {
        var handle = HandleFromId(id);
        if (handle.Length == 0) return new RosterResult(false, "Missing person.", Roster());
        var removed = FriendLinks.Remove(handle, friendId);
        return new RosterResult(removed, removed ? null : "That account is not linked.", Roster());
    }

    public RosterResult AddPerson(string? handle, string? name, string? note)
    {
        var cleanHandle = NormalizeHandle(handle);
        if (cleanHandle.Length < 2)
            return new RosterResult(false, "Handles are lowercase letters, numbers, and underscore.", Roster());

        string? failure = null;
        _settings.UpdateProfile(settings =>
        {
            settings.ProfileRoster ??= new List<ProfilePerson>();
            if (settings.ProfileRoster.Count >= RosterMax)
            {
                failure = $"The roster holds {RosterMax} people.";
                return;
            }
            if (settings.ProfileRoster.Any(person =>
                    string.Equals(NormalizeHandle(person.Handle), cleanHandle, StringComparison.Ordinal)))
            {
                failure = $"@{cleanHandle} is already on your list.";
                return;
            }

            settings.ProfileRoster.Add(new ProfilePerson
            {
                Handle = cleanHandle,
                Name = Cap(name, NameMax),
                Note = Cap(note, NoteMax),
                AddedUtc = DateTimeOffset.UtcNow.ToString("O"),
            });
        });

        return new RosterResult(failure is null, failure, Roster());
    }

    public RosterResult RemovePerson(string? id)
    {
        var handle = HandleFromId(id);
        if (handle.Length == 0)
            return new RosterResult(false, "Missing person.", Roster());

        var removed = false;
        _settings.UpdateProfile(settings =>
        {
            settings.ProfileRoster ??= new List<ProfilePerson>();
            removed = settings.ProfileRoster.RemoveAll(person =>
                string.Equals(NormalizeHandle(person.Handle), handle, StringComparison.Ordinal)) > 0;
        });

        // Their claimed store rows go back to the store list rather than being
        // orphaned against a handle that is gone.
        if (removed) FriendLinks.Forget(handle);

        return new RosterResult(removed, removed ? null : "That person is not on your list.", Roster());
    }

    public RosterResult SetNote(string? id, string? note)
    {
        var handle = HandleFromId(id);
        if (handle.Length == 0)
            return new RosterResult(false, "Missing person.", Roster());

        var found = false;
        var capped = Cap(note, NoteMax);
        _settings.UpdateProfile(settings =>
        {
            settings.ProfileRoster ??= new List<ProfilePerson>();
            foreach (var person in settings.ProfileRoster)
            {
                if (!string.Equals(NormalizeHandle(person.Handle), handle, StringComparison.Ordinal)) continue;
                person.Note = capped;
                found = true;
            }
        });

        return new RosterResult(found, found ? null : "That person is not on your list.", Roster());
    }

    /// <param name="running">The library entry the orchestrator says is live, if any.</param>
    public ExoProfile Profile(GameEntry? running)
    {
        var settings = _settings.Current;
        var games = _library.PeekCachedLibrary()
            .Where(game => !string.Equals(game.Id, "local:add", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var installed = games.Where(game => game.Installed).ToList();
        var known = new HashSet<string>(games.Select(game => game.Id), StringComparer.OrdinalIgnoreCase);

        var minutes = games
            .Select(game => game.PlaytimeMinutes ?? 0)
            .Where(value => value > 0)
            .Sum();

        var stores = _library.StoreMatrix();
        return new ExoProfile(
            Name: Cap(settings.ProfileName, NameMax),
            Handle: NullIfEmpty(NormalizeHandle(settings.ProfileHandle)),
            Pronouns: Cap(settings.ProfilePronouns, PronounsMax),
            StatusText: Cap(settings.ProfileStatusText, StatusMax),
            Bio: Cap(settings.ProfileBio, BioMax),
            Accent: NormalizeAccent(settings.ProfileAccent),
            AvatarGameId: KnownId(settings.ProfileAvatarGameId, known),
            BannerGameId: KnownId(settings.ProfileBannerGameId, known),
            // A picture the user deleted off disk resolves to null, so the page
            // falls back to cover art instead of a broken image.
            AvatarImageUrl: ProfileImageStore.ResolveUrl(settings.ProfileAvatarImage),
            BannerImageUrl: ProfileImageStore.ResolveUrl(settings.ProfileBannerImage),
            GalleryImages: (settings.ProfileGalleryImages ?? new Dictionary<string, string>())
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => (pair.Key, Url: ProfileImageStore.ResolveUrl(pair.Value)))
                .Where(pair => pair.Url is not null)
                .Select(pair => new ProfileGalleryImage(pair.Key, pair.Url!))
                .Take(6)
                .ToList(),
            Layout: NormalizeChoice(settings.ProfileLayout, LayoutKeys),
            BannerHeight: NormalizeChoice(settings.ProfileBannerHeight, BannerHeightKeys),
            ShowcaseStyle: NormalizeChoice(settings.ProfileShowcaseStyle, ShowcaseStyleKeys),
            ShowHandle: settings.ProfileShowHandle,
            Sections: NormalizeSections(settings.ProfileSections),
            HiddenSections: NormalizeHiddenSections(settings.ProfileHiddenSections),
            PlayingId: running?.Id,
            PlayingTitle: running?.Title,
            GameCount: games.Count,
            InstalledCount: installed.Count,
            PlaytimeMinutes: minutes > 0 ? minutes : null,
            // Unlock totals live in the achievement provider per title. Exo does
            // not sum them here rather than show a wrong number.
            UnlockedCount: null,
            StoresConnected: stores.Count(store => store.signedIn),
            RosterCount: (settings.ProfileRoster ?? new List<ProfilePerson>()).Count,
            Showcase: Showcase(settings.ProfileShowcase, games),
            StoreAccounts: StoreAccounts(stores));
    }

    /// <summary>
    /// Persists an authored profile. A null field is left alone; an empty string
    /// clears it. Everything is capped and validated here — a client string never
    /// reaches settings.json as it arrived.
    /// </summary>
    public ExoProfile SetProfile(
        string? name,
        string? handle,
        string? pronouns,
        string? statusText,
        string? bio,
        string? accent,
        string? avatarGameId,
        string? bannerGameId,
        GameEntry? running)
    {
        var known = new HashSet<string>(
            _library.PeekCachedLibrary().Select(game => game.Id),
            StringComparer.OrdinalIgnoreCase);

        _settings.UpdateProfile(settings =>
        {
            if (name is not null) settings.ProfileName = Cap(name, NameMax);
            if (handle is not null) settings.ProfileHandle = NullIfEmpty(NormalizeHandle(handle));
            if (pronouns is not null) settings.ProfilePronouns = Cap(pronouns, PronounsMax);
            if (statusText is not null) settings.ProfileStatusText = Cap(statusText, StatusMax);
            if (bio is not null) settings.ProfileBio = Cap(bio, BioMax);
            if (accent is not null) settings.ProfileAccent = NormalizeAccent(accent);
            if (avatarGameId is not null) settings.ProfileAvatarGameId = KnownId(avatarGameId, known);
            if (bannerGameId is not null) settings.ProfileBannerGameId = KnownId(bannerGameId, known);
        });

        return Profile(running);
    }

    /// <summary>Showcase picks are pinned library ids the user chose, in their order; unknown ids drop out.</summary>
    public IReadOnlyList<string> SetShowcase(IEnumerable<string>? ids)
    {
        var games = _library.PeekCachedLibrary();
        var known = new HashSet<string>(games.Select(game => game.Id), StringComparer.OrdinalIgnoreCase);
        var next = (ids ?? Array.Empty<string>())
            .Select(id => (id ?? string.Empty).Trim())
            .Where(id => id.Length > 0 && known.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(ShowcaseMax)
            .ToList();

        _settings.UpdateProfile(settings => settings.ProfileShowcase = next);
        return next;
    }

    /// <summary>
    /// Saves how the page is arranged: section order and visibility, head
    /// alignment, banner size, and showcase style. Each value is checked against
    /// its fixed key set before it reaches disk.
    /// </summary>
    public ExoProfile SetLook(ProfileLook look, GameEntry? running)
    {
        _settings.UpdateProfile(settings =>
        {
            if (look.Layout is not null)
                settings.ProfileLayout = NormalizeChoice(look.Layout, LayoutKeys);
            if (look.BannerHeight is not null)
                settings.ProfileBannerHeight = NormalizeChoice(look.BannerHeight, BannerHeightKeys);
            if (look.ShowcaseStyle is not null)
                settings.ProfileShowcaseStyle = NormalizeChoice(look.ShowcaseStyle, ShowcaseStyleKeys);
            if (look.ShowHandle is not null)
                settings.ProfileShowHandle = look.ShowHandle.Value;
            if (look.Sections is not null)
                settings.ProfileSections = NormalizeSections(look.Sections).ToList();
            if (look.HiddenSections is not null)
                settings.ProfileHiddenSections = NormalizeHiddenSections(look.HiddenSections).ToList();
        });

        return Profile(running);
    }

    /// <summary>
    /// Points the avatar or banner at a picture from this PC. The path has to
    /// come from the host's own picker — the UI never names a file. The picture
    /// is copied into Exo's cache, and the one it replaces is deleted.
    /// </summary>
    public ProfileImageResult SetImage(string? kind, string? sourcePath, GameEntry? running)
    {
        var slot = ProfileImageStore.NormalizeSlot(kind);
        if (slot is null) return new ProfileImageResult(false, "Unknown image slot.", Profile(running));

        var stored = ProfileImageStore.Save(sourcePath, slot);
        if (stored.FileName is null)
            return new ProfileImageResult(false, stored.Message ?? "That image could not be used.", Profile(running));

        SwapImage(slot, stored.FileName);
        return new ProfileImageResult(true, null, Profile(running));
    }

    /// <summary>Drops an uploaded picture and the file behind it.</summary>
    public ProfileImageResult ClearImage(string? kind, GameEntry? running)
    {
        var slot = ProfileImageStore.NormalizeSlot(kind);
        if (slot is null) return new ProfileImageResult(false, "Unknown image slot.", Profile(running));

        SwapImage(slot, null);
        return new ProfileImageResult(true, null, Profile(running));
    }

    /// <summary>Writes one image slot, then deletes the file it replaced if nothing else points at it.</summary>
    private void SwapImage(string slot, string? fileName)
    {
        string? replaced = null;
        _settings.UpdateProfile(settings =>
        {
            if (slot == "avatar")
            {
                replaced = settings.ProfileAvatarImage;
                settings.ProfileAvatarImage = fileName;
            }
            else if (slot == "banner")
            {
                replaced = settings.ProfileBannerImage;
                settings.ProfileBannerImage = fileName;
            }
            else
            {
                settings.ProfileGalleryImages ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                settings.ProfileGalleryImages.TryGetValue(slot, out replaced);
                if (fileName is null) settings.ProfileGalleryImages.Remove(slot);
                else settings.ProfileGalleryImages[slot] = fileName;
            }
        });

        if (string.IsNullOrWhiteSpace(replaced)) return;
        var current = _settings.Current;
        var stillUsed =
            string.Equals(replaced, current.ProfileAvatarImage, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(replaced, current.ProfileBannerImage, StringComparison.OrdinalIgnoreCase) ||
            (current.ProfileGalleryImages ?? new Dictionary<string, string>()).Values.Contains(replaced, StringComparer.OrdinalIgnoreCase);
        if (!stillUsed) ProfileImageStore.Delete(replaced);
    }

    /// <summary>Only stores whose own session gave up a name. No name, no row.</summary>
    private static IReadOnlyList<StoreAccount> StoreAccounts(
        IReadOnlyList<LibraryService.StoreBackendStatus> stores)
    {
        var accounts = new List<StoreAccount>();
        var steamRoot = SteamAdapter.TryResolveSteamRootPublic();
        var steamName = steamRoot is null ? null : SteamFriends.LoadSelf(steamRoot)?.Name;
        if (!string.IsNullOrWhiteSpace(steamName))
        {
            var row = stores.FirstOrDefault(store =>
                string.Equals(store.store, "steam", StringComparison.OrdinalIgnoreCase));
            accounts.Add(new StoreAccount("steam", row?.displayName ?? "Steam", steamName!.Trim()));
        }

        return accounts;
    }

    /// <summary>Saved picks only. An empty shelf is honest; a guessed one is not.</summary>
    private static IReadOnlyList<string> Showcase(List<string>? saved, List<GameEntry> games)
    {
        var known = new HashSet<string>(games.Select(game => game.Id), StringComparer.OrdinalIgnoreCase);
        return (saved ?? new List<string>())
            .Where(known.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(ShowcaseMax)
            .ToList();
    }

    private static ExoPerson? Map(ProfilePerson person, IReadOnlyDictionary<string, string> names)
    {
        var handle = NormalizeHandle(person.Handle);
        if (handle.Length == 0) return null;
        var links = FriendLinks.For(handle)
            .Select(link => new PersonLink(
                link.Id,
                link.Store,
                names.TryGetValue(link.Id, out var name) ? name : null))
            .ToList();

        return new ExoPerson(
            $"exo:{handle}",
            handle,
            Cap(person.Name, NameMax),
            Cap(person.Note, NoteMax),
            person.AddedUtc,
            links);
    }

    private static string HandleFromId(string? id)
    {
        var raw = (id ?? string.Empty).Trim();
        // online:{userId} is not a handle. The UI must pass exo:{handle}.
        if (raw.StartsWith("online:", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        if (raw.StartsWith("exo:", StringComparison.OrdinalIgnoreCase)) raw = raw[4..];
        return NormalizeHandle(raw);
    }

    private static string NormalizeHandle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var chars = value
            .Trim()
            .ToLowerInvariant()
            .Where(ch => ch is '_' || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
            .Take(HandleMax)
            .ToArray();
        return new string(chars);
    }

    private static string NormalizeAccent(string? value)
    {
        var key = (value ?? string.Empty).Trim().ToLowerInvariant();
        return AccentKeys.Contains(key) ? key : DefaultAccent;
    }

    /// <summary>One of a fixed key set, or the first key. The UI cannot invent a mode.</summary>
    private static string NormalizeChoice(string? value, string[] keys)
    {
        var key = (value ?? string.Empty).Trim().ToLowerInvariant();
        return keys.Contains(key) ? key : keys[0];
    }

    /// <summary>
    /// The user's section order. Unknown keys drop out and any section they have
    /// not moved lands at the end, so a new Exo section still appears.
    /// </summary>
    private static IReadOnlyList<string> NormalizeSections(IEnumerable<string>? saved)
    {
        var ordered = CleanSectionKeys(saved);
        ordered.AddRange(SectionKeys.Where(key => !ordered.Contains(key, StringComparer.Ordinal)));
        return ordered;
    }

    private static IReadOnlyList<string> NormalizeHiddenSections(IEnumerable<string>? saved) =>
        CleanSectionKeys(saved);

    private static List<string> CleanSectionKeys(IEnumerable<string>? saved) =>
        (saved ?? Array.Empty<string>())
            .Select(key => (key ?? string.Empty).Trim().ToLowerInvariant())
            .Where(SectionKeys.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string? KnownId(string? id, HashSet<string> known)
    {
        var trimmed = (id ?? string.Empty).Trim();
        return trimmed.Length > 0 && known.Contains(trimmed) ? trimmed : null;
    }

    private static string? Cap(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
