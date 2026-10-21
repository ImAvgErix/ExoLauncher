using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace ExoLauncher.Helpers;

/// <summary>The same automatic defaults for every embedded browser profile,
/// applied through the official extension's dashboard API.</summary>
internal static class WebAdBlockProfile
{
    internal const string Revision = "exo-app-wide-2";
    internal const string Script = """
        (() => {
          if (location.protocol !== 'chrome-extension:' || !globalThis.vAPI?.messaging?.send) return false;
          if (window.__exoAdBlockProfile) return true;
          window.__exoAdBlockProfile = { state: 'pending', phase: 'initial lists' };
          (async () => {
            const send = request => vAPI.messaging.send('dashboard', request);
            // During first installation the background port can reconnect and
            // resolve outstanding reads without data. Wait for a real response.
            const read = async request => {
              for (let attempt = 0; attempt < 120; attempt++) {
                const value = await send(request);
                if (value && typeof value === 'object') return value;
                await new Promise(resolve => window.setTimeout(resolve, 500));
              }
              throw Error('Dashboard is still initializing');
            };
            const lists = await read({what:'getLists'}); // Wait for the filtering engine to be ready.
            // Match the bundled upstream defaults. Restore the common baseline
            // additively; optional, regional, and custom lists remain intact.
            const baseline = ['ublock-filters','ublock-badware','ublock-privacy',
              'ublock-unbreak','ublock-quick-fixes','easylist','easyprivacy','urlhaus-1','plowe-0'];
            if (!lists.available || baseline.some(key => !lists.available[key])) throw Error('Default filter lists unavailable');
            const missing = baseline.filter(key => lists.available[key].off === true);
            if (missing.length) await send({what:'applyFilterListSelection',toSelect:missing,merge:true});
            window.__exoAdBlockProfile.phase = 'settings';
            const before = await read({what:'userSettings'});
            const filters = await read({what:'readUserFilters'});
            if (typeof filters?.content !== 'string' || filters.content.length > 1000000) throw Error('Filter data unavailable');
            const begin = '! Exo embedded media performance — begin';
            const end = '! Exo embedded media performance — end';
            const block = [begin,
              '! Skip generic DOM surveying on long-lived app documents.',
              '! Network blocking and site-specific cosmetic filters stay enabled.',
              '@@||open.spotify.com^$generichide',
              '@@||music.apple.com^$generichide',
              '@@||music.youtube.com^$generichide', end].join('\n');
            let content = filters.content;
            const start = content.indexOf(begin), finish = content.indexOf(end, start);
            if (start >= 0 && finish >= start) content = content.slice(0,start) + block + content.slice(finish + end.length);
            else content = content.trimEnd() + (content.trim() ? '\n\n' : '') + block + '\n';
            // Match uBlock's saveUserFilters normalization for cold-profile readback.
            content = content.trim();
            // An empty profile has no dormant rules to activate. Once it has
            // custom filters, preserve the user's enabled/disabled choice.
            const enabled = filters.enabled !== false || filters.content.trim().length === 0;
            if (content !== filters.content) await send({what:'writeUserFilters',content,enabled,trusted:filters.trusted === true});
            // Chromium cannot truly suspend requests here: enabling startup
            // suspension cancels requests and reloads tabs, disrupting players.
            // Keep media and WebRTC policies at their normal provider defaults.
            const settings = {autoUpdate:true,collapseBlocked:true,hyperlinkAuditingDisabled:true,
              prefetchingDisabled:true,parseAllABPHideFilters:true,ignoreGenericCosmeticFilters:false,
              uiTheme:'auto',showIconBadge:false,suspendUntilListsAreLoaded:false};
            for (const [name,value] of Object.entries(settings)) if (before[name] !== value) await send({what:'userSettings',name,value});
            if (missing.length || content !== filters.content || before.parseAllABPHideFilters !== true ||
                before.ignoreGenericCosmeticFilters !== false) await send({what:'reloadAllFilters'});
            window.__exoAdBlockProfile.phase = 'readback';
            const after = await read({what:'userSettings'});
            const saved = await read({what:'readUserFilters'});
            const active = await read({what:'getLists'});
            if (saved.content !== content || saved.enabled !== enabled ||
                Object.entries(settings).some(([key,value]) => after[key] !== value) ||
                baseline.some(key => !active.available?.[key] || active.available[key].off === true)) throw Error('Configuration was not saved');
            window.__exoAdBlockProfile = {state:'ready',customFiltersEnabled:saved.enabled !== false};
          })().catch(error => { window.__exoAdBlockProfile = {state:'failed',error:String(error).slice(0,200)}; });
          return true;
        })();
        """;

    internal static async Task<bool> ApplyAsync(CoreWebView2 source, string extensionId, nint owner)
    {
        CoreWebView2Controller? controller = null;
        var stage = "create controller";
        try
        {
            var options = source.Environment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = source.Profile.ProfileName;
            options.IsInPrivateModeEnabled = source.Profile.IsInPrivateModeEnabled;
            controller = await source.Environment.CreateCoreWebView2ControllerAsync(
                CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)owner), options);
            controller.IsVisible = false;
            stage = "load dashboard";
            var core = controller.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            var uri = "chrome-extension://" + extensionId + "/dashboard.html";
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            core.NavigationStarting += (_, e) => { if (e.Uri != uri) e.Cancel = true; };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.NavigationCompleted += (_, e) => ready.TrySetResult(e.IsSuccess && core.Source == uri);
            core.Navigate(uri);
            if (!await ready.Task.WaitAsync(TimeSpan.FromSeconds(8))) throw new InvalidOperationException("Dashboard navigation failed");
            stage = "start configuration";
            if (await core.ExecuteScriptAsync(Script) != "true") throw new InvalidOperationException("Dashboard API unavailable");
            stage = "save configuration";
            // A new extension profile may still be compiling its initial lists.
            // This runs in the background; it must never delay app navigation.
            var deadline = Environment.TickCount64 + 60000;
            var startedAt = Environment.TickCount64;
            var reconnects = 0;
            while (Environment.TickCount64 < deadline)
            {
                using var state = JsonDocument.Parse(await core.ExecuteScriptAsync("window.__exoAdBlockProfile ?? null"));
                if (state.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var status = state.RootElement.GetProperty("state").GetString();
                    if (status == "pending" && state.RootElement.TryGetProperty("phase", out var phase))
                    {
                        stage = "save configuration (" + phase.GetString() + ")";
                        // The very first dashboard can connect before the
                        // extension's background listener exists. A fresh page
                        // opens a fresh port; retry only before any settings write.
                        if (phase.GetString() == "initial lists" && reconnects < 3 &&
                            Environment.TickCount64 - startedAt > 3000)
                        {
                            reconnects++;
                            ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
                            core.Navigate(uri);
                            if (!await ready.Task.WaitAsync(TimeSpan.FromSeconds(8)) ||
                                await core.ExecuteScriptAsync(Script) != "true") return false;
                            startedAt = Environment.TickCount64;
                        }
                    }
                    if (status == "ready") return true;
                    if (status == "failed") throw new InvalidOperationException(state.RootElement.GetProperty("error").GetString());
                }
                await Task.Delay(250);
            }
            throw new TimeoutException("Initial filter lists are still loading");
        }
        catch (Exception error)
        {
            AppLog.Warn("uBlock profile " + stage + ": " + error.Message);
            return false;
        }
        finally { try { controller?.Close(); } catch { } }
    }
}
