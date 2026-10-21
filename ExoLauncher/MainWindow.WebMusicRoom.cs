using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace ExoLauncher;

/// <summary>
/// Exo's own YouTube Music and Apple Music rooms. The service's web player
/// (music.youtube.com, music.apple.com) loads on the embedded profile as
/// always, but stays transparent behind the room (the same background state
/// the startup preload uses); its pages, queue and actions go through
/// <see cref="YouTubeMusicNative"/> and <see cref="AppleMusicNative"/>. The
/// page itself is never the room: signing in happens in the provider's own
/// sign-in window (<see cref="MusicSignInAsync"/>).
/// </summary>
public sealed partial class MainWindow
{
    /// Loads the hidden page if needed and says where the room stands:
    /// loading, signedOut, notPremium (signed in without the subscription) or running.
    internal async Task<object> WebMusicStartAsync(string service)
    {
        var label = MusicWebPolicy.Label(service);
        if (WebClientPreferences.Read().MusicService is { } selected && selected != service)
            return new { ok = false, state = "error", message = "Choose " + label + " in Settings." };
        if (_musicWebService != service) SelectMusicService(service);
        if (_musicWebFailed) ResetMusicWebView();
        if (_musicWeb is null || _musicWebInit is null)
        {
            SetMusicBounds(service, true, 0, 56, Math.Max(1, RootGrid.ActualWidth),
                Math.Max(1, RootGrid.ActualHeight - 56), background: true);
            if (_musicWeb is null) return new { ok = false, state = "error", message = label + " could not open." };
        }
        if (!_musicWebReady) return new { ok = true, state = "loading" };
        var status = await WebMusicCallAsync(service, service == "apple"
            ? AppleMusicNative.View("status", null, null, 0) : YouTubeMusicNative.View("status", null, null, 0), attempts: 2);
        if (status is not { ValueKind: JsonValueKind.Object } answer ||
            !answer.TryGetProperty("ready", out var ready) || ready.ValueKind != JsonValueKind.True)
            return new { ok = true, state = "loading" };
        var signedIn = answer.TryGetProperty("signedIn", out var flag) && flag.ValueKind == JsonValueKind.True;
        // Apple Music and YouTube Music play only for a subscriber (Premium): signed in without one is the
        // screen every music service shows for that, saying why; its Sign in picks another account (YouTube:
        // Google's account chooser; Apple: the page signs the Apple ID out first).
        var unsubscribed = signedIn && answer.TryGetProperty("subscribed", out var subscribed) && subscribed.ValueKind == JsonValueKind.False;
        _youTubeNeedsPremium = service == "youtube" && unsubscribed;
        if (unsubscribed)
            return new { ok = true, state = "notPremium", message = service == "youtube"
                ? "This account has no YouTube Music Premium. Sign in with one that does."
                : label + " needs a subscription. Sign in with an Apple ID that has one." };
        if (!signedIn && service == "youtube")
            return new { ok = true, state = "signedOut", message = "Sign in with a YouTube Music Premium account." };
        return new { ok = true, state = signedIn ? "running" : "signedOut" };
    }

    /// A music page is rows of tracks; past this it is a page script gone wrong, not data for the room.
    private const int MaxMusicPageCharacters = 4 * 1024 * 1024;

    internal async Task<object> WebMusicViewAsync(string service, string? view, string? uri, string? query, int offset)
    {
        var apple = service == "apple";
        if (view is null || !(apple ? AppleMusicNative.Views : YouTubeMusicNative.Views).Contains(view)) return new { ok = false, message = "Unknown page." };
        var validUri = apple ? AppleMusicNative.IsUri(uri) : YouTubeMusicNative.IsUri(uri);
        if ((apple ? view is "playlist" or "album" or "artist" or "lyrics" or "credits" : YouTubeMusicNative.PageViews.Contains(view)) && !validUri ||
            !apple && view == "search" && uri is not null && !validUri)
            return new { ok = false, message = "Unknown page." };
        if (view == "library" && (!(apple ? AppleMusicNative.IsLibraryQuery(query) : YouTubeMusicNative.IsLibraryQuery(query)) ||
            uri is not null && !validUri))
            return new { ok = false, message = "Unknown page." };
        if (view == "songs" && !MusicSongs.IsQuery(query)) return new { ok = false, message = "Unknown page." };
        query = query is { Length: > 200 } ? query[..200] : query;
        offset = Math.Clamp(offset, 0, 100_000);
        var label = MusicWebPolicy.Label(service);
        var data = await WebMusicCallAsync(service, apple
            ? AppleMusicNative.View(view, uri, query, offset) : YouTubeMusicNative.View(view, uri, query, offset));
        if (data is not { ValueKind: JsonValueKind.Object } page) return new { ok = false, message = label + " is not answering. Try again." };
        if (page.TryGetProperty("error", out var error))
        {
            AppLog.Warn(label + " room " + view + ": " + error.GetString());
            return new { ok = false, message = label + " could not load this page." };
        }
        if (page.GetRawText().Length > MaxMusicPageCharacters)
        {
            AppLog.Warn(label + " room " + view + ": page too large, dropped.");
            return new { ok = false, message = label + " could not load this page." };
        }
        return new { ok = true, data = page };
    }

