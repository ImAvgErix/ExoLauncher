using System.Text.Json;
using ExoLauncher.Adapters;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Hours Xbox reports through OpenXBL, with the player's key. Read again about
/// 15 seconds after Exo starts when the last reading is over 12 hours old, on
/// Library › Refresh, and when the key is saved.
/// </summary>
public sealed partial class WebHostBridge
{
    private int _reportedHoursRefreshing;

    /// <summary>openXblKey from settings.set. A changed or removed key drops Xbox hours.</summary>
    private void ApplyReportedHoursSettings(JsonElement p)
    {
        if (!p.TryGetProperty("openXblKey", out var key) || key.ValueKind != JsonValueKind.String) return;
        if (!CatalogApiKeyStore.SaveOpenXbl(key.GetString()))
            throw new InvalidOperationException("OpenXBL key was not saved.");
        ReportedHours.ClearXbox();
        // The old account's hours leave the library now, not at the next refresh.
        _ = Task.Run(async () =>
        {
            try { await _services.Library.RefreshDerivedStateAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { AppLog.Debug("Reported hours library refresh skipped: " + ex.GetType().Name); }
        });
    }

    private Task<object> PlaytimeRefreshAsync(JsonElement p, bool hasParams) =>
        RefreshReportedHoursAsync(ReadString(p, hasParams, "source") ?? "", _bridgeLifetime.Token);

    /// <summary>One source: "xbox". The library is re-read once hours land.</summary>
    private async Task<object> RefreshReportedHoursAsync(string source, CancellationToken ct)
    {
        if (!source.Equals("xbox", StringComparison.OrdinalIgnoreCase))
            return new { ok = false, profilePrivate = false, message = "Unknown hours source." };
        if (Interlocked.CompareExchange(ref _reportedHoursRefreshing, 1, 0) != 0)
            return new { ok = false, profilePrivate = false, message = "Hours are already being read." };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var key = CatalogApiKeyStore.TryReadOpenXbl();
            if (key is null) return new { ok = false, profilePrivate = false, message = "Save an OpenXBL key first." };
            var result = await XboxPlaytime.LoadAsync(key, timeout.Token).ConfigureAwait(false);
            if (result.Ok)
            {
                ReportedHours.SaveXbox(result.Titles);
                try { await _services.Library.RefreshDerivedStateAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { AppLog.Debug("Reported hours library refresh skipped: " + ex.GetType().Name); }
            }
            return new { ok = result.Ok, profilePrivate = false, message = result.Message };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new { ok = false, profilePrivate = false, message = "The service did not answer in time." };
        }
        finally
        {
            Interlocked.Exchange(ref _reportedHoursRefreshing, 0);
        }
    }

    /// <summary>Xbox when a key is saved; when not forced, only if last read over 12 hours ago.</summary>
    private async Task RefreshAllReportedHoursAsync(bool force, CancellationToken ct)
    {
        var at = ReportedHours.Load().XboxAt;
        if (!CatalogApiKeyStore.HasOpenXbl() || !(force || at is null || DateTimeOffset.UtcNow - at > TimeSpan.FromHours(12)))
            return;
        ct.ThrowIfCancellationRequested();
        try { await RefreshReportedHoursAsync("xbox", ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { AppLog.Debug("Hours from xbox skipped: " + ex.GetType().Name); }
    }

    private void ScheduleReportedHoursRefresh()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), _bridgeLifetime.Token).ConfigureAwait(false);
                if (_detached) return;
                await RefreshAllReportedHoursAsync(force: false, _bridgeLifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* host is shutting down */ }
            catch (Exception ex) { AppLog.Debug("Reported hours refresh skipped: " + ex.GetType().Name); }
        });
    }
}
