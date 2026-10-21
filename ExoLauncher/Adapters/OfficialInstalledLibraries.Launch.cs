using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

internal static partial class OfficialInstalledLibraries
{
    internal static async Task<LaunchResult> LaunchWithOptionsAsync(
        GameEntry game, LaunchOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (game.Store == StoreKind.Xbox)
        {
            var xbox = await ResolveXboxLaunchAsync(game, ct).ConfigureAwait(false);
            if (xbox.Kind == "unavailable")
                return new LaunchResult { Ok = false, Message = "Xbox package activation is unavailable. Repair this title in the Xbox app, then refresh Exo." };
            if (xbox.Kind is "package" or "helper")
            {
                if (HasExecutableOverrides(options))
                    return new LaunchResult { Ok = false, Message = "Xbox package activation does not accept Exo executable overrides. Clear this game's launch overrides first." };
                return LaunchXboxActivation(xbox);
            }
        }
        var direct = UsesDirectExecutable(game.Store, game.LaunchTarget);
        // Rockstar's client command preserves its activation and entitlement
        // handling whenever that proven launcher is available.
        if (game.Store == StoreKind.Rockstar && FindRockstarLauncher() is { } launcher && File.Exists(launcher))
            direct = false;
        if (direct)
        {
            if (string.IsNullOrWhiteSpace(game.LaunchTarget) || !File.Exists(game.LaunchTarget))
                return MissingTarget(game.Store.ToString());
            try
            {
                var root = !string.IsNullOrWhiteSpace(game.Path) && Directory.Exists(game.Path)
                    ? game.Path : Path.GetDirectoryName(game.LaunchTarget);
                var before = ProcessHelper.SnapshotLiveProcessIdsUnderPath(root);
                using var started = ProcessHelper.StartGame(game.LaunchTarget, options);
                var confirmed = await ProcessHelper.ConfirmDirectLaunchAsync(started, root, before, ct).ConfigureAwait(false);
                return DirectLaunchOutcome(confirmed);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return new LaunchResult { Ok = false, Message = Cli.CliFailure.Launch(game.Title, ex) }; }
        }
        if (HasExecutableOverrides(options))
            return new LaunchResult
            {
                Ok = false,
                Message = "This store launches through its client, which does not accept Exo executable overrides. Clear this game's launch overrides or configure them in the store client.",
            };
        if (game.Store == StoreKind.BattleNet)
            return await LaunchBattleNetAsync(game, ct).ConfigureAwait(false);
        var result = game.Store switch
        {
            StoreKind.Xbox => LaunchXbox(game),
            StoreKind.Ea => LaunchEa(game),
            StoreKind.Ubisoft => LaunchUbisoft(game),
            StoreKind.Amazon => LaunchAmazon(game),
            StoreKind.Rockstar => LaunchRockstar(game),
            StoreKind.Itch => LaunchItch(game),
            StoreKind.Minecraft => LaunchMinecraft(game),
            StoreKind.Roblox => LaunchRoblox(game),
            StoreKind.Paradox => LaunchParadox(game),
            StoreKind.Wargaming => LaunchWargaming(game),
            _ => MissingTarget(game.Store.ToString()),
        };
        return result.Ok ? ClientHandoffOutcome(result.Message) : result;
    }

    internal static bool UsesDirectExecutable(StoreKind store, string? target) =>
        store is StoreKind.Xbox or StoreKind.Amazon or StoreKind.Itch or StoreKind.Rockstar or
            StoreKind.Roblox or StoreKind.Paradox or StoreKind.Wargaming or StoreKind.Minecraft &&
        !string.IsNullOrWhiteSpace(target) && Path.IsPathFullyQualified(target) &&
        !ProcessHelper.IsNonGameProcessName(Path.GetFileNameWithoutExtension(target));

    internal static bool HasExecutableOverrides(LaunchOptions options) =>
        options.RunAsAdmin || !string.IsNullOrWhiteSpace(options.ExtraArgs) ||
        !string.IsNullOrWhiteSpace(options.WorkingDirectory);

    internal static LaunchResult DirectLaunchOutcome(int? confirmedPid) => new()
    {
        Ok = confirmedPid is > 0,
        Message = confirmedPid is > 0 ? "Game started." : "The game exited before its launch could be confirmed.",
        ProcessId = confirmedPid,
    };

    internal static LaunchResult ClientHandoffOutcome(string message) => new()
    {
        Ok = true,
        HandoffOnly = true,
        Message = message + " Waiting for the game to appear.",
    };
}
