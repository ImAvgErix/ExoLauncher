using System.Text.Json.Nodes;
using ExoLauncher.Helpers;

namespace ExoLauncher;

/// <summary>
/// Capture shrink: recordings are re-encoded by Exo Capture to files a third the size that look the same
/// (<see cref="Services.CaptureShrink"/>), one at a time, biggest first, only while no game runs, nothing is being recorded and a laptop is plugged in, at the add-on's
/// below-normal priority. A game starting stops it. A recording the add-on refuses is remembered in the index so it
/// is not asked again; the original is replaced only by a file the add-on checked.
/// </summary>
public sealed partial class MainWindow
{
    private int _shrinkRunning;
    private CancellationTokenSource? _shrinkCts;
    private string? _shrinkPath;

    /// <summary>Sooner or later, once: after a recording is saved, a game ends, the setting turns on or Exo Capture starts.</summary>
    private void ScheduleShrink(TimeSpan delay)
    {
        if (!CaptureConfig.AutoShrink || !Services.CaptureAddon.Present) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, _lifetimeCts.Token).ConfigureAwait(false);
                await ShrinkAsync(null).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppLog.Warn("Capture shrink: " + ex.GetType().Name + ": " + ex.Message); }
        });
    }

    /// <summary>A game is starting: the add-on stops the shrink within a second and leaves the original alone.</summary>
    private void CancelShrink()
    {
        var path = Volatile.Read(ref _shrinkPath);
        if (path is null || _captureAddon is not { } addon) return;
        _ = addon.CallAsync("shrink.cancel", new JsonObject { ["input"] = path }, CancellationToken.None)
            .ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private string? ShrinkBlocked() =>
        Services.CaptureShrink.Blocked(_captureGameId is not null || RunningLibraryGames().Count > 0,
            _captureState?["recording"]?.GetValue<bool>() == true);

    /// <summary>capture.item shrink: this recording now, whatever the setting says. The answer comes as capture.shrunk.</summary>
    private object StartManualShrink(string path, string relative)
    {
        if (!path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) return new { ok = false, message = "Only recordings can be made smaller." };
        if (CaptureAddon is null) return new { ok = false, message = "Exo Capture is not installed." };
        if (Volatile.Read(ref _shrinkRunning) == 1) return new { ok = false, message = "Exo is already making a recording smaller." };
        if (ShrinkBlocked() is { } why) return new { ok = false, message = why };
        _ = Task.Run(async () =>
        {
            try { await ShrinkAsync(relative).ConfigureAwait(false); }
            catch (Exception ex) { AppLog.Warn("Capture shrink: " + ex.GetType().Name + ": " + ex.Message); }
        });
        return new { ok = true, message = "Making it smaller. This takes a few minutes; Exo says when it is done." };
    }

    /// <summary>One at a time. <paramref name="only"/> is a single recording (asked for); null takes what is pending.</summary>
    private async Task ShrinkAsync(string? only)
    {
        if (Interlocked.CompareExchange(ref _shrinkRunning, 1, 0) != 0) return;
        var asked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            while (!_closingWindow)
            {
                if (only is null && (!CaptureConfig.AutoShrink || OnBattery())) return;
                var relative = only ?? NextShrink(asked);
                if (relative is null) return;
                var path = Path.Combine(CaptureFolder, relative);
                if (CaptureAddon is not { } addon || ShrinkBlocked() is { } why)
                {
                    if (only is not null) Said(path, ShrinkBlocked() ?? "Exo Capture is not installed.");
                    return;
                }
                asked.Add(relative);
                if (!File.Exists(path))
                {
                    if (only is not null) { Said(path, "The recording is gone."); return; }
                    continue;
                }
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
                Volatile.Write(ref _shrinkPath, path);
                _shrinkCts = cts;
                bool stop;
                try { stop = await ShrinkOneAsync(addon, path, relative, only is not null, cts.Token).ConfigureAwait(false); }
                finally { Volatile.Write(ref _shrinkPath, null); _shrinkCts = null; }
                if (stop || only is not null) return;
            }
        }
        finally { Interlocked.Exchange(ref _shrinkRunning, 0); }
    }

    private static bool OnBattery() => Services.PowerPlan.Shared.Read()?.Battery == true;

    private string? NextShrink(HashSet<string> asked)
    {
        lock (_captureGate)
        {
            var videos = CaptureFiles().Where(f => f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                .Select(f => (Info: new FileInfo(f), Relative: RelativeCapture(f)))
                .Where(f => f.Relative is not null && CaptureIndex.ContainsKey(f.Relative) && !asked.Contains(f.Relative))
                .Select(f => (f.Relative!, f.Info.LastWriteTimeUtc, f.Info.Length, CaptureIndex[f.Relative!].Shrink));
            return Services.CaptureShrink.Pending(videos, DateTime.UtcNow).FirstOrDefault();
        }
    }

    /// <summary>True when the rest should wait (the add-on stopped, or the shrink was cancelled).</summary>
    private async Task<bool> ShrinkOneAsync(Services.CaptureAddon addon, string path, string relative, bool asked, CancellationToken ct)
    {
        JsonNode? reply = null;
        try
        {
            // A first look at the recording's own quality; a refusal for quality loss gets one try that stays closer.
            foreach (var target in new[] { 70, 90 })
            {
                reply = await addon.CallAsync("shrink", new JsonObject { ["input"] = path, ["codec"] = "auto", ["targetQuality"] = target }, ct).ConfigureAwait(false);
                if (reply?["skipped"]?.GetValue<string>() != "quality-loss") break;
            }
        }
        catch (OperationCanceledException) { return true; }
        catch (InvalidOperationException ex)
        {
            AppLog.Warn("Capture shrink: " + ex.Message);
            return true;
        }
        var skipped = reply?["skipped"]?.GetValue<string>();
        if (reply?["ok"]?.GetValue<bool>() != true)
        {
            skipped ??= "unknown";
            if (!Services.CaptureShrink.Later(skipped)) Remember(relative, skipped);
            if (asked) Said(path, Services.CaptureShrink.Say(skipped));
            return skipped == "cancelled";
        }
        var output = reply["output"]?.GetValue<string>();
        var before = new FileInfo(path).Length;
        if (output is null || RelativeCapture(output) is null || !output.EndsWith(".shrink.mp4", StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Warn("Capture shrink: the add-on named an output outside the captures folder.");
            return true;
        }
        var problem = Services.CaptureShrink.Replace(path, output, RecycleFile);
        if (problem is not null)
        {
            try { File.Delete(output); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (asked) Said(path, problem);
            return false;
        }
        Remember(relative, Services.CaptureShrink.DoneMark);
        var after = new FileInfo(path).Length;
        AppLog.Info($"Capture shrink: a recording went from {before / 1_048_576} MB to {after / 1_048_576} MB.");
        _bridge?.PostCaptureEvent("capture.shrunk", new { id = CaptureId(path), item = ItemFor(path), before, after, message = (string?)null });
        return false;
    }

    /// <summary>The answer to a recording the user asked about, when nothing was changed.</summary>
    private void Said(string path, string message) =>
        _bridge?.PostCaptureEvent("capture.shrunk", new { id = CaptureId(path), item = (object?)null, before = 0L, after = 0L, message });

    private void Remember(string relative, string mark)
    {
        lock (_captureGate)
        {
            if (CaptureIndex.TryGetValue(relative, out var meta)) meta.Shrink = mark;
        }
        try { SaveCaptureIndex(); } catch (Exception ex) { AppLog.Warn("Capture index: " + ex.Message); }
    }
}
