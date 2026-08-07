using ExoLauncher.Adapters;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

public sealed class LibraryService
{
    private readonly IReadOnlyList<IStoreAdapter> _adapters;
    private IReadOnlyList<GameEntry> _cache = Array.Empty<GameEntry>();
    private DateTimeOffset _cacheAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(30);

    public LibraryService(IReadOnlyList<IStoreAdapter> adapters) => _adapters = adapters;

    public async Task<IReadOnlyList<GameEntry>> GetLibraryAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && _cache.Count > 0 && DateTimeOffset.UtcNow - _cacheAt < Freshness)
            return _cache;

        var discovered = new List<GameEntry>();
        foreach (var adapter in _adapters)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var items = await adapter.GetLibraryAsync(ct).ConfigureAwait(false);
                discovered.AddRange(items);
            }
            catch { /* one store must not block the library */ }
        }

        // Always include demo tiles when empty so the UI is demoable offline.
        if (discovered.Count == 0)
            discovered.AddRange(MockCatalog.Create());
        else if (discovered.Count < 2 && discovered.All(g => g.Store != StoreKind.Riot))
            discovered.AddRange(MockCatalog.Create().Where(g => g.Store == StoreKind.Riot).Take(1));

        _cache = discovered
            .OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _cacheAt = DateTimeOffset.UtcNow;
        return _cache;
    }

    public GameEntry? Find(string id) =>
        _cache.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase));

    public void Invalidate() => _cacheAt = DateTimeOffset.MinValue;

    public object StoreMatrix()
    {
        return _adapters.Select(a => new
        {
            store = a.Id,
            displayName = a.DisplayName,
            agentPresent = a.IsAgentPresent(),
        }).ToList();
    }
}

internal static class MockCatalog
{
    public static IReadOnlyList<GameEntry> Create() =>
    [
        new GameEntry
        {
            Id = "mock:valorant",
            Title = "VALORANT",
            Store = StoreKind.Riot,
            Installed = false,
            Owned = true,
            CanInstall = true,
            Status = "Demo",
            PlaytimeMinutes = 0,
            SizeBytes = 30L * 1024 * 1024 * 1024,
            Deps = ["Riot Client", "Vanguard"],
            LaunchNote = "Demo tile. Real install uses official RiotClientServices; Vanguard required for online play.",
            LaunchTarget = "valorant",
        },
        new GameEntry
        {
            Id = "mock:hades",
            Title = "Hades",
            Store = StoreKind.Steam,
            Installed = false,
            Owned = true,
            CanInstall = true,
            Status = "Demo",
            PlaytimeMinutes = 1240,
            SizeBytes = 15L * 1024 * 1024 * 1024,
            Deps = ["Steam client"],
            LaunchNote = "Demo tile. Real Steam titles install/launch via minimized Steam.",
            LaunchTarget = "1145360",
        },
        new GameEntry
        {
            Id = "mock:celeste",
            Title = "Celeste",
            Store = StoreKind.Local,
            Installed = false,
            Owned = true,
            CanInstall = true,
            Status = "Demo",
            PlaytimeMinutes = 380,
            SizeBytes = 1200L * 1024 * 1024,
            Deps = [],
            LaunchNote = "Demo tile. Local/DRM-free: point Exo at a folder with an exe.",
        },
        new GameEntry
        {
            Id = "mock:control",
            Title = "Control",
            Store = StoreKind.Epic,
            Installed = false,
            Owned = true,
            CanInstall = true,
            Status = "Demo",
            PlaytimeMinutes = 720,
            SizeBytes = 42L * 1024 * 1024 * 1024,
            Deps = ["Legendary"],
            LaunchNote = "Demo tile. Epic installs via Legendary when present — Epic GUI optional.",
            LaunchTarget = "Control",
        },
        new GameEntry
        {
            Id = "mock:disco",
            Title = "Disco Elysium",
            Store = StoreKind.Gog,
            Installed = false,
            Owned = true,
            CanInstall = true,
            Status = "Demo",
            PlaytimeMinutes = 2100,
            SizeBytes = 20L * 1024 * 1024 * 1024,
            Deps = ["gogdl"],
            LaunchNote = "Demo tile. GOG installs via gogdl; Galaxy not required for the happy path.",
        },
    ];
}
