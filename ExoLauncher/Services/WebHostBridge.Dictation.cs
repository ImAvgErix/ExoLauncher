using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Dictation (<see cref="Dictation"/>): dictation.state (on, whether this PC can run it, whether its speech model is here
/// and whole, the model's download size); dictation.prepare (the model now, when the mic was pressed before Exo had it:
/// dictation.progress as it comes); dictation.start; dictation.stop (before, after: the characters either side of the
/// caret, for the spacing) with the text to insert; dictation.cancel. While a take listens, dictation.level about 20 times
/// a second; dictation.ended when it ends by itself (no microphone, or a minute heard); dictation.state when the setting
/// or the model changes. components.failed: one of Exo's parts (<see cref="ExoComponents"/>) failed twice.
/// </summary>
public sealed partial class WebHostBridge
{
    private long _dictationProgressAt;

    private object DictationState() => new
    {
        on = _services.Settings.Current.Dictation,
        available = DictationRecognizer.Available,
        ready = DictationRecognizer.Available && DictationModel.Ready(),
        size = DictationModel.Component.Download,
    };

    private void PostDictationState() => _ = Task.Run(() => PostEvent("dictation.state", DictationState()));

    private async Task<object> DictationPrepareAsync(CancellationToken ct)
    {
        if (!DictationRecognizer.Available) return new { ok = false, message = (string?)"Dictation doesn't run on this PC." };
        try
        {
            await ExoComponents.FetchAsync(DictationModel.Component, ct).ConfigureAwait(false);
            return new { ok = true, message = (string?)null };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Info("Dictation: the speech model wasn't fetched (" + ex.GetType().Name + ").");
            return new { ok = false, message = (string?)"Dictation couldn't get ready. Check your connection; Exo tries again later." };
        }
    }

    private object DictationStart() => Dictation.Start(PostEvent) is { } message
        ? new { ok = false, message = (string?)message }
        : new { ok = true, message = (string?)null };

    private async Task<object> DictationStopAsync(JsonElement p, bool hasParams)
    {
        // One character either side is all the spacing reads; nothing more of the text box comes to the host.
        var before = ReadString(p, hasParams, "before") is { Length: > 0 } b ? b[^1..] : null;
        var after = ReadString(p, hasParams, "after") is { Length: > 0 } a ? a[..1] : null;
        var (text, message) = await Dictation.StopAsync(before, after).ConfigureAwait(false);
        return new { ok = message is null, text, message };
    }

    private static object DictationCancel()
    {
        Dictation.Cancel();
        return new { ok = true };
    }

    /// <summary>settings.set's dictation: off ends a take and takes the mic out of every text box.</summary>
    private void ApplyDictationSetting(JsonElement p)
    {
        if (!p.TryGetProperty("dictation", out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return;
        var on = value.GetBoolean();
        if (on == _services.Settings.Current.Dictation) return;
        _services.Settings.UpdateProfile(s => s.Dictation = on);
        if (!on) Dictation.Cancel();
        PostDictationState();
    }

    // The speech model coming (for whoever asked: setup, the quiet fetch after start, a press of the mic): at most five a second.
    private void OnComponentProgress(ExoComponent component, long done, bool unpacking)
    {
        if (component != DictationModel.Component) return;
        var now = Environment.TickCount64;
        var whole = unpacking && done >= component.Unpack;
        if (!whole && now - Interlocked.Read(ref _dictationProgressAt) < 200) return;
        Interlocked.Exchange(ref _dictationProgressAt, now);
        PostEvent("dictation.progress", new { done = (unpacking ? component.Download : 0) + done, total = component.Download + component.Unpack });
    }

    private void OnComponentFetched(ExoComponent component)
    {
        if (component == DictationModel.Component) PostDictationState();
    }

    private void OnComponentFailed(string feature)
    {
        PostEvent("components.failed", new { feature });
        if (feature == DictationModel.Component.Feature) PostDictationState();
    }
}
