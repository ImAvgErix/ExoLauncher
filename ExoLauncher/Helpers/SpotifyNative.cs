using System.Text.Json;

namespace ExoLauncher.Helpers;

/// <summary>
/// Exo's own Spotify room reads and drives the hidden Spotify client through
/// the platform APIs its own UI uses. Spotify 1.3 keeps them in one registry
/// keyed by <c>Symbol.for(name)</c> (PlayerAPI, LibraryAPI, PlaylistAPI,
/// ConnectAPI, SocialConnectAPI, BuddyFeedAPI…), reached through the React
/// tree; catalogue pages go through the client's own GraphQL request with this
/// build's persisted query hashes (<see cref="SpotifyQueries"/>), and lyrics
/// and friend activity through the client's own RequestBuilder, exactly the
/// calls its lyrics view and Friend Activity panel make. Every request is made
/// by Spotify with its own session: Exo never sees a token, plays no audio
/// itself and changes nothing about what the account may do. Answers are
/// shaped in the page into small DTOs (cards, tracks, a header) so only what
/// the room shows crosses the DevTools socket. A Jam's join token stays in the
/// page; only its share link leaves it.
///
/// How it can fail: the page is not loaded or is frozen while idle (the call
/// answers null and the caller wakes and retries); the user is signed out
/// (no registry: <c>{ signedIn: false }</c>); this build lacks a query or an
/// API (that view answers <c>{ error }</c> and the rest keep working).
/// </summary>
/// One action from Exo's Spotify room, as it reaches the page.
internal sealed record SpotifyAct(string Action, string? Uri, string? Context, int? Index, bool? On, string? Uid, string? Query,
    string? Name, string? Description, string? Image);

