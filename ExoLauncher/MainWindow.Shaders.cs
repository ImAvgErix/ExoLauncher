using ExoLauncher.Helpers;

namespace ExoLauncher;

/// <summary>
/// System > Shaders is Exo Shaders, Exo's own shader add-on: a headless
/// engine (<see cref="Services.ShadersAddon"/>) driven by Exo's own Shaders page.
/// Exo hosts no shader app's window; without the add-on the page says so. Game
/// sheets read the engine's records (<see cref="Services.ScsKillerStatus"/>). The
/// engine runs only after Compile or Refresh is pressed on the page
/// (<see cref="Services.ShadersAddon.StartMethods"/>) and exits soon after that work
/// ends; the page shows the last state it left meanwhile.
/// </summary>
public sealed partial class MainWindow
{
    private Services.ShadersAddon? _shadersAddon;

    // The add-on is first asked for from the pool (shaders.addon) and the UI thread (shaders.call) at once:
    // without this, both could make one.
    private readonly Lock _addonGate = new();

    /// <summary>The shader add-on, when it is installed.</summary>
    private Services.ShadersAddon? ShadersAddon
    {
        get
        {
            if (_shadersAddon is not null || !Services.ShadersAddon.Present) return _shadersAddon;
            using var scope = _addonGate.EnterScope();
            if (_shadersAddon is not null) return _shadersAddon;
            var addon = new Services.ShadersAddon();
            addon.Event += (name, data) =>
            {
                _bridge?.PostShadersEvent(name, data);
                if (name != "ready") return;
                // It exits by itself soon after its work ends (an engine without exitWhenIdle stays, as before).
                _ = addon.CallAsync("exitWhenIdle", new System.Text.Json.Nodes.JsonObject { ["seconds"] = Services.ShadersAddon.IdleExitSeconds }, CancellationToken.None)
                    .ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                // The engine treats its page as shown until told otherwise and
                // sends full state snapshots nobody reads. A page that opened
                // before the engine was ready has already said visible.
                var visible = addon.UiVisible;
                _ = addon.CallAsync("ui.visible", new System.Text.Json.Nodes.JsonObject { ["visible"] = visible }, CancellationToken.None)
                    .ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                // A fresh engine learns Exo's installed games, and again whenever the library changes while it runs.
                addon.SyncLibrary(App.Services.Library.PeekCachedLibrary, force: true);
            };
            App.Services.Library.LibraryUpdated += () => addon.SyncLibrary(App.Services.Library.PeekCachedLibrary);
            return _shadersAddon = addon;
        }
    }

    /// <summary>shaders.addon: whether the add-on is installed, its engine's state, and the last state it left when it exited (shown until it runs). Never starts it.</summary>
    internal object ShadersAddonStatus()
    {
        if (ShadersAddon is not { } addon) return new { ok = true, present = false };
        return new { ok = true, present = true, state = addon.State, message = addon.Message, snapshot = Services.ShadersAddon.AtRest() };
    }

    /// <summary>
    /// shaders.call: one request from the add-on's page to its engine. Only Refresh and Compile
    /// (<see cref="Services.ShadersAddon.StartMethods"/>) start a stopped engine; without one, snapshot is the last
    /// state it left and the rest (ui.visible, queue.move, queue.remove, queue.pause, queue.stop) have nothing to act on.
    /// </summary>
    internal async Task<object> ShadersAddonCallAsync(string? method, System.Text.Json.Nodes.JsonNode? parameters, CancellationToken ct)
    {
        if (ShadersAddon is not { } addon) return new { ok = false, message = "Exo Shaders is not installed." };
        if (string.IsNullOrWhiteSpace(method) || !Services.ShadersAddon.PageMethods.Contains(method)) return new { ok = false, message = "Unknown request." };
        if (method == "ui.visible")
            addon.SetUiVisible(parameters?["visible"] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<bool>(out var on) && on);
        var starts = Services.ShadersAddon.StartMethods.Contains(method);
        object AtRest() => method == "snapshot" ? new { ok = true, result = Services.ShadersAddon.AtRest() } : new { ok = true };
        if (!starts && !addon.Running) return AtRest();
        try { return new { ok = true, result = await addon.CallAsync(method, parameters, ct, starts ? (method == "scan" ? "Refresh" : "Compile") : null).ConfigureAwait(true) }; }
        catch (OperationCanceledException) { throw; }
        // It exited between the check and the call.
        catch (InvalidOperationException) when (!starts && !addon.Running) { return AtRest(); }
        catch (Exception ex)
        {
            // The engine's words reach the page unless they name a path.
            AppLog.Warn("Exo Shaders call " + method + ": " + ex.Message);
            return new { ok = false, message = System.Text.RegularExpressions.Regex.IsMatch(ex.Message, @"[A-Za-z]:\\|\\\\") ? "Exo Shaders could not do that." : ex.Message };
        }
    }
}
