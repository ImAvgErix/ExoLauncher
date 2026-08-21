using Xunit;

namespace ExoLauncher.Tests;

public sealed class OnboardingAccountContractTests
{
    [Fact]
    public void Onboarding_RequestsHandleOnceAndFailsOpenWhenExoIsUnavailable()
    {
        var onboarding = ReadRepoFile("ui", "src", "components", "OnboardingPanel.tsx");
        var account = ReadRepoFile("ui", "src", "components", "AccountPanel.tsx");

        Assert.Equal(1, CountOccurrences(onboarding, "<AccountPanel"));
        Assert.DoesNotContain("exo-onboarding-profile-handle", onboarding, StringComparison.Ordinal);
        Assert.Contains("Choose a handle once.", onboarding, StringComparison.Ordinal);
        Assert.Contains("serviceUnavailable || (!!accountState?.signedIn && !!accountState.handle)", onboarding, StringComparison.Ordinal);
        Assert.Contains("isAccountServiceUnavailable", onboarding, StringComparison.Ordinal);
        Assert.Contains("configured: false", onboarding, StringComparison.Ordinal);
        Assert.DoesNotContain("Continue offline", onboarding, StringComparison.Ordinal);
        Assert.DoesNotContain("offlineChosen", onboarding, StringComparison.Ordinal);
        Assert.Contains("onAccountState={setAccountState}", onboarding, StringComparison.Ordinal);
        Assert.Contains("Create or sign in to your Exo account.", onboarding, StringComparison.Ordinal);
        Assert.DoesNotContain("Profile privacy", onboarding, StringComparison.Ordinal);
        Assert.DoesNotContain("Save this PC to Exo", onboarding, StringComparison.Ordinal);
        Assert.DoesNotContain("Save profile", onboarding, StringComparison.Ordinal);
        Assert.Contains("Profile auto-saved to Exo.", onboarding, StringComparison.Ordinal);
        Assert.Contains("onBlur", onboarding, StringComparison.Ordinal);
        Assert.Contains("host.accountSetProfile()", onboarding, StringComparison.Ordinal);

        Assert.Contains("Continue setup; the library still works", account, StringComparison.Ordinal);
        Assert.Contains("No sign-in method is available", account, StringComparison.Ordinal);
        Assert.Contains("capabilities?.providers.password", account, StringComparison.Ordinal);
        Assert.Contains("capabilities?.providers.google === true", account, StringComparison.Ordinal);
        Assert.Contains("capabilities?.providers.email === true", account, StringComparison.Ordinal);
        Assert.Contains("accountReserveHandle", account, StringComparison.Ordinal);
        Assert.Contains("Choose an available handle once.", account, StringComparison.Ordinal);
        Assert.DoesNotContain("accessToken", onboarding + account, StringComparison.Ordinal);
        Assert.DoesNotContain("authorizationUrl", onboarding, StringComparison.Ordinal);
    }

    [Fact]
    public void Onboarding_ExplainsSteamKeyAndTestsUsableStoreActions()
    {
        var onboarding = ReadRepoFile("ui", "src", "components", "OnboardingPanel.tsx");
        var stores = ReadRepoFile("ui", "src", "lib", "stores.ts");
        var persist = SliceBetween(onboarding, "async function persistSteamKey(value: string)", "async function saveProfile()");
        var keyInput = SliceBetween(onboarding, "id=\"exo-onboarding-steam-key\"", "onChange={(event) => setSteamKeyDraft(event.target.value)}");

        Assert.Contains("Steam Web API key", onboarding, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Steam Web API key\"", onboarding, StringComparison.Ordinal);
        Assert.Contains("host.setSettings({ steamWebApiKey: value.trim() })", persist, StringComparison.Ordinal);
        Assert.Contains("setSteamKeyDraft('')", persist, StringComparison.Ordinal);
        var started = persist.IndexOf("host.setSettings({ steamWebApiKey:", StringComparison.Ordinal);
        var cleared = persist.IndexOf("setSteamKeyDraft('')", StringComparison.Ordinal);
        var settled = persist.IndexOf("await pending", StringComparison.Ordinal);
        Assert.True(started >= 0 && started < cleared && cleared < settled);

        Assert.Contains("type=\"password\"", keyInput, StringComparison.Ordinal);
        Assert.Contains("autoComplete=\"off\"", keyInput, StringComparison.Ordinal);
        Assert.DoesNotContain(" title=", keyInput, StringComparison.Ordinal);
        Assert.Contains("https://steamcommunity.com/dev/apikey", onboarding, StringComparison.Ordinal);
        Assert.Contains("DPAPI-protected on this PC", onboarding, StringComparison.Ordinal);
        Assert.Contains("never enter a publisher key", onboarding, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("canOpenStoreClient(store) && store.clientPresent === true", onboarding, StringComparison.Ordinal);
        Assert.Contains("onboardingStoreLabel(store)", onboarding, StringComparison.Ordinal);
        Assert.Contains("Ready to sign in", stores, StringComparison.Ordinal);
        Assert.Contains("sign-in stays in the official app", stores, StringComparison.Ordinal);
        Assert.Contains("Local check unavailable. Setup can continue.", onboarding, StringComparison.Ordinal);
    }

    [Fact]
    public void Onboarding_FillsTheWindowWithOneAccountPanel()
    {
        var onboarding = ReadRepoFile("ui", "src", "components", "OnboardingPanel.tsx");
        var css = ReadRepoFile("ui", "src", "tokens.css");

        Assert.Contains("Create or sign in to your Exo account", onboarding, StringComparison.Ordinal);
        Assert.Contains("{ id: 'stores', label: 'Stores' }", onboarding, StringComparison.Ordinal);
        Assert.Contains("{ id: 'account', label: 'Account' }", onboarding, StringComparison.Ordinal);
        Assert.Contains("{ id: 'profile', label: 'Make it yours' }", onboarding, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(onboarding, "<AccountPanel"));

        var shell = SliceBetween(css, ".exo-onboarding-shell {", ".exo-onboarding-rail {");
        var storeBody = SliceBetween(css, ".exo-onboarding-store-body {", ".exo-onboarding-store-body::-webkit-scrollbar");
        var accountWrap = SliceBetween(css, ".exo-onboarding-account-wrap {", ".exo-onboarding-account-wrap::-webkit-scrollbar");
        Assert.Contains("width: 100%", shell, StringComparison.Ordinal);
        Assert.Contains("height: 100%", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("width: min(", shell, StringComparison.Ordinal);
        Assert.Contains("flex: 1 1 auto", storeBody, StringComparison.Ordinal);
        Assert.Contains("flex: 1 1 auto", accountWrap, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
            count++;
        return count;
    }

    private static string SliceBetween(string text, string start, string end)
    {
        var startIndex = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, start);
        var from = startIndex + start.Length;
        var endIndex = text.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(endIndex > from, end);
        return text[startIndex..endIndex];
    }

    private static string ReadRepoFile(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ExoLauncher.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(relative).ToArray()));
    }
}
