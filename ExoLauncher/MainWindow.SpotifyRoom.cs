using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher;

/// <summary>
/// Exo's own Spotify room. Spotify runs hidden as the audio engine (it is
/// never placed on screen except to sign in); the room's pages, queue,
/// devices and actions go through <see cref="SpotifyNative"/>. Every call
/// thaws an idle-frozen client first and retries while its page wakes.
/// </summary>
public sealed partial class MainWindow
{
    private SpotifyNative? _spotifyNative;
    private DesktopClientHost? _spotifyNativeHost;

    /// The bridge bound to the current Spotify host; replaced with it.
    private SpotifyNative? SpotifyBridge()
    {
        if (_musicApp is not { Key: "spotify", IsDisposed: false } host) return null;
        if (_spotifyNative is { } bridge && ReferenceEquals(_spotifyNativeHost, host)) return bridge;
        _spotifyNative?.Dispose();
        var install = DesktopClients.LocateSpotify(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        if (install is null) return null;
        _spotifyNativeHost = host;
        return _spotifyNative = new SpotifyNative(install.AppDirectory);
    }

    /// Starts the hidden client if needed and says where the room stands:
    /// notInstalled, storeEdition, loading, signedOut or running.
    internal async Task<object> SpotifyStartAsync()
    {
        if (WebClientPreferences.Read().MusicService is { } selected && selected != "spotify")
            return new { ok = false, state = "error", message = "Choose Spotify in Settings." };
        if (!DesktopClients.Present(DesktopClients.Spotify))
            return DesktopClients.OnlyStoreEdition(DesktopClients.Spotify)
                ? new { ok = true, state = "storeEdition", message = (string?)DesktopClients.StoreEditionMessage }
                : new { ok = true, state = "notInstalled", message = (string?)null };
        if (_musicWebService != "spotify") SelectMusicService("spotify");
        var host = MusicAppHost("spotify");
        host.SetEngineWanted(true);
        // Closed or failed: start it again (the start that ran before is done and would only be reused).
        if (host.State is DesktopClientHost.ClientState.Closed or DesktopClientHost.ClientState.Error)
        {
            _ = host.ReopenAsync();
            return new { ok = true, state = "loading", message = (string?)null };
        }
        _ = host.EnsureRunningAsync();
        if (host.State != DesktopClientHost.ClientState.Running) return new { ok = true, state = "loading" };
        // Open before Exo (start at login), left by an earlier Exo (its pipe ended with it) or restarted by itself:
        // the room can't reach it, so it starts again the way Exo starts it, and a song that was playing goes on.
        if (!SpotifyDevTools.IsConnected)
        {
            var playing = MediaSessions.Current is { HasTrack: true, Status: "playing", Provider: "spotify" } now ? now : null;
            if (!host.RestartForRoom())
                return new { ok = false, state = "error", message = (string?)"Spotify couldn't be started inside Exo. Try again." };
            if (playing is not null && _spotifyResume is null) _spotifyResume = ResumeAfterRestartAsync(playing);
            return new { ok = true, state = "loading", message = (string?)null };
        }
        var status = await SpotifyCallAsync(bridge => bridge.ViewAsync("status", null, null, 0, _lifetimeCts.Token), attempts: 2);
        if (status is not { ValueKind: JsonValueKind.Object } answer) return new { ok = true, state = "loading" };
        var signedIn = answer.TryGetProperty("signedIn", out var flag) && flag.ValueKind == JsonValueKind.True;
        // Exo's room is for Premium accounts: a Free one gets the one screen every music service shows
        // for an account without its subscription (an unknown plan counts as Premium).
        var free = signedIn && answer.TryGetProperty("premium", out var premium) && premium.ValueKind == JsonValueKind.False;
        if (free) return new { ok = true, state = "notPremium", message = "Exo plays Spotify with a Premium account." };
        return new { ok = true, state = signedIn ? "running" : "signedOut" };
    }

    private Task? _spotifyResume;

    /// The song Spotify was playing when the room restarted it: once Spotify is back (it reopens on its last song,
    /// paused), it plays on from where it was, the seconds the restart took included. Gives up after 45 s.
    private async Task ResumeAfterRestartAsync(MediaSessionService.Snapshot was)
    {
        var at = was.PositionMs is { } ms ? ms + (Environment.TickCount64 - was.CapturedAt) : (long?)null;
        var started = Environment.TickCount64;
        try
        {
            while (Environment.TickCount64 - started < 45_000)
            {
                await Task.Delay(1000, _lifetimeCts.Token);
                // The closing Spotify's song stays shown for 4 s (MediaSessionService.HideDelay): only the new one counts.
                if (Environment.TickCount64 - started < 6000 || !SpotifyDevTools.IsConnected || MediaSessions.Current is not { HasTrack: true } now) continue;
                if (now.Status == "playing") return;   // it went on by itself
                if (now.Title != was.Title) { AppLog.Info("Spotify: restarted for the room on another song; left paused."); return; }
                await MediaSessions.ControlAsync("play", null);
                if (at is { } position && now.CanSeek && (now.DurationMs is not { } length || position < length - 2000))
                    await MediaSessions.ControlAsync("seek", position);
                AppLog.Info("Spotify: restarted for the room and played on.");
                return;
            }
        }
        catch (OperationCanceledException) { }
        finally { _spotifyResume = null; }
    }

    /// Signed out: Spotify's hidden login page hands off to the default browser for sign-in, and
    /// the room waits there until the account arrives. Spotify's own window stays hidden throughout.
    internal async Task<object> SpotifySignInAsync()
    {
        if (SpotifyBridge() is not { } bridge) return new { ok = false, message = "Spotify is not open yet. Try again." };
        _musicApp?.Wake("spotify sign-in");
        bool started;
        try { started = await bridge.SignInAsync(_lifetimeCts.Token); }
        catch (Exception error) when (error is not OperationCanceledException) { AppLog.Warn("Spotify sign-in did not start: " + error.GetType().Name); started = false; }
        return started ? new { ok = true, message = (string?)null } : new { ok = false, message = (string?)"Spotify could not start sign-in. Try again." };
    }

    /// The room closed: the hidden client may freeze again once it is idle.
    internal object SpotifyLeave()
    {
        if (_musicApp is { Key: "spotify" } host) host.SetEngineWanted(false);
        return new { ok = true };
    }

    internal async Task<object> SpotifyViewAsync(string? view, string? uri, string? query, int offset)
    {
        if (view is null || !SpotifyNative.Views.Contains(view)) return new { ok = false, message = "Unknown page." };
        if (view is "playlist" or "album" or "artist" or "show" or "lyrics" or "discography" or "credits" or "radio" && !SpotifyNative.IsUri(uri))
            return new { ok = false, message = "Unknown page." };
        if (view == "songs" && uri is not null && !SpotifyNative.IsId(uri)) return new { ok = false, message = "Unknown page." };
        if (view == "library" && (uri is not null && !SpotifyNative.IsUri(uri) || !SpotifyNative.IsLibraryQuery(query)))
            return new { ok = false, message = "Unknown page." };
        if (view == "songs" && !MusicSongs.IsQuery(query)) return new { ok = false, message = "Unknown page." };
        query = query is { Length: > 200 } ? query[..200] : query;
        Task<JsonElement?> Read() => SpotifyCallAsync(bridge => bridge.ViewAsync(view, uri, query, Math.Clamp(offset, 0, 100_000), _lifetimeCts.Token));
        var data = await Read();
        // A query this build keeps in its main bundle is found in the page at runtime, a moment
        // after it loads: asked once more before the room says it failed.
        if (data is { ValueKind: JsonValueKind.Object } first && first.TryGetProperty("error", out var missing) &&
            missing.GetString() is { } reason && reason.StartsWith("missing query", StringComparison.Ordinal))
        {
            try { await Task.Delay(2000, _lifetimeCts.Token); } catch (OperationCanceledException) { return new { ok = false, message = "Spotify is not answering. Try again." }; }
            data = await Read();
        }
        if (data is not { ValueKind: JsonValueKind.Object } page) return new { ok = false, message = "Spotify is not answering. Try again." };
        if (page.TryGetProperty("error", out var error))
        {
            AppLog.Warn("Spotify room " + view + ": " + error.GetString());
            return new { ok = false, message = error.GetString() == "signed-out" ? "Sign in to Spotify." : "Spotify could not load this page." };
        }
        if (page.GetRawText().Length > MaxMusicPageCharacters)
        {
            AppLog.Warn("Spotify room " + view + ": page too large, dropped.");
            return new { ok = false, message = "Spotify could not load this page." };
        }
        return new { ok = true, data = page };
    }

    /// context is the playlist/album/artist the track plays in, the device id for transfer or the folder
    /// something goes into; query is the Songs page's order and filter a song plays in; name, description
    /// and image are a playlist's or folder's new details (image a JPEG data URL), name also a move or a Jam token.
    internal async Task<object> SpotifyActAsync(string? action, string? uri, string? context, int? index, bool? on, string? uid, string? query,
        string? name = null, string? description = null, string? image = null)
    {
        if (!MusicSongs.IsQuery(query)) return new { ok = false, message = "Nothing to play." };
        if (action is null || !SpotifyNative.Actions.Contains(action)) return new { ok = false, message = "Unknown action." };
        if (!SpotifyNative.IsName(name, 300) || !SpotifyNative.IsName(description, 300) || !SpotifyNative.IsCover(image))
            return new { ok = false, message = "Spotify did not take that. Try again." };
        if (action is "createPlaylist" or "createFolder" or "renameFolder" && string.IsNullOrWhiteSpace(name))
            return new { ok = false, message = "Give it a name." };
        if (action is "createPlaylist" or "createFolder" or "moveToFolder" && context is not null && !SpotifyNative.IsUri(context))
            return new { ok = false, message = "Spotify did not take that. Try again." };
        if (action == "movePlaylistTrack" && (!SpotifyNative.IsUri(context) || name is not { Length: > 6 }))
            return new { ok = false, message = "Spotify did not take that. Try again." };
        if (action == "jamJoin" && !SpotifyNative.IsId(name)) return new { ok = false, message = "That is not a Jam link." };
        if (action == "playlistImage" && image is null) return new { ok = false, message = "Choose a picture." };
        var needsUri = action is "play" or "enqueue" or "skipTo" or "like" or "addToPlaylist" or "removeFromPlaylist" or "playNext" or "dequeue" or "queueMove"
            or "renameFolder" or "deleteFolder" or "moveToFolder" or "editPlaylist" or "playlistImage" or "privacy" or "collaborative" or "movePlaylistTrack"
            or "pin" or "played" or "download";
        if (needsUri && !SpotifyNative.IsUri(uri) && !(action == "play" && SpotifyNative.IsUri(context)))
            return new { ok = false, message = "Nothing to play." };
        if (action is "play" or "addToPlaylist" or "removeFromPlaylist" && context is not null && !SpotifyNative.IsUri(context) ||
            action is "addToPlaylist" or "removeFromPlaylist" && context is null)
            return new { ok = false, message = "Nothing to play." };
        if (action == "transfer" && context is not { Length: > 0 and < 120 }) return new { ok = false, message = "Choose a device." };
        if (action == "queueMove" && context is not { Length: > 6 and < 300 }) return new { ok = false, message = "Spotify did not take that. Try again." };
        if (action == "audio" && context is not ("quality" or "crossfade" or "crossfadeMs" or "gapless" or "autoMix" or "normalize" or "mono" or "smartShuffle" or "exclusive" or "equalizer" or "equalizerGain"))
            return new { ok = false, message = "Unknown setting." };
        if (action is "removeFromPlaylist" or "jamRemove" && !SpotifyNative.IsId(uid)) return new { ok = false, message = "Spotify did not take that. Try again." };
        if (uid is { Length: > 80 }) uid = null;
        var act = new SpotifyAct(action, SpotifyNative.IsUri(uri) ? uri : null, context, index, on, uid, query, name, description, image);
        var reply = await SpotifyCallAsync(bridge => bridge.ActAsync(act, _lifetimeCts.Token));
        if (reply is { ValueKind: JsonValueKind.Object } answer && answer.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            return new { ok = true, uri = answer.TryGetProperty("uri", out var made) && SpotifyNative.IsUri(made.GetString()) ? made.GetString() : null };
        if (reply is { ValueKind: JsonValueKind.Object } failed && failed.TryGetProperty("error", out var error))
            AppLog.Warn("Spotify room " + action + ": " + error.GetString());
        return new { ok = false, message = "Spotify did not take that. Try again." };
    }

    /// A frozen client answers nothing until it thaws: wake it and retry for about three seconds.
    private async Task<JsonElement?> SpotifyCallAsync(Func<SpotifyNative, Task<JsonElement?>> call, int attempts = 7)
    {
        for (var attempt = 0; attempt < attempts && !_lifetimeCts.IsCancellationRequested; attempt++)
        {
            if (SpotifyBridge() is not { } bridge) return null;
            _musicApp?.Wake("spotify room");
            if (attempt > 0)
            {
                try { await Task.Delay(500, _lifetimeCts.Token); } catch (OperationCanceledException) { return null; }
            }
            JsonElement? reply;
            try { reply = await call(bridge); }
            catch { reply = null; }
            if (reply is not null) return reply;
        }
        return null;
    }

    private void DropSpotifyBridge()
    {
        _spotifyNative?.Dispose();
        _spotifyNative = null;
        _spotifyNativeHost = null;
    }
}