    /// context is the list or page a song plays in (ytm:list:…, am:albums:…);
    /// query is the Songs page's order and filter a song plays in.
    internal async Task<object> WebMusicActAsync(string service, string? action, string? uri, string? context, int? index, bool? on, string? uid,
        string? query, string? text = null)
    {
        var apple = service == "apple";
        // A queue or catalog row has no uid of its own: the room sends it empty.
        if (uid == "") uid = null;
        if (!MusicSongs.IsQuery(query)) return new { ok = false, message = "Nothing to play." };
        if (action is null || !(apple ? AppleMusicNative.Actions : YouTubeMusicNative.Actions).Contains(action)) return new { ok = false, message = "Unknown action." };
        bool Valid(string? id) => id is null || (apple ? AppleMusicNative.IsUri(id) : YouTubeMusicNative.IsUri(id));
        if (!Valid(uri) || !(apple ? Valid(context) : YouTubeMusicNative.IsContext(action, context)) ||
            !(apple ? AppleMusicNative.IsUid(uid) : YouTubeMusicNative.IsUid(uid)) || !AppleMusicNative.IsText(text))
            return new { ok = false, message = "Nothing to play." };
        if (action is "skipTo" or "like" or "enqueue" or "playNext" or "addToPlaylist" or "station" or "library" or "suggestLess" or "dequeue" && uri is null ||
            action is "editPlaylist" or "deletePlaylist" or "movePlaylistTrack" && context is null || action == "play" && uri is null && context is null)
            return new { ok = false, message = "Nothing to play." };
        var label = MusicWebPolicy.Label(service);
        // Every action is a click the user just made in Exo's room.
        var reply = await WebMusicCallAsync(service, apple
            ? AppleMusicNative.Act(action, uri, context, index, on, uid, query, text) : YouTubeMusicNative.Act(action, uri, context, index, on, uid, query, text), click: apple);
        if (reply is { ValueKind: JsonValueKind.Object } answer && answer.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            return new { ok = true };
        if (reply is { ValueKind: JsonValueKind.Object } failed && failed.TryGetProperty("error", out var error))
            AppLog.Warn(label + " room " + action + ": " + error.GetString());
        return new { ok = false, message = label + " did not take that. Try again." };
    }

    /// Until when the hidden page may open a sign-in window without a click
    /// inside it: Exo's own Sign in button was the click.
    private DateTime _musicSignInUntil;
    private Window? _musicSignInWindow;

    /// Google's own sign-in for YouTube, continuing to music.youtube.com.
    private const string YouTubeSignInUri =
        "https://accounts.google.com/ServiceLogin?service=youtube&passive=true&continue=" +
        "https%3A%2F%2Fwww.youtube.com%2Fsignin%3Faction_handle_signin%3Dtrue%26app%3Ddesktop%26next%3Dhttps%253A%252F%252Fmusic.youtube.com%252F";

    private bool MusicSignInRequested() => DateTime.UtcNow < _musicSignInUntil;

    /// Signed in to YouTube without Premium (the last status read): Sign in opens Google's account chooser.
    private bool _youTubeNeedsPremium;

    /// Google's account chooser for YouTube, to pick or add a Premium account, continuing to music.youtube.com.
    private const string YouTubeChooseAccountUri =
        "https://accounts.google.com/AccountChooser?service=youtube&continue=" +
        "https%3A%2F%2Fwww.youtube.com%2Fsignin%3Faction_handle_signin%3Dtrue%26app%3Ddesktop%26next%3Dhttps%253A%252F%252Fmusic.youtube.com%252F";

    /// The room's Sign in button. Apple Music: the hidden page's own MusicKit
    /// authorize(), which opens Apple's sign-in window. YouTube Music: Google's
    /// sign-in page in Exo's window; when it lands back on music.youtube.com
    /// the window closes and the hidden page reloads signed in. The user signs
    /// in inside the provider's own page; Exo reads nothing from it.
    internal async Task<object> MusicSignInAsync(string? service)
    {
        if (service is not ("apple" or "youtube") || _musicWebService != service || !_musicWebReady)
            return new { ok = false, message = "The player is still opening. Try again." };
        var label = MusicWebPolicy.Label(service);
        if (_musicSignInWindow is { } open)
        {
            open.Activate();
            return new { ok = true };
        }
        if (service == "apple")
        {
            _musicSignInUntil = DateTime.UtcNow.AddMinutes(1);
            var reply = await WebMusicCallAsync(service, AppleMusicNative.SignIn, attempts: 3, click: true);
            if (reply is { ValueKind: JsonValueKind.True }) return new { ok = true };
            _musicSignInUntil = default;
            return new { ok = false, message = label + " could not start sign-in. Try again." };
        }
        var generation = _musicWebGeneration;
        var view = new WebView2 { DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 0, 0, 0) };
        var window = CreateExoWebPopup(label, view);
        _musicSignInWindow = window;
        _musicPopouts[view] = window;
        var closed = false;
        window.Closed += (_, _) =>
        {
            closed = true;
            if (ReferenceEquals(_musicSignInWindow, window)) _musicSignInWindow = null;
            CancelConnectedPermissions(view);
            _musicPopouts.Remove(view);
            try { view.Close(); } catch { }
        };
        window.AppWindow.Resize(new SizeInt32(560, 720));
        window.Activate();
        try
        {
            var environment = await WebViewEnvironmentFactory.GetAsync();
            await view.EnsureCoreWebView2Async(environment, WebViewEnvironmentFactory.EmbeddedControllerOptions(environment));
            if (closed || _musicWebService != service || generation != _musicWebGeneration)
            {
                window.Close();
                return new { ok = false, message = label + " could not start sign-in. Try again." };
            }
            var core = view.CoreWebView2;
            var finished = false;
            // Back on the player: signed in. The hidden page reloads to pick it up.
            core.NavigationStarting += (_, e) =>
            {
                if (finished || !MusicWebPolicy.IsPlayerDocument(service, e.Uri)) return;
                finished = true;
                e.Cancel = true;
                DispatcherQueue.TryEnqueue(() =>
                {
                    try { window.Close(); } catch { }
                    if (_musicWebService == service && generation == _musicWebGeneration) ReloadMusic();
                });
            };
            ConfigureMusicCore(core, environment, view, service, popup: true);
            core.WindowCloseRequested += (_, _) => window.Close();
            core.Navigate(_youTubeNeedsPremium ? YouTubeChooseAccountUri : YouTubeSignInUri);
            return new { ok = true };
        }
        catch (Exception error)
        {
            AppLog.Warn(label + " sign-in window could not open: " + error.GetType().Name);
            try { window.Close(); } catch { }
            return new { ok = false, message = label + " could not start sign-in. Try again." };
        }
    }

    /// A suspended page answers nothing until it resumes: wake it and retry.
    private async Task<JsonElement?> WebMusicCallAsync(string service, string expression, int attempts = 5, bool click = false)
    {
        for (var attempt = 0; attempt < attempts && !_lifetimeCts.IsCancellationRequested; attempt++)
        {
            if (_musicWebService != service || !_musicWebReady || _musicWeb?.CoreWebView2 is not { } core) return null;
            WakeMusicWeb();
            if (attempt > 0)
            {
                try { await Task.Delay(500, _lifetimeCts.Token); } catch (OperationCanceledException) { return null; }
            }
            JsonElement? reply;
            try { reply = await MusicWebEvaluate(core, click)(expression, TimeSpan.FromSeconds(15), _lifetimeCts.Token); }
            catch { reply = null; }
            if (reply is not null) return reply;
        }
        return null;
    }
}
