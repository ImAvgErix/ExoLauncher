using System.Net.Http.Headers;
using System.Text.Json;
using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using Microsoft.Web.WebView2.Core;

namespace ExoLauncher.Services;

/// <summary>
/// Gifts waiting on Steam and Epic, read and answered in a hidden browser on Exo's own profile
/// (<c>gifts-webview</c>), with the same calls each store's own gifts page makes:
/// <list type="bullet">
/// <item>Steam: store.steampowered.com/gifts, then <c>/gifts/&lt;id&gt;/unpack</c> (add to library) or
/// <c>/decline</c> with the page's session id. Exo never holds a Steam token, so Steam's website is signed in to
/// once in that profile, on Valve's own page; Steam keeps that sign-in fresh itself.</item>
/// <item>Epic: egs-platform-service's gifting calls from a store.epicgames.com page (they answer only a real
/// browser), PUT <c>…/gifts/&lt;id&gt;/status</c> to accept or decline. When the store page is not signed in,
/// Exo's Legendary sign-in opens it with a one-time exchange code: no step from you.</item>
/// </list>
/// Pictures, video and fonts are not loaded. One page at a time, on the UI thread, closed as soon as it is done.
/// </summary>
internal sealed class StoreGifts
{
    internal sealed record Snapshot(IReadOnlyList<StoreGift> Gifts, bool SteamSignedIn);

    internal static string ProfileFolder => Path.Combine(PathHelper.AppDataDir, "gifts-webview");

    private const string SteamGiftsUrl = "https://store.steampowered.com/gifts?l=english";
    private const string SteamLoginUrl = "https://store.steampowered.com/login/?redir=gifts%3Fl%3Denglish";
    private const string EpicGiftsUrl = "https://store.epicgames.com/en-US/gifts";
    // A plain page on the store's origin: the gifting calls answer it, and it loads no app and sends no one to sign in.
    private const string EpicOrigin = "https://store.epicgames.com/robots.txt";
    private const string EpicGifting = "https://egs-platform-service.store.epicgames.com/api/v2/private/egs/gifting/gifts";
    private const string EpicExchange = "https://account-public-service-prod.ol.epicgames.com/account/api/oauth/exchange";
    private static readonly TimeSpan PageWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CallWait = TimeSpan.FromSeconds(45);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private StoreAuthWindow? _signIn;

    internal Snapshot Last { get; private set; } = new([], false);

