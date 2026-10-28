using System.Text.Json;

namespace ExoLauncher.Helpers;

/// <summary>
/// Exo's own Apple Music room reads and drives the embedded music.apple.com
/// web player (Exo's WebView2, the <c>embedded</c> profile) the way its own UI
/// does. Pages come from the page's own MusicKit instance
/// (<c>MusicKit.getInstance().api.music</c>), which adds the page's own
/// developer token and the user's sign-in to each request itself; playing,
/// the queue, shuffle, repeat and AutoPlay go through the same instance (setQueue,
/// playNext, playLater, changeToMediaAtIndex, queue.remove, shuffleMode, repeatMode,
/// autoplayEnabled, bitrate); favorites, suggest less, the library, stations and
/// playlist editing are the Apple Music API calls the page's own menus send
/// (/v1/me/favorites, /v1/me/ratings, /v1/me/library, station ra.&lt;id&gt;,
/// POST, PATCH and PUT on /v1/me/library/playlists). Exo never reads a token
/// or a cookie, never plays or decodes audio itself (playback and its DRM stay
/// inside Apple's page), and only acts on what the user clicked. Answers are
/// shaped in the page into the same small DTOs the Spotify and YouTube Music
/// rooms use, with ids written as <c>am:&lt;type&gt;:&lt;id&gt;</c> where type
/// is the Apple Music API's own (songs, albums, playlists, artists, stations,
/// music-videos, library-songs, library-albums, library-playlists,
/// library-artists, library-music-videos, library-playlist-folders).
///
/// How it can fail: the page is loading or suspended (the call answers null
/// and the caller wakes and retries); signed out or without a subscription
/// (status says so and the room shows Exo's sign-in, which starts
/// <see cref="SignIn"/>); a call the API refuses (that view answers
/// <c>{ error }</c>, the rest keep working); a view Apple has renamed (the
/// page's extra shelves are asked for separately, so the page itself still
/// draws). The web player streams AAC at up to 256 kbps: Lossless and Dolby
/// Atmos playback exist only in Apple's own apps, so the room shows an album's
/// formats as facts about the album and never as what is playing.
/// </summary>
internal static class AppleMusicNative
{
    internal static readonly HashSet<string> Views =
        ["status", "home", "browse", "radio", "recent", "history", "search", "library", "songs", "playlists", "playlist", "album", "artist",
            "credits", "queue", "player", "lyrics", "audio"];
    internal static readonly HashSet<string> Actions =
        ["play", "enqueue", "playNext", "skipTo", "like", "shuffle", "repeat", "addToPlaylist", "removeFromPlaylist", "station", "library",
            "suggestLess", "createPlaylist", "editPlaylist", "deletePlaylist", "movePlaylistTrack", "autoplay", "dequeue", "clearQueue", "audio"];

