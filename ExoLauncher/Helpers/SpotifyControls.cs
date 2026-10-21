namespace ExoLauncher.Helpers;

/// <summary>
/// Spotify draws its caption cluster in the host process, outside the page's
/// reach; the client's own Esperanto bridge is the only lever that both
/// removes the buttons and collapses the titlebar strip so the nav reflows
/// into the space. Exo launches the client with a private DevTools pipe
/// (<see cref="SpotifyDevTools"/>) and registers the script through it:
/// SetButtonsVisibility hides the cluster and UpdateTitlebarHeight(1)
/// reclaims the strip. Nothing is written to Spotify's install or profile —
/// a standalone launch keeps its own chrome and there is nothing to restore.
/// </summary>
internal static class SpotifyControls
{
    /// Spotify.exe's own switches for the hidden engine, read from Spotify.dll
    /// 1.3.5 with Ghidra: --minimized makes its first Show() (CEF Views, after
    /// window creation) open minimized instead of on screen; --debug-level
    /// defaults to 14 (max) when absent; --disable-desktop-launcher stops its
    /// launcher autostart registration; the event sender batches every 30 s
    /// unless --event-sender-send-interval (seconds) says otherwise;
    /// --disable-crash-reporting turns off crash reporting (a crashpad
    /// handler is still started, and can outlive the app). DevTools comes over
    /// a pipe only Exo holds, never a port another program could find.
    internal static string EngineArguments =>
        SpotifyDevTools.PipeArgument + " --minimized --debug-level=OFF --disable-desktop-launcher --event-sender-send-interval=300 --disable-crash-reporting";

    private static readonly TimeSpan AttachDeadline = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(15);

    /// The strip reappears on fullscreen and window-state churn, so the calls
    /// re-apply on those events, whenever the page becomes visible, and through
    /// a startup loop that keeps going until the Esperanto bridge has existed
    /// for ten seconds (a starved start can take far longer than the old fixed
    /// 22 seconds to expose it).
    internal const string Script = """
        (function(){
        if (window.top !== window || window.__exoChrome) return; window.__exoChrome = true;
        function call(method, payload) {
          try {
            var enc = new TextEncoder();
            var svc = enc.encode('spotify.desktop.update_ui_esperanto.proto.DesktopUpdateUi');
            var mth = enc.encode(method);
            var buf = new ArrayBuffer(4 + svc.byteLength + 4 + mth.byteLength + 4 + payload.byteLength);
            var dv = new DataView(buf), off = 0;
            [svc, mth, payload].forEach(function (p) { dv.setUint32(off, p.byteLength); off += 4; new Uint8Array(buf, off, p.byteLength).set(p); off += p.byteLength; });
            if (window.executeEsperantoCall)
              window.executeEsperantoCall({ request: buf, persistent: false, onSuccess: function () {}, onFailure: function () {} });
          } catch (_) {}
        }
        // The top bar keeps an empty 135x32 box where the caption buttons
        // were and pulls its right cluster into it with a negative margin.
        // Both rules are hashed per build, so they are found by shape: the
        // box goes and the cluster (What's New, Friend Activity, profile)
        // sits on the right edge with the bar's own inset.
        var reclaimed = false;
        function reclaim() {
          if (reclaimed || !document.styleSheets.length) return;
          var box = null, cluster = null;
          function scan(rules) {
            for (var i = 0; i < rules.length; i++) {
              var r = rules[i];
              if (r.cssRules && !r.selectorText) { scan(r.cssRules); continue; }
              if (!r.style || !r.selectorText) continue;
              if (!box && /(^|[^\d.])135px/.test(r.style.width) && /(^|[^\d.])32px/.test(r.style.height)) box = r.selectorText;
              if (!cluster && r.selectorText.indexOf('spotify__os--is-windows') >= 0 && /\*\s*-1\s*\)?\s*$/.test(r.style.marginInlineEnd)) cluster = r.selectorText;
            }
          }
          for (var s = 0; s < document.styleSheets.length; s++) {
            try { scan(document.styleSheets[s].cssRules); } catch (_) {}
          }
          if (!box) return;
          reclaimed = true;
          var style = document.createElement('style');
          style.id = 'exo-caption-space';
          style.textContent = box + '{display:none!important}' + (cluster ? cluster + '{margin-inline-end:0!important}' : '');
          (document.head || document.documentElement).appendChild(style);
        }
        function hide() { call('SetButtonsVisibility', new Uint8Array([8, 0])); call('UpdateTitlebarHeight', new Uint8Array([8, 1])); reclaim(); }
        var tries = 0, ready = 0, iv = setInterval(function () {
          hide();
          if (window.executeEsperantoCall) ready++;
          if (ready > 40 || ++tries > 480) clearInterval(iv);
        }, 250);
        addEventListener('fullscreenchange', hide);
        document.addEventListener('visibilitychange', function () { if (!document.hidden) hide(); });
        var z; addEventListener('resize', function () { clearTimeout(z); z = setTimeout(hide, 400); });
        })();
        """;

