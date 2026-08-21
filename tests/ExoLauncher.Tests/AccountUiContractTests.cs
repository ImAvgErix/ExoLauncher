using Xunit;

namespace ExoLauncher.Tests;

public sealed class AccountUiContractTests
{
    [Fact]
    public void HostBridge_HasTypedPasswordAndAccountOperations()
    {
        var host = ReadRepoFile("ui", "src", "lib", "host.ts");

        Assert.Contains("export type AccountProvider = 'google' | 'email' | 'password'", host, StringComparison.Ordinal);
        Assert.Contains("export interface AccountState", host, StringComparison.Ordinal);
        Assert.Contains("configured: boolean", host, StringComparison.Ordinal);
        Assert.Contains("providers: AccountProvider[]", host, StringComparison.Ordinal);
        Assert.Contains("accountGet: () => rawCall<AccountState>('account.get')", host, StringComparison.Ordinal);
        Assert.Contains("accountCreatePassword:", host, StringComparison.Ordinal);
        Assert.Contains("'account.createPassword'", host, StringComparison.Ordinal);
        Assert.Contains("accountPasswordSignIn:", host, StringComparison.Ordinal);
        Assert.Contains("'account.signInPassword'", host, StringComparison.Ordinal);
        Assert.Contains("accountReserveHandle:", host, StringComparison.Ordinal);
        Assert.Contains("accountSetProfile: ()", host, StringComparison.Ordinal);
    }