    internal static bool IsUri(string? uri) =>
        uri is { Length: > 5 and < 200 } && uri.StartsWith("am:", StringComparison.Ordinal) &&
        uri.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '_' or '-' or '.');

    /// Library rows carry their own id (a library song) as the uid; queue rows their place in the queue.
    internal static bool IsUid(string? uid) =>
        uid is null || uid.Length is > 0 and < 120 && uid.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    /// "filter|sort": a library filter and one of the sort names the API itself sent (meta.sorts).
    internal static bool IsLibraryQuery(string? query) =>
        query is null || query.Length <= 60 && query.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '|' or '-' or '.' or ':');

    /// A playlist's name and description, or a move in it, as JSON the page parses: typed text, never code.
    internal static bool IsText(string? text) =>
        text is null || text.Length <= 1200 && !text.Any(c => char.IsControl(c) && c is not '\n');

    internal static string View(string view, string? uri, string? query, int offset) =>
        "(" + Adapter + ")().view(" + JsonSerializer.Serialize(view) + "," + JsonSerializer.Serialize(new { uri, text = query, number = offset }) + ")";

    internal static string Act(string action, string? uri, string? context, int? index, bool? on, string? uid, string? query, string? text) =>
        "(" + Adapter + ")().act(" + JsonSerializer.Serialize(action) + "," +
        JsonSerializer.Serialize(new { uri, text = context, number = index ?? -1, on, uid, query, body = text }) + ")";

    /// The page's own sign-in: MusicKit's authorize() opens Apple's sign-in
    /// window (the Sign In button on music.apple.com calls the same). Signed in
    /// without a subscription, the button signs that Apple ID out first so
    /// another can sign in. It is started, not awaited: the user signs in
    /// there, and the room's status check sees it land.
    internal const string SignIn = """
        (function () {
          var m = window.MusicKit && window.MusicKit.getInstance && window.MusicKit.getInstance();
          if (!m || typeof m.authorize !== 'function') return false;
          var ex = window.__exoAppleMusic;
          var start = function () { return Promise.resolve(m.authorize()).catch(function () { }); };
          if (!m.isAuthorized) start();
          else if (ex && ex.subscribed === false && typeof m.unauthorize === 'function') Promise.resolve(m.unauthorize()).then(start, start);
          return true;
        })()
        """;

    /// Installed once per document (v6) and reused.
    private const string Adapter = """
        (function () {
          var w = window, ex = w.__exoAppleMusic;
          if (ex && ex.v === 6) return ex;
          function mk() {
            var M = w.MusicKit, m = null;
            try { m = M && typeof M.getInstance === 'function' ? M.getInstance() : null; } catch (_) { m = null; }
            if (!m || !m.api || typeof m.api.music !== 'function') throw new Error('loading');
            return m;
          }
          // The page's own API client: it signs the request itself.
          function api(path, params, method, body) {
            var opts = method ? { method: method, headers: body ? { 'Content-Type': 'application/json' } : undefined, body: body ? JSON.stringify(body) : undefined } : undefined;
            return Promise.resolve(mk().api.music(path, params || {}, opts)).then(function (r) {
              var d = r && (r.data || r.json) || {};
              if (d.errors && d.errors.length) {
                var e = d.errors[0];
                var err = new Error(String(e.status || '') + ' ' + String(e.title || e.code || 'error'));
                err.status = +e.status || 0;
                throw err;
              }
              return d;
            });
          }
          function tryApi(path, params) { return api(path, params).catch(function () { return null; }); }
          var SF = '{{storefrontId}}';
          function s(v, n) { return typeof v === 'string' ? v.slice(0, n || 300) : ''; }
          function plain(v, n) { return s(String(v || '').replace(/<br\s*\/?>/gi, '\n').replace(/<[^>]*>/g, ''), n); }
          function art(a, size) {
            var url = a && a.url || (typeof a === 'string' ? a : '');
            if (!url) return '';
            size = size || 240;
            url = s(url, 600).replace('{w}', size).replace('{h}', size).replace('{c}', 'bb').replace('{f}', 'jpg');
            return url.indexOf('https://') === 0 ? url : '';
          }
          function link(v) { v = s(v, 400); return v.indexOf('https://music.apple.com/') === 0 ? v : ''; }
          var kinds = { songs: 'track', 'library-songs': 'track', albums: 'album', 'library-albums': 'album', playlists: 'playlist',
            'library-playlists': 'playlist', artists: 'artist', 'library-artists': 'artist', stations: 'station',
            'library-playlist-folders': 'folder', 'music-videos': 'track', 'library-music-videos': 'track' };
          function uri(r) { return r && r.id && r.type ? 'am:' + r.type + ':' + s(r.id, 160) : ''; }
          function parse(u) {
            var m = /^am:([a-z-]+):(.+)$/.exec(u || '');
            return m ? { type: m[1], id: m[2], library: m[1].indexOf('library-') === 0 } : null;
          }
          // A library item points at its catalog twin when the API included it.
          function catalogOf(r) { var c = r && r.relationships && r.relationships.catalog && r.relationships.catalog.data; return c && c[0] || null; }
          function card(r) {
            if (!r || !r.attributes || !kinds[r.type]) return null;
            var a = r.attributes, kind = kinds[r.type];
            var sub = kind === 'album' || kind === 'track' ? a.artistName : kind === 'playlist' ? (a.curatorName || (a.description && a.description.short) || '')
              : kind === 'station' ? (a.isLive ? 'Live' : a.editorialNotes && (a.editorialNotes.short || a.editorialNotes.tagline) || '') : '';
            var image = art(a.artwork) || art(catalogOf(r) && catalogOf(r).attributes && catalogOf(r).attributes.artwork);
            // A library video plays as its catalog twin.
            var id = r.type === 'library-music-videos' && catalogOf(r) ? 'am:music-videos:' + s(catalogOf(r).id, 160) : uri(r);
            return { uri: id, kind: kind, title: s(a.name, 200), subtitle: s(plain(sub), 160), image: image, url: link(a.url) };
          }
          function rel(r, name) { return r && r.relationships && r.relationships[name] && r.relationships[name].data || []; }
          function track(r, fallback) {
            if (!r || !r.attributes) return null;
            var a = r.attributes, artists = rel(r, 'artists').map(function (x) { return { uri: uri(x), name: s(x.attributes && x.attributes.name || '', 120) }; }).filter(function (x) { return x.name; });
            if (!artists.length && a.artistName) artists = [{ uri: '', name: s(a.artistName, 120) }];
            var album = rel(r, 'albums')[0];
            album = album ? { uri: uri(album), name: s(album.attributes && album.attributes.name || a.albumName, 200) } : fallback && fallback.album || null;
            var playable = !!a.playParams || r.type === 'library-songs';
            var cat = catalogOf(r);
            return { uri: uri(r), uid: r.type === 'library-songs' ? s(r.id, 100) : '', title: s(a.name, 200), artists: artists, album: album,
              image: art(a.artwork) || fallback && fallback.image || '', durationMs: +a.durationInMillis || 0,
              explicit: a.contentRating === 'explicit', playable: playable, url: link(a.url || cat && cat.attributes && cat.attributes.url),
              inLibrary: r.type === 'library-songs' ? true : undefined };
          }
          function tracks(list, fallback) { return (list || []).map(function (r) { return track(r, fallback); }).filter(Boolean); }
          function cards(list) { return (list || []).map(card).filter(function (c) { return c && c.title; }); }
          function signedIn() { try { return !!mk().isAuthorized; } catch (_) { return false; } }
          var TRAITS = { 'hi-res-lossless': 'Hi-Res Lossless', lossless: 'Lossless', atmos: 'Dolby Atmos' };
          function badges(a) {
            var t = a && a.audioTraits || [], out = [];
            ['atmos', 'hi-res-lossless', 'lossless'].forEach(function (k) {
              if (t.indexOf(k) >= 0 && !(k === 'lossless' && t.indexOf('hi-res-lossless') >= 0)) out.push(TRAITS[k]);
            });
            if (!out.length && t.indexOf('spatial') >= 0) out.push('Spatial Audio');
            if (a && a.isMasteredForItunes) out.push('Apple Digital Master');
            return out;
          }
          function header(r, wide) {
            var a = r.attributes || {}, desc = a.description && (a.description.standard || a.description.short) || a.editorialNotes && (a.editorialNotes.standard || a.editorialNotes.short) || a.artistBio || '';
            var banner = a.editorialArtwork && (a.editorialArtwork.bannerUber || a.editorialArtwork.storeFlowcase || a.editorialArtwork.subscriptionHero);
            var sub = r.type.indexOf('albums') >= 0 ? [a.artistName, a.genreNames && a.genreNames[0], a.releaseDate && a.releaseDate.slice(0, 4)].filter(Boolean).join(' · ') : a.curatorName || '';
            var cat = catalogOf(r);
            return { uri: uri(r), title: s(a.name, 200), subtitle: s(sub, 160), description: plain(desc, 400),
              image: art(a.artwork, 544) || art(cat && cat.attributes && cat.attributes.artwork, 544),
              banner: wide && banner ? art(banner, 1200).replace(/\/1200x1200[a-z]*\./, '/1200x400sr.') : '',
              url: link(a.url || cat && cat.attributes && cat.attributes.url), badges: badges(a.audioTraits ? a : cat && cat.attributes) };
          }
          // Library and favorites state, as the page's own menus read it.
          function personal(type, id) {
            var lib = type.indexOf('library-') === 0;
            var p = { 'omit[resource]': 'autos', relate: 'library' };
            p['ids[' + type + ']'] = id;
            p['fields[' + type + ']'] = lib ? 'personalRating' : 'inLibrary,personalRating';
            return api(lib ? '/v1/me/library' : '/v1/catalog/' + SF, p).then(function (d) {
              var r = (d.data || [])[0], at = r && r.attributes || {}, l = rel(r, 'library')[0];
              return { favorite: at.personalRating === 1, less: at.personalRating === -1, inLibrary: lib || !!at.inLibrary, libraryId: l && l.id || (lib ? id : '') };
            }, function () { return { favorite: false, less: false, inLibrary: false, libraryId: '' }; });
          }
          // A page's songs with their favorite, suggest-less and library state, in one call per kind (the page's own inLibrary read).
          function rate(list) {
            if (!signedIn() || !list.length) return Promise.resolve(list);
            var by = {};
            list.forEach(function (t) { var p = parse(t.uri); if (p && /songs$/.test(p.type)) (by[p.type] = by[p.type] || []).push(p.id); });
            return Promise.all(Object.keys(by).map(function (type) {
              var lib = type.indexOf('library-') === 0, p = { 'omit[resource]': 'autos' };
              p['ids[' + type + ']'] = by[type].slice(0, 300).join(',');
              p['fields[' + type + ']'] = lib ? 'personalRating' : 'inLibrary,personalRating';
              return tryApi(lib ? '/v1/me/library' : '/v1/catalog/' + SF, p).then(function (d) {
                var at = {};
                ((d && d.data) || []).forEach(function (r) { at[r.type + ':' + r.id] = r.attributes || {}; });
                list.forEach(function (t) {
                  var x = at[t.uri.slice(3)];
                  if (!x) return;
                  t.liked = x.personalRating === 1;
                  t.disliked = x.personalRating === -1;
                  if (!lib) t.inLibrary = !!x.inLibrary;
                });
              });
            })).then(function () { return list; });
          }
          var favorites = {};
          function pageOf(list, next, offset) {
            return offset + list.length + (next ? 100 : 0);
          }
          function shelf(title, list) { var items = cards(list).slice(0, 20); return items.length ? { title: title, items: items } : null; }
          function dateText(v) {
            var t = Date.parse(v || '');
            return t ? new Date(t).toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric', timeZone: 'UTC' }) : '';
          }
          function minutes(ms) {
            var m = Math.round(ms / 60000);
            return m >= 60 ? Math.floor(m / 60) + ' hr ' + (m % 60) + ' min' : m + (m === 1 ? ' minute' : ' minutes');
          }
          // An artist page: its top songs, about, every shelf its own page shows, and its station.
          function artistPage(id) {
            var viewNames = { 'latest-release': 'Latest release', 'full-albums': 'Albums', singles: 'Singles and EPs', 'top-music-videos': 'Music videos',
              'featured-playlists': 'Playlists', 'live-albums': 'Live albums', 'compilation-albums': 'Compilations', 'appears-on-albums': 'Appears on',
              'similar-artists': 'Similar artists' };
            var main = api('/v1/catalog/' + SF + '/artists/' + encodeURIComponent(id), { views: 'top-songs', extend: 'artistBio,bornOrFormed,origin,editorialArtwork,hero', 'include[songs]': 'artists,albums' });
            // The shelves come in a call of their own, so a view Apple renames never takes the page down.
            var more = tryApi('/v1/catalog/' + SF + '/artists/' + encodeURIComponent(id), { views: Object.keys(viewNames).join(','), 'omit[resource]': 'autos' });
            return Promise.all([main, more]).then(function (r) {
              var a = (r[0].data || [])[0], b = r[1] && (r[1].data || [])[0];
              if (!a) throw new Error('no artist');
              var v = a.views || {}, bv = b && b.views || {};
              var top = tracks(v['top-songs'] && v['top-songs'].data).slice(0, 20);
              var h = header(a, true), at = a.attributes || {};
              h.about = plain(at.artistBio, 2000);
              h.facts = [at.origin ? 'From ' + s(at.origin, 120) : '', at.bornOrFormed ? (at.isGroup ? 'Formed ' : 'Born ') + dateText(at.bornOrFormed) : '',
                at.genreNames && at.genreNames.length ? 'Genre ' + at.genreNames.slice(0, 3).join(', ') : ''].filter(Boolean);
              h.station = 'am:stations:ra.' + s(a.id, 100);
              var sections = Object.keys(viewNames).map(function (n) { return shelf(viewNames[n], bv[n] && bv[n].data); }).filter(Boolean);
              return Promise.all([signedIn() ? personal('artists', a.id) : { favorite: false }, rate(top)]).then(function (x) {
                h.saved = !!x[0].favorite;
                return { header: h, contextUri: uri(a), tracks: top, offset: 0, total: top.length, sections: sections };
              });
            });
          }
          // An album, playlist or artist page and its songs (100 at a time).
          function page(a, want) {
            var p = parse(a.uri), offset = Math.max(0, a.number | 0);
            if (!p) return Promise.reject(new Error('unknown page'));
            var base = (p.library ? '/v1/me/library/' : '/v1/catalog/' + SF + '/') + p.type.replace(/^library-/, '') + '/' + encodeURIComponent(p.id);
            if (want === 'artist') {
              var artistId = p.library ? api(base, { include: 'catalog' }).then(function (d) { var c = catalogOf((d.data || [])[0]); return c ? c.id : ''; }) : Promise.resolve(p.id);
              return artistId.then(function (id) { if (!id) throw new Error('no catalog artist'); return artistPage(id); });
            }
            var params = { 'include[songs]': 'artists,albums', 'include[library-songs]': 'catalog', extend: 'audioTraits' };
            if (offset) {
              params.offset = offset; params.limit = 100;
              return api(base + '/tracks', params).then(function (d) {
                var list = tracks(d.data);
                return rate(list).then(function () { return { header: null, contextUri: a.uri, tracks: list, offset: offset, total: pageOf(list, d.next, offset) }; });
              }, function (e) { if (e.status === 404) return { header: null, contextUri: a.uri, tracks: [], offset: offset, total: offset }; throw e; });
            }
            params.include = p.library ? 'tracks,catalog' : 'tracks,artists';
            params.extend = 'audioTraits,editorialArtwork';
            return api(base, params).then(function (d) {
              var r = (d.data || [])[0];
              if (!r) throw new Error('no ' + want);
              var t = r.relationships && r.relationships.tracks || {}, h = header(r, false), at = r.attributes || {};
              var list = tracks(t.data, { image: h.image.replace('544x544', '240x240'), album: want === 'album' ? { uri: a.uri, name: h.title } : null });
              var extra = Promise.resolve(null);
              if (want === 'album') {
                var by = rel(r, 'artists')[0];
                if (by) h.artists = [{ uri: uri(by), name: s(by.attributes && by.attributes.name || '', 120) }];
                var count = at.trackCount || list.length, length = list.reduce(function (n, x) { return n + x.durationMs; }, 0);
                h.facts = [dateText(at.releaseDate), count + (count === 1 ? ' song' : ' songs') + (length ? ', ' + minutes(length) : ''),
                  s(at.recordLabel, 160), s(at.copyright, 200)].filter(Boolean);
                var catId = p.library ? catalogOf(r) && catalogOf(r).id : p.id;
                if (catId) extra = tryApi('/v1/catalog/' + SF + '/albums/' + encodeURIComponent(catId), { views: 'more-by-artist,other-versions,you-might-also-like', 'omit[resource]': 'autos' });
              } else if (want === 'playlist') {
                h.facts = [at.lastModifiedDate ? 'Updated ' + dateText(at.lastModifiedDate) : '', (at.trackCount || list.length) ? (at.trackCount || list.length) + ' songs' : ''].filter(Boolean);
              }
              h.editable = p.type === 'library-playlists' && !!at.canEdit;
              var total = Math.max(at.trackCount || 0, pageOf(list, t.next, 0));
              var out = { header: h, contextUri: a.uri, tracks: list, offset: 0, total: total, sections: [] };
              return Promise.all([extra, rate(list), signedIn() && !p.library ? personal(p.type, p.id) : null]).then(function (x) {
                var v = x[0] && (x[0].data || [])[0] && x[0].data[0].views || {};
                var byName = h.artists && h.artists[0] ? 'More by ' + h.artists[0].name : 'More by this artist';
                out.sections = [shelf(byName, v['more-by-artist'] && v['more-by-artist'].data), shelf('Other versions', v['other-versions'] && v['other-versions'].data),
                  shelf('You might also like', v['you-might-also-like'] && v['you-might-also-like'].data)].filter(Boolean);
                h.saved = p.library || !!(x[2] && x[2].inLibrary);
                return out;
              });
            });
          }
          function queueTrack(it) {
            if (!it) return null;
            var a = it.attributes || {}, type = it.type === 'song' || !it.type ? (/^i\./.test(it.id) ? 'library-songs' : 'songs') : it.type;
            if (type === 'song') type = 'songs';
            if (type === 'musicVideo') type = 'music-videos';
            var artURL = it.artworkURL || a.artwork && a.artwork.url || '';
            return { uri: 'am:' + type + ':' + s(String(it.id), 160), uid: '', title: s(it.title || a.name, 200),
              artists: (it.artistName || a.artistName) ? [{ uri: '', name: s(it.artistName || a.artistName, 120) }] : [],
              album: null, image: art(artURL), durationMs: +it.playbackDuration || +a.durationInMillis || 0,
              explicit: a.contentRating === 'explicit', playable: it.isPlayable !== false, url: link(a.url) };
          }
          function queueItems() { var q = mk().queue; return q && q.items || []; }
          function nowIndex(m) { var i = m.nowPlayingItemIndex; return typeof i === 'number' && i >= 0 ? i : m.queue && m.queue.position || 0; }
          // Repeat in the room: 0 off, 1 the list, 2 the song; MusicKit: 0 none, 1 one, 2 all.
          var toRoom = { 0: 0, 1: 2, 2: 1 }, toKit = { 0: 0, 1: 2, 2: 1 };
          var songInfo = {};
          function lyricTime(v) {
            v = String(v || '').trim();
            if (!v) return NaN;
            if (/s$/.test(v)) return parseFloat(v) * 1000;
            var parts = v.split(':').map(parseFloat), t = 0;
            for (var i = 0; i < parts.length; i++) t = t * 60 + parts[i];
            return t * 1000;
          }
          // Word timing (the syllable TTML): each timed span is a word, the text after it its trailing space. Words under
          // a ttm:role="x-bg" span are the background vocals, kept apart (bg) rather than run into the main line.
          function words(p) {
            var out = [];
            (function walk(n, bg) {
              [].forEach.call(n.childNodes, function (c) {
                if (c.nodeType === 3) { if (out.length) out[out.length - 1].text += c.textContent; return; }
                if (c.nodeType !== 1) return;
                var inBg = bg || c.getAttribute('ttm:role') === 'x-bg';
                var b = c.getAttribute('begin');
                if (b && !c.querySelector('[begin]')) out.push({ start: Math.round(lyricTime(b)), end: Math.round(lyricTime(c.getAttribute('end'))), text: c.textContent || '', bg: inBg });
                else walk(c, inBg);
              });
            })(p, false);
            return out.length > 1 && out.length < 120 && out.every(function (x) { return isFinite(x.start) && isFinite(x.end); })
              ? out.map(function (x) { var w = { start: x.start, end: x.end, text: s(x.text.replace(/\s+/g, ' '), 80) }; if (x.bg) w.bg = true; return w; }) : null;
          }
          function ttmlLines(ttml) {
            var doc = new DOMParser().parseFromString(String(ttml), 'application/xml');
            var root = doc.documentElement, timing = root && (root.getAttribute('itunes:timing') || '');
            var ps = [].slice.call(doc.getElementsByTagName('p'), 0, 400);
            // Who sings each line (ttm:agent, "v1", "v2"…): a duet's other singer is drawn from the right.
            var lines = ps.map(function (n) {
              return { start: lyricTime(n.getAttribute('begin')), text: s((n.textContent || '').replace(/\s+/g, ' ').trim(), 300), words: timing === 'Word' ? words(n) : null,
                agent: s(n.getAttribute('ttm:agent') || '', 12) };
            });
            var synced = timing !== 'None' && lines.length > 0 && lines.every(function (l) { return isFinite(l.start); });
            return { synced: synced, lines: lines.map(function (l) {
              var line = { start: synced ? Math.max(0, Math.round(l.start)) : 0, text: l.text };
              if (synced && l.words) line.words = l.words;
              if (l.agent) line.agent = l.agent;
              return line;
            }) };
          }
          // The Songs page: every library song, read whole (100 a call, six
          // calls at a time) and kept five minutes, then ordered and filtered
          // here so the same order plays.
          var SONG_SORTS = { added: 1, title: 1, artist: 1, album: 1 };
          var libSongs = null, songOrder = null;
          function songRow(r) {
            var t = track(r);
            if (!t) return null;
            var a = r.attributes || {};
            return { t: t, added: Date.parse(a.dateAdded || '') || 0, title: t.title, artist: s(a.artistName, 120), album: s(a.albumName, 200) };
          }
          function allSongs() {
            if (libSongs && Date.now() - libSongs.at < 300000) return libSongs.list;
            var path = '/v1/me/library/songs', base = { include: 'catalog', 'include[library-songs]': 'catalog', limit: 100 };
            function at(offset) { return api(path, Object.assign({ offset: offset }, base)); }
            var list = at(0).then(function (first) {
              var total = first.meta && +first.meta.total || 0, pages = [first.data || []];
              // No total: read on page by page.
              if (!total) {
                var next = function (d, offset) {
                  if (!d.next || offset > 20000) return pages;
                  return at(offset).then(function (x) { pages.push(x.data || []); return next(x, offset + 100); });
                };
                return next(first, 100);
              }
              var offsets = [];
              for (var o = 100; o < Math.min(total, 20000); o += 100) offsets.push(o);
              function batch(i) {
                if (i >= offsets.length) return pages;
                return Promise.all(offsets.slice(i, i + 6).map(function (o) {
                  return at(o).then(function (x) { pages[o / 100] = x.data || []; });
                })).then(function () { return batch(i + 6); });
              }
              return batch(0);
            }).then(function (pages) {
              var out = [];
              pages.forEach(function (p) { (p || []).forEach(function (r) { var row = songRow(r); if (row) out.push(row); }); });
              return out;
            });
            libSongs = { at: Date.now(), list: list };
            list.catch(function () { libSongs = null; });
            return list;
          }
          function songsQuery(t) {
            t = s(t, 200);
            var bar = t.indexOf('|'), sort = bar < 0 ? '' : t.slice(0, bar);
            return { sort: SONG_SORTS[sort] ? sort : 'added', filter: bar < 0 ? '' : t.slice(bar + 1).trim().toLowerCase() };
          }
          function orderedSongs(text) {
            var q = songsQuery(text), key = q.sort + '|' + q.filter;
            return allSongs().then(function (all) {
              if (songOrder && songOrder.all === all && songOrder.key === key) return songOrder;
              var hasAdded = all.some(function (x) { return x.added; }), sort = q.sort === 'added' && !hasAdded ? 'title' : q.sort;
              var list = q.filter ? all.filter(function (x) {
                return x.title.toLowerCase().indexOf(q.filter) >= 0 || x.artist.toLowerCase().indexOf(q.filter) >= 0 || x.album.toLowerCase().indexOf(q.filter) >= 0;
              }) : all.slice();
              var cmp = function (x, y) { return x.localeCompare(y, undefined, { sensitivity: 'base', numeric: true }); };
              list.sort(sort === 'added' ? function (x, y) { return y.added - x.added; }
                : sort === 'artist' ? function (x, y) { return cmp(x.artist, y.artist) || cmp(x.album, y.album) || cmp(x.title, y.title); }
                : sort === 'album' ? function (x, y) { return cmp(x.album, y.album) || cmp(x.title, y.title); }
                : function (x, y) { return cmp(x.title, y.title); });
              var sorts = [{ id: 'title', name: 'Title' }, { id: 'artist', name: 'Artist' }, { id: 'album', name: 'Album' }];
              if (hasAdded) sorts.unshift({ id: 'added', name: 'Date added' });
              songOrder = { all: all, key: key, list: list, sort: sort, sorts: sorts };
              return songOrder;
            });
          }
          // The editorial pages (Browse is "music", Radio is "radio"): every
          // shelf with something in it, as the page's own grouping nests them.
          function grouping(name) {
            return api('/v1/editorial/' + SF + '/groupings', { name: name, 'omit[resource:artists]': 'autos', platform: 'web', 'include[stations]': 'events' }).then(function (g) {
              var out = [];
              (function walk(list, depth) {
                (list || []).forEach(function (el) {
                  if (depth > 4) return;
                  var title = el.attributes && el.attributes.name, items = cards(rel(el, 'contents'));
                  if (title && items.length) out.push({ title: s(title, 120), items: items.slice(0, 20) });
                  walk(rel(el, 'children'), depth + 1);
                  walk(rel(el, 'tabs'), depth + 1);
                });
              })(g.data, 0);
              return out;
            });
          }
          // Signed in with an active subscription (the page's own account read, kept a minute).
          var account = null;
          function subscribed() {
            if (!signedIn()) return Promise.resolve(false);
            if (account && Date.now() - account.at < 60000) return account.p;
            var p = api('/v1/me/account', { meta: 'subscription', 'challenge[subscriptionCapabilities]': 'voice,premium' }).then(function (d) {
              var sub = d.meta && d.meta.subscription;
              return !sub || sub.active !== false;
            }, function () { return true; });
            account = { at: Date.now(), p: p };
            return p;
          }
          // The sort names your library's lists offer, as the API itself sends them (meta=sorts).
          function sortsOf(meta) {
            var list = meta && meta.sorts || [];
            return list.map(function (x) { return typeof x === 'string' ? { id: s(x, 40), name: s(x, 40) } : { id: s(x.name, 40), name: s(x.displayName || x.name, 60) }; })
              .filter(function (x) { return /^[A-Za-z0-9_.:-]+$/.test(x.id) && x.name; });
          }
          var views = {
            // Your library's songs, 200 at a time, in the order asked for.
            songs: function (a) {
              if (!signedIn()) return { tracks: [], offset: 0, total: 0 };
              var offset = Math.max(0, a.number | 0);
              return orderedSongs(a.text).then(function (o) {
                return { contextUri: 'am:library-songs:all', tracks: o.list.slice(offset, offset + 200).map(function (x) { return x.t; }),
                  offset: offset, total: o.list.length, sorts: o.sorts, sort: o.sort };
              });
            },
            status: function () {
              var m = null;
              try { m = mk(); } catch (_) { m = null; }
              if (!m) return { signedIn: false, ready: false };
              return subscribed().then(function (sub) { me.subscribed = m.isAuthorized ? sub : null; return { signedIn: !!m.isAuthorized, subscribed: sub, ready: true }; });
            },
            // Signed in: the page's own Home (listen-now); otherwise, or when
            // that is empty, its New page (the editorial "music" grouping).
            home: function () {
              var mine = signedIn() ? tryApi('/v1/me/recommendations', { name: 'listen-now', 'include[albums]': 'artists', 'include[library-playlists]': 'catalog',
                'include[personal-recommendation]': 'primary-content', 'omit[resource]': 'autos' }) : Promise.resolve(null);
              return mine.then(function (d) {
                var sections = ((d && d.data) || []).map(function (r) {
                  var t = r.attributes && r.attributes.title;
                  return { title: s(t && (t.stringForDisplay || t) || '', 120), items: cards(rel(r, 'contents')).slice(0, 12) };
                }).filter(function (x) { return x.title && x.items.length; });
                if (sections.length) return { greeting: '', sections: sections.slice(0, 16) };
                return grouping('music').then(function (out) { return { greeting: '', sections: out.slice(0, 16) }; });
              });
            },
            // New (Browse): the editorial shelves, then the top charts.
            browse: function () {
              var charts = tryApi('/v1/catalog/' + SF + '/charts', { types: 'songs,albums,playlists,music-videos', limit: 20, 'omit[resource]': 'autos' });
              return Promise.all([grouping('music'), charts]).then(function (r) {
                var c = r[1] && r[1].results || {}, out = r[0].slice(0, 24);
                [['songs', 'Top songs'], ['albums', 'Top albums'], ['playlists', 'Top playlists'], ['music-videos', 'Top music videos']].forEach(function (k) {
                  var list = c[k[0]] && c[k[0]][0];
                  var sh = list && shelf(list.name ? s(list.name, 120) : k[1], list.data);
                  if (sh) out.push(sh);
                });
                return { sections: out };
              });
            },
            // Radio: Apple Music 1, Hits, Country and the rest live, then the stations and shows.
            radio: function () { return grouping('radio').then(function (out) { return { sections: out.slice(0, 24) }; }); },
            recent: function () {
              if (!signedIn()) return { items: [] };
              return tryApi('/v1/me/recent/played', { limit: 10 }).then(function (d) { return { items: cards(d && d.data).slice(0, 8) }; });
            },
            // The songs you played last, newest first (the page's own History).
            history: function () {
              if (!signedIn()) return { tracks: [] };
              return api('/v1/me/recent/played/tracks', { types: 'songs,library-songs', limit: 30 }).then(function (d) { return { tracks: tracks(d.data) }; });
            },
            search: function (a) {
              var q = s(a.text, 200).trim();
              if (!q) return { tracks: [], artists: [], albums: [], playlists: [] };
              return api('/v1/catalog/' + SF + '/search', { term: q, types: 'songs,albums,artists,playlists,stations,music-videos', limit: 25, with: 'topResults',
                'include[songs]': 'artists,albums', 'omit[resource]': 'autos' }).then(function (d) {
                var r = d.results || {};
                function list(n) { return r[n] && r[n].data || []; }
                var top = cards(list('top')).slice(0, 1);
                return { top: top, tracks: tracks(list('songs')), artists: cards(list('artists')), albums: cards(list('albums')), playlists: cards(list('playlists')),
                  stations: cards(list('stations')), videos: cards(list('music-videos')) };
              });
            },
            // Your library: playlists (with folders, as the page shows them),
            // albums, artists, recently added, made for you and music videos,
            // in the sort orders the API offers for each.
            library: function (a) {
              var filters = [{ id: 'playlists', name: 'Playlists' }, { id: 'albums', name: 'Albums' }, { id: 'artists', name: 'Artists' },
                { id: 'recent', name: 'Recently added' }, { id: 'madeforyou', name: 'Made for you' }, { id: 'videos', name: 'Music videos' }];
              var parts = s(a.text, 60).split('|'), filter = parts[0] || 'playlists', sort = parts[1] || '';
              var empty = { items: [], filters: filters, filter: filter === 'playlists' ? '' : filter, sorts: [], sort: '', folder: null };
              if (!signedIn()) return empty;
              var folder = parse(a.uri), meta = null;
              function all(path, params, pages, size) {
                var out = [];
                size = size || 100;
                function next(offset, n) {
                  var p = Object.assign({ limit: size, offset: offset }, params);
                  return api(path, p).then(function (d) {
                    if (!meta) meta = d.meta || {};
                    out = out.concat(d.data || []);
                    return d.next && n > 1 ? next(offset + size, n - 1) : out;
                  });
                }
                return next(0, pages);
              }
              var sorted = function (p) { p.meta = 'sorts'; if (sort) p.sort = sort; return p; };
              var req;
              if (filter === 'playlists') {
                var id = folder && folder.type === 'library-playlist-folders' ? folder.id : 'p.playlistsroot';
                req = all('/v1/me/library/playlist-folders/' + encodeURIComponent(id) + '/children', { 'include[library-playlists]': 'catalog' }, 3)
                  .catch(function () { return id === 'p.playlistsroot' ? all('/v1/me/library/playlists', { include: 'catalog' }, 3) : []; });
              } else if (filter === 'albums') req = all('/v1/me/library/albums', sorted({ include: 'catalog' }), 5);
              else if (filter === 'recent') req = all('/v1/me/library/recently-added', { include: 'catalog' }, 4, 25);
              else if (filter === 'madeforyou') req = all('/v1/me/library/playlists', { 'filter[featured]': 'made-for-you', 'include[library-playlists]': 'catalog' }, 1);
              else if (filter === 'videos') req = all('/v1/me/library/music-videos', sorted({ include: 'catalog' }), 3);
              else req = all('/v1/me/library/artists', sorted({ include: 'catalog' }), 5);
              return req.then(function (list) {
                empty.items = cards(list).slice(0, 500);
                empty.sorts = sortsOf(meta);
                empty.sort = s(meta && meta.currentSort, 40) || sort;
                if (folder && folder.type === 'library-playlist-folders') empty.folder = { uri: a.uri, title: '', parent: '' };
                return empty;
              });
            },
            // Your playlists you can add a song to.
            playlists: function () {
              if (!signedIn()) return { items: [] };
              return api('/v1/me/library/playlists', { limit: 100 }).then(function (d) {
                return { items: (d.data || []).filter(function (r) { return r.attributes && r.attributes.canEdit; }).map(card).filter(Boolean) };
              });
            },
            playlist: function (a) { return page(a, 'playlist'); },
            album: function (a) { return page(a, 'album'); },
            artist: function (a) { return page(a, 'artist'); },
            // A song's credits, by role (the page's own View Credits).
            credits: function (a) {
              var p = parse(a.uri);
              if (!p) return { error: 'unknown song' };
              var songId = p.library ? api('/v1/me/library/songs/' + encodeURIComponent(p.id), { include: 'catalog' }).then(function (d) { var c = catalogOf((d.data || [])[0]); return c ? c.id : ''; }) : Promise.resolve(p.id);
              return songId.then(function (id) {
                if (!id) return { title: '', groups: [] };
                return api('/v1/catalog/' + SF + '/songs/' + encodeURIComponent(id), { include: 'credits,artists,albums' }).then(function (d) {
                  var r = (d.data || [])[0];
                  if (!r) throw new Error('no song');
                  var t = track(r);
                  var groups = rel(r, 'credits').map(function (c) {
                    return { role: s(c.attributes && c.attributes.title, 80), people: rel(c, 'credit-artists').map(function (x) {
                      var at = x.attributes || {};
                      return { name: s(at.name, 120), uri: '', roles: (at.roleNames || []).slice(0, 6).map(function (n) { return s(n, 60); }).join(', ') };
                    }).filter(function (x) { return x.name; }) };
                  }).filter(function (c) { return c.role && c.people.length; });
                  return { title: [t.title, r.attributes.artistName].filter(Boolean).join(' · '), groups: groups, sources: '' };
                });
              });
            },
            // Up next as the page shows it: what plays next (yours and the list's), then AutoPlay.
            queue: function () {
              var m = mk(), items = queueItems(), i = nowIndex(m);
              var current = queueTrack(items[i]), queued = [], auto = [];
              for (var k = i + 1; k < items.length && queued.length + auto.length < 100; k++) {
                var t = queueTrack(items[k]);
                if (!t) continue;
                t.uid = String(k);
                (items[k].isAutoplay ? auto : queued).push(t);
              }
              return { current: current, queued: queued, next: [], autoplayed: auto, autoplay: !!m.autoplayEnabled };
            },
            player: function () {
              var m = mk(), now = m.nowPlayingItem, t = queueTrack(now);
              var out = { trackUri: t ? t.uri : '', contextUri: '', shuffle: m.shuffleMode === 1, repeat: toRoom[m.repeatMode] || 0, liked: false,
                canShuffle: !!t, canRepeat: !!t, image: t ? art(now.artworkURL || '', 544) : '', title: t ? t.title : '', artists: t ? t.artists : [],
                autoplay: !!m.autoplayEnabled };
              if (!t) return out;
              var p = parse(t.uri), key = t.uri;
              var info = songInfo[key] || (songInfo[key] = Promise.all([
                p.library || p.type !== 'songs' ? Promise.resolve(null) : tryApi('/v1/catalog/' + SF + '/songs/' + encodeURIComponent(p.id), { include: 'artists' }),
                signedIn() ? personal(p.type, p.id) : Promise.resolve({ favorite: false })
              ]).then(function (r) {
                var song = r[0] && (r[0].data || [])[0];
                return { artists: song ? tracks([song])[0].artists : null, favorite: !!r[1].favorite };
              }));
              return info.then(function (i) {
                if (i.artists && i.artists.length) out.artists = i.artists;
                out.liked = key in favorites ? favorites[key] : i.favorite;
                return out;
              }, function () { return out; });
            },
            // The song's lyrics as the page's own Lyrics view has them: word
            // by word (syllable TTML) where Apple has it, else line by line.
            lyrics: function (a) {
              var p = parse(a.uri);
              if (!p || !signedIn()) return { lines: [], synced: false };
              var songId = p.library ? api('/v1/me/library/songs/' + encodeURIComponent(p.id), { include: 'catalog' }).then(function (d) { var c = catalogOf((d.data || [])[0]); return c ? c.id : ''; }) : Promise.resolve(p.type === 'songs' ? p.id : '');
              return songId.then(function (id) {
                if (!id) return { lines: [], synced: false };
                var path = '/v1/catalog/' + SF + '/songs/' + encodeURIComponent(id);
                return tryApi(path + '/syllable-lyrics').then(function (d) {
                  return d && d.data && d.data[0] ? d : tryApi(path + '/lyrics');
                }).then(function (d) {
                  var ttml = d && d.data && d.data[0] && d.data[0].attributes && d.data[0].attributes.ttml;
                  return ttml ? ttmlLines(ttml) : { lines: [], synced: false };
                });
              });
            },
            // What the web player streams, as the room's one audio quality choice: AAC at 256 kbps (High) or 64 kbps.
            audio: function () {
              return { qualities: [{ id: '256', name: 'High', detail: 'AAC, 256 kbps' }, { id: '64', name: 'Standard', detail: 'AAC, 64 kbps, for slow connections' }],
                quality: +mk().bitrate === 64 ? '64' : '256' };
            }
          };
          var queueKeys = { albums: 'album', 'library-albums': 'album', playlists: 'playlist', 'library-playlists': 'playlist', stations: 'station' };
          function songIds(list) { return list.filter(function (t) { return t.playable; }).map(function (t) { return parse(t.uri).id; }); }
          function one(p) {
            if (p.type === 'music-videos') return { musicVideo: p.id, startPlaying: true };
            if (/songs$/.test(p.type)) return { song: p.id, startPlaying: true };
            if (queueKeys[p.type]) { var q = { startPlaying: true }; q[queueKeys[p.type]] = p.id; return q; }
            return null;
          }
          function body(a) { try { return JSON.parse(a.body || '{}') || {}; } catch (_) { return {}; } }
          // Every song id of a library playlist, in order (the page's own all-item-ids read).
          function playlistIds(id) {
            var out = [];
            function next(offset) {
              return api('/v1/me/library/playlists/' + encodeURIComponent(id) + '/tracks', { limit: 100, offset: offset }).then(function (d) {
                (d.data || []).forEach(function (r) { out.push({ id: r.id, type: r.type }); });
                return d.next && offset < 5000 ? next(offset + 100) : out;
              }, function (e) { if (e.status === 404) return out; throw e; });
            }
            return next(0);
          }
          var acts = {
            // A song in its album or playlist, a whole list, an artist's top
            // songs, a station, a music video, or one song.
            play: function (a) {
              var song = parse(a.uri), ctx = parse(a.text), m = mk(), index = a.number | 0;
              if (ctx && queueKeys[ctx.type]) {
                var q = {}; q[queueKeys[ctx.type]] = ctx.id; q.startPlaying = true;
                if (song && index >= 0) q.startWith = index;
                return m.setQueue(q).then(function () { return true; });
              }
              // The Songs page: its list as shown, from the chosen song (up to
              // 50 before it, 250 after).
              if (ctx && ctx.type === 'library-songs' && ctx.id === 'all') {
                return orderedSongs(a.query).then(function (o) {
                  var at = Math.max(0, index), list = o.list;
                  if (song && (!list[at] || list[at].t.uri !== a.uri)) {
                    at = 0;
                    for (var i = 0; i < list.length; i++) if (list[i].t.uri === a.uri) { at = i; break; }
                  }
                  var from = Math.max(0, at - 50), ids = list.slice(from, at + 250).map(function (x) { return parse(x.t.uri).id; });
                  if (!ids.length) return false;
                  return m.setQueue({ songs: ids, startWith: Math.min(at - from, ids.length - 1), startPlaying: true }).then(function () { return true; });
                });
              }
              if (ctx && /artists$/.test(ctx.type)) {
                return page({ uri: a.text, number: 0 }, 'artist').then(function (pg) {
                  var ids = songIds(pg.tracks);
                  if (!ids.length) return false;
                  var start = song ? Math.max(0, ids.indexOf(song.id)) : 0;
                  return m.setQueue({ songs: ids, startWith: start, startPlaying: true }).then(function () { return true; });
                });
              }
              var q1 = song ? one(song) : ctx ? one(ctx) : null;
              return q1 ? m.setQueue(q1).then(function () { return true; }) : false;
            },
            enqueue: function (a) {
              var p = parse(a.uri), q = p && one(p);
              if (!q) return false;
              var m = mk();
              if (!m.nowPlayingItem) return m.setQueue(q).then(function () { return true; });
              delete q.startPlaying;
              return Promise.resolve(m.playLater(q)).then(function () { return true; });
            },
            // Play next: right after the playing song (MusicKit's own playNext), or simply played when nothing is.
            playNext: function (a) {
              var p = parse(a.uri), q = p && one(p);
              if (!q) return false;
              var m = mk();
              if (!m.nowPlayingItem) return m.setQueue(q).then(function () { return true; });
              delete q.startPlaying;
              return Promise.resolve(m.playNext(q)).then(function () { return true; });
            },
            skipTo: function (a) {
              var m = mk(), items = queueItems(), at = parseInt(a.uid, 10);
              if (at >= 0 && items[at] && queueTrack(items[at]).uri === a.uri) return Promise.resolve(m.changeToMediaAtIndex(at)).then(function () { return true; });
              for (var i = 0; i < items.length; i++) {
                var t = queueTrack(items[i]);
                if (t && t.uri === a.uri) return Promise.resolve(m.changeToMediaAtIndex(i)).then(function () { return true; });
              }
              return false;
            },
            // A song from the queue at its place (the page checks it is still there, as its own Remove does).
            dequeue: function (a) {
              var m = mk(), at = parseInt(a.uid, 10), item = queueItems()[at];
              if (!(at > nowIndex(m)) || !item || queueTrack(item).uri !== a.uri) return false;
              m.queue.remove(at);
              return true;
            },
            // Clear: what you and the list queued after this song; AutoPlay stays.
            clearQueue: function () {
              var m = mk(), i = nowIndex(m), items = queueItems();
              for (var k = items.length - 1; k > i; k--) if (!items[k].isAutoplay) m.queue.remove(k);
              return true;
            },
            autoplay: function (a) { mk().autoplayEnabled = !!a.on; return true; },
            // A song, artist or playlist: the page's own Favorite. An album or
            // playlist page's Save adds it to (or removes it from) the library.
            like: function (a) {
              var p = parse(a.uri);
              if (!p || !signedIn()) return false;
              if (/albums$/.test(p.type)) return libraryToggle(p, !!a.on);
              var q = {}; q['ids[' + p.type + ']'] = p.id;
              return api('/v1/me/favorites', q, a.on ? 'POST' : 'DELETE').then(function () {
                favorites[a.uri] = !!a.on;
                return true;
              });
            },
            // Suggest less like this, or take it back (the page's own ratings: -1, or none).
            suggestLess: function (a) {
              var p = parse(a.uri);
              if (!p || !signedIn()) return false;
              var path = '/v1/me/ratings/' + p.type + '/' + encodeURIComponent(p.id);
              var call = a.on ? api(path, {}, 'PUT', { attributes: { value: -1 }, type: 'rating' }) : api(path, {}, 'DELETE');
              return call.then(function () { if (a.on) favorites[a.uri] = false; return true; });
            },
            library: function (a) { var p = parse(a.uri); return p && signedIn() ? libraryToggle(p, !!a.on) : false; },
            // A station from a song or an artist (the page's own Create Station: ra.<catalog id>).
            station: function (a) {
              var p = parse(a.uri);
              if (!p) return false;
              var id = p.type === 'stations' ? Promise.resolve(p.id.replace(/^ra\./, '')) : !p.library ? Promise.resolve(p.id)
                : api('/v1/me/library/' + p.type.replace(/^library-/, '') + '/' + encodeURIComponent(p.id), { include: 'catalog' }).then(function (d) { var c = catalogOf((d.data || [])[0]); return c ? c.id : ''; });
              return id.then(function (cid) {
                if (!cid) return false;
                return mk().setQueue({ station: 'ra.' + cid, startPlaying: true }).then(function () { return true; });
              });
            },
            shuffle: function (a) { mk().shuffleMode = a.on ? 1 : 0; return true; },
            repeat: function (a) { mk().repeatMode = toKit[Math.max(0, Math.min(2, a.number | 0))]; return true; },
            addToPlaylist: function (a) {
              var song = parse(a.uri), list = parse(a.text);
              if (!song || !/songs$/.test(song.type) || !list || list.type !== 'library-playlists') return false;
              return api('/v1/me/library/playlists/' + encodeURIComponent(list.id) + '/tracks', { representation: 'resources' }, 'POST',
                { data: [{ id: song.id, type: song.type }] }).then(function () { return true; });
            },
            removeFromPlaylist: function (a) {
              var list = parse(a.text);
              if (!list || list.type !== 'library-playlists' || !a.uid) return false;
              return api('/v1/me/library/playlists/' + encodeURIComponent(list.id) + '/tracks', { 'ids[library-songs]': a.uid, mode: 'all' }, 'DELETE')
                .then(function () { return true; });
            },
            // New playlist: its name and description, with the song it was made from (the page's own New Playlist).
            createPlaylist: function (a) {
              var b = body(a), name = s(b.name, 200).trim(), song = parse(a.uri);
              if (!name || !signedIn()) return false;
              var req = { attributes: { name: name, description: s(b.description, 1000) } };
              if (song && /songs$/.test(song.type)) req.relationships = { tracks: { data: [{ id: song.id, type: song.type }] } };
              return api('/v1/me/library/playlists', {}, 'POST', req).then(function () { return true; });
            },
            // Rename or describe your playlist (the page's own Edit: PATCH name and description).
            editPlaylist: function (a) {
              var list = parse(a.text), b = body(a), name = s(b.name, 200).trim();
              if (!list || list.type !== 'library-playlists' || !name) return false;
              return api('/v1/me/library/playlists/' + encodeURIComponent(list.id), {}, 'PATCH',
                { attributes: { name: name, description: s(b.description, 1000) } }).then(function () { return true; });
            },
            deletePlaylist: function (a) {
              var list = parse(a.text);
              if (!list || list.type !== 'library-playlists') return false;
              return api('/v1/me/library/playlists/' + encodeURIComponent(list.id), {}, 'DELETE').then(function () { return true; });
            },
            // A song to a new place in your playlist: the whole order written back, as the page's own drag does (PUT tracks).
            movePlaylistTrack: function (a) {
              var list = parse(a.text), b = body(a), from = b.from | 0, to = b.to | 0;
              if (!list || list.type !== 'library-playlists' || !a.uid || from < 0 || to < 0) return false;
              return playlistIds(list.id).then(function (ids) {
                if (!ids[from] || ids[from].id !== a.uid || to >= ids.length) return false;
                var moved = ids.splice(from, 1)[0];
                ids.splice(to, 0, moved);
                return api('/v1/me/library/playlists/' + encodeURIComponent(list.id) + '/tracks', {}, 'PUT', { data: ids }).then(function () { return true; });
              });
            },
            // High (256 kbps) or Standard (64 kbps), kept for the next time the page opens.
            audio: function (a) {
              if ([64, 256].indexOf(a.number) < 0) return false;
              mk().bitrate = a.number;
              try { localStorage.setItem('exo.bitrate', String(a.number)); } catch (_) { }
              return true;
            }
          };
          // Save adds the catalog item to the library; unsave removes the
          // library copy the page itself would remove.
          function libraryToggle(p, on) {
            libSongs = null;
            if (on) {
              if (p.library) return true;
              var q = { representation: 'ids' }; q['ids[' + p.type + ']'] = p.id;
              return api('/v1/me/library', q, 'POST').then(function () { return true; });
            }
            var lib = p.library ? Promise.resolve(p.id) : personal(p.type, p.id).then(function (me) { return me.libraryId; });
            return lib.then(function (id) {
              if (!id) return true;
              return api('/v1/me/library/' + p.type.replace(/^library-/, '') + '/' + encodeURIComponent(id), {}, 'DELETE').then(function () { return true; });
            });
          }
          var me = {
            v: 6,
            subscribed: null,
            view: function (name, a) {
              if (!views[name]) return Promise.resolve({ error: 'unknown view' });
              try {
                return Promise.resolve(views[name](a || {})).catch(function (e) { return { error: String(e && e.message || e).slice(0, 200) }; });
              } catch (e) { return Promise.resolve({ error: String(e && e.message || e).slice(0, 200) }); }
            },
            act: function (name, a) {
              if (!acts[name]) return Promise.resolve({ ok: false, error: 'unknown action' });
              try {
                return Promise.resolve(acts[name](a || {})).then(function (v) { return v === false ? { ok: false, error: name + ' refused' } : { ok: true }; },
                  function (e) { return { ok: false, error: String(e && e.message || e).slice(0, 200) }; });
              } catch (e) { return Promise.resolve({ ok: false, error: String(e && e.message || e).slice(0, 200) }); }
            }
          };
          // The quality chosen in Exo, back on the page after it reloads.
          try { var kept = +localStorage.getItem('exo.bitrate'); if (kept === 64 || kept === 256) mk().bitrate = kept; } catch (_) { }
          w.__exoAppleMusic = me;
          return me;
        })
        """;
}
