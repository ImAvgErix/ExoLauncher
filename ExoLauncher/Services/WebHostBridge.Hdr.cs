using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

public sealed partial class WebHostBridge
{
    /// <summary>
    /// System > Graphics > HDR: whether the main screen can show HDR, how bright it really gets
    /// (its EDID), whether Windows holds an HDR calibration for it, and a plain verdict.
    /// </summary>
    private object HdrState()
    {
        if (WindowsHdr.Primary() is not { Supported: true } screen)
            return new { supported = false };
        var nits = WindowsHdr.PeakNits(screen);
        var verdict = nits switch
        {
            null => "unknown",
            >= 600 => "great",
            >= 400 => "good",
            _ => "weak",
        };
        return new
        {
            supported = true,
            on = screen.Enabled,
            name = string.IsNullOrWhiteSpace(screen.Name) ? null : screen.Name,
            peakNits = nits,
            calibrated = WindowsHdr.HasHdrProfile(screen, MainWindow.SdrGammaReplaced(_services.Settings, screen.DevicePath)),
            verdict,
        };
    }

    /// <summary>Game sheet > HDR: what PCGamingWiki says about the game, and its Auto/On/Off choice (set when "mode" is passed).</summary>
    private async Task<object> HdrGameAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var game = await FindGameAsync(ReadString(p, hasParams, "id"), ct).ConfigureAwait(false);
        if (game is null) return new { ok = false, message = "Game not found." };
        if (ReadString(p, hasParams, "mode") is { } mode)
        {
            if (mode is not ("auto" or "on" or "off")) return new { ok = false, message = "Unknown HDR choice." };
            _services.Settings.SetHdrGameMode(game.Id, mode);
        }
        var hdr = await HdrGames.LookupAsync(game, ct).ConfigureAwait(false);
        return new { ok = true, hdr, mode = _services.Settings.HdrGameMode(game.Id) };
    }
}
