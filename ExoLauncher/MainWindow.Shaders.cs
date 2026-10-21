using ExoLauncher.Helpers;

namespace ExoLauncher;

/// <summary>
/// System > Shaders is Exo Shaders, Exo's own shader add-on: a headless
/// engine (<see cref="Services.ShadersAddon"/>) and its page framed in the
/// Shaders sheet. Exo hosts no shader app's window; without the add-on the
/// sheet says so. Game sheets read the engine's records
/// (<see cref="Services.ScsKillerStatus"/>). The engine starts with Exo, like
/// Exo Capture and Exo Mouse, so its page opens at once and it sees every play session.
/// </summary>
public sealed partial class MainWindow
{
    private Services.ShadersAddon? _shadersAddon;

    // The add-ons are first asked for from the pool (the boot prewarm) and the UI thread (a page) at once:
    // without this, both could make one, start two engines, and the second fails as if another app held it.
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
                // The engine treats its page as shown until told otherwise and
                // sends full state snapshots nobody reads. A page that opened
                // before the engine was ready has already said visible.
                var visible = addon.UiVisible;
                _ = addon.CallAsync("ui.visible", new System.Text.Json.Nodes.JsonObject { ["visible"] = visible }, CancellationToken.None)
                    .ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                if (!visible) addon.SetUiVisible(false);
                // A fresh engine learns Exo's installed games, and again whenever the library changes.
                addon.SyncLibrary(App.Services.Library.PeekCachedLibrary, force: true);
            };
            App.Services.Library.LibraryUpdated += () => addon.SyncLibrary(App.Services.Library.PeekCachedLibrary);
            return _shadersAddon = addon;
        }
    }

    /// With the connected rooms: the engine, which queues any rebuild a driver update needs for when the PC is idle.
    private Task PrewarmShaders() => Task.Run(() => ShadersAddon?.Start());

    /// <summary>shaders.addon: whether the add-on is installed, its engine's state, and the page to frame.</summary>
    internal object ShadersAddonStatus()
    {
        if (ShadersAddon is not { } addon) return new { ok = true, present = false };
        addon.Start();
        return new { ok = true, present = true, state = addon.State, message = addon.Message, url = "https://" + Services.ShadersAddon.VirtualHost + "/index.html" };
    }

    /// <summary>shaders.call: one request from the add-on's page to its engine.</summary>
    internal async Task<object> ShadersAddonCallAsync(string? method, System.Text.Json.Nodes.JsonNode? parameters, CancellationToken ct)
    {
        if (ShadersAddon is not { } addon) return new { ok = false, message = "Exo Shaders is not installed." };
        if (string.IsNullOrWhiteSpace(method) || method.Length > 64) return new { ok = false, message = "Unknown request." };
        if (method == "ui.visible")
        {
            var visible = parameters?["visible"] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<bool>(out var on) && on;
            addon.SetUiVisible(visible);
            // A closed page never starts a stopped engine just to say so.
            if (!visible && addon.State != "running") return new { ok = true };
        }
        try { return new { ok = true, result = await addon.CallAsync(method, parameters, ct).ConfigureAwait(true) }; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new { ok = false, message = ex.Message }; }
    }
}
