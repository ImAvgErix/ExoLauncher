using System.Text.Json;
using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Settings > Game stores: who each store Exo signs into is signed in as, and
/// switching or signing out with the store tool's own commands. Only display
/// names and one-way keys reach the page; account names, ids and tokens stay
/// here and are never logged.
/// </summary>
public sealed partial class WebHostBridge
{
    private static readonly TimeSpan SteamExitWait = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _accountsGate = new(1, 1);
    private const string EpicBrowserBusy = "Epic sign-in is still closing. Try again in a moment.";

    private object AccountsList()
    {
        object? steam = null;
        if (SteamInstallation.ResolveRoot() is { } steamRoot)
        {
            var accounts = SteamLoginUsers.Load(steamRoot);
            var current = SteamLoginUsers.Current(accounts, SteamClientReady.ReadActiveUser(), SteamLoginUsers.ReadAutoLoginUser());
            steam = new
            {
                current = current?.PersonaName,
                accounts = accounts.Select(account => new
                {
                    key = account.Key,
                    name = account.PersonaName,
                    current = current is not null && account.SteamId64 == current.SteamId64,
                    remembered = account.Remembered,
                }).ToArray(),
            };
        }

        object? riot = null;
        if (RiotAdapter.TryResolveRiotClientServicesPublic() is not null)
        {
            var live = RiotAccounts.ReadLive(RiotAccounts.LiveDataDir);
            var saved = RiotAccounts.List(RiotAccounts.StoreDir);
            var valorant = RiotAccounts.ReadValorantLastUser(SteamPlaytime.ReadSharedText(RiotAccounts.ValorantMachineIni));
            // The signed-in account shows even before Exo saves it; it is saved on the first switch away.
            var rows = saved.Select(account => (account.Key, account.Puuid, account.Name, Saved: true)).ToList();
            if (live is not null && rows.All(row => row.Key != live.Key))
                rows.Insert(0, (live.Key, live.Puuid, live.Name, Saved: false));
            riot = new
            {
                current = live?.Name,
                accounts = rows.Select(row => new
                {
                    key = row.Key,
                    name = row.Name,
                    current = live is not null && row.Key == live.Key,
                    saved = row.Saved,
                    lastIn = string.Equals(valorant, row.Puuid, StringComparison.OrdinalIgnoreCase) ? new[] { "VALORANT" } : [],
                }).ToArray(),
            };
        }

        var userPath = EpicPlaytime.LegendaryUserPath();
        var (legendaryId, legendaryName) = userPath is null
            ? (null, null)
            : EpicPlaytime.ReadUserIdentity(SteamPlaytime.ReadSharedText(userPath));
        var launcherId = EpicPlaytime.ReadEglAccountId();
        var launcherDiffers = legendaryId is not null && launcherId is not null &&
                              !string.Equals(legendaryId, launcherId, StringComparison.OrdinalIgnoreCase);
        var epic = new
        {
            signedIn = legendaryId is not null,
            name = legendaryName,
            launcherDiffers,
            canImport = (launcherDiffers || legendaryId is null) && EpicPlaytime.HasEglRememberMe(),
            added = EpicAccounts.List().Select(account => new { key = account.Scope, name = account.DisplayName ?? "Epic account" }).ToArray(),
        };

        var gogPath = GogAuthService.FindExistingAuthConfigPath();
        var gog = new
        {
            signedIn = gogPath is not null,
            canSignOut = gogPath is not null && string.Equals(
                Path.GetFullPath(gogPath), Path.GetFullPath(GogAuthService.AuthConfigPath), StringComparison.OrdinalIgnoreCase),
        };

        static string? ReadNileFile(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch { return null; }
        }
        // The name comes from the folder whose session is signed in, never another account's.
        var amazonRoot = NileCli.SessionRoot(NileCli.ConfigRoots(), File.Exists, ReadNileFile);
        var amazon = new
        {
            signedIn = amazonRoot is not null,
            name = amazonRoot is null ? null : NileCli.ReadDisplayName([amazonRoot], ReadNileFile),
        };

        return new { ok = true, steam, riot, epic, gog, amazon };
    }