internal sealed class SpotifyNative : IDisposable
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(12);
    private readonly SpotifyPage _page = new();
    private readonly string _install;

    internal SpotifyNative(string spotifyDirectory) => _install = spotifyDirectory;

    internal static readonly HashSet<string> Views =
        ["status", "home", "search", "library", "collection", "songs", "playlist", "album", "artist", "show", "queue", "devices", "player",
         "lyrics", "friends", "jam", "recent", "playlists", "audio", "discography", "credits", "radio"];
    internal static readonly HashSet<string> Actions =
        ["play", "enqueue", "skipTo", "like", "shuffle", "repeat", "transfer", "addToPlaylist", "removeFromPlaylist",
         "jamStart", "jamLeave", "jamEnd", "jamRemove", "audio", "playNext", "dequeue", "queueMove", "clearQueue",
         "createPlaylist", "createFolder", "renameFolder", "deleteFolder", "moveToFolder", "editPlaylist", "playlistImage",
         "privacy", "collaborative", "movePlaylistTrack", "pin", "played", "download", "speed", "jamJoin", "jamGuestsPlay", "jamGuestVolume"];

    internal Task<JsonElement?> ViewAsync(string view, string? uri, string? query, int offset, CancellationToken cancellation) =>
        CallAsync("view(" + JsonSerializer.Serialize(view) + "," + JsonSerializer.Serialize(new { uri, text = query, number = offset }) + ")", cancellation);

    /// The older actions read context as text; the library and playlist ones read context, and
    /// name (a name, a move "before|uri|uid" or a Jam token), description and image (a JPEG data URL).
    internal Task<JsonElement?> ActAsync(SpotifyAct act, CancellationToken cancellation) =>
        CallAsync("act(" + JsonSerializer.Serialize(act.Action) + "," + JsonSerializer.Serialize(new
        {
            uri = act.Uri, text = act.Context, context = act.Context, number = act.Index ?? -1, on = act.On, uid = act.Uid,
            query = act.Query, name = act.Name, description = act.Description, image = act.Image,
        }) + ")", cancellation);

    /// Signed out, Spotify's hidden login page starts its own browser sign-in (its "Log in" button,
    /// which hands off to the default browser); Spotify's window never shows. False when the page is not the login page.
    internal async Task<bool> SignInAsync(CancellationToken cancellation) =>
        await _page.EvaluateAsync("(function(){var b=document.querySelector('[data-testid=\"login-button\"]');if(!b)return false;b.click();return true})()",
            CallTimeout, cancellation) is { ValueKind: JsonValueKind.True };

    /// <summary>Whether the page answers at all: the wake-up before an action, which is then sent once and never repeated.</summary>
    internal Task<JsonElement?> PingAsync(CancellationToken cancellation) => _page.EvaluateAsync("1", TimeSpan.FromSeconds(2), cancellation);

    private async Task<JsonElement?> CallAsync(string call, CancellationToken cancellation)
    {
        // The first read unzips the 12 MB xpui.spa: off the UI thread.
        var known = await Task.Run(() => SpotifyQueries.Read(_install), cancellation);
        var result = await _page.EvaluateAsync("(" + Adapter + ")(" + JsonSerializer.Serialize(known) + ")." + call, CallTimeout, cancellation);
        if (known.Count < SpotifyQueries.Names.Length) await LearnAsync(cancellation);
        return result;
    }

    /// Queries the page found at runtime are remembered for this build (<see cref="SpotifyQueries.Remember"/>).
    private async Task LearnAsync(CancellationToken cancellation)
    {
        try
        {
            var found = await _page.EvaluateAsync("(function(){var e=window.__exoSpotify;return e&&e.known?e.known(" + JsonSerializer.Serialize(SpotifyQueries.Names) + "):null})()", CallTimeout, cancellation);
            if (found is not { ValueKind: JsonValueKind.Object } names) return;
            SpotifyQueries.Remember(_install, names.EnumerateObject()
                .Where(pair => pair.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(pair => pair.Name, pair => pair.Value.GetString()!, StringComparer.Ordinal));
        }
        catch (Exception error) when (error is not OperationCanceledException) { AppLog.Debug("Spotify queries not read from the page: " + error.GetType().Name); }
    }

    public void Dispose() => _page.Dispose();

    /// Valid Spotify URIs only: the room never sends anything else into the page.
    internal static bool IsUri(string? uri) =>
        uri is { Length: > 8 and < 200 } && uri.StartsWith("spotify:", StringComparison.Ordinal) &&
        uri.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '_' or '-' or '.' or '%');

    /// A library filter and sort ("filter|sort"): Spotify's own ids, numbers or a tag URI.
    internal static bool IsLibraryQuery(string? query) =>
        query is null || query.Length <= 200 && query.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '_' or '-' or '|');

    /// A plain name (a playlist, a folder): no control characters.
    internal static bool IsName(string? name, int max) => name is null || name.Length <= max && !name.Any(char.IsControl);

    /// A new playlist cover: a JPEG data URL Exo's room drew (at most 640 px, well under 1 MB).
    internal static bool IsCover(string? image) =>
        image is null || image.Length < 1_400_000 && image.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal);

    /// A Jam member id or a playlist row id: short and plain.
    internal static bool IsId(string? id) =>
        id is { Length: > 0 and <= 80 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or ':');

    /// Installed once per document (v5) and reused. hashes is name → sha256
    /// for this build's persisted queries.
    private const string Adapter = """
        (function (hashes) {
          var w = window, ex = w.__exoSpotify;
          if (ex && ex.v === 22) { ex.hashes = hashes; return ex; }
          var found = { registry: null, gql: null }, seen = {}, scanned = 0;
          // A persisted query document: { name, operation, sha256Hash, value }.
          function note(d) {
            try {
              if (d && typeof d === 'object' && d.operation === 'query' && typeof d.name === 'string' &&
                  typeof d.sha256Hash === 'string' && /^[0-9a-f]{64}$/.test(d.sha256Hash)) seen[d.name] = d.sha256Hash;
            } catch (_) {}
          }
          // Queries of the main bundle are not in xpui.spa (it loads from a
          // V8 snapshot): read their declarations from the source of the
          // module factories the page has registered, through the rspack
          // runtime its chunks register with (this build exposes no module
          // cache). Source text only; no factory runs. At most every 10 s:
          // a route chunk loaded since adds its queries.
          var decl = /"([A-Za-z0-9_]{1,80})","query","([0-9a-f]{64})"/g;
          function scan() {
            var now = Date.now();
            if (scanned && now - scanned < 2000) return; scanned = now;
            var req = null, chunks = w.rspackChunk || w.webpackChunkclient_web;
            try { if (chunks && chunks.push) chunks.push([[Symbol('exo')], {}, function (r) { req = r; }]); } catch (_) {}
            var factories = req && req.m;
            if (!factories) return;
            for (var id in factories) {
              var src = '';
              try { src = Function.prototype.toString.call(factories[id]); } catch (_) { continue; }
              if (src.indexOf('"query"') < 0) continue;
              decl.lastIndex = 0;
              for (var m; (m = decl.exec(src));) if (!seen[m[1]]) seen[m[1]] = m[2];
            }
          }
          function hash(name) {
            var h = me.hashes && me.hashes[name];
            if (h) return h;
            if (!seen[name]) scan();
            return seen[name];
          }
          function key(n, p) { for (var k in n) if (k.lastIndexOf(p, 0) === 0) return n[k]; return null; }
          function locate() {
            if (found.registry && found.gql) return found;
            var roots = [document.getElementById('main'), document.getElementById('root')].concat([].slice.call(document.body ? document.body.children : []));
            for (var r = 0; r < roots.length && !(found.registry && found.gql); r++) {
              var c = roots[r] && key(roots[r], '__reactContainer$');
              if (!c) continue;
              var stack = [c.stateNode && c.stateNode.current || c], seen = 0;
              while (stack.length && seen++ < 200000) {
                var f = stack.pop(), v = f.memoizedProps && f.memoizedProps.value;
                if (v && typeof v === 'object') {
                  try {
                    if (!found.registry && v._map instanceof Map && typeof v.resolve === 'function' && v._map.has(Symbol.for('PlayerAPI'))) found.registry = v;
                    else if (!found.registry && typeof v.getRegistry === 'function') { var g = v.getRegistry(); if (g && typeof g.resolve === 'function') found.registry = g; }
                    if (!found.gql && typeof v.request === 'function' && Object.keys(v).length === 1) {
                      found.gql = v;
                      // Every query Spotify's own UI runs names its document;
                      // remembered for a build the runtime scan cannot read.
                      var own = v.request;
                      v.request = function (d) { note(d); return own.apply(this, arguments); };
                    }
                  } catch (_) {}
                }
                if (found.registry && found.gql) break;
                if (f.sibling) stack.push(f.sibling);
                if (f.child) stack.push(f.child);
              }
            }
            return found;
          }
          function api(name) {
            var reg = locate().registry;
            if (!reg) throw new Error('signed-out');
            var a = reg.resolveNoThrow ? reg.resolveNoThrow(Symbol.for(name)) : reg.resolve(Symbol.for(name));
            if (!a) throw new Error('missing ' + name);
            return a;
          }
          function gql(name, vars) {
            var g = locate().gql, h = hash(name);
            if (!g) throw new Error('missing graphql');
            if (!h) throw new Error('missing query ' + name);
            return g.request({ name: name, operation: 'query', sha256Hash: h, value: null }, vars).then(function (r) {
              if (!r || !r.data) throw new Error('empty ' + name);
              return r.data;
            });
          }
          function s(v, n) { return typeof v === 'string' ? v.slice(0, n || 300) : ''; }
          // Spotify's own image URIs: a single image, or a mosaic of four
          // covers (a playlist without its own picture), drawn at 300 or 640 px.
          function img(url, size) {
            url = s(url, 400);
            if (url.indexOf('spotify:image:') === 0) return 'https://i.scdn.co/image/' + url.slice(14);
            if (url.indexOf('spotify:mosaic:') === 0) return 'https://mosaic.scdn.co/' + (size >= 480 ? 640 : 300) + '/' + url.slice(15).split(':').join('');
            return url.indexOf('https://') === 0 ? url : '';
          }
          // Smallest source at least min px wide (240 for rows and cards, 480
          // for page headers), else the largest there is.
          function pick(list, min) {
            min = min || 240;
            if (!list) return '';
            if (typeof list === 'string') return img(list, min);
            if (list.sources) list = list.sources;
            if (list.items) list = list.items[0] && list.items[0].sources || [];
            if (!Array.isArray(list) || !list.length) return '';
            var best = null, big = null;
            for (var i = 0; i < list.length; i++) {
              var x = list[i]; if (!x || !x.url) continue;
              var wd = x.width || x.maxWidth || (x.label === 'xlarge' ? 640 : x.label === 'large' ? 640 : x.label === 'standard' ? 300 : x.label === 'small' ? 64 : 0);
              if (wd >= min && (!best || wd < best.w)) best = { u: x.url, w: wd };
              if (!big || wd > big.w) big = { u: x.url, w: wd };
            }
            return img((best || big || {}).u, min);
          }
          function names(list) {
            var items = list && list.items ? list.items : list || [];
            var out = [];
            for (var i = 0; i < items.length && out.length < 6; i++) {
              var a = items[i]; if (!a) continue;
              var nm = a.name || a.profile && a.profile.name;
              if (nm) out.push({ uri: s(a.uri, 120), name: s(nm, 120) });
            }
            return out;
          }
          function kind(uri) {
            if (/:collection:your-episodes$/.test(uri || '')) return 'episodes';
            if (/:folder:/.test(uri || '')) return 'folder';
            var m = /^spotify:(?:user:[^:]+:)?([a-z]+)/.exec(uri || ''); return m ? m[1] : '';
          }
          function card(d) {
            if (!d) return null;
            if (d.data && !d.uri) d = d.data;
            var uri = s(d.uri, 160); if (!uri) return null;
            var k = kind(uri), sub = '';
            if (k === 'collection' || d.type === 'collection') k = 'collection';
            if (k === 'album') sub = names(d.artists).map(function (a) { return a.name; }).join(', ');
            else if (k === 'playlist') sub = s((d.ownerV2 && d.ownerV2.data && d.ownerV2.data.name) || (d.owner && (d.owner.displayName || d.owner.name)) || d.madeForName || '', 120);
            else if (k === 'show') sub = s(d.publisher && (d.publisher.name || d.publisher) || '', 120);
            else if (k === 'folder' && typeof d.playlistCount === 'number') sub = d.playlistCount + (d.playlistCount === 1 ? ' playlist' : ' playlists');
            var image = pick(d.coverArt) || pick(d.images) || pick(d.visuals && d.visuals.avatarImage) || pick(d.avatarImage) || pick(d.album && d.album.images);
            return { uri: uri, kind: k, title: s(d.name || d.profile && d.profile.name, 200), subtitle: sub, image: image };
          }
          function track(t, fallbackImage, extra) {
            if (!t) return null;
            if (t.track) t = t.track;
            if (t.item && t.item.data) t = t.item.data;
            if (t.data && !t.uri) t = t.data;
            var uri = s(t.uri, 160), k = kind(uri); if (k !== 'track' && k !== 'episode' && k !== 'local') return null;
            var album = t.albumOfTrack || t.album || null;
            var show = t.show || t.podcastV2 && t.podcastV2.data || null;
            var ms = t.duration && (t.duration.totalMilliseconds || t.duration.milliseconds) || 0;
            var artists = names(t.artists);
            if (!artists.length && show && show.name) artists = [{ uri: s(show.uri, 120), name: s(show.name, 120) }];
            // In Liked Songs, from the client's own library cache (what its hearts read).
            var liked;
            if (k === 'track') try { liked = !!api('LibraryAPI').containsSync(uri); } catch (_) { liked = undefined; }
            // An episode's release date and where you are in it (ShowAPI.getContents' playedState).
            var ps = t.playedState || null, date = t.releaseDate && s(t.releaseDate.isoString, 10) || '';
            return {
              liked: liked, date: date || undefined,
              played: ps ? ps.state === 'COMPLETED' : undefined,
              progressMs: ps && ps.state !== 'COMPLETED' && ps.playPositionMilliseconds > 0 ? Number(ps.playPositionMilliseconds) | 0 : undefined,
              uri: uri, uid: s(extra && extra.uid || t.uid, 80), title: s(t.name, 200),
              artists: artists,
              album: album && album.uri ? { uri: s(album.uri, 120), name: s(album.name, 200) } : null,
              image: pick(album && (album.coverArt || album.images)) || pick(t.images) || pick(t.coverArt) || pick(show && (show.images || show.coverArt)) || fallbackImage || '',
              durationMs: typeof ms === 'number' ? ms : 0,
              explicit: !!(t.isExplicit || t.contentRating && t.contentRating.label === 'EXPLICIT'),
              playable: t.isPlayable !== false && !(t.playability && t.playability.playable === false)
            };
          }
          function tracks(list, fallbackImage) {
            var out = [];
            for (var i = 0; list && i < list.length; i++) { var t = track(list[i], fallbackImage, list[i]); if (t) out.push(t); }
            return out;
          }
          function cards(list, limit) {
            var out = [];
            for (var i = 0; list && i < list.length && out.length < (limit || 50); i++) {
              var c = card(list[i] && (list[i].content || list[i].item || list[i]));
              if (c && c.title) out.push(c);
            }
            return out;
          }
          // The player's item lists only 300 px covers; its metadata names the 640 px one.
          function cover(item) {
            var m = item && item.metadata || {};
            return img(m.image_xlarge_url || m.image_large_url, 640) || pick(item && (item.images || item.album && item.album.images), 480);
          }
          // The Songs page's orders: getTracks' sort, and the sorting metadata
          // Spotify's own Liked Songs page plays its list with.
          var SONG_SORTS = {
            added: { field: { field: 'ADDED_AT', order: 'DESC' }, criteria: 'added_at DESC, album_title, album_artist_name, album_disc_number, album_track_number',
              list: 'addTime DESC,album.name,album.artist.name,discNumber,trackNumber' },
            title: { field: { field: 'NAME', order: 'ASC' }, criteria: 'title ASC', list: 'name ASC' },
            artist: { field: { field: 'ARTIST_NAME', order: 'ASC' }, criteria: 'artist_name ASC, album_title, album_disc_number, album_track_number',
              list: 'artist.name ASC,album.name,discNumber,trackNumber' },
            album: { field: { field: 'ALBUM_NAME', order: 'ASC' }, criteria: 'album_title ASC, album_disc_number, album_track_number',
              list: 'album.name ASC,discNumber,trackNumber' }
          };
          var SONG_SORT_NAMES = [{ id: 'added', name: 'Date added' }, { id: 'title', name: 'Title' }, { id: 'artist', name: 'Artist' }, { id: 'album', name: 'Album' }];
          function songsQuery(t) {
            t = s(t, 200);
            var bar = t.indexOf('|'), sort = bar < 0 ? '' : t.slice(0, bar);
            return { sort: SONG_SORTS[sort] ? sort : 'added', filter: bar < 0 ? '' : t.slice(bar + 1).trim() };
          }
          // Liked Songs as the Songs page shows it: its order, and its filter
          // the way the Liked Songs page's own filtered play sends it.
          function songsContext(liked, q) {
            var o = SONG_SORTS[q.sort], ctx = { uri: liked, metadata: { 'sorting.criteria': o.criteria, list_util_sort: o.list } };
            if (q.filter) {
              var util = 'text contains ' + encodeURIComponent(q.filter);
              ctx.url = 'context://' + liked + '?filter=' + encodeURIComponent(util);
              ctx.metadata['filtering.predicate'] = 'text =^# "' + q.filter.replace(/"/g, '') + '"';
              ctx.metadata.list_util_filter = util;
            }
            return ctx;
          }
          // A write whose arguments this build may shape differently: a failure names the method's
          // own source head, so app.log shows what Spotify's API expects.
          function call(name, method, args) {
            var a = api(name), f = a[method];
            if (typeof f !== 'function') throw new Error('missing ' + name + '.' + method);
            return Promise.resolve().then(function () { return f.apply(a, args); }).catch(function (e) {
              var head = ''; try { head = Function.prototype.toString.call(f).replace(/\s+/g, ' ').slice(0, 160); } catch (_) {}
              throw new Error(name + '.' + method + ': ' + String(e && e.message || e).slice(0, 120) + ' | ' + head);
            });
          }
          // Inside a folder (Spotify's own "after the folder" means into it), else the top of the list.
          function place(folder) { return folder ? { after: { uri: folder } } : { before: 'start' }; }
          // Liked Songs' genre and mood chips (getTracksFilterTags), kept here: their filters stay in the page.
          var tagList = null;
          function tags() {
            var lib = api('LibraryAPI');
            if (!lib.getTracksFilterTags) return Promise.resolve([]);
            return Promise.resolve(lib.getTracksFilterTags()).then(function (list) {
              tagList = (list || []).slice(0, 30).filter(function (x) { return x && x.filter && x.name; });
              return tagList;
            }, function () { return []; });
          }
          function tagFilter(id) {
            if (!id) return Promise.resolve(null);
            return (tagList ? Promise.resolve(tagList) : tags()).then(function (list) {
              var i = /^tag(\d+)$/.exec(id); return i && list[+i[1]] ? list[+i[1]].filter : null;
            });
          }
          // Downloaded for offline (OfflineAPI.getAvailabilitySync): true, false, or null when this build does not say.
          function downloaded(uri) {
            try {
              var v = api('OfflineAPI').getAvailabilitySync(uri);
              v = v && typeof v === 'object' ? v.availability : v;
              return typeof v === 'string' ? (v === 'yes' || v === 'downloaded' ? true : v === 'downloading' || v === 'waiting' ? 'downloading' : false) : null;
            } catch (_) { return null; }
          }
          // An artist's About: biography, followers, monthly listeners, world rank, top cities, links.
          function about(ar) {
            var p = ar.profile || {}, st = ar.stats || {};
            var bio = s(p.biography && p.biography.text, 4000).replace(/<[^>]*>/g, '').replace(/&amp;/g, '&').replace(/&quot;/g, '"').replace(/&#39;/g, "'");
            return { bio: bio, followers: Number(st.followers) || 0, listeners: Number(st.monthlyListeners) || 0, rank: Number(st.worldRank) || 0,
              image: pick(ar.visuals && ar.visuals.gallery && ar.visuals.gallery.items && ar.visuals.gallery.items[0], 640),
              cities: (st.topCities && st.topCities.items || []).slice(0, 5).map(function (c) {
                return { city: s(c.city, 80), country: s(c.country, 8), listeners: Number(c.numberOfListeners) || 0 };
              }),
              links: (p.externalLinks && p.externalLinks.items || []).slice(0, 8).map(function (l) { return { name: s(l.name, 40), url: s(l.url, 300) }; })
                .filter(function (l) { return l.name && /^https:\/\//.test(l.url); }) };
          }
          // Tour dates on the artist's page (the overview's concerts).
          function concerts(ar) {
            var ev = ar.goods && ar.goods.events, list = ev && ev.concerts && ev.concerts.items || [];
            return list.slice(0, 20).map(function (c) {
              c = c && c.data || c || {};
              var v = c.venue || {}, l = c.location || v.location || {};
              return { title: s(c.title, 200), date: s(c.startDateIsoString || c.date && c.date.isoString, 25),
                place: s(l.name || l.city, 120), venue: s(v.name, 120), uri: s(c.uri, 160) };
            }).filter(function (c) { return c.date; });
          }
          function likedUri() { try { return String(api('LibraryLikedSongsUri')); } catch (_) { return ''; } }
          function option(o) { return o ? { id: s(String(o.id), 160), name: s(o.name, 60) } : null; }
          // The current Jam, refreshed from Spotify; its share link from the
          // client's own short-link cache (fetched with the join token, which
          // never leaves the page).
          function jamState() {
            var sc = api('SocialConnectAPI'), experience = '';
            try { experience = s(String(sc.getJamExperience()), 20); } catch (_) {}
            return Promise.resolve(sc.fetchCurrentSession()).then(function (x) {
              x = x || sc.getCurrentSession();
              if (!x || !x.sessionId) return { experience: experience, session: null };
              var cached = sc.getCachedShortInviteLink(x.joinSessionToken);
              return Promise.resolve(cached || sc.getShortInviteLink(x.joinSessionToken, x.joinSessionUri)).then(null, function () { return null; }).then(function (link) {
                return { experience: experience, session: {
                  owner: !!x.isSessionOwner, link: s(link && link.shareableUrl, 300),
                  canRemove: !!(x.userCapabilities && x.userCapabilities.removeParticipants),
                  // The Jam panel's own two switches: guests may change what plays (not queue-only), guests may change the volume.
                  guestsPlay: !x.queueOnlyMode,
                  guestVolume: x.participantVolumeControl === 'UNAVAILABLE' || x.participantVolumeControl === undefined ? null : x.participantVolumeControl === 'ENABLED',
                  members: (x.sessionMembers || []).slice(0, 50).map(function (m) {
                    return { id: s(m.id, 80), name: s(m.displayName, 120), image: img(m.imageUrl), me: !!m.isCurrentUser, host: m.id === x.sessionOwnerId };
                  }) } };
              });
            });
          }
          // Spotify's settings object (Settings > Audio quality): found by shape in the platform registry.
          function settings() {
            var reg = locate().registry;
            if (!reg) throw new Error('signed-out');
            var keys = reg._map ? Array.from(reg._map.keys()) : [];
            for (var i = 0; i < keys.length; i++) {
              var v; try { v = reg.resolveNoThrow ? reg.resolveNoThrow(keys[i]) : reg.resolve(keys[i]); } catch (e) { continue; }
              if (v && v.quality && v.quality.streamingQuality) return v;
            }
            throw new Error('missing settings');
          }
          function value(setting) {
            if (!setting) return Promise.resolve(null);
            var g = setting.getValue || setting.get;
            return Promise.resolve(g ? g.call(setting) : setting.value);
          }
          // Smart Shuffle is a play mode of what is playing (ShuffleAPI: 0 off, 1 shuffle, 2 Smart Shuffle), only
          // where Spotify offers it (your playlists, Liked Songs): null elsewhere. Playback's smartShuffle
          // setting is only whether Spotify lists the mode at all.
          function smartShuffleState() {
            try {
              var sh = api('ShuffleAPI'), ctx = api('PlayerAPI').getState().context, uri = ctx && ctx.uri;
              if (!uri) return Promise.resolve(null);
              return Promise.all([sh.getAvailableShuffleModes(uri), sh.getShuffle(uri)]).then(function (r) {
                return (r[0] || []).indexOf(2) < 0 ? null : r[1] === 2;
              }).catch(function () { return null; });
            } catch (e) { return Promise.resolve(null); }
          }
          var views = {
            // Settings > Playback and Audio quality, Spotify's own: quality (0 Automatic, 1 Low, 2 Normal, 3 High,
            // 4 Very high, 5 Lossless), crossfade, gapless, AutoMix, volume normalization, mono, exclusive mode
            // (bit-perfect lossless on the output device) and the equalizer.
            audio: function () {
              var st = settings(), q = st.quality, p = st.playback, eq = null, ex = null;
              try { eq = api('EqualizerAPI'); } catch (e) { }
              try { ex = api('ExclusiveModeAPI'); } catch (e) { }
              return Promise.all([value(q.streamingQuality), value(q.maxSupportedQuality), value(p.audioCrossfade), value(p.audioCrossfadeMs),
                value(p.gapless), value(p.autoMix), value(q.normalizeVolume), value(p.monoAudio),
                eq && eq.isSupported ? Promise.resolve(eq.isSupported()) : false,
                eq && eq.getFilters ? Promise.resolve(eq.getFilters()) : null,
                // On or off arrives through a subscription, which answers with the current state at once.
                eq && eq.subscribeToEnabledState ? new Promise(function (resolve) {
                  var off = null, done = false;
                  off = eq.subscribeToEnabledState(function (on) { if (done) return; done = true; resolve(!!on); if (typeof off === 'function') setTimeout(off, 0); });
                  setTimeout(function () { if (!done) { done = true; resolve(null); } }, 500);
                }) : null,
                ex && ex.getExclusiveModeEnabled ? Promise.resolve(ex.getExclusiveModeEnabled()) : null, smartShuffleState()]).then(function (r) {
                var exclusive = r[11], smartShuffle = r[12];
                return {
                  quality: r[0], lossless: r[1] === 2, crossfade: !!r[2], crossfadeMs: r[3] | 0, gapless: !!r[4], autoMix: !!r[5],
                  normalize: !!r[6], mono: !!r[7], equalizer: { supported: !!r[8], enabled: r[10], filters: r[9] || null },
                  exclusive: exclusive === null ? null : !!exclusive, smartShuffle: smartShuffle === null ? null : !!smartShuffle
                };
              });
            },
            // premium: the account's catalogue (ProductStateAPI, the "catalogue" value Spotify's own
            // Premium checks read); null when this build does not say, which the room takes as Premium.
            status: function () {
              var f = locate();
              scan();
              var q = Object.keys(me.hashes || {});
              for (var n in seen) if (q.indexOf(n) < 0) q.push(n);
              var out = { signedIn: !!f.registry, graphql: !!f.gql, queries: q.slice(0, 200), premium: null };
              if (!f.registry) return out;
              var ps = null; try { ps = api('ProductStateAPI'); } catch (_) {}
              if (!ps || !ps.getValues) return out;
              return Promise.resolve(ps.getValues()).then(function (v) {
                var c = v && (v.catalogue || v.type);
                if (typeof c === 'string' && c) out.premium = c.toLowerCase() !== 'free';
                return out;
              }, function () { return out; });
            },
            home: function () {
              return gql('home', { timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC', sp_t: '', facet: '',
                sectionItemsLimit: 12, includeHomeChapterVideoCards: false, homeEndUserIntegration: 'INTEGRATION_WEB_PLAYER',
                includeEpisodeContentRatingsV2: false }).then(function (d) {
                var h = d.home || {}, sections = [], items = h.sectionContainer && h.sectionContainer.sections && h.sectionContainer.sections.items || [];
                for (var i = 0; i < items.length && sections.length < 16; i++) {
                  var sec = items[i], title = sec.data && sec.data.title && (sec.data.title.transformedLabel || sec.data.title.translatedBaseText);
                  var list = cards(sec.sectionItems && sec.sectionItems.items, 12);
                  if (title && list.length) sections.push({ title: s(title, 120), items: list });
                }
                return { greeting: s(h.greeting && (h.greeting.transformedLabel || h.greeting.translatedBaseText), 80), sections: sections };
              });
            },
            search: function (a) {
              var q = s(a.text, 200).trim();
              if (!q) return { tracks: [], artists: [], albums: [], playlists: [], shows: [], audiobooks: [], episodes: [], top: [] };
              return gql('searchDesktop', { searchTerm: q, offset: 0, limit: 10, numberOfTopResults: 5, includeAudiobooks: true,
                includeArtistHasConcertsField: false, includePreReleases: true, includeAuthors: false }).then(function (d) {
                var r = d.searchV2 || {};
                return {
                  top: cards(r.topResultsV2 && r.topResultsV2.itemsV2, 5).filter(function (c) { return c.kind !== 'track'; }),
                  tracks: tracks(r.tracksV2 && r.tracksV2.items),
                  artists: cards(r.artists && r.artists.items, 10),
                  albums: cards(r.albumsV2 && r.albumsV2.items, 10),
                  playlists: cards(r.playlists && r.playlists.items, 10),
                  shows: cards(r.podcasts && r.podcasts.items, 10),
                  audiobooks: cards(r.audiobooks && r.audiobooks.items, 10),
                  episodes: tracks(r.episodes && r.episodes.items).slice(0, 10)
                };
              });
            },
            // Your Library as Spotify's own sidebar has it: its filters
            // (playlists, albums, artists, podcasts…), its sort orders, and a
            // folder's contents when one is open. text is "filter|sort".
            library: function (a) {
              var lib = api('LibraryAPI'), parts = s(a.text, 200).split('|'), opts = { limit: 500, offset: 0 };
              if (parts[0]) opts.filters = [parts[0]];
              if (parts[1]) opts.sortOrder = parts[1];
              if (a.uri) opts.folderUri = a.uri;
              return lib.getContents(opts).then(function (r) {
                var liked = likedUri(), list = cards(r.items, 500), flags = {};
                (r.items || []).forEach(function (x) { if (x && x.uri) flags[x.uri] = x; });
                list.forEach(function (c) { var x = flags[c.uri]; if (x) { c.pinned = !!x.pinned; c.owned = !!x.isOwnedBySelf; } });
                if (liked && !a.uri && !parts[0] && !list.some(function (c) { return c.uri === liked; }))
                  list.unshift({ uri: liked, kind: 'collection', title: 'Liked Songs', subtitle: '', image: '' });
                list.forEach(function (c) { if (c.uri === liked) { c.kind = 'collection'; c.title = c.title || 'Liked Songs'; } });
                var selected = r.selectedFilters && r.selectedFilters[r.selectedFilters.length - 1];
                return { items: list,
                  filters: (r.availableFilters || []).slice(0, 20).map(option).filter(Boolean),
                  filter: selected ? s(String(selected.id), 160) : '',
                  sorts: (r.availableSortOrders || []).slice(0, 12).map(option).filter(Boolean),
                  sort: r.selectedSortOrder ? s(String(r.selectedSortOrder.id), 40) : '',
                  folder: a.uri ? { uri: s(a.uri, 160), title: s(r.openedFolderName, 200), parent: s(r.parentFolderUri, 160) } : null };
              });
            },
            // Playlists a song can be added to (yours and collaborative ones).
            playlists: function () {
              var lib = api('LibraryAPI'), f = lib.getStaticallyKnownFilters().playlist.filterId;
              return lib.getContents({ limit: 500, offset: 0, filters: [f], flattenTree: true }).then(function (r) {
                return { items: cards((r.items || []).filter(function (x) { return x && (x.canAddTo || x.isOwnedBySelf); }), 300) };
              });
            },
            collection: function (a) {
              var offset = Math.max(0, a.number | 0);
              return api('LibraryAPI').getTracks({ limit: 100, offset: offset }).then(function (r) {
                var liked = likedUri();
                return { header: { uri: liked, title: 'Liked Songs', subtitle: '', image: '' }, contextUri: liked,
                  tracks: tracks(r.items), offset: offset, total: r.unfilteredTotalLength || r.totalLength || 0 };
              });
            },
            // The Songs page: Liked Songs in Spotify's own order and filter,
            // 200 at a time (getTracks as its Liked Songs page calls it).
            songs: function (a) {
              // a.uri: a genre or mood chip ("tag3"), as Spotify's Liked Songs page filters by them.
              var q = songsQuery(a.text), offset = Math.max(0, a.number | 0), liked = likedUri();
              var opts = { limit: 200, offset: offset, sort: SONG_SORTS[q.sort].field, filters: [] };
              if (q.filter) opts.filters.push({ field: 'TEXT', operator: 'CONTAINS', value: q.filter });
              return Promise.all([offset ? null : tags(), tagFilter(a.uri)]).then(function (t) {
                if (t[1]) opts.filters.push(t[1]);
                if (!opts.filters.length) delete opts.filters;
                return api('LibraryAPI').getTracks(opts).then(function (r) {
                  return { contextUri: liked, tracks: tracks(r.items), offset: offset, liked: true, sorts: SONG_SORT_NAMES, sort: q.sort,
                    tags: t[0] ? t[0].map(function (x, i) { return { id: 'tag' + i, name: s(x.name, 60) }; }) : undefined,
                    total: q.filter || t[1] ? r.totalLength || 0 : r.unfilteredTotalLength || r.totalLength || 0 };
                });
              });
            },
            playlist: function (a) {
              var p = api('PlaylistAPI'), offset = Math.max(0, a.number | 0);
              return Promise.all([offset ? null : p.getMetadata(a.uri, {}), p.getContents(a.uri, { limit: 100, offset: offset })]).then(function (r) {
                var m = r[0] || {}, image = pick(m.images, 480), own = !!m.isOwnedBySelf, saved;
                try { saved = own ? undefined : !!api('LibraryAPI').containsSync(a.uri); } catch (_) {}
                var header = offset ? null : { uri: s(a.uri, 160), title: s(m.name, 200), image: image,
                    subtitle: s((m.owner && (m.owner.displayName || m.owner.username)) || '', 120),
                    description: s(m.description, 400).replace(/<[^>]*>/g, ''), editable: !!m.canRemove, owned: own,
                    saved: saved, downloaded: downloaded(a.uri) };
                // Your own playlist: whether it is public (RootlistAPI) and open to others' songs (its base permission).
                var more = header && own ? [
                  call('RootlistAPI', 'getPublishedState', [a.uri]).then(function (v) { header.public = !!v; }, function () { }),
                  call('PlaylistPermissionsAPI', 'getBasePermission', [a.uri]).then(function (v) {
                    v = v && (v.permissionLevel || v.level || v); header.collaborative = v === 'CONTRIBUTOR';
                  }, function () { })] : [];
                return Promise.all(more).then(function () {
                  return { header: header, contextUri: s(a.uri, 160), tracks: tracks(r[1].items), offset: offset, total: r[1].totalLength || 0 };
                });
              });
            },
            album: function (a) {
              return gql('getAlbum', { uri: a.uri, locale: '', offset: 0, limit: 300 }).then(function (d) {
                var al = d.albumUnion || {}, image = pick(al.coverArt, 480);
                var year = al.date && al.date.isoString ? al.date.isoString.slice(0, 4) : '';
                var artists = names(al.artists);
                var list = tracks(al.tracksV2 && al.tracksV2.items, pick(al.coverArt)), ms = 0;
                list.forEach(function (t) { ms += t.durationMs; });
                var by = al.moreAlbumsByArtist && al.moreAlbumsByArtist.items && al.moreAlbumsByArtist.items[0];
                by = by && by.discography && (by.discography.popularReleasesAlbums || by.discography.albums);
                return { header: { uri: s(al.uri, 160), title: s(al.name, 200), image: image,
                    subtitle: artists.map(function (x) { return x.name; }).join(', ') + (year ? ' · ' + year : ''), artists: artists, saved: !!al.saved,
                    date: al.date && s(al.date.isoString, 10) || '', label: s(al.label, 200),
                    copyright: (al.copyright && al.copyright.items || []).slice(0, 4).map(function (c) { return s(c && c.text, 200); }).filter(Boolean),
                    durationMs: ms, downloaded: downloaded(al.uri) },
                  contextUri: s(al.uri, 160), tracks: list, offset: 0, total: al.tracksV2 && al.tracksV2.totalCount || 0,
                  albums: cards(by && by.items, 12).filter(function (c) { return c.uri !== al.uri; }) };
              });
            },
            artist: function (a) {
              return gql('queryArtistOverview', { uri: a.uri, locale: '', includePrerelease: true }).then(function (d) {
                var ar = d.artistUnion || {}, disc = ar.discography || {}, rel = ar.relatedContent || {};
                function releases(x) { return (x && x.items || []).map(function (i) { return i.releases && i.releases.items ? i.releases.items[0] : i; }); }
                var listeners = ar.stats && ar.stats.monthlyListeners;
                return { header: { uri: s(ar.uri, 160), title: s(ar.profile && ar.profile.name, 200),
                    image: pick(ar.visuals && ar.visuals.avatarImage, 480), banner: pick(ar.headerImage && ar.headerImage.data, 1000),
                    subtitle: listeners ? Number(listeners).toLocaleString() + ' monthly listeners' : '', saved: !!ar.saved },
                  contextUri: s(ar.uri, 160),
                  tracks: tracks(disc.topTracks && disc.topTracks.items),
                  albums: cards(releases(disc.albums), 20), singles: cards(releases(disc.singles), 20),
                  related: cards(rel.relatedArtists && rel.relatedArtists.items, 12),
                  compilations: cards(releases(disc.compilations), 20),
                  appearsOn: cards(releases(rel.appearsOn), 20),
                  featuring: cards(rel.featuringV2 && rel.featuringV2.items, 20),
                  discoveredOn: cards(rel.discoveredOnV2 && rel.discoveredOnV2.items, 20),
                  playlists: cards(ar.profile && ar.profile.playlistsV2 && ar.profile.playlistsV2.items, 20),
                  about: about(ar), concerts: concerts(ar) };
              });
            },
            // Every release, newest first, 50 at a time (the artist's Discography page).
            discography: function (a) {
              var offset = Math.max(0, a.number | 0);
              return gql('queryArtistDiscographyAll', { uri: a.uri, offset: offset, limit: 50, order: 'DATE_DESC' }).then(function (d) {
                var all = d.artistUnion && d.artistUnion.discography && d.artistUnion.discography.all || {};
                var items = (all.items || []).map(function (i) { return i.releases && i.releases.items ? i.releases.items[0] : i; });
                var list = [];
                items.forEach(function (x) {
                  var c = card(x); if (!c || !c.title) return;
                  var y = x.date && x.date.year, t = s(x.type, 20).toLowerCase();
                  c.subtitle = [y, t ? t[0].toUpperCase() + t.slice(1) : ''].filter(Boolean).join(' · ');
                  list.push(c);
                });
                return { items: list, offset: offset, total: all.totalCount || list.length };
              });
            },
            // Song credits, as Spotify's own Credits dialog reads them.
            credits: function (a) {
              return gql('queryTrackCreditsGroupedModal', { trackUri: a.uri, contributorsLimit: 100, contributorsOffset: 0 }).then(function (d) {
                var t = d.trackUnion || {}, ct = t.creditsTrait || {}, groups = [], by = {};
                function add(role, person) {
                  role = s(role, 80) || 'Credits';
                  if (!by[role]) groups.push(by[role] = { role: role, people: [] });
                  if (by[role].people.length < 40 && person.name) by[role].people.push(person);
                }
                (ct.contributors && ct.contributors.items || []).slice(0, 200).forEach(function (c) {
                  if (!c) return;
                  var inner = c.contributors && (c.contributors.items || c.contributors);
                  if (Array.isArray(inner)) {
                    inner.forEach(function (p) { add(c.role || c.name, { name: s(p && (p.name || p.profile && p.profile.name), 120), uri: s(p && p.uri, 120) }); });
                    return;
                  }
                  var roles = c.roles && (c.roles.items || c.roles) || [c.role];
                  (Array.isArray(roles) ? roles : [roles]).forEach(function (r) {
                    add(r && (r.role || r.name || r), { name: s(c.name || c.profile && c.profile.name, 120), uri: s(c.uri, 120) });
                  });
                });
                return { title: s(t.name, 200), groups: groups.slice(0, 20),
                  sources: (ct.sources && ct.sources.items || []).map(function (x) { return s(x && x.name, 80); }).filter(Boolean).join(', ') };
              });
            },
            // Song or artist radio: the playlist Spotify makes from a seed (inspiredby-mix), opened as a page.
            radio: function (a) {
              return api('RequestBuilder').build().withHost('https://spclient.wg.spotify.com/inspiredby-mix/v2')
                .withPath('/seed_to_playlist/' + encodeURIComponent(a.uri)).withQueryParameters({ 'response-format': 'json' })
                .withEndpointIdentifier('/seed_to_playlist/{uri}').send().then(function (r) {
                  var b = r && r.body, item = b && b.mediaItems && b.mediaItems[0];
                  if (!item || !/^spotify:playlist:/.test(item.uri || '')) throw new Error('no radio');
                  return { uri: s(item.uri, 160) };
                });
            },
            // A podcast: its details and episodes, 50 at a time.
            show: function (a) {
              var sh = api('ShowAPI'), offset = Math.max(0, a.number | 0);
              return Promise.all([offset ? null : sh.getMetadata(a.uri), sh.getContents(a.uri, { offset: offset, limit: 50 })]).then(function (r) {
                var m = r[0] || {}, image = pick(m.images || m.coverArt, 480), saved = false;
                // Episodes name no artist; the room shows the publisher as plain text.
                var episodes = tracks(r[1] && r[1].items, pick(m.images || m.coverArt));
                episodes.forEach(function (t) { if (!t.artists.length && m.publisherName) t.artists = [{ uri: '', name: s(m.publisherName, 120) }]; });
                try { saved = !!api('LibraryAPI').containsSync(a.uri); } catch (_) {}
                return { header: offset ? null : { uri: s(a.uri, 160), title: s(m.name, 200), image: image,
                    subtitle: s(m.publisherName || m.publisher && (m.publisher.name || m.publisher) || '', 120),
                    description: s(m.description, 400).replace(/<[^>]*>/g, ''), saved: saved, downloaded: downloaded(a.uri) },
                  contextUri: s(a.uri, 160), tracks: episodes, offset: offset,
                  total: r[1] && (r[1].totalCount || r[1].totalLength) || 0 };
              });
            },
            queue: function () {
              return Promise.resolve(api('PlayerAPI').getQueue()).then(function (q) {
                q = q || {};
                return { current: track(q.current), queued: tracks((q.queued || []).slice(0, 50)), next: tracks((q.nextUp || []).slice(0, 50)) };
              });
            },
            devices: function () {
              return Promise.resolve(api('ConnectDevicesAPI').getDevices()).then(function (list) {
                return { devices: (list || []).slice(0, 30).map(function (d) {
                  return { id: s(d.id, 120), name: s(d.name, 120), type: s(d.type, 40), active: !!d.isActive, local: !!d.isLocal };
                }) };
              });
            },
            player: function () {
              var st = api('PlayerAPI').getState() || {}, item = st.item || {}, uri = s(item.uri, 160);
              var lib = api('LibraryAPI'), liked = false;
              try { liked = uri ? !!lib.containsSync(uri) : false; } catch (_) {}
              var r = st.restrictions || {};
              return { trackUri: uri, contextUri: st.context && s(st.context.uri, 160) || '', shuffle: !!st.shuffle,
                repeat: st.repeat | 0, liked: liked, canShuffle: r.canToggleShuffle !== false,
                canRepeat: r.canToggleRepeatContext !== false || r.canToggleRepeatTrack !== false,
                image: cover(item), title: s(item.name, 200), speed: typeof st.speed === 'number' && st.speed > 0 ? st.speed : 1,
                artists: names(item.artists || (item.show ? [item.show] : [])) };
            },
            // The playing song's lyrics, as the client's own lyrics view asks
            // for them: color-lyrics with the cover the song plays with.
            lyrics: function (a) {
              var st = api('PlayerAPI').getState() || {}, item = st.item || {};
              // reason: why there are no lines (not the playing song, no cover to ask with, none on Spotify), for the log.
              if (!a.uri || item.uri !== a.uri || kind(a.uri) !== 'track') return { lines: [], synced: false, reason: 'notPlaying' };
              var art = cover(item);
              if (!art) return { lines: [], synced: false, reason: 'noCover' };
              return api('RequestBuilder').build().withHost('https://spclient.wg.spotify.com/color-lyrics/v2')
                .withPath('/track/' + encodeURIComponent(a.uri.split(':')[2]) + '/image/' + encodeURIComponent(art))
                .withHeaders([{ key: 'App-Platform', value: 'WebPlayer' }]).withQueryParameters({ format: 'json', vocalRemoval: false }).withEndpointIdentifier('/track/{trackId}').send()
                .then(function (r) {
                  var l = r && r.body && r.body.lyrics;
                  if (!l || !l.lines) return { lines: [], synced: false, reason: 'empty:' + typeof (r && r.body) };
                  return { synced: l.syncType === 'LINE_SYNCED' || l.syncType === 'SYLLABLE_SYNCED',
                    lines: l.lines.slice(0, 500).map(function (x) { return { start: Number(x.startTimeMs) || 0, text: s(x.words, 300) }; }) };
                }, function (e) {
                  if (e && e.status === 404) return { lines: [], synced: false, reason: 'none' };
                  throw e;
                });
            },
            // Friend Activity: what the people you follow are playing, from
            // the same buddy list Spotify's own panel reads.
            friends: function () {
              return api('BuddyFeedAPI').presenceView.getBuddyFeed().then(function (r) {
                var list = r && r.body && r.body.friends || [];
                return { friends: list.slice(0, 100).map(function (f) {
                  var u = f.user || {}, t = f.track || {}, c = t.context || {};
                  return { user: { uri: s(u.uri, 160), name: s(u.name, 120), image: img(u.imageUrl) },
                    track: { uri: s(t.uri, 160), title: s(t.name, 200), image: img(t.imageUrl),
                      artist: t.artist ? { uri: s(t.artist.uri, 160), name: s(t.artist.name, 120) } : null,
                      context: c.uri ? { uri: s(c.uri, 160), name: s(c.name, 200) } : null },
                    at: Number(f.timestamp) || 0 };
                }).filter(function (f) { return f.user.name && f.track.title; }) };
              });
            },
            jam: function () { return jamState(); },
            recent: function () {
              var r = api('RecentlyPlayedAPI'), tries = 0;
              return new Promise(function (done) {
                (function look() {
                  var list = null;
                  try { list = r.getContexts(); } catch (_) {}
                  if (list || ++tries > 15) return done({ items: cards(list || [], 12) });
                  setTimeout(look, 200);
                })();
              });
            }
          };
          var acts = {
            play: function (a) {
              var p = api('PlayerAPI'), opts = { featureIdentifier: 'exo' };
              if (typeof a.query === 'string' && a.text && a.text === likedUri())
                return p.play(songsContext(a.text, songsQuery(a.query)), opts, a.uri ? { skipTo: { uri: a.uri } } : {});
              if (a.text) return p.play({ uri: a.text }, opts, a.uri ? { skipTo: a.number >= 0 ? { uri: a.uri, index: a.number } : { uri: a.uri } } : {});
              return p.play({ uri: a.uri }, opts, {});
            },
            enqueue: function (a) { return api('PlayerAPI').addToQueue([{ uri: a.uri }]); },
            // Play next: ahead of everything you queued (Spotify's own Play next), or simply queued when nothing is.
            playNext: function (a) {
              var p = api('PlayerAPI');
              return Promise.resolve(p.getQueue()).then(function (q) {
                // A queued item keeps its song under .track (uri, uid), as the queue view reads it.
                var first = q && q.queued && q.queued[0];
                first = first && (first.track || first);
                return first && first.uri ? p.insertIntoQueue([{ uri: a.uri }], { before: { uri: first.uri, uid: first.uid || null } }) : p.addToQueue([{ uri: a.uri }]);
              });
            },
            dequeue: function (a) { return api('PlayerAPI').removeFromQueue([{ uri: a.uri, uid: a.uid || null }]); },
            // text: "before" or "after", then the other queued song's uri and uid ("before|spotify:track:x|uid").
            queueMove: function (a) {
              var t = String(a.text || '').split('|');
              if (t.length < 2 || (t[0] !== 'before' && t[0] !== 'after')) return false;
              var place = {}; place[t[0]] = { uri: t[1], uid: t[2] || null };
              return api('PlayerAPI').reorderQueue([{ uri: a.uri, uid: a.uid || null }], place);
            },
            clearQueue: function () { return api('PlayerAPI').clearQueue(); },
            skipTo: function (a) { return api('PlayerAPI').skipTo(a.uid ? { uri: a.uri, uid: a.uid } : { uri: a.uri }); },
            like: function (a) { var l = api('LibraryAPI'); return a.on ? l.add({ uris: [a.uri] }) : l.remove({ uris: [a.uri] }); },
            shuffle: function (a) { return api('PlayerAPI').setShuffle(!!a.on); },
            repeat: function (a) { return api('PlayerAPI').setRepeat(Math.max(0, Math.min(2, a.number | 0))); },
            transfer: function (a) { return api('ConnectAPI').transferPlayback(a.text, {}); },
            addToPlaylist: function (a) { return api('PlaylistAPI').add(a.text, [a.uri], { after: 'end' }); },
            removeFromPlaylist: function (a) { return api('PlaylistAPI').remove(a.text, [{ uri: a.uri, uid: a.uid }]); },
            // One audio setting from Exo's Sound panel: text names it; number or on is the value.
            audio: function (a) {
              var st = settings(), q = st.quality, p = st.playback;
              switch (a.text) {
                case 'quality': return q.streamingQuality.setValue(Math.max(0, Math.min(5, a.number | 0)));
                case 'crossfade': return p.audioCrossfade.setValue(!!a.on);
                case 'crossfadeMs': return p.audioCrossfadeMs.setValue(Math.max(0, Math.min(12000, a.number | 0)));
                case 'gapless': return p.gapless.setValue(!!a.on);
                // AutoMix only mixes playlists Spotify picks; a crossfade with it makes every other change smooth too.
                case 'autoMix':
                  return Promise.resolve(p.autoMix.setValue(!!a.on)).then(function () { return a.on ? value(p.audioCrossfade) : true; })
                    .then(function (on) { return on ? true : p.audioCrossfade.setValue(true); });
                case 'normalize': return q.normalizeVolume.setValue(!!a.on);
                case 'mono': return p.monoAudio.setValue(!!a.on);
                case 'smartShuffle':
                  var ctx = api('PlayerAPI').getState().context;
                  if (!ctx || !ctx.uri) return false;
                  return Promise.resolve(a.on ? p.smartShuffle.setValue(true) : true)
                    .then(function () { return api('ShuffleAPI').setShuffle(ctx.uri, a.on ? 2 : 0); });
                case 'exclusive': return api('ExclusiveModeAPI').setExclusiveModeEnabled(!!a.on);
                case 'equalizer': return api('EqualizerAPI').setEnabledState(!!a.on);
                case 'equalizerGain':
                  // uid: the band's key (audio.equalizer.*); number: its gain in dB, -12 to 12.
                  if (!/^audio\.equalizer\.[a-z_0-9]+$/.test(a.uid || '')) return false;
                  return api('EqualizerAPI').setFilterGain(a.uid, Math.max(-12, Math.min(12, a.number | 0)));
                default: return false;
              }
            },
            // Your library and playlists, through the same APIs Spotify's own sidebar, menus and Edit details use.
            // text: a name; uri: a song to put in a new playlist; context: the folder something goes into.
            createPlaylist: function (a) {
              var name = s(a.name, 100).trim(); if (!name) return false;
              return call('RootlistAPI', 'createPlaylist', [name, place(a.context)]).then(function (r) {
                var uri = typeof r === 'string' ? r : r && r.uri;
                if (!uri) return false;
                return a.uri ? call('PlaylistAPI', 'add', [uri, [a.uri], { after: 'end' }]).then(function () { return uri; }) : uri;
              });
            },
            createFolder: function (a) {
              var name = s(a.name, 100).trim(); if (!name) return false;
              return call('RootlistAPI', 'createFolder', [name, place(a.context)]).then(function (r) { return typeof r === 'string' ? r : r && r.uri || true; });
            },
            renameFolder: function (a) { var name = s(a.name, 100).trim(); return name ? call('RootlistAPI', 'renameFolder', [{ uri: a.uri }, name]) : false; },
            deleteFolder: function (a) { return call('RootlistAPI', 'removeFolder', [{ uri: a.uri }]); },
            // Into a folder, or out to the top of the list (no context).
            moveToFolder: function (a) {
              return /:folder:/.test(a.uri) ? call('RootlistAPI', 'moveFolder', [{ uri: a.uri }, place(a.context)])
                : call('RootlistAPI', 'move', [[{ uri: a.uri }], place(a.context)]);
            },
            // Edit details: text is the name, description the description (both as Spotify stores them).
            editPlaylist: function (a) {
              var d = {}, name = s(a.name, 100).trim();
              if (name) d.name = name;
              if (typeof a.description === 'string') d.description = s(a.description, 300);
              return call('PlaylistAPI', 'updateDetails', [a.uri, d]);
            },
            // A new cover, as Edit details uploads it: the picture becomes an upload token, then the playlist's.
            playlistImage: function (a) {
              var m = /^data:image\/jpeg;base64,([A-Za-z0-9+/=]+)$/.exec(a.image || '');
              if (!m) return false;
              var bytes = Uint8Array.from(atob(m[1]), function (c) { return c.charCodeAt(0); });
              var file = new File([bytes], 'cover.jpg', { type: 'image/jpeg' }), p = api('PlaylistAPI');
              return call('PlaylistAPI', 'uploadImage', p.uploadImage.length >= 2 ? [a.uri, file] : [file]).then(function (token) {
                token = token && typeof token === 'object' ? token.uploadToken || token.token : token;
                if (!token) throw new Error('no upload token');
                return call('PlaylistAPI', 'updateDetails', [a.uri, { imageUploadToken: token }]);
              });
            },
            privacy: function (a) { return call('RootlistAPI', 'setPublishedState', [a.uri, !!a.on]); },
            collaborative: function (a) { return call('PlaylistPermissionsAPI', 'setBasePermission', [a.uri, a.on ? 'CONTRIBUTOR' : 'VIEWER']); },
            // A song to another place in your playlist; text: "before" or "after", then the other song's uri and uid.
            movePlaylistTrack: function (a) {
              var t = String(a.name || '').split('|');
              if (t.length < 2 || (t[0] !== 'before' && t[0] !== 'after')) return false;
              var at = {}; at[t[0]] = { uri: t[1], uid: t[2] || null };
              return call('PlaylistAPI', 'move', [a.context, [{ uri: a.uri, uid: a.uid || null }], at]);
            },
            pin: function (a) { return a.on ? call('LibraryAPI', 'pin', [a.uri, { before: 'start' }]) : call('LibraryAPI', 'unpin', [a.uri]); },
            played: function (a) { return call('LibraryAPI', a.on ? 'markAsPlayed' : 'markAsUnPlayed', [[a.uri]]); },
            download: function (a) { return call('OfflineAPI', a.on ? 'addDownload' : 'removeDownload', [a.uri]); },
            // Podcasts and audiobooks: 0.5 to 3.5 times, in tenths (number is the speed times ten).
            speed: function (a) { return call('PlayerAPI', 'setSpeed', [Math.max(5, Math.min(35, a.number | 0)) / 10]); },
            // Join a Jam from its link's token, remotely (on this computer), as Spotify's own Join dialog does.
            jamJoin: function (a) {
              var token = s(a.name, 200);
              return call('SocialConnectAPI', 'getSessionInfo', [token]).then(function (x) {
                if (!x) return false;
                return call('SocialConnectAPI', 'joinSession', [token, 'DEEPLINKING', 'REMOTE', String(Date.now())]).then(function (r) { return !!r; });
              });
            },
            // The Jam's two switches (host): guests change what plays, guests change the volume.
            jamGuestsPlay: function (a) {
              var sc = api('SocialConnectAPI'), x = sc.getCurrentSession();
              return call('SocialConnectAPI', 'setQueueOnlyMode', sc.setQueueOnlyMode.length >= 2 ? [x && x.sessionId, !a.on] : [!a.on]);
            },
            jamGuestVolume: function (a) {
              var sc = api('SocialConnectAPI'), x = sc.getCurrentSession();
              return call('SocialConnectAPI', 'setParticipantVolumeControl', sc.setParticipantVolumeControl.length >= 2 ? [x && x.sessionId, !!a.on] : [!!a.on]);
            },
            jamStart: function () { return api('SocialConnectAPI').createSession().then(function (x) { return !!x; }); },
            jamLeave: function () { return api('SocialConnectAPI').leaveSession(); },
            jamEnd: function () { return api('SocialConnectAPI').deleteSession(); },
            jamRemove: function (a) { return api('SocialConnectAPI').removeSessionMember(a.uid); }
          };
          var me = {
            v: 22, hashes: hashes,
            // The queries this page knows now (from the build's files or found at runtime), for Exo to remember.
            known: function (names) { var o = {}; for (var i = 0; i < names.length; i++) { var h = hash(names[i]); if (h) o[names[i]] = h; } return o; },
            view: function (name, a) {
              if (!views[name]) return Promise.resolve({ error: 'unknown view' });
              try {
                return Promise.resolve(views[name](a || {})).catch(function (e) { return { error: String(e && e.message || e).slice(0, 200) }; });
              } catch (e) { return Promise.resolve({ error: String(e && e.message || e).slice(0, 200) }); }
            },
            // An action that answers false (a Jam Spotify refused) is a failure.
            act: function (name, a) {
              if (!acts[name]) return Promise.resolve({ ok: false, error: 'unknown action' });
              try {
                // A new playlist or folder answers its uri, for the room to open it.
                return Promise.resolve(acts[name](a || {})).then(function (v) {
                  return v === false ? { ok: false, error: name + ' refused' } : typeof v === 'string' && /^spotify:/.test(v) ? { ok: true, uri: s(v, 160) } : { ok: true };
                }, function (e) { return { ok: false, error: String(e && e.message || e).slice(0, 400) }; });
              } catch (e) { return Promise.resolve({ ok: false, error: String(e && e.message || e).slice(0, 400) }); }
            }
          };
          w.__exoSpotify = me;
          return me;
        })
        """;
}
