using System.Globalization;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Adapters;

/// <summary>
/// Steam ownership comes from the signed-in Steam client. Exo never starts
/// Steam to ask: Steam starts when the user plays, installs, updates, verifies
/// or switches accounts. When a scan finds Steam running but not answering yet
/// (still signing in), this waits in the background until the client answers
/// the ownership query, then asks for a Steam rescan. The library scan never
/// waits on this.
/// </summary>
internal static class SteamClientReady
{
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromSeconds(90);
    private static int _watching;

    /// <summary>Raised once a client that was still signing in answers ownership queries.</summary>
    internal static event Action? ClientReady;

    /// <summary>
    /// When Steam is running, waits for it to answer <paramref name="probeAppIds"/>
    /// through the IPC helper. One wait at a time; returns true when this call
    /// started it. A closed Steam stays closed.
    /// </summary>
    internal static bool WatchIfRunning(IReadOnlyList<string> probeAppIds)
    {
        if (!ProcessHelper.IsProcessRunning("steam")) return false;
        if (Interlocked.Exchange(ref _watching, 1) != 0) return false;
        _ = Task.Run(() => RunAsync(probeAppIds.Take(8).ToArray()));
        return true;
    }

    private static async Task RunAsync(IReadOnlyList<string> probeAppIds)
    {
        try
        {
            var started = DateTimeOffset.UtcNow;
            var deadline = started + SignInTimeout;
            while (ReadActiveUser() == 0)
            {
                if (DateTimeOffset.UtcNow >= deadline || !ProcessHelper.IsProcessRunning("steam")) return;
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }

            // ActiveUser flips before the client's license list is loaded.
            // Poll the same IPC query the scan uses until it answers.
            var answered = probeAppIds.Count == 0;
            while (!answered && DateTimeOffset.UtcNow < deadline + TimeSpan.FromSeconds(30))
            {
                answered = await SteamClientIpc.QueryOwnedAsync(probeAppIds, CancellationToken.None, batchTimeout: TimeSpan.FromSeconds(6))
                    .ConfigureAwait(false) is not null;
                if (!answered) await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            if (!answered) return;

            AppLog.Info($"Steam client answered after {(DateTimeOffset.UtcNow - started).TotalSeconds:0}s; rescanning Steam ownership.");
            try { ClientReady?.Invoke(); }
            catch (Exception ex) { AppLog.Debug("Steam ready listener failed: " + ex.Message); }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Steam ownership wait failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally { Interlocked.Exchange(ref _watching, 0); }
    }

    internal static uint ReadActiveUser()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            return key?.GetValue("ActiveUser") switch
            {
                int signed => unchecked((uint)signed),
                uint unsigned => unsigned,
                long wide when wide is > 0 and <= uint.MaxValue => (uint)wide,
                string text when uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => 0,
            };
        }
        catch
        {
            return 0;
        }
    }
}