    private async Task<object> AccountsSwitchAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var store = ReadString(p, hasParams, "store");
        if (!await _accountsGate.WaitAsync(0, ct).ConfigureAwait(false))
            return new { ok = false, message = "Another account change is still running." };
        try
        {
            return store switch
            {
                "steam" => await SwitchSteamAsync(ReadString(p, hasParams, "key"), ct).ConfigureAwait(false),
                "riot" => await SwitchRiotAsync(ReadString(p, hasParams, "key"), ct).ConfigureAwait(true),
                "epic" => await SwitchEpicAsync(ct).ConfigureAwait(true),
                "gog" => await SwitchGogAsync(ct).ConfigureAwait(true),
                "amazon" => await SwitchAmazonAsync(ct).ConfigureAwait(true),
                _ => new { ok = false, message = "Exo cannot switch accounts for this store." },
            };
        }
        finally
        {
            _accountsGate.Release();
        }
    }

    private async Task<object> AccountsSignOutAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var store = ReadString(p, hasParams, "store");
        if (!await _accountsGate.WaitAsync(0, ct).ConfigureAwait(false))
            return new { ok = false, message = "Another account change is still running." };
        try
        {
            var (ok, message) = store switch
            {
                "epic" => !EpicAuthService.ForgetBrowserSignIn()
                    ? (false, EpicBrowserBusy)
                    : EpicAdapter.ResolveLegendary() is { } legendary &&
                      await EpicAccounts.SignOutMainAsync(legendary, ct).ConfigureAwait(false)
                        ? (true, "Signed out of Epic.")
                        : (false, "Epic sign-out did not finish. Try again."),
                "gog" => SignOutGog(),
                "amazon" => await SignOutAmazonAsync(ct).ConfigureAwait(false),
                _ => (false, "Exo cannot sign out of this store."),
            };
            if (ok) AfterAccountChange(store);
            return new { ok, message };
        }
        finally
        {
            _accountsGate.Release();
        }
    }

    private async Task<object> AccountsEpicImportAsync(CancellationToken ct)
    {
        if (!await _accountsGate.WaitAsync(0, ct).ConfigureAwait(false))
            return new { ok = false, message = "Another account change is still running." };
        try
        {
            if (EpicAdapter.ResolveLegendary() is not { } legendary)
                return new { ok = false, message = "Epic sign-in is not available right now. Try again." };
            if (!EpicAuthService.ForgetBrowserSignIn())
                return new { ok = false, message = EpicBrowserBusy };
            var (ok, message) = await EpicAccounts.ImportLauncherAccountAsync(legendary, ct).ConfigureAwait(false);
            if (!ok) return new { ok, message };
            AfterAccountChange("epic");
            return new { ok, message = $"Exo uses {message}." };
        }
        finally
        {
            _accountsGate.Release();
        }
    }

    private async Task<object> AccountsEpicAddAsync(CancellationToken ct)
    {
        if (!await _accountsGate.WaitAsync(0, ct).ConfigureAwait(false))
            return new { ok = false, message = "Another account change is still running." };
        try
        {
            if (EpicAdapter.ResolveLegendary() is not { } legendary)
                return new { ok = false, message = "Epic sign-in is not available right now. Try again." };
            var pending = EpicAccounts.NewPendingFolder();
            var result = await _services.EpicAuth.SignInAsync(legendary, ct, configFolder: pending, otherAccount: true).ConfigureAwait(true);
            if (!result.Ok)
            {
                EpicAccounts.Discard(pending);
                return new { ok = false, message = result.Message };
            }
            var mainId = EpicPlaytime.LegendaryUserPath() is { } userPath
                ? EpicPlaytime.ReadUserIdentity(SteamPlaytime.ReadSharedText(userPath)).AccountId
                : null;
            var (ok, message) = EpicAccounts.Adopt(pending, mainId is null ? null : EpicPlaytime.AccountScopeFor(mainId));
            if (!ok) return new { ok, message };
            AppLog.Info("Epic: another account was added for playtime.");
            EpicAccounts.RefreshAddedInBackground(force: true);
            return new { ok, message = $"Added {message}." };
        }
        finally
        {
            _accountsGate.Release();
        }
    }

    private async Task<object> AccountsEpicRemoveAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var key = ReadString(p, hasParams, "key");
        if (!EpicAccounts.IsScope(key)) return new { ok = false, message = "Unknown account." };
        if (!await _accountsGate.WaitAsync(0, ct).ConfigureAwait(false))
            return new { ok = false, message = "Another account change is still running." };
        try
        {
            var removed = await EpicAccounts.RemoveAsync(key!, ct).ConfigureAwait(false);
            if (removed) AppLog.Info("Epic: an added account was removed.");
            return new { ok = removed, message = removed ? null : "That account is not added." };
        }
        finally
        {
            _accountsGate.Release();
        }
    }

    /// <summary>
    /// Switches Steam to a remembered account the way its own account picker does: Steam
    /// closes, the auto-login account is written, and Steam starts again hidden and signs
    /// in. The call returns once Steam is signed in as that account, or, when Steam has no
    /// saved sign-in for it, with Steam's own sign-in on screen (Exo hides Steam's windows,
    /// so a sign-in it raised would otherwise never be seen).
    /// </summary>
    private async Task<object> SwitchSteamAsync(string? key, CancellationToken ct)
    {
        AppLog.Info("Steam: account switch requested.");
        var root = SteamInstallation.ResolveRoot();
        var exe = root is null ? null : Path.Combine(root, "steam.exe");
        if (root is null || exe is null || !File.Exists(exe))
        {
            AppLog.Warn("Steam: account switch refused; steam.exe was not found.");
            return new { ok = false, message = "Steam is not installed." };
        }
        var accounts = SteamLoginUsers.Load(root);
        var target = accounts.FirstOrDefault(account => account.Key == key);
        if (target is null)
        {
            AppLog.Warn($"Steam: account switch refused; the account is not in loginusers.vdf ({accounts.Count} there).");
            return new { ok = false, message = "Steam no longer remembers that account." };
        }
        var current = SteamLoginUsers.Current(accounts, SteamClientReady.ReadActiveUser(), SteamLoginUsers.ReadAutoLoginUser());
        if (current?.SteamId64 == target.SteamId64)
        {
            AppLog.Info("Steam: account switch skipped; Steam already uses that account.");
            return new { ok = true, message = $"Steam is signed in as {target.PersonaName}." };
        }
        if (SteamLoginUsers.IsGameRunning())
        {
            AppLog.Info("Steam: account switch refused; a Steam game is running.");
            return new { ok = false, message = "A Steam game is running. Close it, then switch." };
        }

        AccountProgress("steam", "Closing Steam…");
        AppLog.Info("Steam: switching the signed-in account; closing Steam.");
        if (!await CloseSteamAsync(exe, ct).ConfigureAwait(false))
            return new { ok = false, message = "Steam did not close. Close Steam, then try again." };

        // Steam rewrites loginusers.vdf as it exits, so the switch is written after.
        string? failure = null;
        try
        {
            if (!SteamLoginUsers.WriteSwitch(root, target))
                failure = "Steam no longer remembers that account.";
        }
        catch (Exception ex)
        {
            AppLog.Warn("Steam: account switch could not be saved: " + ex.GetType().Name);
            failure = "Exo could not switch the Steam account. Try again.";
        }
        if (failure is not null)
        {
            // Started again so a failed switch does not leave Steam closed.
            StartSteamHidden(exe);
            return new { ok = false, message = failure };
        }
        AppLog.Info("Steam: auto-login account written" + (target.Remembered ? "; starting Steam hidden." : "; Steam has no saved sign-in for it."));
        SteamPlaytime.Invalidate();

        var signedIn = false;
        if (target.Remembered)
        {
            AccountProgress("steam", $"Signing in as {target.PersonaName}…");
            StartSteamHidden(exe);
            signedIn = await WaitForSteamUserAsync(target.AccountId, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        }
        if (signedIn)
        {
            AppLog.Info("Steam: signed in as the switched account.");
            AfterAccountChange("steam");
            return new { ok = true, message = $"Steam is signed in as {target.PersonaName}." };
        }

        // No saved sign-in (never remembered, or Steam refused the one it had): Steam's own sign-in, on screen.
        if (target.Remembered) AppLog.Warn("Steam: the switched account did not sign in within 60 s; showing Steam's sign-in.");
        var opened = await OpenStoreClientAsync("steam").ConfigureAwait(true);
        RescanWhenSteamSignsIn(target.AccountId, TimeSpan.FromMinutes(10));
        return new
        {
            ok = false,
            message = opened.Ok
                ? $"Steam has no saved sign-in for {target.PersonaName}. Sign in once in Steam's window."
                : opened.Message,
        };
    }

    /// <summary>True once Steam reports <paramref name="accountId"/> signed in; false after <paramref name="wait"/> or when Steam exits.</summary>
    private static async Task<bool> WaitForSteamUserAsync(string? accountId, TimeSpan wait, CancellationToken ct)
    {
        var until = DateTimeOffset.UtcNow + wait;
        var started = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow < until)
        {
            if (accountId is not null && SteamClientReady.ReadActiveUser().ToString(System.Globalization.CultureInfo.InvariantCulture) == accountId) return true;
            if (DateTimeOffset.UtcNow - started > TimeSpan.FromSeconds(20) && !ProcessHelper.IsProcessRunning("steam")) return false;
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>A step of an account change, for the row that started it.</summary>
    private void AccountProgress(string store, string line) => PostEvent("accounts.progress", new { store, line });

    /// <summary>
    /// Reads the library again once Steam is signed in as <paramref name="accountId"/>
    /// (any account when null), or after <paramref name="wait"/>.
    /// </summary>
    private void RescanWhenSteamSignsIn(string? accountId, TimeSpan wait, string? restoreAutoLogin = null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var until = DateTimeOffset.UtcNow + wait;
                var signedIn = false;
                while (DateTimeOffset.UtcNow < until)
                {
                    var active = SteamClientReady.ReadActiveUser();
                    if (accountId is null ? active != 0 : active.ToString(System.Globalization.CultureInfo.InvariantCulture) == accountId) { signedIn = true; break; }
                    // Steam closed from its sign-in without one: no need to wait out the rest.
                    if (restoreAutoLogin is not null && DateTimeOffset.UtcNow > until - wait + TimeSpan.FromSeconds(30) && !ProcessHelper.IsProcessRunning("steam")) break;
                    await Task.Delay(1000, _bridgeLifetime.Token).ConfigureAwait(false);
                }
                // Add account left without a sign-in: Steam goes back to signing in as before.
                if (!signedIn && !string.IsNullOrEmpty(restoreAutoLogin)) SteamLoginUsers.RestoreAutoLogin(restoreAutoLogin);
                SteamPlaytime.Invalidate();
                AfterAccountChange("steam");
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>Starts Steam again hidden; its windows are hidden as they appear.</summary>
    private static void StartSteamHidden(string exe)
    {
        HiddenStoreRuntime.BeginHandoff(StoreWindowHider.SteamMainProcessNames);
        using (ProcessHelper.StartHidden(exe, SteamUpdateCommandPlan.HiddenClientStartArguments())) { }
    }

    /// <summary>Closes Steam with its own <c>-shutdown</c>; false when it is still running after 30 s.</summary>
    private static async Task<bool> CloseSteamAsync(string exe, CancellationToken ct)
    {
        if (!ProcessHelper.IsProcessRunning("steam")) return true;
        using (ProcessHelper.StartHiddenCli(exe, "-shutdown")) { }
        var deadline = DateTimeOffset.UtcNow + SteamExitWait;
        while (ProcessHelper.IsProcessRunning("steam") && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(500, ct).ConfigureAwait(false);
        if (!ProcessHelper.IsProcessRunning("steam")) return true;
        AppLog.Warn("Steam: did not close within 30 s.");
        return false;
    }

    private async Task<object> AccountsAddAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var store = ReadString(p, hasParams, "store");
        if (!await _accountsGate.WaitAsync(0, ct).ConfigureAwait(false))
            return new { ok = false, message = "Another account change is still running." };
        try
        {
            return store switch
            {
                "steam" => await AddSteamAsync(ct).ConfigureAwait(true),
                "riot" => await AddRiotAsync(ct).ConfigureAwait(true),
                _ => new { ok = false, message = "Exo cannot add accounts for this store." },
            };
        }
        finally
        {
            _accountsGate.Release();
        }
    }

    private async Task<object> AccountsRemoveAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var store = ReadString(p, hasParams, "store");
        var key = ReadString(p, hasParams, "key");
        if (!await _accountsGate.WaitAsync(0, ct).ConfigureAwait(false))
            return new { ok = false, message = "Another account change is still running." };
        try
        {
            return store switch
            {
                "steam" => await RemoveSteamAsync(key, ct).ConfigureAwait(false),
                "riot" => RemoveRiot(key),
                _ => new { ok = false, message = "Exo cannot remove accounts for this store." },
            };
        }
        finally
        {
            _accountsGate.Release();
        }
    }

    /// <summary>
    /// Steam's own sign-in for another account: Steam closes, the auto-login account
    /// is cleared, and Steam opens on its account picker. Steam keeps the new account
    /// when Remember me is on; Exo never sees the password.
    /// </summary>
    private async Task<object> AddSteamAsync(CancellationToken ct)
    {
        var root = SteamInstallation.ResolveRoot();
        var exe = root is null ? null : Path.Combine(root, "steam.exe");
        if (exe is null || !File.Exists(exe)) return new { ok = false, message = "Steam is not installed." };
        if (SteamLoginUsers.IsGameRunning())
            return new { ok = false, message = "A Steam game is running. Close it, then add an account." };
        AccountProgress("steam", "Closing Steam…");
        AppLog.Info("Steam: adding an account; closing Steam for its sign-in.");
        if (!await CloseSteamAsync(exe, ct).ConfigureAwait(false))
            return new { ok = false, message = "Steam did not close. Close Steam, then try again." };
        // Put back if no account signs in, so the next Steam start doesn't ask which account.
        var previous = SteamLoginUsers.ReadAutoLoginUser();
        try { SteamLoginUsers.ClearAutoLogin(); }
        catch (Exception ex)
        {
            AppLog.Warn("Steam: auto-login could not be cleared: " + ex.GetType().Name);
            StartSteamHidden(exe);
            return new { ok = false, message = "Exo could not open Steam's sign-in. Try again." };
        }
        var opened = await OpenStoreClientAsync("steam").ConfigureAwait(true);
        if (!opened.Ok) return new { ok = false, message = opened.Message };
        RescanWhenSteamSignsIn(null, TimeSpan.FromMinutes(10), previous);
        return new { ok = true, message = "Sign in to the other account in Steam with Remember me on." };
    }

    /// <summary>Steam forgets a remembered account that is not the signed-in one.</summary>
    private async Task<object> RemoveSteamAsync(string? key, CancellationToken ct)
    {
        var root = SteamInstallation.ResolveRoot();
        var exe = root is null ? null : Path.Combine(root, "steam.exe");
        if (root is null || exe is null || !File.Exists(exe)) return new { ok = false, message = "Steam is not installed." };
        var accounts = SteamLoginUsers.Load(root);
        var target = accounts.FirstOrDefault(account => account.Key == key);
        if (target is null) return new { ok = true, message = (string?)null };
        var current = SteamLoginUsers.Current(accounts, SteamClientReady.ReadActiveUser(), SteamLoginUsers.ReadAutoLoginUser());
        if (current?.SteamId64 == target.SteamId64)
            return new { ok = false, message = "Switch to another account first." };
        if (SteamLoginUsers.IsGameRunning())
            return new { ok = false, message = "A Steam game is running. Close it, then remove the account." };

        // Steam rewrites loginusers.vdf as it exits, so it closes first and starts hidden again after.
        var wasRunning = ProcessHelper.IsProcessRunning("steam");
        if (!await CloseSteamAsync(exe, ct).ConfigureAwait(false))
            return new { ok = false, message = "Steam did not close. Close Steam, then try again." };
        string? failure = null;
        try
        {
            if (!SteamLoginUsers.WriteRemove(root, target)) failure = "Steam no longer remembers that account.";
        }
        catch (Exception ex)
        {
            AppLog.Warn("Steam: account could not be removed: " + ex.GetType().Name);
            failure = "Exo could not remove that account. Try again.";
        }
        if (wasRunning) StartSteamHidden(exe);
        if (failure is not null) return new { ok = false, message = failure };
        AppLog.Info("Steam: a remembered account was removed.");
        return new { ok = true, message = $"Steam forgot {target.PersonaName}." };
    }

    /// <summary>Riot Client Services and its UI: what holds the session files. Its crash handler holds none.</summary>
    private static readonly string[] RiotSessionProcesses = ["RiotClientServices", .. StoreWindowHider.RiotUiProcessNames];

    /// <summary>
    /// Closes the Riot Client for an account change: never while a Riot game or its
    /// client (LeagueClient) runs. Riot's own quit first (Riot Client Services takes its
    /// UI with it, also when it runs in the background with no window), then a normal
    /// window close, then, for the UI processes alone, a forced end. Riot Client Services
    /// is only ever asked; Vanguard and games are never touched. Returns why it could not, or null.
    /// </summary>
    private async Task<string?> CloseRiotClientAsync(CancellationToken ct)
    {
        if (RiotAccounts.RunningGame() is { } game)
        {
            AppLog.Info($"Riot: account change refused; {game} is running.");
            return $"{game} is running. Close it, then try again.";
        }
        string[] Running() => RiotSessionProcesses.Where(ProcessHelper.IsProcessRunning).ToArray();
        async Task<bool> ClosedWithinAsync(TimeSpan wait)
        {
            var deadline = DateTimeOffset.UtcNow + wait;
            while (Running().Length > 0 && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(500, ct).ConfigureAwait(false);
            return Running().Length == 0;
        }
        if (Running().Length == 0) return null;
        AccountProgress("riot", "Closing the Riot Client…");
        var asked = await Adapters.Riot.RiotClientApi.RequestQuitAsync(ct).ConfigureAwait(false);
        AppLog.Info("Riot: closing the Riot Client for an account change; its quit was " + (asked ? "accepted." : "not answered."));
        if (await ClosedWithinAsync(TimeSpan.FromSeconds(asked ? 20 : 5)).ConfigureAwait(false)) return null;

        ProcessHelper.TryCloseProcesses(StoreWindowHider.RiotUiProcessNames);
        if (await ClosedWithinAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false)) return null;

        // The UI only, and only Riot's own copy of it (under the Riot Client's folder).
        var install = RiotAdapter.TryResolveRiotClientServicesPublic() is { } rcs ? Path.GetDirectoryName(rcs) : null;
        foreach (var name in StoreWindowHider.RiotUiProcessNames)
            foreach (var process in System.Diagnostics.Process.GetProcessesByName(name))
                using (process)
                    try
                    {
                        if (install is not null && ProcessHelper.MatchesOptionalPath(process, install) && ProcessTermination.Kill(process))
                            AppLog.Info($"Riot: {name} did not close; ended it.");
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        // With its UI gone, Riot Client Services is asked once more.
        if (Running().Contains("RiotClientServices")) await Adapters.Riot.RiotClientApi.RequestQuitAsync(ct).ConfigureAwait(false);
        if (await ClosedWithinAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false)) return null;

        AppLog.Warn("Riot: the Riot Client did not close (" + string.Join(", ", Running()) + " still running); account change skipped.");
        return "The Riot Client did not close. Quit it from its tray icon, then try again.";
    }

    private static async Task StartRiotHiddenAsync(string rcs, CancellationToken ct)
    {
        try
        {
            using var api = await Adapters.Riot.RiotClientApi.ConnectAsync(rcs, TimeSpan.FromSeconds(35), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            AppLog.Debug("Riot: hidden start after an account change: " + ex.GetType().Name);
        }
    }

    /// <summary>
    /// Signs the Riot Client in as a saved account: the Riot Client closes, the
    /// signed-in account's session is saved (Riot rotates its tokens, so the copy is
    /// always the latest), the chosen account's session files are put back, and the
    /// Riot Client starts hidden.
    /// </summary>
    private async Task<object> SwitchRiotAsync(string? key, CancellationToken ct)
    {
        if (RiotAdapter.TryResolveRiotClientServicesPublic() is not { } rcs)
            return new { ok = false, message = "Riot Client is not installed." };
        var target = RiotAccounts.List(RiotAccounts.StoreDir).FirstOrDefault(account => account.Key == key);
        if (target is null) return new { ok = false, message = "Exo has no saved sign-in for that account. Add it again." };
        if (RiotAccounts.ReadLive(RiotAccounts.LiveDataDir)?.Key == target.Key)
            return new { ok = true, message = $"Riot Client is signed in as {target.Name}." };
        if (await CloseRiotClientAsync(ct).ConfigureAwait(false) is { } refusal)
            return new { ok = false, message = refusal };

        string? failure = null;
        try
        {
            // The signed-in account is saved before it is replaced; one that could not be saved (a session too big, an identity that
            // would not read) is left signed in rather than lost.
            var live = RiotAccounts.ReadLive(RiotAccounts.LiveDataDir) is not null;
            if (RiotAccounts.SaveLive(RiotAccounts.LiveDataDir, RiotAccounts.StoreDir) is null && live)
                failure = "Exo could not save the signed-in Riot account, so it left it signed in.";
            else if (!RiotAccounts.Restore(RiotAccounts.LiveDataDir, RiotAccounts.StoreDir, target.Key))
                failure = "Exo could not use that account's saved sign-in. Add it again.";
        }
        catch (Exception ex)
        {
            AppLog.Warn("Riot: account switch could not be written: " + ex.GetType().Name);
            failure = "Exo could not switch the Riot account. Try again.";
        }
        AccountProgress("riot", failure is null ? $"Signing in as {target.Name}…" : "Starting the Riot Client again…");
        await StartRiotHiddenAsync(rcs, ct).ConfigureAwait(false);
        AfterAccountChange("riot");
        if (failure is not null) return new { ok = false, message = failure };
        AppLog.Info("Riot: switched the signed-in account.");
        return new { ok = true, message = $"Riot Client is signed in as {target.Name}." };
    }

    /// <summary>
    /// The Riot Client's own sign-in for another account: the signed-in account is
    /// saved first, its session files are cleared, and the Riot Client opens on its
    /// sign-in. With Stay signed in, the new account shows here and is saved on the next switch.
    /// </summary>
    private async Task<object> AddRiotAsync(CancellationToken ct)
    {
        if (RiotAdapter.TryResolveRiotClientServicesPublic() is null)
            return new { ok = false, message = "Riot Client is not installed." };
        if (await CloseRiotClientAsync(ct).ConfigureAwait(false) is { } refusal)
            return new { ok = false, message = refusal };
        try
        {
            var live = RiotAccounts.ReadLive(RiotAccounts.LiveDataDir) is not null;
            var kept = RiotAccounts.SaveLive(RiotAccounts.LiveDataDir, RiotAccounts.StoreDir);
            if (kept is not null) AppLog.Info("Riot: saved the signed-in account before adding another.");
            // Signing it out with no copy would lose it for good.
            else if (live) return new { ok = false, message = "Exo could not save the signed-in Riot account, so it left it signed in." };
            RiotAccounts.ClearLive(RiotAccounts.LiveDataDir);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Riot: could not prepare the sign-in for another account: " + ex.GetType().Name);
            return new { ok = false, message = "Exo could not open the Riot sign-in. Try again." };
        }
        AccountProgress("riot", "Opening the Riot sign-in…");
        var opened = await OpenStoreClientAsync("riot").ConfigureAwait(true);
        AppLog.Info("Riot: sign-in for another account " + (opened.Ok ? "opened." : "did not open."));
        if (!opened.Ok) return new { ok = false, message = opened.Message };
        _ = Task.Run(async () =>
        {
            // The added account shows (and is saved on the next switch) once it signs in.
            try
            {
                var until = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10);
                while (DateTimeOffset.UtcNow < until && RiotAccounts.ReadLive(RiotAccounts.LiveDataDir) is null)
                    await Task.Delay(2000, _bridgeLifetime.Token).ConfigureAwait(false);
                var added = RiotAccounts.ReadLive(RiotAccounts.LiveDataDir) is not null;
                AppLog.Info(added ? "Riot: the added account signed in." : "Riot: no account signed in within 10 minutes of Add account.");
                AfterAccountChange("riot");
            }
            catch (OperationCanceledException) { }
        });
        return new { ok = true, message = "Sign in to the other account in the Riot Client with Stay signed in checked." };
    }

    private static object RemoveRiot(string? key)
    {
        var name = RiotAccounts.List(RiotAccounts.StoreDir).FirstOrDefault(account => account.Key == key)?.Name;
        try
        {
            if (name is null || !RiotAccounts.Remove(RiotAccounts.StoreDir, key!)) return new { ok = true, message = (string?)null };
        }
        catch (Exception ex)
        {
            AppLog.Warn("Riot: saved account could not be removed: " + ex.GetType().Name);
            return new { ok = false, message = (string?)"Exo could not remove that account. Try again." };
        }
        AppLog.Info("Riot: a saved account was removed.");
        return new { ok = true, message = (string?)$"Exo forgot {name}'s saved sign-in." };
    }

    private async Task<object> SwitchEpicAsync(CancellationToken ct)
    {
        if (EpicAdapter.ResolveLegendary() is not { } legendary)
            return new { ok = false, message = "Epic sign-in is not available right now. Try again." };
        // The switch signs in on a fresh profile; the shared one would bring the old account back on the next Sign in.
        if (!EpicAuthService.ForgetBrowserSignIn())
            return new { ok = false, message = EpicBrowserBusy };
        if (!await EpicAccounts.SignOutMainAsync(legendary, ct).ConfigureAwait(true))
            return new { ok = false, message = "Epic sign-out did not finish. Try again." };
        var result = await _services.EpicAuth.SignInAsync(legendary, ct, otherAccount: true).ConfigureAwait(true);
        AfterAccountChange("epic");
        return new { ok = result.Ok, message = result.Ok ? "Epic account switched." : result.Message + " Exo is signed out of Epic." };
    }

    private (bool Ok, string Message) SignOutGog()
    {
        var path = GogAuthService.FindExistingAuthConfigPath();
        if (path is null) return (true, "Signed out of GOG.");
        if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(GogAuthService.AuthConfigPath), StringComparison.OrdinalIgnoreCase))
            return (false, "GOG is signed in through Heroic. Sign out there.");
        try
        {
            GogAuthService.MarkSignedOut();
            File.Delete(path);
            // The sign-in page would otherwise sign the same account back in silently.
            try
            {
                var browser = Path.Combine(PathHelper.AppDataDir, "gog-webview");
                if (Directory.Exists(browser)) Directory.Delete(browser, recursive: true);
            }
            catch (Exception ex) { AppLog.Warn("GOG sign-in browser profile was busy: " + ex.GetType().Name); }
            return (true, "Signed out of GOG.");
        }
        catch (Exception ex)
        {
            AppLog.Warn("GOG sign-out failed: " + ex.GetType().Name);
            return (false, "GOG sign-out did not finish. Try again.");
        }
    }

    private async Task<object> SwitchGogAsync(CancellationToken ct)
    {
        // gogdl has no sign-out of its own; the sign-in browser forgets GOG's page too.
        // A profile that could not be emptied would sign in as the last account again.
        try
        {
            var browser = Path.Combine(PathHelper.AppDataDir, "gog-webview");
            if (Directory.Exists(browser)) Directory.Delete(browser, recursive: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("GOG sign-in browser profile was busy: " + ex.GetType().Name);
            return new { ok = false, message = "GOG sign-in is still closing. Try again in a moment." };
        }
        var (ok, message) = SignOutGog();
        if (!ok) return new { ok, message };
        if (GogAdapter.ResolveGogdl() is not { } gogdl)
            return new { ok = false, message = "GOG sign-in is not available right now. Try again." };
        var result = await _services.GogAuth.SignInAsync(gogdl, ct).ConfigureAwait(true);
        AfterAccountChange("gog");
        return new { ok = result.Ok, message = result.Ok ? "GOG account switched." : result.Message + " Exo is signed out of GOG." };
    }

    private async Task<(bool Ok, string Message)> SignOutAmazonAsync(CancellationToken ct)
    {
        if (AmazonAdapter.ResolveNile() is not { } nile)
            return (false, "Amazon sign-out is not available right now. Try again.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var (exit, _, _) = await CliRunner.RunAsync(nile, NileCli.AuthLogoutArgs(), null, null, timeout.Token).ConfigureAwait(false);
            AppLog.Info($"Amazon: nile auth --logout exited {exit}.");
            return exit == 0 ? (true, "Signed out of Amazon.") : (false, "Amazon sign-out did not finish. Try again.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            AppLog.Warn("Amazon sign-out failed: " + ex.GetType().Name);
            return (false, "Amazon sign-out did not finish. Try again.");
        }
    }

    private async Task<object> SwitchAmazonAsync(CancellationToken ct)
    {
        var (ok, message) = await SignOutAmazonAsync(ct).ConfigureAwait(true);
        if (!ok) return new { ok, message };
        if (_services.FindAdapterById("amazon") is not { } adapter)
            return new { ok = false, message = "Amazon sign-in is not available right now." };
        var result = await adapter.AuthenticateAsync(ct).ConfigureAwait(true);
        AfterAccountChange("amazon");
        return new { ok = result.Ok, message = result.Ok ? "Amazon account switched." : result.Message + " Exo is signed out of Amazon." };
    }

    /// <summary>A store's account changed: its sign-in state and library are read again.</summary>
    private void AfterAccountChange(string? store)
    {
        _services.Library.InvalidateStoreMatrix();
        _services.Library.Invalidate();
        _ = Task.Run(async () =>
        {
            try { await _services.Library.GetLibraryAsync(force: true).ConfigureAwait(false); }
            catch (Exception ex) { AppLog.Warn($"Library refresh after a {store} account change failed: " + ex.GetType().Name); }
        });
    }
}
