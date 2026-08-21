using ExoLauncher.Models;

namespace ExoLauncher.Services.Achievements;

/// <summary>
/// Explicit "not yet / cannot" coverage so a store is never a silent gap.
/// These backends have no local file or official API Exo is allowed to read.
/// </summary>
public sealed class UnsupportedStoreAchievementProvider : IAchievementProvider
{
    private readonly string _message;

    public UnsupportedStoreAchievementProvider(StoreKind store, string message)
    {
        Store = store;
        _message = message;
    }

    public string Id => Store.ToString().ToLowerInvariant();
    public StoreKind Store { get; }
    public AchievementProviderCapabilities Capabilities => AchievementProviderCapabilities.None;
    public bool CanObserveUnlocks => false;
    public string CoverageMessage => _message;
    public TimeSpan SuggestedPollInterval => TimeSpan.FromHours(1);

    public bool Supports(GameEntry game) => game.Store == Store;

    public Task<AchievementSnapshot> GetSnapshotAsync(
        GameEntry game,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AchievementSnapshot
        {
            GameId = game.Id,
            ProviderId = Id,
            SourceGameId = game.LaunchTarget ?? game.Id,
            CoverageKey = "unsupported",
            Coverage = AchievementCoverageStatus.Unsupported,
            Capabilities = AchievementProviderCapabilities.None,
            ObservedAtUtc = DateTimeOffset.UtcNow,
            Message = _message,
        });
    }

    public static IReadOnlyList<UnsupportedStoreAchievementProvider> All() =>
    [
        For(StoreKind.Xbox),
        For(StoreKind.Ea),
        For(StoreKind.Ubisoft),
        For(StoreKind.BattleNet),
        For(StoreKind.Amazon),
        For(StoreKind.Riot),
        For(StoreKind.Itch),
        For(StoreKind.Minecraft),
        For(StoreKind.Roblox),
        For(StoreKind.Paradox),
        For(StoreKind.Wargaming),
        For(StoreKind.Rockstar),
        For(StoreKind.Local),
    ];

    public static UnsupportedStoreAchievementProvider For(StoreKind store) =>
        new(store, MessageFor(store));

    public static string MessageFor(StoreKind store) => store switch
    {
        StoreKind.Xbox =>
            "Xbox achievements live on Xbox Live. A desktop app cannot read them without Microsoft account OAuth Exo does not have, and the Xbox app does not expose unlocks as local files.",
        StoreKind.Ea =>
            "EA achievements live in the EA App overlay. There is no public API, and EA's local caches are not a documented account-progress file.",
        StoreKind.Ubisoft =>
            "Ubisoft Connect achievements are not in a documented local catalog. The spool/protobuf files and Demux protocol are not an official API Exo can use.",
        StoreKind.BattleNet =>
            "Battle.net has no unified achievement bus. WoW, Diablo, and other titles each have their own game APIs.",
        StoreKind.Amazon =>
            "Amazon Games has no achievement system Exo can read.",
        StoreKind.Riot =>
            "Riot challenges live inside the game client. Reading them would mean unofficial APIs or injecting into a Vanguard/EAC process, which Exo will not do.",
        StoreKind.Itch =>
            "itch has no achievement system.",
        StoreKind.Minecraft =>
            "Java advancements are per-world save files, not a launcher channel. Bedrock achievements are Xbox Live.",
        StoreKind.Roblox =>
            "Roblox experience badges are not a launcher achievement bus, and reading them needs a Roblox session Exo does not keep.",
        StoreKind.Paradox =>
            "Paradox Launcher has no achievements. Titles bought on Steam or Epic are covered by those stores.",
        StoreKind.Wargaming =>
            "Wargaming medals are per-game APIs with an application id, not something Game Center exposes locally.",
        StoreKind.Rockstar =>
            "Rockstar Social Club achievements are not exposed as local files or a public desktop API.",
        StoreKind.Local =>
            "Added folders have no store achievement channel.",
        StoreKind.Steam or StoreKind.Epic or StoreKind.Gog =>
            "This store is covered by a real provider.",
        _ => "Achievement sync is not available for this source.",
    };
}
