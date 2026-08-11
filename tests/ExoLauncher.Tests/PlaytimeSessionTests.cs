using System.Diagnostics;
using System.Text.Json;
using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class PlaytimeSessionTests
{
    [Fact]
    public async Task TrackGameSession_CreditsWhenProcessRunsThenExits()
    {
        // ping -n 3 ≈ 2s on Windows; enough to observe + exit inside timeouts.
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "ping.exe",
            Arguments = "-n 3 127.0.0.1",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
        Assert.NotNull(proc);

        var credited = await ProcessHelper.TrackGameSessionAsync(
            seedPid: proc!.Id,
            installRoot: null,
            processNames: ["ping"],
            ignoredNames: null,
            appearTimeout: TimeSpan.FromSeconds(5),
            goneDebounce: TimeSpan.FromSeconds(1),
            ct: CancellationToken.None);

        Assert.True(credited);
    }

    [Fact]
    public async Task TrackGameSession_ReturnsFalseWhenNothingAppears()
    {
        var credited = await ProcessHelper.TrackGameSessionAsync(
            seedPid: null,
            installRoot: null,
            processNames: ["definitely-not-a-real-exo-process-xyz"],
            ignoredNames: null,
            appearTimeout: TimeSpan.FromMilliseconds(400),
            goneDebounce: TimeSpan.FromMilliseconds(200),
            ct: CancellationToken.None);

        Assert.False(credited);
    }

    [Fact]
    public async Task TrackGameSession_IgnoresBootstrapSeedPid()
    {
        // Seed is ping, but "ping" is treated as a bootstrap name — must not credit.
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "ping.exe",
            Arguments = "-n 2 127.0.0.1",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
        Assert.NotNull(proc);

        var credited = await ProcessHelper.TrackGameSessionAsync(
            seedPid: proc!.Id,
            installRoot: null,
            processNames: ["definitely-not-a-real-exo-process-xyz"],
            ignoredNames: ["ping"],
            appearTimeout: TimeSpan.FromSeconds(2),
            goneDebounce: TimeSpan.FromMilliseconds(200),
            ct: CancellationToken.None);

        Assert.False(credited);
    }

    [Fact]
    public async Task TrackGameSession_StopsWaitingWhenObservedHandoffCloses()
    {
        using var handoff = Process.Start(new ProcessStartInfo
        {
            FileName = "ping.exe",
            Arguments = "-n 2 127.0.0.1",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
        Assert.NotNull(handoff);

        var started = Stopwatch.StartNew();
        var credited = await ProcessHelper.TrackGameSessionAsync(
            seedPid: handoff!.Id,
            installRoot: null,
            processNames: ["definitely-not-a-real-exo-process-xyz"],
            ignoredNames: ["ping"],
            appearTimeout: TimeSpan.FromSeconds(10),
            goneDebounce: TimeSpan.FromMilliseconds(250),
            handoffProcessNames: ["ping"],
            ct: CancellationToken.None);

        Assert.False(credited);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(6));
    }

    [Fact]
    public async Task TrackGameSession_NamedProcessMustRunUnderInstallRoot()
    {
        var unrelatedInstallRoot = Path.Combine(
            Path.GetTempPath(),
            "exo-process-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unrelatedInstallRoot);
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "ping.exe",
            Arguments = "-n 5 127.0.0.1",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
        Assert.NotNull(proc);

        try
        {
            var credited = await ProcessHelper.TrackGameSessionAsync(
                seedPid: null,
                installRoot: unrelatedInstallRoot,
                processNames: ["ping"],
                ignoredNames: null,
                appearTimeout: TimeSpan.FromMilliseconds(500),
                goneDebounce: TimeSpan.FromMilliseconds(200),
                ct: CancellationToken.None);

            Assert.False(credited);
        }
        finally
        {
            try
            {
                if (!proc!.HasExited) proc.Kill(entireProcessTree: true);
            }
            catch { /* process cleanup is best-effort */ }
            try { Directory.Delete(unrelatedInstallRoot, recursive: true); }
            catch { /* temp cleanup is best-effort */ }
        }
    }

    [Fact]
    public void PlaytimeService_EndSession_PersistsMinutes()
    {
        var id = "riot:playtime-fixture-" + Guid.NewGuid().ToString("N")[..8];
        PlaytimeService.BeginSession(id);
        // Force a countable window without sleeping a full minute, then exercise
        // the real EndSession path rather than bypassing it with AddExoMinutes.
        var active = (System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset>)
            typeof(PlaytimeService)
                .GetField("ActiveSessions", System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static)!
                .GetValue(null)!;
        active[id] = DateTimeOffset.UtcNow.AddMinutes(-7);
        PlaytimeService.EndSession(id);

        var enriched = PlaytimeService.Enrich(
        [
            new Models.GameEntry
            {
                Id = id,
                Title = "Fixture",
                Store = Models.StoreKind.Riot,
                Installed = true,
                LaunchTarget = "valorant",
            },
        ]);

        Assert.Equal(7, enriched[0].PlaytimeMinutes);
    }

    [Fact]
    public void RocketLeague_CombinesDistinctEpicAndSteamStoreHistories()
    {
        var games = new List<Models.GameEntry>
        {
            new()
            {
                Id = "epic:Sugar",
                Title = "Rocket League",
                Store = Models.StoreKind.Epic,
                Installed = true,
                LaunchTarget = "Sugar",
                PlaytimeMinutes = 11_307,
            },
            new()
            {
                Id = "steam:252950",
                Title = "Rocket League",
                Store = Models.StoreKind.Steam,
                Installed = false,
                LaunchTarget = "252950",
                // Must not be counted twice when Steam's VDF has the same value.
                PlaytimeMinutes = 2_500,
            },
        };
        var steam = new Dictionary<string, SteamPlaytime.Entry>(StringComparer.Ordinal)
        {
            ["252950"] = new(2_500, null),
        };

        var total = PlaytimeService.CombineRocketLeagueStoreMinutes(games, steam);

        Assert.Equal(13_807, total);
        Assert.True(PlaytimeService.IsRocketLeague(games[0]));
        Assert.True(PlaytimeService.IsRocketLeague(games[1]));
    }

    [Fact]
    public void PlaytimeService_DoesNotUploadExoFallbackBesideRealLifetimeSource()
    {
        var id = "epic:fallback-overlap-fixture-" + Guid.NewGuid().ToString("N")[..8];
        var title = "Fallback overlap fixture " + Guid.NewGuid().ToString("N")[..8];
        PlaytimeService.AddExoMinutes(id, 11);

        var enriched = PlaytimeService.Enrich(
        [
            new Models.GameEntry
            {
                Id = id,
                Title = title,
                Store = Models.StoreKind.Epic,
                Installed = true,
                LaunchTarget = id["epic:".Length..],
                PlaytimeMinutes = 120,
            },
        ]);
        var gameKey = PlaytimeService.GameKey(enriched[0]);
        var observations = PlaytimeService.SnapshotObservations("fixture-device");

        Assert.Equal(120, enriched[0].PlaytimeMinutes);
        Assert.Contains(observations, value =>
            value.GameKey == gameKey && value.Source == "epic" && value.TotalSeconds == 7_200);
        Assert.DoesNotContain(observations, value =>
            value.GameKey == gameKey && value.Source == "exo_session");
    }

    [Fact]
    public void PlaytimeService_MigratesLegacyRiotLifetimeWithoutPersistingRawIdentity()
    {
        const string fictionalLegacyAccount = "fixture-account-never-a-real-user";
        var legacyPath = Path.Combine(PathHelper.AppDataDir, "tracker-gg-playtime.json");
        var neutralPath = Path.Combine(PathHelper.AppDataDir, "exo-imported-lifetime.json");
        File.Delete(legacyPath);
        File.Delete(neutralPath);
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(new
        {
            accountId = fictionalLegacyAccount,
            minutes = new Dictionary<string, int>
            {
                ["riot:valorant"] = 105699,
                ["riot:league_of_legends"] = 403,
                ["riot:zero"] = 0,
                ["riot:negative"] = -5,
            },
        }));
        try
        {
            Models.GameEntry[] games =
            [
                new Models.GameEntry
                {
                    Id = "riot:valorant",
                    Title = "VALORANT",
                    Store = Models.StoreKind.Riot,
                    Installed = true,
                    LaunchTarget = "valorant",
                },
                new Models.GameEntry
                {
                    Id = "riot:league_of_legends",
                    Title = "League of Legends",
                    Store = Models.StoreKind.Riot,
                    Installed = true,
                    LaunchTarget = "league_of_legends",
                },
            ];

            var enriched = PlaytimeService.Enrich(games);

            Assert.Equal(105699, enriched[0].PlaytimeMinutes);
            Assert.Equal(403, enriched[1].PlaytimeMinutes);
            Assert.False(File.Exists(legacyPath));
            Assert.True(File.Exists(neutralPath));

            var persistedJson = File.ReadAllText(neutralPath);
            Assert.DoesNotContain(fictionalLegacyAccount, persistedJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("accountId", persistedJson, StringComparison.OrdinalIgnoreCase);
            using (var persisted = JsonDocument.Parse(persistedJson))
            {
                Assert.Equal(
                    ["accountKey", "exoSessionBaselineMinutes", "minutes", "observedAt"],
                    persisted.RootElement
                        .EnumerateObject()
                        .Select(property => property.Name)
                        .OrderBy(name => name, StringComparer.Ordinal)
                        .ToArray());
                Assert.Matches(
                    "^[0-9a-f]{20}$",
                    persisted.RootElement.GetProperty("accountKey").GetString() ?? string.Empty);
                Assert.All(
                    persisted.RootElement.GetProperty("minutes").EnumerateObject(),
                    property => Assert.True(property.Value.GetInt32() > 0));
                Assert.Equal(2, persisted.RootElement.GetProperty("minutes").EnumerateObject().Count());
            }

            // The raw legacy file is already gone. A later load must preserve
            // the lifetime totals exclusively from the neutral Exo import.
            var afterLegacyRemoval = PlaytimeService.Enrich(games);
            Assert.Equal(105699, afterLegacyRemoval[0].PlaytimeMinutes);
            Assert.Equal(403, afterLegacyRemoval[1].PlaytimeMinutes);

            var observations = PlaytimeService.SnapshotObservations("fixture-device");
            Assert.Contains(observations, value =>
                value.GameKey == "valorant" &&
                value.Source == "imported_lifetime" &&
                value.TotalSeconds == 105699L * 60L);
            Assert.DoesNotContain(observations, value =>
                value.CoverageKey.Contains(fictionalLegacyAccount, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(legacyPath);
            File.Delete(neutralPath);
            foreach (var temporary in Directory.EnumerateFiles(
                         PathHelper.AppDataDir,
                         "exo-imported-lifetime.json.*.tmp"))
                File.Delete(temporary);
        }
    }

    [Fact]
    public void PlaytimeService_AddsOnlySessionsObservedAfterFrozenRiotImport()
    {
        const string fictionalLegacyAccount = "fixture-account-never-a-real-user";
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var id = "riot:post-import-fixture-" + suffix;
        var title = "Post import fixture " + suffix;
        var legacyPath = Path.Combine(PathHelper.AppDataDir, "tracker-gg-playtime.json");
        var neutralPath = Path.Combine(PathHelper.AppDataDir, "exo-imported-lifetime.json");
        File.Delete(legacyPath);
        File.Delete(neutralPath);
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(new
        {
            accountId = fictionalLegacyAccount,
            minutes = new Dictionary<string, int> { [id] = 400 },
        }));
        File.SetLastWriteTimeUtc(legacyPath, DateTime.UtcNow.AddHours(-1));

        try
        {
            // This session happened after the frozen import was observed, so it
            // is new coverage rather than an overlapping lifetime fallback.
            PlaytimeService.AddExoMinutes(id, 48);
            var enriched = PlaytimeService.Enrich(
            [
                new Models.GameEntry
                {
                    Id = id,
                    Title = title,
                    Store = Models.StoreKind.Riot,
                    Installed = true,
                    LaunchTarget = id["riot:".Length..],
                },
            ]);

            Assert.Equal(448, enriched[0].PlaytimeMinutes);
            var observations = PlaytimeService.SnapshotObservations("fixture-device");
            Assert.Contains(observations, value =>
                value.GameKey == PlaytimeService.GameKey(enriched[0]) &&
                value.Source == "imported_lifetime" &&
                value.TotalSeconds == 400L * 60L);
            Assert.Contains(observations, value =>
                value.GameKey == PlaytimeService.GameKey(enriched[0]) &&
                value.Source == "exo_session" &&
                value.TotalSeconds == 48L * 60L);

            // The persisted zero baseline is essential: a restart must keep
            // treating the same 48 minutes as the cumulative post-import row,
            // not capture it as a new baseline and make those minutes vanish.
            using var neutral = JsonDocument.Parse(File.ReadAllText(neutralPath));
            Assert.Equal(
                0,
                neutral.RootElement
                    .GetProperty("exoSessionBaselineMinutes")
                    .GetProperty(id)
                    .GetInt32());
        }
        finally
        {
            File.Delete(legacyPath);
            File.Delete(neutralPath);
            foreach (var temporary in Directory.EnumerateFiles(
                         PathHelper.AppDataDir,
                         "exo-imported-lifetime.json.*.tmp"))
                File.Delete(temporary);
        }
    }
}
