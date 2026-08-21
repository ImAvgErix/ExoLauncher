using System.Text.Json;
using ExoLauncher.Adapters;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class StoreDiagnosticsTests
{
    [Fact]
    public void CheckStoresLocal_UsesPresenceOnlyAndNeverScansTheLibrary()
    {
        var adapter = new LocalProbeAdapter();
        var library = new LibraryService([adapter], new SettingsService());

        var result = library.CheckStoresLocal();

        Assert.Equal("complete", result.state);
        Assert.Equal("local_check_complete", result.code);
        Assert.Equal(0, adapter.LibraryReads);
        var store = Assert.Single(result.stores);
        Assert.Equal("present", store.client);
        Assert.Equal("present", store.backend);
        Assert.Contains(store.readiness, new[] { "ready", "limited" });
    }

    [Fact]
    public void AmazonLocalSession_IsReportedWithoutExposingTheAccountId()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-nile-check-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(root, "nile");
        Directory.CreateDirectory(config);
        const string accountId = "amzn1.account.secret-test-value";
        File.WriteAllText(
            Path.Combine(config, "current_user.json"),
            $$"""{"user_id":"{{accountId}}","name":"Ada"}""");
        var previous = Environment.GetEnvironmentVariable("NILE_CONFIG_PATH");
        Environment.SetEnvironmentVariable("NILE_CONFIG_PATH", root);
        try
        {
            var library = new LibraryService([new AmazonAdapter()], new SettingsService());

            var status = Assert.Single(library.StoreMatrix());

            Assert.True(status.signedIn);
            Assert.DoesNotContain(accountId, JsonSerializer.Serialize(status), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NILE_CONFIG_PATH", previous);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void LocalCheckDtos_ExposeOnlyBoundedStatesCodesAndTimestamps()
    {
        Assert.Equal(
            ["checkedAtUtc", "code", "state", "stores"],
            typeof(LibraryService.StoreLocalCheck)
                .GetProperties()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            ["backend", "cache", "client", "code", "readiness", "session", "store"],
            typeof(LibraryService.StoreLocalCheckItem)
                .GetProperties()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    public void FailedLocalProbe_ReturnsACodeWithoutTheRawError()
    {
        var library = new LibraryService([new ThrowingProbeAdapter()], new SettingsService());

        var result = library.CheckStoresLocal();

        Assert.Equal("partial", result.state);
        var item = Assert.Single(result.stores);
        Assert.Equal("probe_failed", item.code);
        Assert.Equal("unknown", item.readiness);
        Assert.DoesNotContain("fixture secret", JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StoresCheckRpc_StaysOffTheUiThreadAndCannotStartExpansiveWork()
    {
        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");
        Assert.Contains("\"stores.check\" => await StoresCheckAsync()", bridge, StringComparison.Ordinal);

        var start = bridge.IndexOf("private async Task<object> StoresCheckAsync()", StringComparison.Ordinal);
        var end = bridge.IndexOf("private object StoreMatrixWithLayers()", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var check = bridge[start..end];
        Assert.Contains("Task.Run", check, StringComparison.Ordinal);
        Assert.Contains("CheckStoresLocal", check, StringComparison.Ordinal);
        Assert.DoesNotContain("GetLibraryAsync", check, StringComparison.Ordinal);
        Assert.DoesNotContain("AuthenticateAsync", check, StringComparison.Ordinal);
        Assert.DoesNotContain("Http", check, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Process", check, StringComparison.OrdinalIgnoreCase);

        var library = ReadRepoFile("ExoLauncher", "Services", "LibraryService.cs");
        var localStart = library.IndexOf("public StoreLocalCheck CheckStoresLocal()", StringComparison.Ordinal);
        var localEnd = library.IndexOf("public IReadOnlyList<StoreBackendStatus>? PeekStoreMatrix()", localStart, StringComparison.Ordinal);
        Assert.True(localStart >= 0 && localEnd > localStart);
        var localCheck = library[localStart..localEnd];
        Assert.DoesNotContain("GetLibraryAsync", localCheck, StringComparison.Ordinal);
        Assert.DoesNotContain("AuthenticateAsync", localCheck, StringComparison.Ordinal);
        Assert.DoesNotContain("Ensure", localCheck, StringComparison.Ordinal);
        Assert.DoesNotContain("Download", localCheck, StringComparison.Ordinal);
        Assert.DoesNotContain("Http", localCheck, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Process", localCheck, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LibraryAndMatrixUseTheSameFullStoreMapper()
    {
        var bridge = ReadRepoFile("ExoLauncher", "Services", "WebHostBridge.cs");
        var libraryStart = bridge.IndexOf("private async Task<object> LibraryGetAsync", StringComparison.Ordinal);
        var libraryEnd = bridge.IndexOf("private async Task<object> GameGetAsync", libraryStart, StringComparison.Ordinal);
        var library = bridge[libraryStart..libraryEnd];
        Assert.Contains("MapStoreMatrix(", library, StringComparison.Ordinal);

        var matrixStart = bridge.IndexOf("private object StoreMatrixWithLayers()", StringComparison.Ordinal);
        var matrixEnd = bridge.IndexOf("private object GameProgress", matrixStart, StringComparison.Ordinal);
        var matrix = bridge[matrixStart..matrixEnd];
        Assert.Contains("MapStoreMatrix(", matrix, StringComparison.Ordinal);
    }

    [Fact]
    public void CapabilityMapper_HasNoAmbientMachineReads()
    {
        var matrix = ReadRepoFile("ExoLauncher", "Services", "StoreLayerMatrix.cs");
        Assert.Contains("public sealed record Context(", matrix, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamWebApiKeyStore", matrix, StringComparison.Ordinal);
        Assert.DoesNotContain("GogGalaxyFriends", matrix, StringComparison.Ordinal);
        Assert.DoesNotContain("NileCli", matrix, StringComparison.Ordinal);
        Assert.DoesNotContain("File.", matrix, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.", matrix, StringComparison.Ordinal);
    }

    [Fact]
    public void OnboardingRunsOneGuardedLocalCheckWithoutDirtyingTheLibraryOrOpeningClients()
    {
        var onboarding = ReadRepoFile("ui", "src", "components", "OnboardingPanel.tsx");
        var start = onboarding.IndexOf("function checkOnboardingStoresOnce()", StringComparison.Ordinal);
        var end = onboarding.IndexOf("export interface OnboardingPanelProps", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var check = onboarding[start..end];
        Assert.Contains("onboardingStoreCheck ??=", check, StringComparison.Ordinal);
        Assert.Contains("host.storesCheck()", check, StringComparison.Ordinal);
        Assert.Contains("host.storesMatrix()", check, StringComparison.Ordinal);
        Assert.DoesNotContain("setRefreshLibrary", check, StringComparison.Ordinal);
        Assert.DoesNotContain("storesAuth", check, StringComparison.Ordinal);
        Assert.DoesNotContain("showStore", check, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsCheckKeepsLastKnownRowsWhenTheProbeFails()
    {
        var settings = ReadRepoFile("ui", "src", "components", "SettingsPanel.tsx");
        var start = settings.IndexOf("async function checkStores()", StringComparison.Ordinal);
        var end = settings.IndexOf("const getDependency", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var check = settings[start..end];
        Assert.Contains("host.storesCheck()", check, StringComparison.Ordinal);
        Assert.Contains("Local check failed. Showing the last known results.", check, StringComparison.Ordinal);
        Assert.DoesNotContain("setCheckedStores([])", check, StringComparison.Ordinal);
        Assert.DoesNotContain("onStores", check, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsSegmentedControl_UsesRovingKeyboardRadioBehavior()
    {
        var settings = ReadRepoFile("ui", "src", "components", "SettingsPanel.tsx");
        var start = settings.IndexOf("function Segmented<", StringComparison.Ordinal);
        var end = settings.IndexOf("function LayerList", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var segmented = settings[start..end];
        Assert.Contains("role=\"radiogroup\"", segmented, StringComparison.Ordinal);
        Assert.Contains("tabIndex={on ? 0 : -1}", segmented, StringComparison.Ordinal);
        Assert.Contains("'ArrowLeft'", segmented, StringComparison.Ordinal);
        Assert.Contains("'ArrowRight'", segmented, StringComparison.Ordinal);
        Assert.Contains("'ArrowUp'", segmented, StringComparison.Ordinal);
        Assert.Contains("'ArrowDown'", segmented, StringComparison.Ordinal);
        Assert.Contains("'Home'", segmented, StringComparison.Ordinal);
        Assert.Contains("'End'", segmented, StringComparison.Ordinal);
        Assert.Contains(".focus()", segmented, StringComparison.Ordinal);
    }

    [Fact]
    public void OnboardingStepChange_FocusesTheNewLabelledPoliteRegionOnlyWhenRequested()
    {
        var onboarding = ReadRepoFile("ui", "src", "components", "OnboardingPanel.tsx");
        Assert.Contains("const stepFocusPendingRef = useRef(false)", onboarding, StringComparison.Ordinal);
        Assert.Contains("if (!stepFocusPendingRef.current) return", onboarding, StringComparison.Ordinal);
        Assert.Contains("stepHeadingRef.current?.focus()", onboarding, StringComparison.Ordinal);
        Assert.Contains("tabIndex={-1}", onboarding, StringComparison.Ordinal);
        Assert.Contains("role=\"region\"", onboarding, StringComparison.Ordinal);
        Assert.Contains("aria-live=\"polite\"", onboarding, StringComparison.Ordinal);
        Assert.Contains("aria-labelledby={`exo-onboarding-${step}-title`}", onboarding, StringComparison.Ordinal);

        var chooseStart = onboarding.IndexOf("function chooseStep(next: StepId)", StringComparison.Ordinal);
        var chooseEnd = onboarding.IndexOf("function clearStoreBusy", chooseStart, StringComparison.Ordinal);
        Assert.True(chooseStart >= 0 && chooseEnd > chooseStart);
        Assert.Contains("stepFocusPendingRef.current = true", onboarding[chooseStart..chooseEnd], StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ExoLauncher.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(new[] { directory!.FullName }.Concat(relative).ToArray()));
    }

    private class LocalProbeAdapter : IStoreAdapter, IStoreClientPresence
    {
        public int LibraryReads { get; private set; }
        public StoreKind Store => StoreKind.Riot;
        public string Id => "riot";
        public string DisplayName => "Riot Games";
        public virtual bool IsAgentPresent() => true;
        public bool IsClientPresent() => true;
        public Task<AuthResult> AuthenticateAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("A local check must not authenticate.");
        public Task<IReadOnlyList<GameEntry>> GetLibraryAsync(CancellationToken ct = default)
        {
            LibraryReads++;
            throw new InvalidOperationException("A local check must not scan the library.");
        }
        public Task<InstallResult> InstallAsync(GameEntry game, string? installPath, IProgress<InstallProgress>? progress, CancellationToken ct = default) =>
            throw new InvalidOperationException("A local check must not install.");
        public Task<InstallResult> UpdateAsync(GameEntry game, IProgress<InstallProgress>? progress, CancellationToken ct = default) =>
            throw new InvalidOperationException("A local check must not update.");
        public Task<LaunchResult> LaunchAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
            throw new InvalidOperationException("A local check must not launch.");
        public Task<InstallResult> UninstallAsync(GameEntry game, CancellationToken ct = default) =>
            throw new InvalidOperationException("A local check must not uninstall.");
        public InstallProgress GetDownloadProgress(string gameId) => new();
        public Task CleanupAfterExitAsync(GameEntry game, LaunchOptions options, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class ThrowingProbeAdapter : LocalProbeAdapter
    {
        public override bool IsAgentPresent() => throw new InvalidOperationException("fixture secret");
    }
}
