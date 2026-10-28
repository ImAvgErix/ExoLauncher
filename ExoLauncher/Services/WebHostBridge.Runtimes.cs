using System.Text.Json;

namespace ExoLauncher.Services;

/// <summary>
/// Game runtimes (<see cref="DependencyService"/>). runtimes.list: each runtime this PC has or lacks, with the newest
/// version; runtimes.install: the ones a mode picks (missing, update, repair), or the named ids, downloaded with
/// runtimes.progress events and installed in one elevated run; runtimes.cancel stops the downloads.
/// </summary>
public sealed partial class WebHostBridge
{
    // The installed games say which of XNA, OpenAL and PhysX are worth installing.
    private Task<object> RuntimesListAsync(CancellationToken ct) =>
        _services.Dependencies.ListAsync([.. _services.Library.AllSourceEntries().Where(game => game.Installed)], ct);

    private async Task<object> RuntimesInstallAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var ids = hasParams && p.TryGetProperty("ids", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(id => id.ValueKind == JsonValueKind.String).Select(id => id.GetString()!).Take(64).ToArray()
            : null;
        var mode = ReadString(p, hasParams, "mode") ?? "missing";
        var result = await _services.Dependencies.InstallAsync(ids, mode, progress => PostEvent("runtimes.progress", new
        {
            phase = progress.Phase,
            id = progress.Id,
            name = progress.Name,
            index = progress.Index,
            count = progress.Count,
            bytes = progress.Bytes,
            total = progress.Total,
            doneBytes = progress.DoneBytes,
            allBytes = progress.AllBytes,
        }), ct).ConfigureAwait(false);
        return new
        {
            ok = result.Ok,
            message = result.Message,
            declined = result.Declined,
            canceled = result.Canceled,
            results = result.Results.Select(r => new { id = r.Id, name = r.Name, outcome = r.Outcome, message = r.Message, code = r.Code }),
            items = result.Items,
        };
    }
}
