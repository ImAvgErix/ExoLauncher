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
                var items = await adapter.DiscoverAsync(ct).ConfigureAwait(false);
                discovered.AddRange(items);
            }
            catch { /* one store must not block the library */ }
        }

        // Always include mock demos when the real library is empty so the UI is demoable.
        if (discovered.Count == 0)
            discovered.AddRange(MockCatalog.Create());

        // Prefer real titles; still append a couple of demos if only one store answered.
        if (discovered.All(g => !g.Id.StartsWith("mock:", StringComparison.Ordinal))
            && discovered.Count < 3)
        {
            discovered.AddRange(MockCatalog.Create().Take(2));
        }

        _cache = discovered
            .OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _cacheAt = DateTimeOffset.UtcNow;
        return _cache;
    }

    public GameEntry? Find(string id) =>
        _cache.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase));

    public object StoreMatrix()
    {
        return _adapters.Select(a => new
        {
            store = a.Store.ToString().ToLowerInvariant(),
            displayName = a.DisplayName,
            agentPresent = a.IsAgentPresent(),
        }).ToList();
    }
}

/// <summary>Demo games for empty libraries and UI development.</summary>
internal static class MockCatalog
{
    public static IReadOnlyList<GameEntry> Create() => new[]
    {
        new GameEntry
        {
            Id = "mock:valorant",
            Title = "VALORANT",
            Store = StoreKind.Riot,
            Installed = false,
            Status = "Demo",
            PlaytimeMinutes = 0,
            SizeBytes = 30L * 1024 * 1024 * 1024,
            Deps = new[] { "Riot Client", "Vanguard" },
            LaunchNote = "Demo entry. Real VALORANT needs Riot Client + Vanguard on disk.",
            LaunchTarget = "valorant",
        },
        new GameEntry
        {
            Id = "mock:hades",
            Title = "Hades",
            Store = StoreKind.Steam,
            Installed = false,
            Status = "Demo",
            PlaytimeMinutes = 1240,
            SizeBytes = 15L * 1024 * 1024 * 1024,
            Deps = new[] { "Steam client" },
            LaunchNote = "Demo entry. Real Steam titles launch via steam://run.",
            LaunchTarget = "1145360",
        },
        new GameEntry
        {
            Id = "mock:celeste",
            Title = "Celeste",
            Store = StoreKind.Local,
            Installed = false,
            Status = "Demo",
            PlaytimeMinutes = 380,
            SizeBytes = 1200L * 1024 * 1024,
            Deps = Array.Empty<string>(),
            LaunchNote = "Demo entry. Local/DRM-free titles launch the exe directly.",
        },
        new GameEntry
        {
            Id = "mock:control",
            Title = "Control",
            Store = StoreKind.Epic,
            Installed = false,
            Status = "Demo",
            PlaytimeMinutes = 720,
            SizeBytes = 42L * 1024 * 1024 * 1024,
            Deps = new[] { "Legendary or Epic Launcher" },
            LaunchNote = "Demo entry. Epic prefers Legendary when present.",
        },
        new GameEntry
        {
            Id = "mock:disco",
            Title = "Disco Elysium",
            Store = StoreKind.Gog,
            Installed = false,
            Status = "Demo",
            PlaytimeMinutes = 2100,
            SizeBytes = 20L * 1024 * 1024 * 1024,
            Deps = new[] { "GOG Galaxy (optional offline)" },
            LaunchNote = "Demo entry. GOG offline builds are first-class local launches.",
        },
        new GameEntry
        {
            Id = "mock:forza",
            Title = "Forza Horizon",
            Store = StoreKind.Xbox,
            Installed = false,
            Status = "Demo",
            PlaytimeMinutes = 540,
            SizeBytes = 100L * 1024 * 1024 * 1024,
            Deps = new[] { "Gaming Services" },
            LaunchNote = "Demo entry. Xbox titles keep Gaming Services as backend.",
        },
    };
}
