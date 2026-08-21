using ExoLauncher.Adapters;
using ExoLauncher.Models;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class EpicPlaytimeTests
{
    [Fact]
    public async Task Cache_RefreshesOffTheInitialRead_AndKeepsLastGoodSnapshotAfterFailure()
    {
        var now = DateTimeOffset.Parse("2026-08-11T12:00:00Z");
        var calls = 0;
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new EpicPlaytimeCache(
            async _ =>
            {
                var call = Interlocked.Increment(ref calls);
                if (call == 1)
                {
                    await releaseFirst.Task;
                    return new EpicPlaytimeFetchResult(true,
                        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Sugar"] = 120 });
                }
                return EpicPlaytimeFetchResult.Failed;
            },
            ttl: TimeSpan.FromMinutes(10),
            failureRetry: TimeSpan.FromMinutes(1),
            utcNow: () => now);

        var firstRefresh = cache.RefreshIfStaleAsync();

        // The library can read the old snapshot while the remote call is still pending.
        Assert.False(firstRefresh.IsCompleted);
        Assert.Empty(cache.Snapshot());
        releaseFirst.TrySetResult();
        await firstRefresh;
        Assert.Equal(120, cache.Snapshot()["Sugar"]);

        // A failed refresh never erases the last verified Epic playtime.
        now += TimeSpan.FromMinutes(11);
        await cache.RefreshIfStaleAsync();
        Assert.Equal(120, cache.Snapshot()["Sugar"]);
        Assert.Equal(2, Volatile.Read(ref calls));

        // Failure backoff prevents every follow-up library scan from retrying the endpoint.
        await cache.RefreshIfStaleAsync();
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task Cache_QuarantinesLastGoodMinutesWhenTheActiveEpicAccountChanges()
    {
        var call = 0;
        var cache = new EpicPlaytimeCache(
            _ => Task.FromResult(++call == 1
                ? new EpicPlaytimeFetchResult(true,
                    new Dictionary<string, int> { ["Sugar"] = 120 }, "account-a")
                : new EpicPlaytimeFetchResult(true,
                    new Dictionary<string, int> { ["Sugar"] = 900 }, "account-b")),
            TimeSpan.FromHours(1), TimeSpan.FromMinutes(1));

        await cache.RefreshIfStaleAsync("account-a");
        Assert.Equal(120, cache.Snapshot("account-a")["Sugar"]);

        await cache.RefreshIfStaleAsync("account-b");

        Assert.Empty(cache.Snapshot("account-a"));
        Assert.Equal(900, cache.Snapshot("account-b")["Sugar"]);
    }

    [Fact]
    public void ParseMinutesJson_ReadsEpicSecondsByArtifact()
    {
        const string json = """
            [
              { "accountId": "private", "artifactId": "Sugar", "totalTime": 678429 },
              { "accountId": "private", "artifactId": "OtherGame", "totalTime": 120 }
            ]
            """;

        var result = EpicPlaytime.ParseMinutesJson(json);

        Assert.Equal(11_307, result["Sugar"]);
        Assert.Equal(2, result["OtherGame"]);
    }

    [Fact]
    public void ParseMinutesJson_DoesNotShiftALifetimeHourForTimezone()
    {
        var json = """[{ "artifactId": "Sugar", "totalTime": 710220 }]""";
        Assert.Equal(11_837, EpicPlaytime.ParseMinutesJson(json)["Sugar"]);
    }

    [Fact]
    public void ParseMinutesJson_AcceptsWrappedRowsAndKeepsLargestValue()
    {
        const string json = """
            {
              "playtimeList": [
                { "artifactId": "Sugar", "totalTime": "600" },
                { "artifactId": "sugar", "totalTime": 1200 },
                { "artifactId": "Bad", "totalTime": -1 },
                { "artifactId": "Missing" }
              ]
            }
            """;

        var result = EpicPlaytime.ParseMinutesJson(json);

        Assert.Single(result);
        Assert.Equal(20, result["Sugar"]);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"errorCode\":\"errors.com.epicgames.common.not_found\"}")]
    [InlineData("not json")]
    public void TryParseMinutesJson_RejectsMalformedOrNonPlaytimeSuccessBodies(string json)
    {
        var valid = EpicPlaytime.TryParseMinutesJson(json, out var minutes);

        Assert.False(valid);
        Assert.Empty(minutes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"account_id\":\"account\",\"access_token\":\"token\",\"token_type\":\"basic\"}")]
    [InlineData("{\"account_id\":\"account\\nheader\",\"access_token\":\"token\",\"token_type\":\"bearer\"}")]
    public void ParseSessionJson_RejectsMissingOrUnsafeCredentials(string json)
    {
        Assert.Null(EpicPlaytime.ParseSessionJson(json));
    }

    [Fact]
    public void LegendaryUserJsonCandidates_IncludeHeroicRoamingAndExoAppDataSessions()
    {
        var paths = EpicPlaytime.LegendaryUserJsonCandidates().ToList();

        Assert.Contains(paths, path =>
            path.Contains(Path.Combine("heroic", "legendaryConfig", "legendary", "user.json"),
                StringComparison.OrdinalIgnoreCase));
        Assert.Contains(paths, path =>
            path.Contains(Path.Combine(".config", "legendary", "user.json"),
                StringComparison.OrdinalIgnoreCase));
        Assert.Contains(paths, path =>
            path.Contains(Path.Combine("legendary", "user.json"), StringComparison.OrdinalIgnoreCase) &&
            path.Contains("Roaming", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(paths, path =>
            path.Contains(Path.Combine("ExoLauncher", "legendary", "user.json"),
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParseSessionJson_ReadsRefreshTokenAndExpiry()
    {
        const string json = """
            {
              "account_id": "account-id",
              "access_token": "expired-access",
              "refresh_token": "refresh-secret",
              "token_type": "bearer",
              "expires_at": "2020-01-01T00:00:00.000Z"
            }
            """;

        var session = EpicPlaytime.ParseSessionJson(json);

        Assert.NotNull(session);
        Assert.Equal("refresh-secret", session!.RefreshToken);
        Assert.True(EpicPlaytime.AccessTokenNeedsRefresh(session, DateTimeOffset.Parse("2026-08-13T00:00:00Z")));
    }

    [Fact]
    public void TryParseRememberMeData_ReadsLegacyEglJsonArray()
    {
        const string raw = """
            [{"email":"friend@example.com","token":"egl-refresh-token"}]
            """;

        Assert.True(EpicPlaytime.TryParseRememberMeData(raw, out var email, out var refresh));
        Assert.Equal("friend@example.com", email);
        Assert.Equal("egl-refresh-token", refresh);
    }

    [Fact]
    public void EglRememberMeIniCandidates_IncludeWindowsEditor()
    {
        var paths = EpicPlaytime.EglRememberMeIniCandidates().ToList();

        Assert.Contains(paths, path =>
            path.Contains(Path.Combine("EpicGamesLauncher", "Saved", "Config", "WindowsEditor",
                "GameUserSettings.ini"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(paths, path =>
            path.Contains(Path.Combine("EpicGamesLauncher", "Saved", "Config", "Windows",
                "GameUserSettings.ini"), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParseSessionJson_AcceptsLegendaryBearerShape()
    {
        const string json = """
            {
              "account_id": "account-id",
              "access_token": "secret-token-value",
              "token_type": "bearer"
            }
            """;

        var session = EpicPlaytime.ParseSessionJson(json);

        Assert.NotNull(session);
        Assert.Equal("account-id", session!.AccountId);
        Assert.Equal("secret-token-value", session.AccessToken);
    }

    [Fact]
    public void ParseSessionJson_AcceptsHeroicCamelCaseBearerShape()
    {
        const string json = """
            {
              "accountId": "account-id",
              "accessToken": "secret-token-value",
              "tokenType": "bearer"
            }
            """;

        var session = EpicPlaytime.ParseSessionJson(json);

        Assert.NotNull(session);
        Assert.Equal("account-id", session!.AccountId);
        Assert.Equal("secret-token-value", session.AccessToken);
    }

    [Fact]
    public void TryReadRememberMeFromIni_RejectsEncryptedEglData()
    {
        const string ini = """
            [RememberMe]
            Enable=True
            Data="CtpScvJAe96u+DECMq3TpXd8vfrAKOJfc3zDEXupNIVL4b/mJOjhk18azxykW8Yg"
            """;

        Assert.False(EpicPlaytime.TryReadRememberMeFromIni(ini, out var email, out var refresh));
        Assert.Null(email);
        Assert.Null(refresh);
    }

    [Fact]
    public void ParseSessionJson_AcceptsEg1TokenType()
    {
        const string json = """
            {
              "account_id": "account-id",
              "access_token": "secret-token-value",
              "token_type": "eg1"
            }
            """;

        var session = EpicPlaytime.ParseSessionJson(json);
        Assert.NotNull(session);
        Assert.Equal("account-id", session!.AccountId);
    }

    [Fact]
    public void PersistMinutes_RoundTripsForMatchingAccountScopeOnly()
    {
        var scope = "abc123def4567890abcd";
        EpicPlaytime.PersistMinutes(scope, new Dictionary<string, int> { ["Fortnite"] = 344, ["Sugar"] = 11_567 });

        var loaded = EpicPlaytime.LoadPersistedMinutes(scope);
        Assert.Equal(344, loaded["Fortnite"]);
        Assert.Equal(11_567, loaded["Sugar"]);
        Assert.Empty(EpicPlaytime.LoadPersistedMinutes("ffffffffffffffffffff"));
    }

    [Fact]
    public void Apply_AddsNativeEpicTimeToFortniteByTitleOnCatalogSearchHit()
    {
        var fortnite = new GameEntry
        {
            Id = "epic:catalog:fortnite",
            Title = "Fortnite",
            Store = StoreKind.Epic,
            Installed = false,
            CanInstall = true,
        };

        var result = EpicPlaytime.Apply(
            [fortnite],
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Fortnite"] = 344,
            });

        Assert.Equal(344, result[0].PlaytimeMinutes);
    }

    [Fact]
    public void Apply_AddsNativeEpicTimeToFortniteByIdWhenLaunchTargetMissing()
    {
        var fortnite = new GameEntry
        {
            Id = "epic:Fortnite",
            Title = "Fortnite",
            Store = StoreKind.Epic,
            Installed = true,
        };

        var result = EpicPlaytime.Apply(
            [fortnite],
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Fortnite"] = 4_800,
            });

        Assert.Equal(4_800, result[0].PlaytimeMinutes);
    }

    [Fact]
    public void Apply_AddsNativeEpicTimeToRocketLeagueByAppName()
    {
        var rocket = new GameEntry
        {
            Id = "epic:Sugar",
            Title = "Rocket League",
            Store = StoreKind.Epic,
            Installed = true,
            LaunchTarget = "Sugar",
            Path = @"C:\Games\rocketleague",
        };
        var steam = new GameEntry
        {
            Id = "steam:252950",
            Title = "Rocket League",
            Store = StoreKind.Steam,
            Installed = false,
            LaunchTarget = "252950",
            PlaytimeMinutes = 50,
        };

        var result = EpicPlaytime.Apply(
            [rocket, steam],
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["sugar"] = 11_307,
            });

        Assert.Equal(11_307, result[0].PlaytimeMinutes);
        Assert.Equal(rocket.Path, result[0].Path);
        Assert.Equal(50, result[1].PlaytimeMinutes);
    }
}