    [Fact]
    public void AccountPanel_IsOneIdentityFlowAndAutoSaves()
    {
        var panel = ReadRepoFile("ui", "src", "components", "AccountPanel.tsx");
        var onboarding = ReadRepoFile("ui", "src", "components", "OnboardingPanel.tsx");
        var settings = ReadRepoFile("ui", "src", "components", "SettingsPanel.tsx");

        Assert.Contains("account && !account.configured", panel, StringComparison.Ordinal);
        Assert.Contains("capabilities?.providers.password", panel, StringComparison.Ordinal);
        Assert.Contains("Create account", panel, StringComparison.Ordinal);
        Assert.Contains("Sign in", panel, StringComparison.Ordinal);
        Assert.Contains("suggestedHandle", panel, StringComparison.Ordinal);
        Assert.Contains("Auto-saved to Exo", panel, StringComparison.Ordinal);
        Assert.Contains("host.accountSetProfile()", panel, StringComparison.Ordinal);
        Assert.Contains("initialState?: AccountState | null", panel, StringComparison.Ordinal);
        Assert.Contains("const healthPromise = host.onlineHealth()", panel, StringComparison.Ordinal);
        Assert.Contains("setLoading(false)", panel, StringComparison.Ordinal);
        Assert.Contains("Optional methods not enabled on this deployment", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("Save this PC to Exo", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("Profile privacy", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("Store discovery", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("<span>Optional</span>", panel, StringComparison.Ordinal);

        Assert.Equal(1, CountOccurrences(onboarding, "<AccountPanel"));
        Assert.DoesNotContain("Continue offline", onboarding, StringComparison.Ordinal);
        Assert.DoesNotContain("offlineChosen", onboarding, StringComparison.Ordinal);
        Assert.Contains("serviceUnavailable || (!!accountState?.signedIn && !!accountState.handle)", onboarding, StringComparison.Ordinal);
        Assert.Contains("isAccountServiceUnavailable", onboarding, StringComparison.Ordinal);
        Assert.Contains("Profile privacy", settings, StringComparison.Ordinal);
        Assert.Contains("Store discovery", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Profile privacy", onboarding, StringComparison.Ordinal);
    }

    [Fact]
    public void PasswordForms_AreBoundedAndKeepSecretsTransient()
    {
        var host = ReadRepoFile("ui", "src", "lib", "host.ts");
        var panel = ReadRepoFile("ui", "src", "components", "AccountPanel.tsx");

        Assert.Contains("providers: { google: boolean; email: boolean; password: boolean }", host, StringComparison.Ordinal);
        Assert.Contains("PASSWORD_MIN_LENGTH = 12", panel, StringComparison.Ordinal);
        Assert.Contains("PASSWORD_MAX_LENGTH = 128", panel, StringComparison.Ordinal);
        Assert.Contains("autoComplete={mode === 'create' ? 'new-password' : 'current-password'}", panel, StringComparison.Ordinal);
        Assert.Contains("Passwords must match", panel, StringComparison.Ordinal);
        Assert.Contains("Email verification and password recovery are not available yet", panel, StringComparison.Ordinal);
        Assert.Contains("clearPasswordFields", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("sessionStorage", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("accessToken", panel, StringComparison.OrdinalIgnoreCase);

        var accountState = host[
            host.IndexOf("export interface AccountState", StringComparison.Ordinal)..
            host.IndexOf("export interface AccountOperationResponse", StringComparison.Ordinal)];
        Assert.DoesNotContain("password", accountState, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AccountPanel_ReactsToNativeSessionInvalidation()
    {
        var panel = ReadRepoFile("ui", "src", "components", "AccountPanel.tsx");

        Assert.Contains("onHostEvent('account.updated'", panel, StringComparison.Ordinal);
        Assert.Contains("publishAccount(next)", panel, StringComparison.Ordinal);
        Assert.Contains("clearPasswordFields()", panel, StringComparison.Ordinal);
        Assert.Contains("void load().catch", panel, StringComparison.Ordinal);
    }

    [Fact]
    public void PrivacyAndUniqueStoreLinking_LiveInSettings()
    {
        var settings = ReadRepoFile("ui", "src", "components", "SettingsPanel.tsx");

        Assert.Contains("Profile privacy", settings, StringComparison.Ordinal);
        Assert.Contains("Find mutual store friends", settings, StringComparison.Ordinal);
        Assert.Contains("Verified accounts are unique to this Exo profile", settings, StringComparison.Ordinal);
        Assert.Contains("host.onlineLinkStore(store)", settings, StringComparison.Ordinal);
        Assert.Contains("host.onlineUnlinkStore(store)", settings, StringComparison.Ordinal);
        Assert.Contains("host.onlineMatchStore('steam')", settings, StringComparison.Ordinal);
        Assert.Contains("Refresh matches", settings, StringComparison.Ordinal);
        Assert.Contains("Coming soon", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlineHostSurface_KeepsNativeSecretsOutOfReact()
    {
        var host = ReadRepoFile("ui", "src", "lib", "host.ts");

        Assert.Contains("export interface OnlineDiagnostics", host, StringComparison.Ordinal);
        Assert.Contains("export interface OnlineResult<T>", host, StringComparison.Ordinal);
        Assert.Contains("'account.updated': AccountState", host, StringComparison.Ordinal);
        Assert.Contains("'online.presence': OnlinePresenceEvent", host, StringComparison.Ordinal);
        Assert.Contains("onlineSetPrivacy:", host, StringComparison.Ordinal);
        Assert.Contains("onlineUploadMedia:", host, StringComparison.Ordinal);
        Assert.Contains("onlinePresence:", host, StringComparison.Ordinal);
        Assert.DoesNotContain("accessToken", host, StringComparison.Ordinal);
        Assert.DoesNotContain("nativePath", host, StringComparison.Ordinal);
        Assert.DoesNotContain("authorizationUrl", host, StringComparison.Ordinal);
    }

    [Fact]
    public void MagicLinkSender_IsBoundedAndIdempotentWhenResendIsConfigured()
    {
        var email = ReadRepoFile("services", "exo-id", "src", "email.ts");

        Assert.Contains("new AbortController()", email, StringComparison.Ordinal);
        Assert.Contains("setTimeout(() => controller.abort(), 8_000)", email, StringComparison.Ordinal);
        Assert.Contains("\"Idempotency-Key\": idempotencyKey", email, StringComparison.Ordinal);
        Assert.Contains("clearTimeout(timeout)", email, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
            count++;
        return count;
    }
}
