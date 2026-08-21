using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class ExoAccountTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly string[] MachineKeys =
    [
        "defaultInstallRoot",
        "launchOverrides",
        "appVersion",
        "copyPortableIntoLibrary",
        "allowResize",
        "trophyNotificationPositionX",
        "trophyNotificationPositionY",
        "profileAvatarImage",
        "profileBannerImage",
        "onboardingComplete",
        "closeStoreClientsAfterLaunch",
        "antiCheatSafeMode",
        "autoInstallRedistributables",
        "minimizeWhilePlaying",
        "favorites",
        "recent",
        "lastPlayed",
        "profileRoster",
        "profileHandle",
        "checkForUpdates",
        "trophyNotificationDurationSeconds",
    ];

    [Fact]
    public void Pkce_S256_MatchesRfc7636Appendix()
    {
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", ExoPkce.ChallengeS256(verifier));
    }

    [Fact]
    public void Pkce_Verifier_ComesFromCryptographicRng()
    {
        var first = ExoPkce.CreateVerifier();
        var second = ExoPkce.CreateVerifier();
        Assert.NotEqual(first, second);
        Assert.InRange(first.Length, 43, 128);
        Assert.Equal(ExoPkce.ChallengeS256(first).Length, ExoPkce.ChallengeS256(second).Length);
    }

    [Fact]
    public async Task LoopbackListener_BindsLoopbackOnly_AndClosesAfterUse()
    {
        using var listener = ExoLoopbackListener.Start();
        Assert.True(ExoLoopbackListener.IsLoopbackOnlyPrefix(listener.Prefix));
        Assert.StartsWith("http://127.0.0.1:", listener.Prefix, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0.0.0.0", listener.Prefix, StringComparison.Ordinal);
        Assert.True(listener.IsListening);
        Assert.Equal(ExoIdContract.LoopbackRedirectUri(listener.Port), listener.RedirectUriString);

        var lan = Array.Find(
            Dns.GetHostAddresses(Dns.GetHostName()),
            address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address));
        if (lan is not null)
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(lan, listener.Port);
            var winner = await Task.WhenAny(connect, Task.Delay(400));
            Assert.True(winner != connect || connect.IsFaulted || !client.Connected);
        }

        var state = ExoPkce.CreateState();
        var wait = listener.WaitForCallbackAsync(state, TimeSpan.FromSeconds(5), CancellationToken.None);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        var response = await http.GetAsync($"{listener.RedirectUri}?code=ok-code&state={Uri.EscapeDataString(state)}");
        var body = await response.Content.ReadAsStringAsync();
        var result = await wait;

        Assert.True(result.Ok);
        Assert.Equal("ok-code", result.Code);
        Assert.Equal(ExoLoopbackListener.CloseTabHtml, body);
        Assert.DoesNotContain("<script", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=", body, StringComparison.OrdinalIgnoreCase);
        Assert.False(listener.IsListening);
    }

    [Fact]
    public async Task LoopbackListener_IgnoresRootAndOnlyAcceptsTheExactCallbackPath()
    {
        using var listener = ExoLoopbackListener.Start();
        var state = ExoPkce.CreateState();
        var wait = listener.WaitForCallbackAsync(state, TimeSpan.FromSeconds(5), CancellationToken.None);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };

        var root = await http.GetAsync($"{listener.Prefix}?code=wrong&state={Uri.EscapeDataString(state)}");
        Assert.Equal(HttpStatusCode.NotFound, root.StatusCode);
        Assert.False(wait.IsCompleted);

        var callback = await http.GetAsync(
            $"{listener.RedirectUri}?code=right&state={Uri.EscapeDataString(state)}");
        var result = await wait;

        Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
        Assert.True(result.Ok);
        Assert.Equal("right", result.Code);
    }

    [Fact]
    public async Task LoopbackListener_RejectsMismatchedState_AndDoesNotReturnACode()
    {
        using var listener = ExoLoopbackListener.Start();
        var expected = ExoPkce.CreateState();
        var wait = listener.WaitForCallbackAsync(expected, TimeSpan.FromSeconds(5), CancellationToken.None);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        await http.GetAsync($"{listener.RedirectUri}?code=stolen&state=not-the-state");
        var result = await wait;

        Assert.False(result.Ok);
        Assert.True(result.StateMismatch);
        Assert.Null(result.Code);
        Assert.False(listener.IsListening);
    }

    [Fact]
    public async Task LoopbackListener_TreatsAccessDeniedAsAQuietOutcome()
    {
        using var listener = ExoLoopbackListener.Start();
        var state = ExoPkce.CreateState();
        var wait = listener.WaitForCallbackAsync(state, TimeSpan.FromSeconds(5), CancellationToken.None);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        await http.GetAsync($"{listener.RedirectUri}?error=access_denied&state={Uri.EscapeDataString(state)}");
        var result = await wait;

        Assert.False(result.Ok);
        Assert.Equal("access_denied", result.Error);
        Assert.Equal("Sign-in was cancelled.", result.Message);
        Assert.False(listener.IsListening);
    }

    [Fact]
    public async Task LoopbackListener_TimesOutWhenTheBrowserNeverReturns()
    {
        using var listener = ExoLoopbackListener.Start();
        var result = await listener.WaitForCallbackAsync(
            ExoPkce.CreateState(),
            TimeSpan.FromMilliseconds(250),
            CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("timed out", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(listener.IsListening);
    }

    [Fact]
    public void SessionStore_WritesDpapiBlobWithRestrictiveAcl_AndNeverPlaintext()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, ExoSessionStore.FileName);
            var store = new ExoSessionStore(path);
            var session = new ExoSession
            {
                AccessToken = "access-token-fixture",
                RefreshToken = "refresh-token-fixture",
                AccountId = "acc_1",
                Handle = "erix",
                Email = "user@example.com",
                Provider = "google",
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
            };

            store.Save(session);

            Assert.True(File.Exists(path));
            var blob = File.ReadAllBytes(path);
            var utf8 = Encoding.UTF8.GetString(blob);
            Assert.DoesNotContain("access-token-fixture", utf8, StringComparison.Ordinal);
            Assert.DoesNotContain("refresh-token-fixture", utf8, StringComparison.Ordinal);
            Assert.DoesNotContain("user@example.com", utf8, StringComparison.Ordinal);

            var sddl = ExoSessionFileAcl.ReadSddl(path);
            Assert.Contains(WindowsIdentity.GetCurrent().User!.Value, sddl, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("WD)", sddl, StringComparison.Ordinal);
            Assert.DoesNotContain("BU)", sddl, StringComparison.Ordinal);

            var loaded = store.TryLoad();
            Assert.NotNull(loaded);
            Assert.Equal("access-token-fixture", loaded!.AccessToken);
            Assert.Equal("erix", loaded.Handle);

            Assert.True(store.Delete());
            Assert.False(File.Exists(path));
            Assert.Null(store.TryLoad());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* */ }
        }
    }

    [Fact]
    public void Contract_ClientPathsAndAllowlistsMatchContractMarkdown()
    {
        var contract = File.ReadAllText(Path.Combine(RepoRoot(), "services", "exo-id", "CONTRACT.md"));
        var identityDir = Path.Combine(RepoRoot(), "ExoLauncher", "Services", "ExoIdentity");

        Assert.True(ExoIdContract.HttpTimeout < ExoIdContract.AuthCodeLifetime);
        Assert.Equal(TimeSpan.FromSeconds(60), ExoIdContract.AuthCodeLifetime);
        Assert.Equal(TimeSpan.FromMinutes(10), ExoIdContract.PendingLoginLifetime);
        Assert.Equal(TimeSpan.FromMinutes(5), ExoIdContract.MagicLinkLifetime);

        foreach (var path in ExoIdContract.DocumentedPaths)
        {
            Assert.Contains(path, contract);
            Assert.StartsWith("/v1/", path, StringComparison.Ordinal);
        }

        Assert.Equal("/v1/auth/start", ExoIdContract.AuthStartPath);
        Assert.Equal("/v1/auth/token", ExoIdContract.AuthTokenPath);
        Assert.Equal("/v1/auth/sign-out", ExoIdContract.AuthSignOutPath);
        Assert.Equal("/v1/me", ExoIdContract.MePath);
        Assert.Equal("/v1/handle", ExoIdContract.HandlePath);
        Assert.Equal("/v1/profile", ExoIdContract.ProfilePath);
        Assert.Equal("/v1/sync", ExoIdContract.SyncPath);
        Assert.Equal("/callback", ExoIdContract.CallbackPath);
        Assert.Equal("S256", ExoIdContract.CodeChallengeMethod);

        foreach (var key in ExoSyncedSettings.ProfileKeys)
            Assert.Contains("`" + key + "`", contract);
        foreach (var key in ExoSyncedSettings.SyncKeys)
            Assert.Contains(key, contract);

        Assert.Contains("`sortMode`", contract);
        Assert.Contains("trophyNotificationPosition", contract);
        Assert.DoesNotContain("profileName", ExoSyncedSettings.ProfileKeys);
        Assert.DoesNotContain("showLevel", ExoSyncedSettings.ProfileKeys);
        Assert.DoesNotContain("sortMode", ExoSyncedSettings.ProfileKeys);
        Assert.Contains("sortMode", ExoSyncedSettings.SyncKeys);
        Assert.Contains("trophyNotificationPosition", ExoSyncedSettings.SyncKeys);

        foreach (var code in ExoIdErrors.Catalog)
        {
            Assert.Contains("`" + code + "`", contract);
            Assert.False(string.IsNullOrWhiteSpace(ExoIdErrors.UserMessage(code)), code);
        }

        foreach (var file in Directory.GetFiles(identityDir, "*.cs"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("/oauth/authorize", text, StringComparison.Ordinal);
            Assert.DoesNotContain("/oauth/token", text, StringComparison.Ordinal);
            Assert.DoesNotContain("/oauth/revoke", text, StringComparison.Ordinal);
            Assert.DoesNotContain("/v1/account", text, StringComparison.Ordinal);
            Assert.DoesNotContain("grant_type", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SyncedSettings_SplitsProfileAndSync_AndDeniesMachineKeys()
    {
        foreach (var key in ExoSyncedSettings.ProfileSettingsKeys)
        {
            Assert.DoesNotContain(key, ExoSyncedSettings.Deny);
            Assert.DoesNotContain(key, ExoSyncedSettings.SyncSettingsKeys);
        }

        foreach (var key in ExoSyncedSettings.SyncSettingsKeys)
        {
            Assert.DoesNotContain(key, ExoSyncedSettings.Deny);
            Assert.DoesNotContain(key, ExoSyncedSettings.ProfileSettingsKeys);
        }

        foreach (var key in MachineKeys)
        {
            Assert.Contains(key, ExoSyncedSettings.Deny);
            Assert.DoesNotContain(key, ExoSyncedSettings.ProfileSettingsKeys);
            Assert.DoesNotContain(key, ExoSyncedSettings.SyncSettingsKeys);
        }

        var all = ExoSyncedSettings.AllSettingsKeys();
        Assert.NotEmpty(all);
        foreach (var key in all)
        {
            var profile = ExoSyncedSettings.ProfileSettingsKeys.Contains(key);
            var sync = ExoSyncedSettings.SyncSettingsKeys.Contains(key);
            var denied = ExoSyncedSettings.Deny.Contains(key);
            var buckets = (profile ? 1 : 0) + (sync ? 1 : 0) + (denied ? 1 : 0);
            Assert.True(buckets == 1, key + " must be on exactly one list.");
        }

        var settings = new AppSettings
        {
            ProfileName = "Erix",
            ProfilePronouns = "they",
            ProfileStatusText = "around",
            ProfileBio = "bio",
            ProfileAccent = "ash",
            ProfileLayout = "left",
            ProfileBannerHeight = "standard",
            ProfileShowcaseStyle = "grid",
            ProfileSections = ["facts"],
            ProfileHiddenSections = ["about"],
            ProfileShowcase = ["steam:1"],
            ProfileAvatarGameId = "steam:2",
            DefaultInstallRoot = @"D:\Games",
            AppVersion = "9.9.9",
            ProfileHandle = "not-synced",
            ProfileAvatarImage = "avatar.webp",
            OnboardingComplete = true,
            SortMode = "recent",
            TrophyNotificationsEnabled = false,
            TrophyNotificationPosition = "top-right",
            TrophyNotificationPositionX = 0.25,
            TrophyNotificationPreset = "exo",
            TrophyNotificationSound = true,
            TrophyNotificationSoundCue = "exo",
            Favorites = ["steam:1"],
            LaunchOverrides =
            {
                ["steam:1"] = new GameLaunchOverride { WorkingDirectory = @"D:\Games\Title" },
            },
        };

        var profileJson = ExoSyncedSettings.ExtractProfile(settings);
        using (var document = JsonDocument.Parse(profileJson.ToJsonString()))
        {
            foreach (var property in document.RootElement.EnumerateObject())
                Assert.Contains(property.Name, ExoSyncedSettings.ProfileKeys);
            Assert.Equal("Erix", document.RootElement.GetProperty("displayName").GetString());
            Assert.False(document.RootElement.TryGetProperty("showLevel", out _));
            Assert.False(document.RootElement.TryGetProperty("sortMode", out _));
            Assert.False(document.RootElement.TryGetProperty("profileHandle", out _));
            Assert.False(document.RootElement.TryGetProperty("defaultInstallRoot", out _));
        }

        var syncJson = ExoSyncedSettings.ExtractSync(settings);
        using (var document = JsonDocument.Parse(syncJson.ToJsonString()))
        {
            foreach (var property in document.RootElement.EnumerateObject())
                Assert.Contains(property.Name, ExoSyncedSettings.SyncKeys);
            Assert.Equal("recent", document.RootElement.GetProperty("sortMode").GetString());
            Assert.Equal("top-right", document.RootElement.GetProperty("trophyNotificationPosition").GetString());
            Assert.False(document.RootElement.TryGetProperty("trophyNotificationPositionX", out _));
            Assert.False(document.RootElement.TryGetProperty("displayName", out _));
            Assert.False(document.RootElement.TryGetProperty("profileName", out _));
        }

        var vector = ExoSyncedSettings.FieldVector(syncJson, "device-1", new DateTimeOffset(2026, 8, 18, 21, 0, 0, TimeSpan.Zero));
        using (var document = JsonDocument.Parse(vector.ToJsonString()))
        {
            Assert.Equal("device-1", document.RootElement.GetProperty("deviceId").GetString());
            var sort = document.RootElement.GetProperty("fields").GetProperty("sortMode");
            Assert.Equal("recent", sort.GetProperty("value").GetString());
            Assert.Equal("2026-08-18T21:00:00.000Z", sort.GetProperty("updatedAt").GetString());
        }
    }

    [Fact]
    public void SyncedSettings_ApplyIgnoresDenylistedKeys()
    {
        var settings = new AppSettings
        {
            DefaultInstallRoot = @"C:\Keep",
            ProfileName = "Local",
            SortMode = "name",
            Favorites = ["steam:keep"],
            TrophyNotificationPositionX = 1d,
            ProfileHandle = "mine",
        };
        using var incoming = JsonDocument.Parse(
            """{"displayName":"Remote","showLevel":false,"profileShowLevel":false,"sortMode":"recent","defaultInstallRoot":"E:\\Nope","favorites":["steam:evil"],"profileHandle":"sqatted","trophyNotificationPositionX":0.1}""");
        var filteredProfile = ExoSyncedSettings.FilterProfile(incoming.RootElement);
        Assert.False(filteredProfile.ContainsKey("showLevel"));
        ExoSyncedSettings.Apply(settings, incoming.RootElement);
        Assert.Equal("Remote", settings.ProfileName);
        Assert.Equal("recent", settings.SortMode);
        Assert.Equal(@"C:\Keep", settings.DefaultInstallRoot);
        Assert.Equal(["steam:keep"], settings.Favorites);
        Assert.Equal("mine", settings.ProfileHandle);
        Assert.Equal(1d, settings.TrophyNotificationPositionX);
    }

    [Fact]
    public void Handle_AllowsDisplayCasing_RequiresALetter_RefusesNonAscii()
    {
        Assert.True(ExoHandle.TryValidate("Erix", out var mixed, out _));
        Assert.Equal("Erix", mixed);
        Assert.Equal("erix", ExoHandle.Normalize(mixed));

        Assert.True(ExoHandle.TryValidate("erix_1", out var lower, out _));
        Assert.Equal("erix_1", lower);

        Assert.False(ExoHandle.TryValidate("12", out _, out var shortMessage));
        Assert.Contains("3–24", shortMessage, StringComparison.Ordinal);

        Assert.False(ExoHandle.TryValidate("123", out _, out var digits));
        Assert.Contains("letter", digits, StringComparison.OrdinalIgnoreCase);

        Assert.False(ExoHandle.TryValidate("еrix", out _, out var lookalike));
        Assert.Contains("ASCII", lookalike, StringComparison.Ordinal);

        Assert.False(ExoHandle.TryValidate("admin", out _, out var reserved));
        Assert.Contains("reserved", reserved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Errors_MapEveryContractCode()
    {
        Assert.Equal("That handle is taken.", ExoIdErrors.UserMessage("HANDLE_TAKEN"));
        Assert.Equal("Google sign-in is not set up.", ExoIdErrors.UserMessage("GOOGLE_NOT_CONFIGURED"));
        Assert.Equal("Email sign-in is not set up.", ExoIdErrors.UserMessage("EMAIL_NOT_CONFIGURED"));
        Assert.Equal("Sign-in expired. Try again.", ExoIdErrors.UserMessage("INVALID_GRANT"));
        Assert.Contains("30", ExoIdErrors.RateLimited(30));
        foreach (var code in ExoIdErrors.Catalog)
            Assert.False(string.IsNullOrWhiteSpace(ExoIdErrors.UserMessage(code)), code);
    }

    [Fact]
    public async Task SignedOut_LibraryScanStillReturnsGames()
    {
        var game = new GameEntry
        {
            Id = "steam:123",
            Title = "Known Steam Game",
            Store = StoreKind.Steam,
            Installed = true,
            LaunchTarget = "123",
            Path = Path.GetTempPath(),
        };
        var library = new LibraryService(new IStoreAdapter[] { new SignedOutLibraryAdapter(game) }, new SettingsService());
        var games = await library.GetLibraryAsync(force: true);
        Assert.Contains(games, item => item.Id == "steam:123");
        Assert.NotEmpty(games);
    }

    [Fact]
    public async Task GetAccount_WhenSignedOut_DoesNotNeedTheIdentityService()
    {
        var store = new ExoSessionStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin"));
        using var service = new ExoAccountService(
            store,
            new FakeIdentityHandler(),
            _ => false,
            () => throw new InvalidOperationException("listener must not start while signed out"),
            origin: "https://untrusted.example.invalid");
        var json = JsonSerializer.Serialize(await service.GetAccountAsync(), JsonOpts);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.False(root.GetProperty("signedIn").GetBoolean());
        Assert.False(root.GetProperty("configured").GetBoolean());
        Assert.Empty(root.GetProperty("providers").EnumerateArray());
        Assert.DoesNotContain("listener must not start", json, StringComparison.Ordinal);

        var configuredStore = new ExoSessionStore(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin"));
        using var configuredService = new ExoAccountService(
            configuredStore,
            new FakeIdentityHandler(),
            _ => false,
            () => throw new InvalidOperationException("listener must not start while signed out"),
            origin: "http://127.0.0.1");
        using var configuredDocument = JsonDocument.Parse(
            JsonSerializer.Serialize(await configuredService.GetAccountAsync(), JsonOpts));
        Assert.True(configuredDocument.RootElement.GetProperty("configured").GetBoolean());
    }

    [Fact]
    public async Task AccountProviders_DeriveFromLiveHealthCapabilities()
    {
        var store = new ExoSessionStore(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin"));
        var handler = new FakeIdentityHandler
        {
            GoogleConfigured = false,
            EmailConfigured = false,
        };
        using var service = new ExoAccountService(
            store,
            handler,
            _ => false,
            () => throw new InvalidOperationException("listener must not start while signed out"),
            origin: "http://127.0.0.1:8787");

        using var unavailable = JsonDocument.Parse(
            JsonSerializer.Serialize(await service.GetAccountAsync(), JsonOpts));

        Assert.True(unavailable.RootElement.GetProperty("configured").GetBoolean());
        Assert.Empty(unavailable.RootElement.GetProperty("providers").EnumerateArray());
        Assert.Equal(1, handler.HealthCallCount);

        handler.GoogleConfigured = true;
        using var available = JsonDocument.Parse(
            JsonSerializer.Serialize(await service.GetAccountAsync(), JsonOpts));
        Assert.Equal(
            ["google"],
            available.RootElement.GetProperty("providers")
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .ToArray());
        Assert.Equal(2, handler.HealthCallCount);
    }

    [Fact]
    public void IdentityOrigin_AllowsLoopbackAndOnlyThePinnedProductionOrigin()
    {
        const string pinned = "https://identity.example.invalid";

        Assert.True(ExoIdContract.IsAllowedOrigin("http://127.0.0.1:8787", pinned));
        Assert.True(ExoIdContract.IsAllowedOrigin("http://[::1]:8787", pinned));
        Assert.True(ExoIdContract.IsAllowedOrigin(pinned, pinned));
        Assert.False(ExoIdContract.IsAllowedOrigin("https://other.example.invalid", pinned));
        Assert.False(ExoIdContract.IsAllowedOrigin("http://identity.example.invalid", pinned));
        Assert.False(ExoIdContract.IsAllowedOrigin("https://identity.example.invalid/path", pinned));

        Assert.Equal("http://127.0.0.1:8787", ExoIdContract.ResolveOrigin("http://127.0.0.1:8787"));
        Assert.Throws<InvalidOperationException>(() =>
            ExoIdContract.ResolveOrigin("https://other.example.invalid"));

        Assert.Equal("https://exo-id.exo-erix.workers.dev", ExoIdContract.ProductionOrigin);
        Assert.Equal(
            "wss://exo-id.exo-erix.workers.dev/v1/presence/socket",
            ExoIdContract.ResolvePresenceSocketUri(ExoIdContract.ProductionOrigin)?.AbsoluteUri);
        Assert.Equal(
            "ws://127.0.0.1:8787/v1/presence/socket",
            ExoIdContract.ResolvePresenceSocketUri("http://127.0.0.1:8787")?.AbsoluteUri);
    }

    [Fact]
    public void SessionStore_ReportsWhenWindowsPreventsDeletion()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-auth-delete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, ExoSessionStore.FileName);
        try
        {
            var store = new ExoSessionStore(path);
            store.Save(new ExoSession
            {
                AccessToken = "locked-token-fixture",
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
            });

            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.False(store.Delete());
                Assert.True(File.Exists(path));
            }

            Assert.True(store.Delete());
            Assert.False(File.Exists(path));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* */ }
        }
    }

    [Fact]
    public async Task SignIn_RejectsApple_AndUnconfiguredOrigin()
    {
        var store = new ExoSessionStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin"));
        using var service = new ExoAccountService(
            store,
            new FakeIdentityHandler(),
            _ => throw new InvalidOperationException("browser"),
            () => throw new InvalidOperationException("listener"),
            origin: "https://untrusted.example.invalid");

        var apple = JsonSerializer.Serialize(await service.SignInAsync("apple", settings: null), JsonOpts);
        Assert.Contains("\"ok\":false", apple, StringComparison.Ordinal);
        Assert.Contains("Apple sign-in is not available.", apple, StringComparison.Ordinal);

        var missing = JsonSerializer.Serialize(await service.SignInAsync("google", settings: null), JsonOpts);
        Assert.Contains("\"ok\":false", missing, StringComparison.Ordinal);
        Assert.Contains("not configured", missing, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SignIn_StartsThenExchangesPkce_AndKeepsTokensOffTheBridgePayload()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-auth-flow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new ExoSessionStore(Path.Combine(root, ExoSessionStore.FileName));
        var handler = new FakeIdentityHandler();
        string? opened = null;
        using var service = new ExoAccountService(
            store,
            handler,
            url =>
            {
                opened = url;
                return true;
            },
            ExoLoopbackListener.Start,
            origin: "http://127.0.0.1");

        var result = await service.SignInAsync("google", settings: null);
        var json = JsonSerializer.Serialize(result, JsonOpts);

        Assert.Contains("\"ok\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"signedIn\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"handle\":\"Erix\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.AccessToken, json, StringComparison.Ordinal);
        Assert.Contains("/v1/auth/continue/", opened, StringComparison.Ordinal);
        Assert.Equal(ExoIdContract.AuthStartPath, handler.LastStartPath);
        Assert.Equal(ExoIdContract.AuthTokenPath, handler.LastTokenPath);
        Assert.Equal(1, handler.TokenCallCount);
        Assert.Equal("S256", handler.LastCodeChallengeMethod);
        Assert.False(string.IsNullOrEmpty(handler.LastCodeVerifier));
        Assert.Contains("\"codeVerifier\"", handler.LastTokenBody, StringComparison.Ordinal);
        Assert.DoesNotContain("grant_type", handler.LastTokenBody, StringComparison.Ordinal);
        Assert.DoesNotContain("client_id", handler.LastTokenBody, StringComparison.Ordinal);
        Assert.Equal("application/json", handler.LastTokenContentType);
        Assert.True(File.Exists(store.Path));
        Assert.DoesNotContain(handler.AccessToken, Encoding.UTF8.GetString(File.ReadAllBytes(store.Path)), StringComparison.Ordinal);

        var loaded = store.TryLoad();
        Assert.NotNull(loaded);
        Assert.True(string.IsNullOrEmpty(loaded!.RefreshToken));

        using var accountDocument = JsonDocument.Parse(
            JsonSerializer.Serialize(await service.GetAccountAsync(), JsonOpts));
        Assert.True(accountDocument.RootElement.GetProperty("signedIn").GetBoolean());
        Assert.True(accountDocument.RootElement.GetProperty("configured").GetBoolean());
        Assert.Equal(
            ["google", "email"],
            accountDocument.RootElement.GetProperty("providers")
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .ToArray());

        var settingsPath = Path.Combine(root, "settings.json");
        if (File.Exists(settingsPath))
        {
            var settingsText = File.ReadAllText(settingsPath);
            Assert.DoesNotContain(handler.AccessToken, settingsText, StringComparison.Ordinal);
        }

        var signOut = JsonSerializer.Serialize(await service.SignOutAsync(), JsonOpts);
        Assert.Contains("\"ok\":true", signOut, StringComparison.Ordinal);
        Assert.True(handler.Revoked);
        Assert.Equal(ExoIdContract.AuthSignOutPath, handler.LastSignOutPath);
        Assert.False(File.Exists(store.Path));
        try { Directory.Delete(root, recursive: true); } catch { /* */ }
    }

    [Fact]
    public async Task SignIn_EmailDoesNotOpenTheBrowser()
    {
        var store = new ExoSessionStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin"));
        var handler = new FakeIdentityHandler();
        var opened = false;
        using var service = new ExoAccountService(
            store,
            handler,
            _ =>
            {
                opened = true;
                return true;
            },
            ExoLoopbackListener.Start,
            origin: "http://127.0.0.1");

        var json = JsonSerializer.Serialize(
            await service.SignInAsync("email", settings: null, CancellationToken.None, "user@example.com"),
            JsonOpts);
        Assert.Contains("\"ok\":true", json, StringComparison.Ordinal);
        Assert.False(opened);
        Assert.Equal("email", handler.LastProvider);
        Assert.Equal("user@example.com", handler.LastEmail);
        Assert.Equal(202, handler.LastStartStatus);
    }

    [Fact]
    public async Task ReserveHandle_MapsTaken_AndPutsDisplayCasing()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-auth-handle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new ExoSessionStore(Path.Combine(root, ExoSessionStore.FileName));
        store.Save(new ExoSession
        {
            AccessToken = "access-token-fixture",
            AccountId = "acc_1",
            Email = "user@example.com",
            Provider = "google",
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
        });
        var handler = new FakeIdentityHandler { HandleTaken = true };
        using var service = new ExoAccountService(
            store,
            handler,
            _ => false,
            () => throw new InvalidOperationException("listener"),
            origin: "http://127.0.0.1");

        var taken = JsonSerializer.Serialize(await service.ReserveHandleAsync("Erix", settings: null), JsonOpts);
        Assert.Contains("\"ok\":false", taken, StringComparison.Ordinal);
        Assert.Contains("That handle is taken.", taken, StringComparison.Ordinal);
        Assert.Equal(ExoIdContract.HandlePath, handler.LastHandlePath);
        Assert.Equal("PUT", handler.LastHandleMethod);
        Assert.Equal("Erix", handler.LastHandle);
        try { Directory.Delete(root, recursive: true); } catch { /* */ }
    }

    [Fact]
    public void CriticalPaths_DoNotCallTheAccountService()
    {
        var root = RepoRoot();
        var library = File.ReadAllText(Path.Combine(root, "ExoLauncher", "Services", "LibraryService.cs"));
        var launch = File.ReadAllText(Path.Combine(root, "ExoLauncher", "Services", "LaunchOrchestrator.cs"));
        var app = File.ReadAllText(Path.Combine(root, "ExoLauncher", "App.xaml.cs"));
        var services = File.ReadAllText(Path.Combine(root, "ExoLauncher", "Services", "AppServices.cs"));
        var bridge = File.ReadAllText(Path.Combine(root, "ExoLauncher", "Services", "WebHostBridge.cs"));

        foreach (var text in new[] { library, launch, app, services })
        {
            Assert.DoesNotContain("ExoAccountService", text, StringComparison.Ordinal);
            Assert.DoesNotContain("account.signIn", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ExoSessionStore", text, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("_account", Slice(bridge, "private async Task<object> LibraryGetAsync", "private async Task<object> GameGetAsync"));
        Assert.DoesNotContain("_account", Slice(bridge, "private async Task<object> GameLaunchAsync", "private async Task<object> GameStopAsync"));
        Assert.DoesNotContain("_account", Slice(bridge, "private async Task<object> GameInstallAsync", "private async Task<object> GameUpdateAsync"));
        Assert.DoesNotContain("_account", Slice(bridge, "private async Task<object> GameUpdateAsync", "private async Task<object> GameUninstallAsync"));
        Assert.DoesNotContain("_account", Slice(bridge, "private async Task<object> GameUninstallAsync", "private async Task<object> GameRepairAsync"));

        Assert.Contains("\"account.get\" =>", bridge, StringComparison.Ordinal);
        Assert.Contains("\"account.signIn\" =>", bridge, StringComparison.Ordinal);
        Assert.Contains("\"account.signOut\" =>", bridge, StringComparison.Ordinal);
        Assert.Contains("\"account.reserveHandle\" =>", bridge, StringComparison.Ordinal);
        Assert.Contains("\"account.getProfile\" =>", bridge, StringComparison.Ordinal);
        Assert.Contains("\"account.setProfile\" =>", bridge, StringComparison.Ordinal);

        foreach (var file in Directory.GetFiles(Path.Combine(root, "ExoLauncher", "Services", "ExoIdentity"), "*.cs"))
        {
            var text = File.ReadAllText(file);
            foreach (var call in new[] { "AppLog.Info(", "AppLog.Warn(", "AppLog.Error(", "AppLog.Debug(" })
            {
                var index = 0;
                while ((index = text.IndexOf(call, index, StringComparison.Ordinal)) >= 0)
                {
                    var end = text.IndexOf(';', index);
                    var line = end > index ? text[index..end] : text[index..Math.Min(text.Length, index + 160)];
                    Assert.DoesNotContain("AccessToken", line, StringComparison.Ordinal);
                    Assert.DoesNotContain("RefreshToken", line, StringComparison.Ordinal);
                    Assert.DoesNotContain("access_token", line, StringComparison.Ordinal);
                    index += call.Length;
                }
            }
        }
    }

    private static string Slice(string text, string startMarker, string endMarker)
    {
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        var end = text.IndexOf(endMarker, start + 1, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, startMarker);
        return text[start..end];
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ExoLauncher.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private sealed class SignedOutLibraryAdapter(GameEntry game) : IStoreAdapter
    {
        public StoreKind Store => StoreKind.Steam;
        public string Id => "steam";
        public string DisplayName => "Steam";
        public bool IsAgentPresent() => true;
        public Task<AuthResult> AuthenticateAsync(CancellationToken ct = default) =>
            Task.FromResult(new AuthResult { Ok = true });
        public Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GameEntry>>([game]);
        public Task<InstallResult> InstallAsync(
            GameEntry gameEntry, string? installPath, IProgress<InstallProgress>? progress, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = false, Message = "not used" });
        public Task<InstallResult> UpdateAsync(
            GameEntry gameEntry, IProgress<InstallProgress>? progress, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = false, Message = "not used" });
        public Task<LaunchResult> LaunchAsync(GameEntry gameEntry, LaunchOptions options, CancellationToken ct = default) =>
            Task.FromResult(new LaunchResult { Ok = false, Message = "not used" });
        public Task<InstallResult> UninstallAsync(GameEntry gameEntry, CancellationToken ct = default) =>
            Task.FromResult(new InstallResult { Ok = false, Message = "not used" });
        public InstallProgress GetDownloadProgress(string gameId) =>
            new() { GameId = gameId, Phase = InstallPhase.Idle };
        public Task CleanupAfterExitAsync(GameEntry gameEntry, LaunchOptions options, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeIdentityHandler : HttpMessageHandler
    {
        public string AccessToken { get; } = "access-token-fixture";
        public string ExpectedCode { get; } = "ok-code";
        public string? LastStartPath { get; private set; }
        public string? LastTokenPath { get; private set; }
        public string? LastSignOutPath { get; private set; }
        public string? LastHandlePath { get; private set; }
        public string? LastHandleMethod { get; private set; }
        public string? LastHandle { get; private set; }
        public string? LastCodeVerifier { get; private set; }
        public string? LastCodeChallengeMethod { get; private set; }
        public string? LastTokenBody { get; private set; }
        public string? LastTokenContentType { get; private set; }
        public string? LastProvider { get; private set; }
        public string? LastEmail { get; private set; }
        public int LastStartStatus { get; private set; }
        public int TokenCallCount { get; private set; }
        public bool Revoked { get; private set; }
        public bool HandleTaken { get; init; }
        public bool GoogleConfigured { get; set; } = true;
        public bool EmailConfigured { get; set; } = true;
        public int HealthCallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath.TrimEnd('/') ?? "";
            if (path.Equals(ExoIdContract.HealthPath, StringComparison.OrdinalIgnoreCase))
            {
                HealthCallCount++;
                return Json(200,
                    "{\"ok\":true,\"service\":\"exo-id\",\"capabilities\":{\"providers\":{\"google\":" +
                    (GoogleConfigured ? "true" : "false") + ",\"email\":" +
                    (EmailConfigured ? "true" : "false") +
                    "},\"profiles\":true,\"friends\":true,\"media\":true,\"presence\":true}}");
            }
            if (path.Equals(ExoIdContract.AuthStartPath, StringComparison.OrdinalIgnoreCase))
            {
                LastStartPath = path;
                var body = await ReadJsonAsync(request).ConfigureAwait(false);
                LastProvider = body["provider"]?.GetValue<string>();
                LastEmail = body["email"]?.GetValue<string>();
                LastCodeChallengeMethod = body["codeChallengeMethod"]?.GetValue<string>();
                var redirect = body["redirectUri"]?.GetValue<string>();
                var state = body["state"]?.GetValue<string>();
                CompleteLoopback(redirect, state);
                var origin = request.RequestUri!.GetLeftPart(UriPartial.Authority);
                if (LastProvider == "email")
                {
                    LastStartStatus = 202;
                    return Json(202, """{"loginId":"login1","expiresIn":600,"authorizationUrl":null}""");
                }

                LastStartStatus = 200;
                return Json(200,
                    "{\"loginId\":\"login1\",\"expiresIn\":600,\"authorizationUrl\":\"" +
                    origin + ExoIdContract.AuthContinuePrefix + "/login1\"}");
            }

            if (path.Equals(ExoIdContract.AuthTokenPath, StringComparison.OrdinalIgnoreCase))
            {
                TokenCallCount++;
                LastTokenPath = path;
                LastTokenContentType = request.Content?.Headers.ContentType?.MediaType;
                LastTokenBody = request.Content is null
                    ? ""
                    : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var body = string.IsNullOrWhiteSpace(LastTokenBody)
                    ? new JsonObject()
                    : JsonNode.Parse(LastTokenBody) as JsonObject ?? new JsonObject();
                LastCodeVerifier = body["codeVerifier"]?.GetValue<string>();
                if (body["code"]?.GetValue<string>() != ExpectedCode)
                    return Json(400, """{"error":{"code":"INVALID_GRANT","message":"code is invalid or expired."}}""");

                return Json(200,
                    "{\"tokenType\":\"Bearer\",\"accessToken\":\"" + AccessToken +
                    "\",\"expiresIn\":604800,\"expiresAt\":\"" +
                    DateTimeOffset.UtcNow.AddDays(7).ToString("o") +
                    "\",\"user\":{\"id\":\"acc_1\",\"name\":\"\",\"email\":\"user@example.com\",\"handle\":{\"display\":\"Erix\",\"normalized\":\"erix\"}}}");
            }

            if (path.Equals(ExoIdContract.AuthSignOutPath, StringComparison.OrdinalIgnoreCase))
            {
                LastSignOutPath = path;
                Revoked = true;
                return Json(200, """{"ok":true}""");
            }

            if (path.Equals(ExoIdContract.MePath, StringComparison.OrdinalIgnoreCase))
                return Json(200,
                    """{"id":"acc_1","name":"","email":"user@example.com","handle":{"display":"Erix","normalized":"erix"},"profile":{},"session":{"id":"s1","expiresAt":"2026-08-25T00:00:00.000Z"}}""");

            if (path.Equals(ExoIdContract.ProfilePath, StringComparison.OrdinalIgnoreCase))
                return Json(200, """{"values":{"displayName":"Erix","accent":"ash"},"fields":{}}""");

            if (path.Equals(ExoIdContract.SyncPath, StringComparison.OrdinalIgnoreCase))
                return Json(200, """{"values":{"sortMode":"recent"},"fields":{}}""");

            if (path.Equals(ExoIdContract.HandlePath, StringComparison.OrdinalIgnoreCase))
            {
                LastHandlePath = path;
                LastHandleMethod = request.Method.Method;
                if (request.Method == HttpMethod.Put)
                {
                    var body = await ReadJsonAsync(request).ConfigureAwait(false);
                    LastHandle = body["handle"]?.GetValue<string>();
                    if (HandleTaken)
                        return Json(409, """{"error":{"code":"HANDLE_TAKEN","message":"That handle is taken."}}""");
                    return Json(200,
                        "{\"handle\":{\"display\":\"" + (LastHandle ?? "Erix") +
                        "\",\"normalized\":\"erix\",\"claimedAt\":\"2026-08-18T00:00:00.000Z\",\"changedAt\":\"2026-08-18T00:00:00.000Z\"}}");
                }

                return Json(200, """{"handle":{"display":"Erix","normalized":"erix"}}""");
            }

            return Json(404, """{"error":{"code":"NOT_FOUND","message":"Not found."}}""");
        }

        private void CompleteLoopback(string? redirect, string? state)
        {
            if (string.IsNullOrEmpty(redirect) || string.IsNullOrEmpty(state))
                return;
            _ = Task.Run(async () =>
            {
                await Task.Delay(80);
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
                await http.GetAsync($"{redirect}?code={Uri.EscapeDataString(ExpectedCode)}&state={Uri.EscapeDataString(state)}");
            });
        }

        private static async Task<JsonObject> ReadJsonAsync(HttpRequestMessage request)
        {
            var text = request.Content is null ? "" : await request.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
                return new JsonObject();
            return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
        }

        private static HttpResponseMessage Json(int status, string body) =>
            new((HttpStatusCode)status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
    }
}
