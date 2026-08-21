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
    public void RiotLastPlayed_ReadsQuotedAndUnquotedSessionTimestamps()
    {
        var map = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        RiotLastPlayed.ReadLastSessionBlock(
            """
            install:
                last-session-timestamp:
                    league_of_legends.live: 1700000000
                    valorant.live: "1700003600"
                patch-notes:
                    valorant.live:
                        ignored: 1
            """,
            map);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), map["league_of_legends"]);
        Assert.False(map.ContainsKey("lion"));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700003600), map["valorant"]);
    }

    [Fact]
    public void RiotLastPlayed_ReadsNestedProductBlocksAndMillisecondTimestamps()
    {
        var map = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        RiotLastPlayed.ReadLastSessionBlock(
            """
            install:
                last-session-timestamp:
                    valorant:
                        live: 1700003600000
                    bacon:
                        live: '1700000000'
                launcher_server.enabled: true
            """,
            map);

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1700003600000), map["valorant"]);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), map["bacon"]);
    }

    [Fact]
    public void EpicEglLastPlayed_ParsesLauncherLastPlayedGameLine()
    {
        Assert.True(EpicEglLastPlayed.TryParseLastPlayedGame(
            "9773aa1aa54f4f7b80e44bef04986cea:530145df28a24424923f5828cc9031a1:Sugar,2026-08-12T20:12:55.789Z",
            out var app,
            out var when));
        Assert.Equal("Sugar", app);
        Assert.Equal(DateTimeOffset.Parse("2026-08-12T20:12:55.789Z"), when);
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
    public void PlaytimeService_BeginSession_StampsLastPlayedBeforeAnyMinutesExist()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var id = "epic:launch-stamp-fixture-" + suffix;
        var beforeLaunch = DateTimeOffset.UtcNow.AddSeconds(-1);

        PlaytimeService.BeginSession(id);
        try
        {
            var enriched = PlaytimeService.Enrich(
            [
                new Models.GameEntry
                {
                    Id = id,
                    Title = "Launch stamp fixture " + suffix,
                    Store = Models.StoreKind.Epic,
                    Installed = true,
                    LaunchTarget = id["epic:".Length..],
                    PlaytimeMinutes = 120,
                    // The reading the store had before this launch. Exo used to
                    // publish it right after Play and claim "5h ago".
                    LastPlayedUtc = DateTimeOffset.UtcNow.AddHours(-5),
                },
            ]);

            Assert.True(enriched[0].LastPlayedUtc >= beforeLaunch);
            // A started session is not credited time. The vendor counter stands.
            Assert.Equal(120, enriched[0].PlaytimeMinutes);
        }
        finally
        {
            PlaytimeService.CancelSession(id);
        }
    }

    [Fact]
    public void PlaytimeService_IgnoresAStoreTimestampAheadOfTheClock()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var enriched = PlaytimeService.Enrich(
        [
            new Models.GameEntry
            {
                Id = "steam:4000010",
                Title = "Future stamp fixture " + suffix,
                Store = Models.StoreKind.Steam,
                Installed = true,
                LaunchTarget = "4000010",
                // Epic's launcher stamps local wall-clock with a Z suffix, so
                // east of UTC its last-played lands ahead of the clock.
                LastPlayedUtc = DateTimeOffset.UtcNow.AddHours(3),
            },
        ]);

        Assert.Null(enriched[0].LastPlayedUtc);
    }

    [Fact]
    public void PlaytimeService_LeavesAStoreItCannotReadWithoutHours()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var enriched = PlaytimeService.Enrich(
        [
            new Models.GameEntry
            {
                Id = "steam:4000009",
                Title = "Unreadable store fixture " + suffix,
                Store = Models.StoreKind.Steam,
                Installed = true,
                LaunchTarget = "4000009",
            },
        ]);

        // No localconfig entry, no Exo session: nothing is known, so nothing is
        // claimed. A zero here would read as "never played".
        Assert.Null(enriched[0].PlaytimeMinutes);
        Assert.Null(enriched[0].LastPlayedUtc);
    }

    [Fact]
    public void PlaytimeService_DoesNotAddExoFallbackToNativeLifetime()
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
        Assert.Equal(120, enriched[0].PlaytimeMinutes);
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

    [Fact]
    public void PlaytimeService_EnrichTwice_ReturnsTheSameMinutes()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        Models.GameEntry[] games =
        [
            new Models.GameEntry
            {
                Id = "epic:enrich-twice-" + suffix,
                Title = "Enrich twice " + suffix,
                Store = Models.StoreKind.Epic,
                Installed = true,
                LaunchTarget = "enrich-twice-" + suffix,
                PlaytimeMinutes = 11_837,
            },
        ];

        var first = PlaytimeService.Enrich(games);
        var second = PlaytimeService.Enrich(games);

        Assert.Equal(11_837, first[0].PlaytimeMinutes);
        Assert.Equal(first[0].PlaytimeMinutes, second[0].PlaytimeMinutes);
    }

    [Fact]
    public void PlaytimeService_LocalVersusUtcLastPlayed_DoesNotChangeMinutes()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var local = new DateTimeOffset(2026, 8, 18, 19, 20, 33, TimeSpan.FromHours(-5));
        var utc = local.ToUniversalTime();
        Assert.Equal(local.UtcTicks, utc.UtcTicks);

        Models.GameEntry Game(DateTimeOffset lastPlayed) => new()
        {
            Id = "epic:offset-minutes-" + suffix,
            Title = "Offset minutes " + suffix,
            Store = Models.StoreKind.Epic,
            Installed = true,
            LaunchTarget = "offset-minutes-" + suffix,
            PlaytimeMinutes = 11_837,
            LastPlayedUtc = lastPlayed,
        };

        var fromLocal = PlaytimeService.Enrich([Game(local)]);
        var fromUtc = PlaytimeService.Enrich([Game(utc)]);

        Assert.Equal(11_837, fromLocal[0].PlaytimeMinutes);
        Assert.Equal(11_837, fromUtc[0].PlaytimeMinutes);
        Assert.Equal(fromLocal[0].PlaytimeMinutes, fromUtc[0].PlaytimeMinutes);
    }

    [Fact]
    public void PlaytimeService_SessionLength_UsesTheInstantNotTheWallClockOffset()
    {
        var id = "epic:offset-session-" + Guid.NewGuid().ToString("N")[..8];
        PlaytimeService.BeginSession(id);
        var active = (System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset>)
            typeof(PlaytimeService)
                .GetField("ActiveSessions", System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static)!
                .GetValue(null)!;
        var endedAt = DateTimeOffset.UtcNow;
        active[id] = endedAt.AddMinutes(-90).ToOffset(TimeSpan.FromHours(-5));
        PlaytimeService.EndSession(id);

        var enriched = PlaytimeService.Enrich(
        [
            new Models.GameEntry
            {
                Id = id,
                Title = "Offset session",
                Store = Models.StoreKind.Epic,
                Installed = true,
                LaunchTarget = id["epic:".Length..],
            },
        ]);

        Assert.Equal(90, enriched[0].PlaytimeMinutes);
    }
}
