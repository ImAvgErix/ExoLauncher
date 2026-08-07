using System.Text.Json;
using System.Text.Json.Serialization;
using ExoLauncher.Models;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;

namespace ExoLauncher.Services;

/// <summary>
/// JSON-RPC bridge between the React UI (WebView2) and native services.
/// UI owns pixels; this host owns discovery, launch, and deps.
/// </summary>
public sealed class WebHostBridge
{
    private readonly AppServices _services;
    private readonly DispatcherQueue _queue;
    private CoreWebView2? _web;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public WebHostBridge(AppServices services, DispatcherQueue queue)
    {
        _services = services;
        _queue = queue;
    }

    public void Attach(CoreWebView2 web)
    {
        _web = web;
        web.WebMessageReceived += OnMessage;
    }

    public void Detach()
    {
        if (_web is null) return;
        try { _web.WebMessageReceived -= OnMessage; } catch { }
        _web = null;
    }

    private void OnMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? raw = null;
        try { raw = e.TryGetWebMessageAsString(); } catch { }
        if (string.IsNullOrWhiteSpace(raw))
        {
            try { raw = e.WebMessageAsJson; } catch { return; }
        }
        if (string.IsNullOrWhiteSpace(raw)) return;
        _ = HandleAsync(raw);
    }

    private async Task HandleAsync(string raw)
    {
        string? id = null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var method = root.TryGetProperty("method", out var mEl) ? mEl.GetString() : null;
            var hasParams = root.TryGetProperty("params", out var paramsEl);

            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(method))
                return;

            object? result = method switch
            {
                "library.get" => await LibraryGetAsync(paramsEl, hasParams).ConfigureAwait(true),
                "library.refresh" => await LibraryGetAsync(paramsEl, hasParams: true, force: true).ConfigureAwait(true),
                "game.get" => await GameGetAsync(paramsEl, hasParams).ConfigureAwait(true),
                "game.launch" => await GameLaunchAsync(paramsEl, hasParams).ConfigureAwait(true),
                "deps.list" => DepsList(),
                "deps.offerInstall" => DepsOfferInstall(paramsEl, hasParams),
                "stores.matrix" => _services.Library.StoreMatrix(),
                "settings.get" => BuildSettings(),
                "settings.set" => SetSettings(paramsEl, hasParams),
                "shell.minimize" => MinimizeWindow(),
                "shell.close" => CloseWindow(),
                "shell.openUrl" => OpenUrl(paramsEl, hasParams),
                "app.version" => new { version = _services.AppVersion },
                _ => throw new InvalidOperationException($"Unknown method: {method}")
            };

            PostResponse(id!, ok: true, result: result);
        }
        catch (Exception ex)
        {
            if (id is not null)
                PostResponse(id, ok: false, error: ex.Message);
        }
    }

    private async Task<object> LibraryGetAsync(JsonElement p, bool hasParams, bool force = false)
    {
        if (hasParams && p.ValueKind == JsonValueKind.Object
            && p.TryGetProperty("force", out var f) && f.ValueKind == JsonValueKind.True)
            force = true;

        var games = await _services.Library.GetLibraryAsync(force).ConfigureAwait(true);
        return new
        {
            games = games.Select(MapGame).ToList(),
            count = games.Count,
            stores = _services.Library.StoreMatrix(),
        };
    }

    private async Task<object> GameGetAsync(JsonElement p, bool hasParams)
    {
        var id = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(id))
            return new { ok = false, message = "Missing game id." };

        await _services.Library.GetLibraryAsync().ConfigureAwait(true);
        var game = _services.Library.Find(id!);
        if (game is null)
            return new { ok = false, message = "Game not found." };

        return new { ok = true, game = MapGame(game) };
    }

    private async Task<object> GameLaunchAsync(JsonElement p, bool hasParams)
    {
        var id = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(id))
            return new { ok = false, message = "Missing game id." };

        await _services.Library.GetLibraryAsync().ConfigureAwait(true);
        var game = _services.Library.Find(id!);
        if (game is null)
            return new { ok = false, message = "Game not found. Refresh the library." };

        if (_services.Settings.Current.MinimizeWhilePlaying)
            MinimizeWindow();

        var result = await _services.Launcher.LaunchAsync(game).ConfigureAwait(true);
        PostEvent("launch.status", new
        {
            id = game.Id,
            ok = result.Ok,
            message = result.Message,
            processId = result.ProcessId,
            backendStarted = result.BackendStarted,
        });

        return new
        {
            ok = result.Ok,
            message = result.Message,
            processId = result.ProcessId,
            backendStarted = result.BackendStarted,
        };
    }

    private object DepsList() => new
    {
        items = _services.Dependencies.DetectAll().Select(d => new
        {
            id = d.Id,
            name = d.Name,
            status = d.Status,
            detail = d.Detail,
            canOfferInstall = d.CanOfferInstall,
            officialUrl = d.OfficialUrl,
        }).ToList(),
    };

    private object DepsOfferInstall(JsonElement p, bool hasParams)
    {
        var id = ReadString(p, hasParams, "id");
        if (string.IsNullOrWhiteSpace(id))
            return new { ok = false, message = "Missing dependency id." };
        return _services.Dependencies.OfferInstall(id!);
    }

    private object BuildSettings()
    {
        var s = _services.Settings.Current;
        return new
        {
            appVersion = _services.AppVersion,
            closeStoreClientsAfterLaunch = s.CloseStoreClientsAfterLaunch,
            autoInstallRedistributables = s.AutoInstallRedistributables,
            minimizeWhilePlaying = s.MinimizeWhilePlaying,
            antiCheatSafeMode = true, // always
        };
    }

    private object SetSettings(JsonElement p, bool hasParams)
    {
        if (!hasParams || p.ValueKind != JsonValueKind.Object)
            return BuildSettings();

        bool? close = null, auto = null, min = null;
        if (p.TryGetProperty("closeStoreClientsAfterLaunch", out var c) &&
            (c.ValueKind is JsonValueKind.True or JsonValueKind.False))
            close = c.GetBoolean();
        if (p.TryGetProperty("autoInstallRedistributables", out var a) &&
            (a.ValueKind is JsonValueKind.True or JsonValueKind.False))
            auto = a.GetBoolean();
        if (p.TryGetProperty("minimizeWhilePlaying", out var m) &&
            (m.ValueKind is JsonValueKind.True or JsonValueKind.False))
            min = m.GetBoolean();

        _services.Settings.ApplyPatch(close, auto, min);
        return BuildSettings();
    }

    private object MinimizeWindow()
    {
        void Go()
        {
            try
            {
                if (App.MainAppWindow?.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)
                    p.Minimize();
            }
            catch { }
        }
        if (!_queue.HasThreadAccess) _queue.TryEnqueue(Go); else Go();
        return new { ok = true };
    }

    private object CloseWindow()
    {
        void Go()
        {
            try { App.MainAppWindow?.Close(); } catch { }
        }
        if (!_queue.HasThreadAccess) _queue.TryEnqueue(Go); else Go();
        return new { ok = true };
    }

    private object OpenUrl(JsonElement p, bool hasParams)
    {
        var url = ReadString(p, hasParams, "url");
        if (string.IsNullOrWhiteSpace(url)) return new { ok = false };
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return new { ok = false };
        if (uri.Scheme is not ("https" or "http")) return new { ok = false };
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true,
            });
            return new { ok = true };
        }
        catch (Exception ex)
        {
            return new { ok = false, message = ex.Message };
        }
    }

    private static object MapGame(GameEntry g) => new
    {
        id = g.Id,
        title = g.Title,
        store = g.Store.ToString().ToLowerInvariant(),
        installed = g.Installed,
        path = g.Path,
        coverUrl = g.CoverUrl,
        playtimeMinutes = g.PlaytimeMinutes,
        sizeBytes = g.SizeBytes,
        status = g.Status,
        deps = g.Deps,
        launchNote = g.LaunchNote,
        launchTarget = g.LaunchTarget,
    };

    private static string? ReadString(JsonElement p, bool hasParams, string name)
    {
        if (!hasParams || p.ValueKind != JsonValueKind.Object) return null;
        if (!p.TryGetProperty(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
    }

    private void PostResponse(string id, bool ok, object? result = null, string? error = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["ok"] = ok,
        };
        if (ok) payload["result"] = result;
        else payload["error"] = error ?? "error";
        PostJson(payload);
    }

    private void PostEvent(string name, object? data)
    {
        PostJson(new Dictionary<string, object?>
        {
            ["event"] = name,
            ["data"] = data,
        });
    }

    private void PostJson(object payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, JsonOpts);
            var web = _web;
            if (web is null) return;

            void Send()
            {
                try { web.PostWebMessageAsJson(json); } catch { }
            }

            if (!_queue.HasThreadAccess)
                _queue.TryEnqueue(Send);
            else
                Send();
        }
        catch { }
    }
}
