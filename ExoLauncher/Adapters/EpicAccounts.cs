using ExoLauncher.Adapters.Cli;
using ExoLauncher.Helpers;

namespace ExoLauncher.Adapters;

/// <summary>
/// Epic accounts beside Exo's own Legendary sign-in. Each added account has its
/// own Legendary config folder (LEGENDARY_CONFIG_PATH) under Exo's data folder,
/// named by its one-way scope; Legendary writes and refreshes its session there.
/// Added accounts only bring their Epic playtime: games install and launch with
/// Exo's own Epic account, because Legendary keeps its installed-games list per
/// config folder. Account ids, tokens and names are never logged.
/// </summary>
internal static class EpicAccounts
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CliTimeout = TimeSpan.FromSeconds(60);
    private static int _refreshing;
    // A refresh and a removal never run Legendary in, or write the reading of, the same folder at once.
    private static readonly SemaphoreSlim FolderGate = new(1, 1);
    private static DateTimeOffset _refreshedAt = DateTimeOffset.MinValue;

    internal sealed record Added(string Scope, string? DisplayName, string Folder);

    internal static string Root => Path.Combine(PathHelper.AppDataDir, "epic-accounts");

    /// <summary>The Legendary config folder Exo's own Epic sign-in lives in.</summary>
    internal static string MainConfigFolder()
    {
        var custom = Environment.GetEnvironmentVariable("LEGENDARY_CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(custom)) return custom;
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdg)) return Path.Combine(xdg, "legendary");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "legendary");
    }

    internal static IReadOnlyDictionary<string, string> EnvironmentFor(string folder) =>
        new Dictionary<string, string> { ["LEGENDARY_CONFIG_PATH"] = folder };

    internal static EpicPlaytime.Session? ReadSession(string folder)
    {
        var json = SteamPlaytime.ReadSharedText(Path.Combine(folder, "user.json"));
        return EpicPlaytime.ParseSessionJson(json);
    }

    /// <summary>Added accounts whose folder holds a session for the account its name says.</summary>
    internal static IReadOnlyList<Added> List()
    {
        var result = new List<Added>();
        try
        {
            if (!Directory.Exists(Root)) return result;
            foreach (var folder in Directory.EnumerateDirectories(Root))
            {
                var scope = Path.GetFileName(folder);
                if (!IsScope(scope)) continue;
                var (id, name) = EpicPlaytime.ReadUserIdentity(SteamPlaytime.ReadSharedText(Path.Combine(folder, "user.json")));
                if (id is null || EpicPlaytime.AccountScopeFor(id) != scope) continue;
                result.Add(new Added(scope, name, folder));
            }
        }
        catch (Exception ex) { AppLog.Debug("Epic added accounts unreadable: " + ex.GetType().Name); }
        return result;
    }

    internal static bool IsScope(string? value) =>
        value is { Length: 20 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>A new, empty folder an added account signs in to.</summary>
    internal static string NewPendingFolder()
    {
        var folder = Path.Combine(Root, "pending-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>
    /// Moves a pending sign-in to its scope folder. Exo's own account or one
    /// already added is not added twice. Returns the display name, or an error line.
    /// </summary>
    internal static (bool Ok, string Message) Adopt(string pending, string? mainScope)
    {
        try
        {
            var (id, name) = EpicPlaytime.ReadUserIdentity(SteamPlaytime.ReadSharedText(Path.Combine(pending, "user.json")));
            if (id is null) return (false, "Epic sign-in did not finish. Try again.");
            var scope = EpicPlaytime.AccountScopeFor(id);
            if (scope == mainScope) return (false, "That is already Exo's Epic account.");
            var target = Path.Combine(Root, scope);
            if (Directory.Exists(target))
            {
                return (false, "That account is already added.");
            }
            Directory.Move(pending, target);
            return (true, name ?? "Epic account");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Epic added account could not be saved: " + ex.GetType().Name);
            return (false, "Exo could not add that account. Try again.");
        }
        finally
        {
            Discard(pending);
        }
    }

    /// <summary>Signs the added account out with Legendary's own command, then removes its folder and reading.</summary>
    internal static async Task<bool> RemoveAsync(string scope, CancellationToken ct)
    {
        await FolderGate.WaitAsync(ct).ConfigureAwait(false);
        try { return await RemoveCoreAsync(scope, ct).ConfigureAwait(false); }
        finally { FolderGate.Release(); }
    }

    private static async Task<bool> RemoveCoreAsync(string scope, CancellationToken ct)
    {
        var added = List().FirstOrDefault(account => account.Scope == scope);
        if (added is null) return false;
        if (EpicAdapter.ResolveLegendary() is { } legendary)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(CliTimeout);
                await CliRunner.RunAsync(legendary, LegendaryCli.AuthDeleteArgs(), null, null, timeout.Token, EnvironmentFor(added.Folder))
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                AppLog.Debug("Epic added account sign-out: " + ex.GetType().Name);
            }
        }
        Discard(added.Folder);
        EpicPlaytime.ForgetPersistedMinutes(scope);
        EpicPlaytime.RaiseOtherAccountsUpdated();
        return true;
    }

    /// <summary>Signs Exo's own Epic account out with <c>legendary auth --delete</c>.</summary>
    internal static async Task<bool> SignOutMainAsync(string legendary, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CliTimeout);
            var (exit, _, _) = await CliRunner.RunAsync(legendary, LegendaryCli.AuthDeleteArgs(), null, null, timeout.Token)
                .ConfigureAwait(false);
            AppLog.Info($"Epic: legendary auth --delete exited {exit}.");
            return exit == 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            AppLog.Warn("Epic sign-out failed: " + ex.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// Takes the Epic Games Launcher's sign-in with <c>legendary auth --import</c>
    /// (which signs the launcher out). It imports into an empty folder first, so a
    /// failed import leaves Exo's current sign-in as it was; on success Legendary's
    /// own session file replaces the current one.
    /// </summary>
    internal static async Task<(bool Ok, string Message)> ImportLauncherAccountAsync(string legendary, CancellationToken ct)
    {
        var pending = NewPendingFolder();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CliTimeout);
            var (exit, _, _) = await CliRunner.RunAsync(legendary, LegendaryCli.AuthImportArgs(), null, null, timeout.Token, EnvironmentFor(pending))
                .ConfigureAwait(false);
            AppLog.Info($"Epic: legendary auth --import exited {exit}.");
            var imported = Path.Combine(pending, "user.json");
            var (id, name) = EpicPlaytime.ReadUserIdentity(SteamPlaytime.ReadSharedText(imported));
            if (exit != 0 || id is null)
                return (false, "The Epic Games Launcher's sign-in could not be used. Use Switch account instead.");

            var main = MainConfigFolder();
            Directory.CreateDirectory(main);
            File.Move(imported, Path.Combine(main, "user.json"), overwrite: true);
            return (true, name ?? "Epic account");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            AppLog.Warn("Epic launcher sign-in import failed: " + ex.GetType().Name);
            return (false, "The Epic Games Launcher's sign-in could not be used. Use Switch account instead.");
        }
        finally
        {
            Discard(pending);
        }
    }

    /// <summary>
    /// Fetches every added account's Epic playtime, at most every ten minutes
    /// (<paramref name="force"/>: now, for a just-added account). One run at a
    /// time: Legendary must not refresh one config folder twice at once.
    /// </summary>
    internal static void RefreshAddedInBackground(bool force = false)
    {
        if (!force && DateTimeOffset.UtcNow - _refreshedAt < RefreshEvery) return;
        if (Interlocked.Exchange(ref _refreshing, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try { await RefreshAddedAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { AppLog.Debug("Epic added accounts refresh failed: " + ex.GetType().Name); }
            finally
            {
                _refreshedAt = DateTimeOffset.UtcNow;
                Interlocked.Exchange(ref _refreshing, 0);
            }
        });
    }

    private static async Task RefreshAddedAsync(CancellationToken ct)
    {
        await FolderGate.WaitAsync(ct).ConfigureAwait(false);
        try { await RefreshAddedCoreAsync(ct).ConfigureAwait(false); }
        finally { FolderGate.Release(); }
    }

    private static async Task RefreshAddedCoreAsync(CancellationToken ct)
    {
        var accounts = List();
        if (accounts.Count == 0) return;
        var legendary = EpicAdapter.ResolveLegendary();
        var changed = false;
        foreach (var account in accounts)
        {
            var session = ReadSession(account.Folder);
            if (session is null) continue;
            if (EpicPlaytime.AccessTokenNeedsRefresh(session, DateTimeOffset.UtcNow) && legendary is not null)
            {
                // Legendary refreshes and saves its own session; Exo never writes tokens.
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(CliTimeout);
                    await CliRunner.RunJsonAsync(legendary, LegendaryCli.StatusJsonArgs(), null, timeout.Token, EnvironmentFor(account.Folder))
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    AppLog.Debug("Epic added account refresh: " + ex.GetType().Name);
                }
                session = ReadSession(account.Folder) ?? session;
            }
            var minutes = await EpicPlaytime.FetchMinutesAsync(session, ct).ConfigureAwait(false);
            if (minutes is null) continue;
            var before = EpicPlaytime.LoadPersistedMinutes(account.Scope);
            EpicPlaytime.PersistMinutes(account.Scope, minutes);
            changed |= before.Count != minutes.Count || minutes.Any(pair => before.GetValueOrDefault(pair.Key) != pair.Value);
        }
        if (changed) EpicPlaytime.RaiseOtherAccountsUpdated();
    }

    internal static void Discard(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception ex) { AppLog.Debug("Epic account folder cleanup: " + ex.GetType().Name); }
    }
}