    /// <summary>Every gift waiting on both stores. UI thread only.</summary>
    internal async Task<Snapshot> ReadAsync(nint owner, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            using var page = await Page.OpenAsync(owner).ConfigureAwait(true);
            var gifts = new List<StoreGift>();
            var steamSignedIn = false;
            try
            {
                if (await page.HasCookieAsync("https://store.steampowered.com", "steamLoginSecure").ConfigureAwait(true) &&
                    await OpenSteamAsync(page, ct).ConfigureAwait(true))
                {
                    steamSignedIn = true;
                    gifts.AddRange(StoreGiftParser.ParseSteam(await page.RunAsync("document.documentElement.outerHTML", CallWait, ct).ConfigureAwait(true)));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                AppLog.Warn($"Gifts: Steam not read: {ex.GetType().Name}: {ex.Message}");
            }
            try
            {
                if (await EpicPlaytime.ResolveSessionAsync(ct).ConfigureAwait(true) is { } account &&
                    await EpicCallAsync(page, account, EpicListScript(account), ct).ConfigureAwait(true) is { Status: 200 } list)
                    gifts.AddRange(StoreGiftParser.ParseEpic(list.Body, DateTimeOffset.UtcNow));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                AppLog.Warn($"Gifts: Epic not read: {ex.GetType().Name}: {ex.Message}");
            }
            Last = new Snapshot(gifts, steamSignedIn);
            AppLog.Info($"Gifts: {gifts.Count(g => g.Store == "steam")} on Steam{(steamSignedIn ? "" : " (website not signed in)")}, {gifts.Count(g => g.Store == "epic")} on Epic.");
            return Last;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Accepts (adds to the library) or declines one gift. UI thread only.</summary>
    internal async Task<(bool Ok, string Message)> AnswerAsync(nint owner, string store, string id, bool accept, CancellationToken ct)
    {
        if (!StoreGiftParser.IsGiftId(store, id)) return (false, "That gift is no longer here.");
        await _gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            using var page = await Page.OpenAsync(owner).ConfigureAwait(true);
            var (ok, message) = store == "steam"
                ? await AnswerSteamAsync(page, id, accept, ct).ConfigureAwait(true)
                : await AnswerEpicAsync(page, id, accept, ct).ConfigureAwait(true);
            AppLog.Info($"Gifts: {(accept ? "accept" : "decline")} on {store} {(ok ? "done" : "refused")}.");
            if (ok) Last = Last with { Gifts = Last.Gifts.Where(g => !(g.Store == store && g.Id == id)).ToList() };
            return (ok, ok ? (accept ? "Added to your library." : "Declined.") : message ?? (accept ? "The gift was not accepted. Try again." : "The gift was not declined. Try again."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            AppLog.Warn($"Gifts: {(accept ? "accept" : "decline")} on {store} failed: {ex.GetType().Name}");
            return (false, "The store did not answer. Try again.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<(bool, string?)> AnswerSteamAsync(Page page, string id, bool accept, CancellationToken ct)
    {
        if (!await OpenSteamAsync(page, ct).ConfigureAwait(true)) return (false, "Sign in to Steam's website again in Settings > Game stores.");
        string body;
        if (accept)
            body = $"new URLSearchParams({{ sessionid: g_sessionID, bPrivately: '0' }})";
        else
        {
            var gift = StoreGiftParser.ParseSteam(await page.RunAsync("document.documentElement.outerHTML", CallWait, ct).ConfigureAwait(true))
                .FirstOrDefault(g => g.Id == id);
            if (gift?.SenderId is not { } sender) return (false, "That gift is no longer here.");
            body = $"new URLSearchParams({{ sessionid: g_sessionID, steamid_sender: {JsonSerializer.Serialize(sender)}, note: '' }})";
        }
        var answer = await page.RunAsync(
            $"fetch('/gifts/' + {JsonSerializer.Serialize(id)} + '/{(accept ? "unpack" : "decline")}', {{ method: 'POST', credentials: 'include', body: {body} }}).then(r => r.text())",
            CallWait, ct).ConfigureAwait(true);
        return StoreGiftParser.ParseSteamAnswer(answer);
    }

    private async Task<(bool, string?)> AnswerEpicAsync(Page page, string id, bool accept, CancellationToken ct)
    {
        if (await EpicPlaytime.ResolveSessionAsync(ct).ConfigureAwait(true) is not { } account) return (false, "Sign in to Epic in Settings > Game stores.");
        var put = JsonSerializer.Serialize(new { newStatus = accept ? "accepted" : "declined" });
        var answer = await EpicCallAsync(page, account, Fetch(
            $"{JsonSerializer.Serialize(EpicGifting + "/")} + {JsonSerializer.Serialize(id)} + '/status'",
            $"{{ method: 'PUT', credentials: 'include', headers: {{ 'content-type': 'application/json' }}, body: {JsonSerializer.Serialize(put)} }}"), ct).ConfigureAwait(true);
        return answer is null ? (false, null) : StoreGiftParser.ParseEpicAnswer(answer.Status, answer.Body);
    }

    /// <summary>Opens Steam's gifts page; false when Steam sends it to its sign-in instead.</summary>
    private static async Task<bool> OpenSteamAsync(Page page, CancellationToken ct)
    {
        var at = await page.GoAsync(SteamGiftsUrl, uri => uri.Host == "store.steampowered.com", ct).ConfigureAwait(true);
        return at is not null && !at.AbsolutePath.StartsWith("/login", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record Answer(int Status, string? Body);

    /// <summary>
    /// Runs one gifting call from Epic's store origin. When the store is not signed in in this profile (401/403, or
    /// no answer), it is signed in with an exchange code from Legendary's session, landing on the store's own gifts
    /// page (which only opens signed in), and the call runs again there.
    /// </summary>
    private static async Task<Answer?> EpicCallAsync(Page page, EpicPlaytime.Session account, string script, CancellationToken ct)
    {
        if (!Uri.TryCreate(page.Source, UriKind.Absolute, out var at) || at.Host != "store.epicgames.com")
            await page.GoAsync(EpicOrigin, uri => uri.Host == "store.epicgames.com", ct).ConfigureAwait(true);
        var answer = Read(await page.RunAsync(script, CallWait, ct).ConfigureAwait(true));
        if (answer is not (null or { Status: 0 or 401 or 403 })) return answer;
        if (await ExchangeCodeAsync(account, ct).ConfigureAwait(true) is not { } code) return answer;
        var landed = await page.GoAsync(
            "https://www.epicgames.com/id/exchange?exchangeCode=" + Uri.EscapeDataString(code) + "&redirectUrl=" + Uri.EscapeDataString(EpicGiftsUrl),
            uri => uri.Host == "store.epicgames.com" && uri.AbsolutePath.EndsWith("/gifts", StringComparison.OrdinalIgnoreCase), ct).ConfigureAwait(true);
        if (landed is null) return answer;
        AppLog.Info("Gifts: Epic's store signed in with Exo's Epic account.");
        return Read(await page.RunAsync(script, CallWait, ct).ConfigureAwait(true));

        static Answer? Read(string? json)
        {
            if (json is null) return null;
            using var doc = JsonDocument.Parse(json);
            return new Answer(doc.RootElement.GetProperty("status").GetInt32(),
                doc.RootElement.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() : null);
        }
    }

    /// <summary><c>fetch(url, init)</c> in the page, answered as <c>{"status":…,"body":"…"}</c>.</summary>
    private static string Fetch(string url, string init) =>
        $"fetch({url}, {init}).then(async r => JSON.stringify({{ status: r.status, body: (await r.text()).slice(0, 200000) }}), e => JSON.stringify({{ status: 0 }}))";

    /// <summary>
    /// Received gifts, each still-waiting one's offer given its catalog title and key art (the store's own
    /// catalogOffer query), so the parser sees one list.
    /// </summary>
    private static string EpicListScript(EpicPlaytime.Session account)
    {
        var url = EpicGifting + "?identityId=" + Uri.EscapeDataString(account.AccountId) + "&locale=en-US&start=0&count=50&type=received";
        const string Query = "query giftOffer($namespace:String!,$id:String!){Catalog{catalogOffer(namespace:$namespace,id:$id,locale:\"en-US\"){title keyImages{type url}}}}";
        return $$"""
            (async () => {
              let r;
              try { r = await fetch({{JsonSerializer.Serialize(url)}}, { credentials: 'include' }); }
              catch { return JSON.stringify({ status: 0 }); }
              if (!r.ok) return JSON.stringify({ status: r.status });
              const list = await r.json();
              for (const gift of (list.data || []).filter(g => g && g.offer && (g.status === 'NEW' || g.status === 'OPENED'))) {
                try {
                  const q = await fetch('/graphql', { method: 'POST', credentials: 'include', headers: { 'content-type': 'application/json' },
                    body: JSON.stringify({ query: {{JsonSerializer.Serialize(Query)}}, variables: { namespace: gift.offer.sandboxId, id: gift.offer.offerId } }) });
                  const offer = (await q.json())?.data?.Catalog?.catalogOffer;
                  if (offer) { gift.offer.title = offer.title; gift.offer.keyImages = offer.keyImages; }
                } catch { }
              }
              return JSON.stringify({ status: 200, body: JSON.stringify(list) });
            })()
            """;
    }

    private static async Task<string?> ExchangeCodeAsync(EpicPlaytime.Session account, CancellationToken ct)
    {
        using var http = ExoHttp.Create(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, EpicExchange);
        request.Headers.Authorization = new AuthenticationHeaderValue("bearer", account.AccessToken);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(true);
        if (!response.IsSuccessStatusCode)
        {
            AppLog.Warn($"Gifts: Epic exchange code refused ({(int)response.StatusCode}).");
            return null;
        }
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(true));
        return doc.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString() : null;
    }

    /// <summary>
    /// Steam's own sign-in page in Exo's sign-in window, on the gifts profile, until Steam sends it on to the gifts
    /// page. UI thread only.
    /// </summary>
    internal async Task<(bool Ok, string Message)> SignInSteamAsync(CancellationToken ct)
    {
        _signIn?.Close();
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new StoreAuthWindow(new StoreAuthWindow.Options(
            "Steam website sign-in",
            "Sign in to Steam",
            ProfileFolder,
            SteamLoginUrl,
            uri => uri.Host is "store.steampowered.com" or "login.steampowered.com" or "help.steampowered.com" or "steamcommunity.com",
            InterceptNavigation: url =>
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host != "store.steampowered.com" ||
                    !uri.AbsolutePath.StartsWith("/gifts", StringComparison.OrdinalIgnoreCase))
                    return false;
                done.TrySetResult(true);
                return true;
            }));
        _signIn = window;
        window.Closed += () => done.TrySetResult(false);
        try
        {
            Directory.CreateDirectory(ProfileFolder);
            await window.StartAsync().ConfigureAwait(true);
            var signedIn = await done.Task.WaitAsync(TimeSpan.FromMinutes(10), ct).ConfigureAwait(true);
            return signedIn ? (true, "Signed in to Steam's website.") : (false, "The Steam window was closed before sign-in finished.");
        }
        catch (TimeoutException)
        {
            return (false, "Steam sign-in timed out. Try again.");
        }
        finally
        {
            window.Close();
            if (ReferenceEquals(_signIn, window)) _signIn = null;
        }
    }

    /// <summary>Forgets the Steam website sign-in (its cookies in the gifts profile). UI thread only.</summary>
    internal async Task SignOutSteamAsync(nint owner, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            using var page = await Page.OpenAsync(owner).ConfigureAwait(true);
            foreach (var url in new[] { "https://store.steampowered.com", "https://login.steampowered.com", "https://help.steampowered.com", "https://steamcommunity.com" })
                await page.DeleteCookiesAsync(url).ConfigureAwait(true);
            Last = new Snapshot(Last.Gifts.Where(g => g.Store != "steam").ToList(), false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A hidden page on the gifts profile, owned by Exo's window.</summary>
    private sealed class Page : IDisposable
    {
        private readonly CoreWebView2Controller _controller;

        private Page(CoreWebView2Controller controller) => _controller = controller;

        private CoreWebView2 Core => _controller.CoreWebView2;

        internal string? Source => Core.Source;

        internal static async Task<Page> OpenAsync(nint owner)
        {
            Directory.CreateDirectory(ProfileFolder);
            // The sign-in window opens this profile too: the same options let both share one browser.
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                WebViewRuntimeLocator.ResolveBrowserExecutableFolder(),
                ProfileFolder,
                new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = false });
            var controller = await environment.CreateCoreWebView2ControllerAsync(
                CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)owner));
            controller.IsVisible = false;
            var core = controller.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            foreach (var kind in new[] { CoreWebView2WebResourceContext.Image, CoreWebView2WebResourceContext.Media, CoreWebView2WebResourceContext.Font })
                core.AddWebResourceRequestedFilter("*", kind);
            core.WebResourceRequested += (_, e) => e.Response = environment.CreateWebResourceResponse(null, 204, "No Content", "");
            return new Page(controller);
        }

        /// <summary>Navigates, then waits for a finished page <paramref name="arrived"/> accepts (redirects and script hops on the way).</summary>
        internal async Task<Uri?> GoAsync(string url, Func<Uri, bool> arrived, CancellationToken ct)
        {
            var landed = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Completed(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs e)
            {
                if (e.IsSuccess && Uri.TryCreate(sender.Source, UriKind.Absolute, out var at) && arrived(at)) landed.TrySetResult(at);
            }
            Core.NavigationCompleted += Completed;
            try
            {
                Core.Navigate(url);
                return await landed.Task.WaitAsync(PageWait, ct).ConfigureAwait(true);
            }
            catch (TimeoutException)
            {
                AppLog.Warn("Gifts: a store page did not finish loading: " + StoreAuthWindow.Describe(Core.Source));
                return null;
            }
            finally
            {
                Core.NavigationCompleted -= Completed;
            }
        }

        /// <summary>
        /// Runtime.evaluate (awaitPromise, returnByValue) in the page; the string it ends with, or null. A store page
        /// that moves on once more (its own redirect, Cloudflare's check) ends the call (WebView2 throws), so it runs
        /// again on the settled page, up to three times.
        /// </summary>
        internal async Task<string?> RunAsync(string expression, TimeSpan timeout, CancellationToken ct)
        {
            var request = JsonSerializer.Serialize(new { expression, awaitPromise = true, returnByValue = true });
            string reply;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    reply = await Core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", request).AsTask().WaitAsync(timeout, ct).ConfigureAwait(true);
                    break;
                }
                catch (Exception ex) when (attempt < 3 && ex is ArgumentException or System.Runtime.InteropServices.COMException)
                {
                    AppLog.Debug($"Gifts: page call ended ({ex.GetType().Name}) at {StoreAuthWindow.Describe(Core.Source)}; again.");
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(true);
                }
            }
            using var json = JsonDocument.Parse(reply);
            return !json.RootElement.TryGetProperty("exceptionDetails", out _) &&
                   json.RootElement.TryGetProperty("result", out var result) &&
                   result.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        internal async Task<bool> HasCookieAsync(string url, string name) =>
            (await Core.CookieManager.GetCookiesAsync(url)).Any(cookie => cookie.Name == name);

        internal async Task DeleteCookiesAsync(string url)
        {
            foreach (var cookie in await Core.CookieManager.GetCookiesAsync(url))
                Core.CookieManager.DeleteCookie(cookie);
        }

        public void Dispose() => _controller.Close();
    }
}
