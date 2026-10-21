using System.Text.Json;

namespace ExoLauncher.Services;

/// <summary>The game sheet's Shaders line (shaders.status) and shaders.changed, both from the Exo Shaders engine.</summary>
public sealed partial class WebHostBridge
{
    private async Task<object> ShadersStatusAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var game = await FindGameAsync(ReadString(p, hasParams, "id"), ct).ConfigureAwait(false);
        if (game is null) return new { ok = false, state = "error", reason = "Game not found." };
        var status = await Task.Run(() => _services.Shaders.For(game), ct).ConfigureAwait(false);
        return new
        {
            ok = true,
            state = status.State,
            reason = status.Reason,
            engineId = status.EngineId,
            warmed = status.WarmedAt is { } at
                ? new { at = at.ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture), driver = status.Driver }
                : null,
        };
    }

    /// <summary>shaders.estimates: how long each game the engine lists takes to compile here, in seconds, by its engine id.</summary>
    private async Task<object> ShadersEstimatesAsync(CancellationToken ct) =>
        new { ok = true, seconds = await Task.Run(_services.Shaders.CompileSeconds, ct).ConfigureAwait(false) };

    private void OnShadersChanged() => PostEvent("shaders.changed", new { });
}