    /// Attaches to every page target the client exposes and registers the
    /// script for its current document and every navigation after it. The
    /// session stays attached for the life of the page: Chromium drops a
    /// Page.addScriptToEvaluateOnNewDocument registration the moment its
    /// session detaches, so a one-shot attach that landed on a document Spotify
    /// then replaced (a fast start attaches before xpui has loaded) or a later
    /// reload left the caption buttons back on screen. Page targets that
    /// appear later (a replaced main page) are picked up by a slow poll. The
    /// returned task completes after the first attach, so the room can show
    /// the client; the sessions end with Spotify or with the next launch.
    /// Any failure leaves the hosted session running with its controls — no
    /// pipe means the process was adopted, not launched.
    internal static async Task HideWindowControlsAsync(CancellationToken cancellation)
    {
        var generation = Interlocked.Increment(ref _generation);
        try
        {
            var targets = await WaitForTargetsAsync(cancellation).ConfigureAwait(false);
            if (targets.Count == 0)
            {
                AppLog.Warn("Spotify's DevTools pipe listed no page; caption controls stay.");
                return;
            }
            var sessions = new Dictionary<string, string>();
            var attached = 0;
            foreach (var target in targets)
                if (await AttachAsync(target, sessions, cancellation).ConfigureAwait(false))
                    attached++;
            if (attached > 0)
                AppLog.Info("Spotify caption controls hidden on " + attached + " page target(s).");
            _ = Task.Run(() => WatchAsync(generation, sessions, cancellation), CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            AppLog.Warn("Spotify controls could not be hidden: " + error.Message);
        }
    }

    private static int _generation;
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(5);

    /// Attaches page targets that appear after the first attach. Ends when a
    /// newer launch took over, Exo shuts down, or the pipe closed (Spotify exited).
    private static async Task WatchAsync(int generation, Dictionary<string, string> sessions, CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested && Volatile.Read(ref _generation) == generation)
            {
                await Task.Delay(WatchInterval, cancellation).ConfigureAwait(false);
                if (!SpotifyDevTools.IsConnected) return;
                foreach (var gone in sessions.Where(pair => !SpotifyDevTools.IsLive(pair.Value)).Select(pair => pair.Key).ToList())
                    sessions.Remove(gone);
                foreach (var (target, _) in await SpotifyDevTools.PagesAsync(cancellation).ConfigureAwait(false))
                {
                    if (sessions.ContainsKey(target) || Volatile.Read(ref _generation) != generation) continue;
                    if (await AttachAsync(target, sessions, cancellation).ConfigureAwait(false))
                        AppLog.Info("Spotify caption controls hidden on a new page target.");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            AppLog.Warn("Spotify caption watch stopped: " + error.Message);
        }
    }

    private static async Task<List<string>> WaitForTargetsAsync(CancellationToken cancellation)
    {
        var deadline = Environment.TickCount64 + (long)AttachDeadline.TotalMilliseconds;
        while (Environment.TickCount64 < deadline && SpotifyDevTools.IsConnected)
        {
            cancellation.ThrowIfCancellationRequested();
            var pages = await SpotifyDevTools.PagesAsync(cancellation).ConfigureAwait(false);
            if (pages.Count > 0) return pages.Select(page => page.Id).ToList();
            await Task.Delay(500, cancellation).ConfigureAwait(false);
        }
        return [];
    }

    /// Attaches one page and registers the script on it; the session stays attached.
    private static async Task<bool> AttachAsync(string target, Dictionary<string, string> sessions, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(ReplyTimeout);
        try
        {
            if (await SpotifyDevTools.AttachAsync(target, timeout.Token).ConfigureAwait(false) is not { } session) return false;
            // Spotify's optimization adds the Canvas pause to the same script.
            var script = Script + "\n" + SpotifyAmoledTheme.OptimizeScript;
            var failed = false;
            foreach (var (method, parameters) in new (string, object?)[]
                     {
                         ("Page.enable", null),
                         ("Page.addScriptToEvaluateOnNewDocument", new { source = script }),
                         ("Runtime.evaluate", new { expression = script }),
                     })
            {
                var reply = await SpotifyDevTools.SendAsync(method, parameters, session, timeout.Token).ConfigureAwait(false);
                if (reply is not { } message) return false;
                if (message.TryGetProperty("error", out var error))
                {
                    failed = true;
                    AppLog.Warn("Spotify debug command failed: " + error.GetRawText());
                }
            }
            if (failed) return false;
            sessions[target] = session;
            return true;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return false; }
    }
}
