using ExoLauncher.Models;
using ExoLauncher.Services;
using ExoLauncher.Services.Achievements;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class AchievementServiceTests
{
    [Fact]
    public async Task FirstSnapshotEstablishesBaselineWithoutHistoricalNotifications()
    {
        var path = TempStatePath();
        try
        {
            var provider = new SequenceProvider(Snapshot(unlocked: true));
            using var service = new AchievementService([provider], path, TimeSpan.FromHours(1));
            var notifications = new List<AchievementUnlock>();
            service.AchievementUnlocked += notifications.Add;

            var result = await service.RefreshAsync(Game());

            Assert.Equal(AchievementCoverageStatus.Complete, result.Coverage);
            Assert.Empty(notifications);
            Assert.True(File.Exists(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        }
        finally
        {
            DeleteStateDirectory(path);
        }
    }

    [Fact]
    public async Task LockedToUnlockedDiffNotifiesExactlyOnceAcrossRefreshAndRestart()
    {
        var path = TempStatePath();
        try
        {
            var provider = new SequenceProvider(
                Snapshot(unlocked: false),
                Snapshot(unlocked: true),
                Snapshot(unlocked: true));
            var notifications = new List<AchievementUnlock>();

            using (var service = new AchievementService([provider], path, TimeSpan.FromHours(1)))
            {
                service.AchievementUnlocked += notifications.Add;
                await service.RefreshAsync(Game());
                await service.RefreshAsync(Game());
                await service.RefreshAsync(Game());
            }

            Assert.Single(notifications);
            Assert.Equal("ACH_ONE", notifications[0].Entry.Definition.ExternalId);
            Assert.True(notifications[0].IsPerfected);

            var afterRestart = new SequenceProvider(Snapshot(unlocked: true));
            using var restarted = new AchievementService([afterRestart], path, TimeSpan.FromHours(1));
            restarted.AchievementUnlocked += notifications.Add;
            await restarted.RefreshAsync(Game());

            Assert.Single(notifications);
            var persisted = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("raw-account-id", persisted, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteStateDirectory(path);
        }
    }

    [Fact]
    public async Task RelockAndNewlyVisibleUnlockedRowsDoNotCreateDuplicateOrHistoricalNotifications()
    {
        var path = TempStatePath();
        try
        {
            var provider = new SequenceProvider(
                Snapshot(unlocked: false),
                Snapshot(unlocked: true),
                Snapshot(unlocked: false, includeHistoricalSecond: true),
                Snapshot(unlocked: true, includeHistoricalSecond: true));
            using var service = new AchievementService([provider], path, TimeSpan.FromHours(1));
            var notifications = new List<AchievementUnlock>();
            service.AchievementUnlocked += notifications.Add;

            await service.RefreshAsync(Game());
            await service.RefreshAsync(Game());
            var corrected = await service.RefreshAsync(Game());
            var persistedCorrection = service.GetLatestSnapshot(Game().Id);
            await service.RefreshAsync(Game());

            var notification = Assert.Single(notifications);
            Assert.Equal("ACH_ONE", notification.Entry.Definition.ExternalId);
            Assert.False(Assert.Single(
                corrected.Entries,
                row => row.Definition.ExternalId == "ACH_ONE").State.Unlocked);
            Assert.NotNull(persistedCorrection);
            Assert.False(Assert.Single(
                persistedCorrection.Entries,
                row => row.Definition.ExternalId == "ACH_ONE").State.Unlocked);
        }
        finally
        {
            DeleteStateDirectory(path);
        }
    }

    [Fact]
    public async Task CompleteSnapshotRemovesRowsTheProviderRetracted()
    {
        var path = TempStatePath();
        try
        {
            var provider = new SequenceProvider(
                Snapshot(unlocked: false, includeHistoricalSecond: true),
                Snapshot(unlocked: false));
            using var service = new AchievementService([provider], path, TimeSpan.FromHours(1));

            await service.RefreshAsync(Game());
            await service.RefreshAsync(Game());

            var persisted = Assert.IsType<AchievementSnapshot>(service.GetLatestSnapshot(Game().Id));
            Assert.Single(persisted.Entries);
            Assert.DoesNotContain(
                persisted.Entries,
                row => row.Definition.ExternalId == "ACH_ALREADY");
            Assert.Equal(1, service.GetSummary(Game().Id)?.Total);
            Assert.Equal(0, service.GetSummary(Game().Id)?.Unlocked);
        }
        finally
        {
            DeleteStateDirectory(path);
        }
    }

    [Fact]
    public async Task UnsupportedGamesReturnExplicitCoverageWithoutPersistingState()
    {
        var path = TempStatePath();
        try
        {
            using var service = new AchievementService([], path, TimeSpan.FromHours(1));
            var game = new GameEntry
            {
                Id = "riot:valorant",
                Title = "VALORANT",
                Store = StoreKind.Riot,
                Installed = true,
                LaunchTarget = "valorant",
            };

            var coverage = service.GetCoverage(game);
            var snapshot = await service.RefreshAsync(game);

            Assert.Equal(AchievementCoverageStatus.Unsupported, coverage.Status);
            Assert.Equal(AchievementCoverageStatus.Unsupported, snapshot.Coverage);
            Assert.Contains("not available", snapshot.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(path));
        }
        finally
        {
            DeleteStateDirectory(path);
        }
    }

    [Fact]
    public async Task SteamZeroPlaceholderIsNeverPersistedOrExposedAsASummary()
    {
        var path = TempStatePath();
        try
        {
            using var service = new AchievementService(
                [new SteamZeroPlaceholderProvider()], path, TimeSpan.FromHours(1));
            var game = new GameEntry
            {
                Id = "steam:1110910",
                Title = "Mortal Shell",
                Store = StoreKind.Steam,
                Installed = true,
                LaunchTarget = "1110910",
            };

            var result = await service.RefreshAsync(game);

            Assert.Equal(AchievementCoverageStatus.Partial, result.Coverage);
            Assert.Null(service.GetLatestSnapshot(game.Id));
            Assert.Null(service.GetSummary(game.Id));
            Assert.Empty(service.GetCurrentSnapshots());
            Assert.False(File.Exists(path));
        }
        finally
        {
            DeleteStateDirectory(path);
        }
    }

    [Fact]
    public async Task LegacySteamCommunityCatalogSnapshotIsNeverExposedAsAccountProgress()
    {
        var path = TempStatePath();
        try
        {
            using var service = new AchievementService(
                [new LegacySteamCommunityCatalogProvider()], path, TimeSpan.FromHours(1));
            var game = new GameEntry
            {
                Id = "steam:1110910",
                Title = "Mortal Shell",
                Store = StoreKind.Steam,
                Installed = true,
                LaunchTarget = "1110910",
            };

            var result = await service.RefreshAsync(game);

            Assert.Equal(AchievementCoverageStatus.Partial, result.Coverage);
            Assert.Equal(0, result.ReportedUnlocked);
            Assert.Equal(37, result.ReportedTotal);
            Assert.Null(service.GetLatestSnapshot(game.Id));
            Assert.Null(service.GetSummary(game.Id));
            Assert.Empty(service.GetCurrentSnapshots());
        }
        finally
        {
            DeleteStateDirectory(path);
        }
    }

    [Fact]
    public async Task SessionPollingRefreshesDuringSessionAndOnceAfterStop()
    {
        var path = TempStatePath();
        try
        {
            var provider = new SequenceProvider(Snapshot(unlocked: false));
            using var service = new AchievementService(
                [provider], path, TimeSpan.FromMilliseconds(15));

            await service.BeginSessionAsync(Game());
            await provider.WaitForCallsAsync(2, TimeSpan.FromSeconds(2));
            var beforeStop = provider.CallCount;

            var final = await service.EndSessionAsync(Game().Id);

            Assert.NotNull(final);
            Assert.True(provider.CallCount >= beforeStop + 1,
                $"Expected a final refresh after {beforeStop} calls, saw {provider.CallCount}.");
        }
        finally
        {
            DeleteStateDirectory(path);
        }
    }

    [Fact]
    public async Task ReadApisExposeLatestSnapshotAndBridgeSafeSummary()
    {
        var path = TempStatePath();
        try
        {
            var provider = new SequenceProvider(Snapshot(unlocked: true));
            using var service = new AchievementService([provider], path, TimeSpan.FromHours(1));

            await service.RefreshAsync(Game());

            var latest = service.GetLatestSnapshot(Game().Id);
            var summary = service.GetSummary(Game().Id);
            Assert.NotNull(latest);
            Assert.Equal("epic:0123456789abcdef0123456789abcdef", latest!.CoverageKey);
            Assert.NotNull(summary);
            Assert.Equal(1, summary!.Total);
            Assert.Equal(1, summary.Unlocked);
            Assert.Equal(100, summary.CompletionPercent);
            Assert.True(summary.Perfected);

            var bridgeJson = System.Text.Json.JsonSerializer.Serialize(summary);
            Assert.DoesNotContain("coverageKey", bridgeJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("raw-account", bridgeJson, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteStateDirectory(path);
        }
    }

    [Fact]
    public async Task CurrentSnapshotsAreNewestFirstAndBounded()
    {
        var path = TempStatePath();
        try
        {
            using var service = new AchievementService(
                [new EchoProvider()], path, TimeSpan.FromHours(1));
            await service.RefreshAsync(Game("epic:A", "A"));
            await service.RefreshAsync(Game("epic:B", "B"));
            await service.RefreshAsync(Game("epic:C", "C"));

            var bounded = service.GetCurrentSnapshots(limit: 2);

            Assert.Equal(2, bounded.Count);
            Assert.Equal(new[] { "C", "B" }, bounded.Select(row => row.SourceGameId));
            Assert.Empty(service.GetCurrentSnapshots(limit: 0));
            Assert.Equal(3, service.GetCurrentSnapshots(limit: 5000).Count);
        }
        finally
        {
            DeleteStateDirectory(path);
        }
    }

    [Fact]
    public async Task RawAccountCoverageIsRejectedBeforePersistence()
    {
        var path = TempStatePath();
        try
        {
            var raw = Snapshot(unlocked: false) with { CoverageKey = "epic:raw-account-id" };
            using var service = new AchievementService(
                [new SequenceProvider(raw)], path, TimeSpan.FromHours(1));

            var result = await service.RefreshAsync(Game());

            Assert.Equal(AchievementCoverageStatus.Unavailable, result.Coverage);
            Assert.False(File.Exists(path));
        }
        finally
        {
            DeleteStateDirectory(path);
        }
    }

    [Fact]
    public async Task CentralGateRejectsEveryContradictoryProviderSnapshot()
    {
        var validLocked = Snapshot(unlocked: false);
        var duplicateCase = Entry("ach_one", unlocked: false);
        var unlockedRow = Entry("ACH_ONE", unlocked: true);
        var invalidSnapshots = new AchievementSnapshot[]
        {
            validLocked with { ReportedTotal = null },
            validLocked with { ReportedUnlocked = null },
            validLocked with { ReportedTotal = -1 },
            validLocked with { ReportedUnlocked = 2 },
            validLocked with { ReportedTotal = 0 },
            validLocked with
            {
                ReportedUnlocked = 0,
                Entries = [unlockedRow],
            },
            validLocked with
            {
                ReportedTotal = 2,
                ReportedUnlocked = 0,
                Entries = [validLocked.Entries[0], duplicateCase],
            },
            validLocked with
            {
                ReportedTotal = 2,
                Coverage = AchievementCoverageStatus.Complete,
                Entries = [validLocked.Entries[0]],
            },
        };

        foreach (var invalid in invalidSnapshots)
        {
            var path = TempStatePath();
            try
            {
                using var service = new AchievementService(
                    [new SequenceProvider(invalid)], path, TimeSpan.FromHours(1));
                var notifications = new List<AchievementUnlock>();
                service.AchievementUnlocked += notifications.Add;

                var result = await service.RefreshAsync(Game());

                Assert.Equal(AchievementCoverageStatus.Unavailable, result.Coverage);
                Assert.Null(service.GetLatestSnapshot(Game().Id));
                Assert.Null(service.GetSummary(Game().Id));
                Assert.Empty(notifications);
                Assert.False(File.Exists(path));
            }
            finally
            {
                DeleteStateDirectory(path);
            }
        }
    }

    [Fact]
    public async Task PartialSnapshotsUseReportedSummaryAndExposeOnlyCurrentRows()
    {
        var path = TempStatePath();
        try
        {
            var firstObserved = DateTimeOffset.Parse("2026-08-10T03:00:00Z");
            var secondObserved = firstObserved.AddMinutes(1);
            var first = PartialSnapshot(
                firstObserved,
                total: 37,
                unlocked: 1,
                EntryAt("ACH_ONE", unlocked: true, firstObserved),
                EntryAt("ACH_OLD_LOCKED", unlocked: false, firstObserved),
                EntryAt("ACH_OLD_HIDDEN", unlocked: false, firstObserved));
            var second = PartialSnapshot(
                secondObserved,
                total: 30,
                unlocked: 1,
                EntryAt("ACH_ONE", unlocked: true, secondObserved));
            using var service = new AchievementService(
                [new SequenceProvider(first, second)], path, TimeSpan.FromHours(1));

            await service.RefreshAsync(Game());
            await service.RefreshAsync(Game());

            var latest = Assert.IsType<AchievementSnapshot>(service.GetLatestSnapshot(Game().Id));
            var summary = Assert.IsType<GameAchievementSummary>(service.GetSummary(Game().Id));
            Assert.Single(latest.Entries);
            Assert.Equal("ACH_ONE", latest.Entries[0].Definition.ExternalId);
            Assert.Equal(30, summary.Total);
            Assert.Equal(1, summary.Unlocked);
        }
        finally
        {
            DeleteStateDirectory(path);
        }
    }

    [Fact]
    public async Task LogicallyCorruptPersistedStateIsNeverExposed()
    {
        var path = TempStatePath();
        try
        {
            using (var writer = new AchievementService(
                       [new SequenceProvider(Snapshot(unlocked: false))],
                       path,
                       TimeSpan.FromHours(1)))
            {
                await writer.RefreshAsync(Game());
            }

            var json = await File.ReadAllTextAsync(path);
            const string validTotal = "\"reportedTotal\": 1";
            Assert.Contains(validTotal, json, StringComparison.Ordinal);
            await File.WriteAllTextAsync(path, json.Replace(
                validTotal,
                "\"reportedTotal\": 0",
                StringComparison.Ordinal));

            using var reader = new AchievementService(
                [new SequenceProvider(Snapshot(unlocked: false))],
                path,
                TimeSpan.FromHours(1));

            Assert.Null(reader.GetLatestSnapshot(Game().Id));
            Assert.Null(reader.GetSummary(Game().Id));
            Assert.Empty(reader.GetCurrentSnapshots());
        }
        finally
        {
            DeleteStateDirectory(path);
        }
    }

    [Fact]
    public async Task AccountCoverageChangeEstablishesANewBaselineWithoutNotifications()
    {
        var path = TempStatePath();
        try
        {
            var firstAccount = Snapshot(unlocked: false);
            var secondAccount = Snapshot(unlocked: true) with
            {
                CoverageKey = "epic:fedcba9876543210fedcba9876543210",
            };
            using var service = new AchievementService(
                [new SequenceProvider(firstAccount, secondAccount)], path, TimeSpan.FromHours(1));
            var notifications = new List<AchievementUnlock>();
            service.AchievementUnlocked += notifications.Add;

            await service.RefreshAsync(Game());
            await service.RefreshAsync(Game());

            Assert.Empty(notifications);
            Assert.Equal(2, service.GetCurrentSnapshots().Count);
        }
        finally
        {
            DeleteStateDirectory(path);
        }
    }

    [Fact]
    public async Task NotificationDeliveryOutbox_ReplaysAfterRestartUntilNativePresenterAcknowledges()
    {
        var path = TempStatePath();
        try
        {
            var delivered = new List<AchievementNotificationDelivery>();
            string deliveryId;
            using (var service = new AchievementService(
                       [new SequenceProvider(Snapshot(unlocked: false), Snapshot(unlocked: true))],
                       path,
                       TimeSpan.FromHours(1)))
            {
                service.NotificationDeliveryRequested += delivery =>
                {
                    // Delivery must reach a UI listener only after it survived
                    // an on-disk write, otherwise a crash loses the toast.
                    Assert.Contains(delivery.DeliveryId, File.ReadAllText(path), StringComparison.Ordinal);
                    delivered.Add(delivery);
                };
                await service.RefreshAsync(Game());
                await service.RefreshAsync(Game());

                var first = Assert.Single(delivered);
                deliveryId = first.DeliveryId;
                Assert.Single(service.GetPendingNotificationDeliveries());
                Assert.Equal("ACH_ONE", first.Unlock.Entry.Definition.ExternalId);
            }

            using (var restarted = new AchievementService(
                       [new SequenceProvider(Snapshot(unlocked: true))], path, TimeSpan.FromHours(1)))
            {
                var replayed = new List<AchievementNotificationDelivery>();
                restarted.NotificationDeliveryRequested += replayed.Add;
                await restarted.RefreshAsync(Game());

                var replay = Assert.Single(replayed);
                Assert.Equal(deliveryId, replay.DeliveryId);
                Assert.True(restarted.AcknowledgeNotificationDelivery(replay.DeliveryId));
                Assert.Empty(restarted.GetPendingNotificationDeliveries());
            }

            using var acknowledged = new AchievementService(
                [new SequenceProvider(Snapshot(unlocked: true))], path, TimeSpan.FromHours(1));
            var afterAcknowledgement = new List<AchievementNotificationDelivery>();
            acknowledged.NotificationDeliveryRequested += afterAcknowledgement.Add;
            await acknowledged.RefreshAsync(Game());
            Assert.Empty(afterAcknowledgement);
        }
        finally
        {
            DeleteStateDirectory(path);
        }
    }

    [Fact]
    public async Task NotificationDeliveryOutbox_DoesNotReplayForDifferentAccountCoverage()
    {
        var path = TempStatePath();
        try
        {
            using (var service = new AchievementService(
                       [new SequenceProvider(Snapshot(unlocked: false), Snapshot(unlocked: true))],
                       path,
                       TimeSpan.FromHours(1)))
            {
                await service.RefreshAsync(Game());
                await service.RefreshAsync(Game());
                Assert.Single(service.GetPendingNotificationDeliveries());
            }

            var otherAccount = Snapshot(unlocked: true) with
            {
                CoverageKey = "epic:fedcba9876543210fedcba9876543210",
            };
            using var restarted = new AchievementService(
                [new SequenceProvider(otherAccount)], path, TimeSpan.FromHours(1));
            var replayed = new List<AchievementNotificationDelivery>();
            restarted.NotificationDeliveryRequested += replayed.Add;

            await restarted.RefreshAsync(Game());

            Assert.Empty(replayed);
            Assert.Single(restarted.GetPendingNotificationDeliveries());
        }
        finally
        {
            DeleteStateDirectory(path);
        }
    }

    [Fact]
    public async Task VersionOneState_MigratesItsBaselineWithoutRebaselining()
    {
        var path = TempStatePath();
        try
        {
            using (var original = new AchievementService(
                       [new SequenceProvider(Snapshot(unlocked: false))], path, TimeSpan.FromHours(1)))
            {
                await original.RefreshAsync(Game());
            }

            File.WriteAllText(path, File.ReadAllText(path).Replace("\"version\": 2", "\"version\": 1", StringComparison.Ordinal));

            using var migrated = new AchievementService(
                [new SequenceProvider(Snapshot(unlocked: true))], path, TimeSpan.FromHours(1));
            var delivered = new List<AchievementNotificationDelivery>();
            migrated.NotificationDeliveryRequested += delivered.Add;

            await migrated.RefreshAsync(Game());

            Assert.Single(delivered);
            Assert.Equal("ACH_ONE", delivered[0].Unlock.Entry.Definition.ExternalId);
        }
        finally
        {
            DeleteStateDirectory(path);
        }
    }

    [Fact]
    public async Task PartialFirstSeenUnlock_NotifiesOnlyWhenTheAccountSummaryIdentifiesOneRow()
    {
        var path = TempStatePath();
        try
        {
            var initial = PartialSnapshot(
                DateTimeOffset.Parse("2026-08-11T00:00:00Z"), 3, 0,
                EntryAt("ACH_ONE", unlocked: false, DateTimeOffset.Parse("2026-08-11T00:00:00Z")));
            var exactDelta = PartialSnapshot(
                DateTimeOffset.Parse("2026-08-11T00:01:00Z"), 3, 1,
                EntryAt("ACH_ONE", unlocked: false, DateTimeOffset.Parse("2026-08-11T00:01:00Z")),
                EntryAt("ACH_TWO", unlocked: true, DateTimeOffset.Parse("2026-08-11T00:01:00Z")));
            using var service = new AchievementService(
                [new SequenceProvider(initial, exactDelta)], path, TimeSpan.FromHours(1));
            var deliveries = new List<AchievementNotificationDelivery>();
            service.NotificationDeliveryRequested += deliveries.Add;

            await service.RefreshAsync(Game());
            await service.RefreshAsync(Game());

            var delivery = Assert.Single(deliveries);
            Assert.Equal("ACH_TWO", delivery.Unlock.Entry.Definition.ExternalId);
            Assert.Single(service.GetPendingNotificationDeliveries());
        }
        finally
        {
            DeleteStateDirectory(path);
        }
    }

    [Fact]
    public async Task PartialFirstSeenUnlock_DoesNotGuessWhenMultipleUnlockedRowsAppear()
    {
        var path = TempStatePath();
        try
        {
            var firstAt = DateTimeOffset.Parse("2026-08-11T00:00:00Z");
            var secondAt = firstAt.AddMinutes(1);
            var initial = PartialSnapshot(firstAt, 4, 0, EntryAt("ACH_ONE", unlocked: false, firstAt));
            var ambiguous = PartialSnapshot(secondAt, 4, 2,
                EntryAt("ACH_ONE", unlocked: false, secondAt),
                EntryAt("ACH_TWO", unlocked: true, secondAt),
                EntryAt("ACH_THREE", unlocked: true, secondAt));
            using var service = new AchievementService(
                [new SequenceProvider(initial, ambiguous)], path, TimeSpan.FromHours(1));
            var deliveries = new List<AchievementNotificationDelivery>();
            service.NotificationDeliveryRequested += deliveries.Add;

            await service.RefreshAsync(Game());
            await service.RefreshAsync(Game());

            Assert.Empty(deliveries);
            Assert.Empty(service.GetPendingNotificationDeliveries());
        }
        finally
        {
            DeleteStateDirectory(path);
        }
    }

    private static GameEntry Game(string id = "epic:Sugar", string launchTarget = "Sugar") => new()
    {
        Id = id,
        Title = launchTarget,
        Store = StoreKind.Epic,
        Installed = true,
        LaunchTarget = launchTarget,
    };

    private static AchievementSnapshot Snapshot(bool unlocked, bool includeHistoricalSecond = false)
    {
        var rows = new List<AchievementEntry>
        {
            Entry("ACH_ONE", unlocked),
        };
        if (includeHistoricalSecond)
            rows.Add(Entry("ACH_ALREADY", unlocked: true));

        return new AchievementSnapshot
        {
            ProviderId = "epic",
            SourceGameId = "Sugar",
            CoverageKey = "epic:0123456789abcdef0123456789abcdef",
            Coverage = AchievementCoverageStatus.Complete,
            Capabilities = AchievementProviderCapabilities.Snapshot |
                           AchievementProviderCapabilities.Progress |
                           AchievementProviderCapabilities.Rarity |
                           AchievementProviderCapabilities.CompleteCatalog,
            ReportedTotal = rows.Count,
            ReportedUnlocked = rows.Count(row => row.State.Unlocked),
            ObservedAtUtc = DateTimeOffset.UtcNow,
            Entries = rows,
        };
    }

    private static AchievementEntry Entry(string id, bool unlocked) => new()
    {
        Definition = new AchievementDefinition
        {
            ProviderId = "epic",
            SourceGameId = "Sugar",
            ExternalId = id,
            Name = id == "ACH_ONE" ? "One" : "Already unlocked",
            Description = "Test achievement",
        },
        State = new AchievementState
        {
            ExternalId = id,
            Unlocked = unlocked,
            UnlockedAtUtc = unlocked ? DateTimeOffset.Parse("2026-08-10T01:00:00Z") : null,
            ProgressCurrent = unlocked ? 100 : 0,
            ProgressTarget = 100,
            ObservedAtUtc = DateTimeOffset.UtcNow,
        },
    };

    private static AchievementEntry EntryAt(
        string id,
        bool unlocked,
        DateTimeOffset observedAtUtc) => Entry(id, unlocked) with
    {
        State = Entry(id, unlocked).State with { ObservedAtUtc = observedAtUtc },
    };

    private static AchievementSnapshot PartialSnapshot(
        DateTimeOffset observedAtUtc,
        int total,
        int unlocked,
        params AchievementEntry[] entries) => new()
    {
        ProviderId = "epic",
        SourceGameId = "Sugar",
        CoverageKey = "epic:0123456789abcdef0123456789abcdef",
        Coverage = AchievementCoverageStatus.Partial,
        Capabilities = AchievementProviderCapabilities.Snapshot |
                       AchievementProviderCapabilities.Progress,
        ReportedTotal = total,
        ReportedUnlocked = unlocked,
        ObservedAtUtc = observedAtUtc,
        Entries = entries,
    };

    private static string TempStatePath()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "exo-achievement-tests", Guid.NewGuid().ToString("N"));
        return Path.Combine(directory, "achievements.json");
    }

    private static void DeleteStateDirectory(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }

    private sealed class SequenceProvider(params AchievementSnapshot[] snapshots) : IAchievementProvider
    {
        private readonly AchievementSnapshot[] _snapshots = snapshots;
        private int _calls;

        public string Id => "epic";
        public StoreKind Store => StoreKind.Epic;
        public AchievementProviderCapabilities Capabilities =>
            AchievementProviderCapabilities.Snapshot |
            AchievementProviderCapabilities.Progress |
            AchievementProviderCapabilities.Rarity |
            AchievementProviderCapabilities.CompleteCatalog;
        public int CallCount => Volatile.Read(ref _calls);

        public bool Supports(GameEntry game) => game.Store == StoreKind.Epic;

        public Task<AchievementSnapshot> GetSnapshotAsync(
            GameEntry game, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _calls);
            var index = Math.Min(call - 1, _snapshots.Length - 1);
            return Task.FromResult(_snapshots[index]);
        }

        public async Task WaitForCallsAsync(int expected, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            while (CallCount < expected)
                await Task.Delay(5, cts.Token);
        }
    }

    private sealed class EchoProvider : IAchievementProvider
    {
        private long _clock;

        public string Id => "epic";
        public StoreKind Store => StoreKind.Epic;
        public AchievementProviderCapabilities Capabilities =>
            AchievementProviderCapabilities.Snapshot |
            AchievementProviderCapabilities.CompleteCatalog;

        public bool Supports(GameEntry game) => game.Store == StoreKind.Epic;

        public Task<AchievementSnapshot> GetSnapshotAsync(
            GameEntry game, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = game.LaunchTarget!;
            var tick = Interlocked.Increment(ref _clock);
            return Task.FromResult(new AchievementSnapshot
            {
                ProviderId = Id,
                SourceGameId = source,
                CoverageKey = "epic:0123456789abcdef0123456789abcdef",
                Coverage = AchievementCoverageStatus.Complete,
                Capabilities = Capabilities,
                ReportedTotal = 1,
                ReportedUnlocked = 0,
                ObservedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(tick),
                Entries =
                [
                    new AchievementEntry
                    {
                        Definition = new AchievementDefinition
                        {
                            ProviderId = Id,
                            SourceGameId = source,
                            ExternalId = "ACH_ONE",
                            Name = "One",
                        },
                        State = new AchievementState
                        {
                            ExternalId = "ACH_ONE",
                            ObservedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(tick),
                        },
                    },
                ],
            });
        }
    }

    private sealed class SteamZeroPlaceholderProvider : IAchievementProvider
    {
        public string Id => "steam";
        public StoreKind Store => StoreKind.Steam;
        public AchievementProviderCapabilities Capabilities => AchievementProviderCapabilities.Snapshot;
        public bool Supports(GameEntry game) => game.Store == StoreKind.Steam;

        public Task<AchievementSnapshot> GetSnapshotAsync(
            GameEntry game, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AchievementSnapshot
            {
                ProviderId = Id,
                SourceGameId = game.LaunchTarget!,
                CoverageKey = "steam:0123456789abcdef0123456789abcdef",
                Coverage = AchievementCoverageStatus.Partial,
                Capabilities = Capabilities,
                ReportedTotal = 0,
                ReportedUnlocked = 0,
                ObservedAtUtc = DateTimeOffset.UtcNow,
            });
    }

    private sealed class LegacySteamCommunityCatalogProvider : IAchievementProvider
    {
        public string Id => "steam";
        public StoreKind Store => StoreKind.Steam;
        public AchievementProviderCapabilities Capabilities => AchievementProviderCapabilities.Snapshot;
        public bool Supports(GameEntry game) => game.Store == StoreKind.Steam;

        public Task<AchievementSnapshot> GetSnapshotAsync(
            GameEntry game, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AchievementSnapshot
            {
                ProviderId = Id,
                SourceGameId = game.LaunchTarget!,
                CoverageKey = "steam:0123456789abcdef0123456789abcdef",
                Coverage = AchievementCoverageStatus.Partial,
                Capabilities = Capabilities,
                ReportedTotal = 37,
                ReportedUnlocked = 0,
                ObservedAtUtc = DateTimeOffset.UtcNow,
                Message = "Steam Community achievement data.",
            });
    }
}
