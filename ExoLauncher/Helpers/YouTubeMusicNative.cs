using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExoLauncher.Helpers;

/// <summary>
/// Exo's own YouTube Music room reads and drives the embedded music.youtube.com
/// page (Exo's WebView2, the <c>embedded</c> profile) the way its own UI does.
/// Pages come from the page's own InnerTube client (<c>ytmusic-app</c>'s
/// networkManager: /browse, /search, /next, /browse/edit_playlist,
/// /playlist/create, /playlist/delete, /like/*), which signs each request
/// itself. The queue changes the way the page's own menus and queue do:
/// Play next, Add to queue and Remove from queue run through a
/// <c>ytmusic-menu-service-item-renderer</c> (the element its song menus are
/// made of), a move through the queue's own drop handler, and autoplay and
/// the song/video switch are the page's own store actions.
/// Exo never reads a cookie or a token, never plays or decodes audio itself,
/// and only acts on what the user clicked (and, once, puts audio quality at
/// the highest the account offers). Answers are shaped in the page into the
/// same small DTOs the Spotify room uses (cards, tracks, a header), with ids
/// written as <c>ytm:&lt;kind&gt;:&lt;id&gt;</c>.
///
/// How it can fail: the page is loading or suspended (the call answers null
/// and the caller wakes and retries); YouTube changed a renderer (that view
/// answers <c>{ error }</c> or fewer items, the rest keep working); signed
/// out or without Premium (status says so and the room shows Exo's sign-in,
/// which opens Google's).
/// </summary>
internal static class YouTubeMusicNative
{
    internal static readonly HashSet<string> Views =
        ["status", "home", "explore", "browse", "search", "library", "songs", "playlist", "album", "artist", "show", "queue", "player",
         "lyrics", "related", "playlists", "audio"];
    internal static readonly HashSet<string> Actions =
        ["play", "skipTo", "like", "dislike", "shuffle", "repeat", "enqueue", "playNext", "dequeue", "queueMove", "clearQueue",
         "radio", "shufflePlay", "autoplay", "video", "addToPlaylist", "removeFromPlaylist", "movePlaylistTrack", "createPlaylist",
         "editPlaylist", "deletePlaylist", "audio"];

    /// Views whose uri names the page they read.
    internal static readonly HashSet<string> PageViews = ["playlist", "album", "artist", "show", "browse", "lyrics", "related"];

    /// The room's ids: ytm:track:&lt;videoId&gt;, ytm:list:&lt;playlistId&gt;,
    /// ytm:playlist|album|artist|show:&lt;browseId&gt;, ytm:browse:&lt;browseId&gt;[:&lt;params&gt;]
    /// (Explore, charts, moods, a discography; params are the page's own, base64),
    /// ytm:filter:&lt;search filter&gt;.
    internal static bool IsUri(string? uri) =>
        uri is { Length: > 6 and < 600 } && uri.StartsWith("ytm:", StringComparison.Ordinal) &&
        uri.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '_' or '-' or '%' or '=' or '.' or '+' or '/');

    /// A queue item's id (its index in the page's queue) or a playlist row's setVideoId.
    internal static bool IsUid(string? uid) =>
        uid is null || uid.Length <= 100 && uid.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static readonly Regex Move = new(@"^(before|after)\|(ytm:[A-Za-z0-9:_%=.+/-]{3,600})?\|[A-Za-z0-9_-]{0,100}$", RegexOptions.CultureInvariant);

    /// A move's target, as the queue and a playlist send it: before|after, then the song and row it goes by.
    internal static bool IsContext(string? action, string? context) =>
        context is null || IsUri(context) || action == "queueMove" && Move.IsMatch(context);

    internal static bool IsLibraryQuery(string? query) =>
        query is null || query.Length <= 40 && query.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '|');

    internal static string View(string view, string? uri, string? query, int offset) =>
        "(" + Adapter + ")().view(" + JsonSerializer.Serialize(view) + "," + JsonSerializer.Serialize(new { uri, text = query, number = offset }) + ")";

    /// text: typed words as JSON (a playlist's name, description and privacy, a move in it), as the room sends every service.
    internal static string Act(string action, string? uri, string? context, int? index, bool? on, string? uid, string? query, string? text = null) =>
        "(" + Adapter + ")().act(" + JsonSerializer.Serialize(action) + "," +
        JsonSerializer.Serialize(new { uri, text = context, number = index ?? -1, on, uid, query, body = text }) + ")";

    /// Installed once per document (v5) and reused.
    private const string Adapter = """
        (function () {
          var w = window, ex = w.__exoYouTubeMusic;
          if (ex && ex.v === 5) return ex;
          var conts = {};
          function app() { var a = document.querySelector('ytmusic-app'); if (!a) throw new Error('loading'); return a; }
          function fetchJson(path, body) {
            var nm = app().networkManager;
            if (!nm || typeof nm.fetch !== 'function') throw new Error('loading');
            return Promise.resolve(nm.fetch(path, body));
          }
          function store() {
            var tags = ['ytmusic-app', 'ytmusic-player-bar', 'ytmusic-fullbleed-thumbnail-renderer', 'ytmusic-player-page', 'ytmusic-nav-bar'];
            for (var i = 0; i < tags.length; i++) { var el = document.querySelector(tags[i]); if (el && el.store && el.store.getState) return el.store; }
            var all = document.querySelectorAll('*');
            for (var j = 0; j < all.length && j < 4000; j++) { if (all[j].store && all[j].store.getState) return all[j].store; }
            return null;
          }
          function cfg(key) { return w.ytcfg && w.ytcfg.get ? w.ytcfg.get(key) : undefined; }
          // A service endpoint (queueAddEndpoint, removeFromQueueEndpoint, a setting's
          // updateServiceEndpoint) run the way a tap on the page's own menu item runs it.
          function service(endpoint) {
            var host = app(), el = document.createElement('ytmusic-menu-service-item-renderer');
            el.style.display = 'none';
            host.appendChild(el);
            var b = el.ytmusicServiceMenuItemBehavior;
            if (!b || typeof b.handleCommand !== 'function') { el.remove(); throw new Error('loading'); }
            return Promise.resolve(b.handleCommand(endpoint)).then(function (v) { el.remove(); return v; }, function (e) { el.remove(); throw e; });
          }
          function s(v, n) { return typeof v === 'string' ? v.slice(0, n || 300) : ''; }
          // An action's typed words (JSON), and the privacy a playlist can have.
          function body(a) { try { return JSON.parse(a.body || '{}') || {}; } catch (e) { return {}; } }
          var PRIVACY = /^(PUBLIC|UNLISTED|PRIVATE)$/;
          function text(t) {
            if (!t) return '';
            if (typeof t === 'string') return t;
            if (t.simpleText) return t.simpleText;
            return (t.runs || []).map(function (r) { return r.text; }).join('');
          }
          // Google image URLs carry their size (=w226-h226…): ask for the size
          // the room draws, from the largest the page lists.
          function thumb(x, size) {
            var list = x && (x.musicThumbnailRenderer && x.musicThumbnailRenderer.thumbnail || x.croppedSquareThumbnailRenderer && x.croppedSquareThumbnailRenderer.thumbnail || x.thumbnail || x);
            list = list && list.thumbnails || list;
            if (!Array.isArray(list) || !list.length) return '';
            var url = s(list[list.length - 1].url, 600);
            size = size || 240;
            if (/=w\d+-h\d+/.test(url)) url = url.replace(/=w\d+-h\d+/, '=w' + size + '-h' + size);
            else if (/=s\d+/.test(url)) url = url.replace(/=s\d+/, '=s' + size);
            return url.indexOf('https://') === 0 ? url : url.indexOf('//') === 0 ? 'https:' + url : '';
          }
          var pages = { MUSIC_PAGE_TYPE_ALBUM: 'album', MUSIC_PAGE_TYPE_AUDIOBOOK: 'album', MUSIC_PAGE_TYPE_PLAYLIST: 'playlist',
            MUSIC_PAGE_TYPE_ARTIST: 'artist', MUSIC_PAGE_TYPE_USER_CHANNEL: 'artist', MUSIC_PAGE_TYPE_LIBRARY_ARTIST: 'artist',
            MUSIC_PAGE_TYPE_PODCAST_SHOW_DETAIL_PAGE: 'show', MUSIC_PAGE_TYPE_NON_MUSIC_AUDIO_TRACK_PAGE: 'episode' };
          function target(ep) {
            if (!ep) return null;
            if (ep.browseEndpoint) {
              var b = ep.browseEndpoint, id = s(b.browseId, 160), cfgs = b.browseEndpointContextSupportedConfigs && b.browseEndpointContextSupportedConfigs.browseEndpointContextMusicConfig;
              if (!id) return null;
              var k = pages[cfgs && cfgs.pageType] || (/^UC/.test(id) ? 'artist' : /^MPRE/.test(id) ? 'album' : /^VL/.test(id) ? 'playlist' : /^MPSP/.test(id) ? 'show' : /^MPED/.test(id) ? 'episode' : '');
              // An episode's page plays it; a page with params (a discography, an artist's
              // "More", a mood) and any other page Exo has no view of open as a browse page.
              if (k === 'episode') return { kind: 'track', uri: 'ytm:track:' + id.slice(4) };
              if (!k || b.params && k === 'artist') return { kind: 'browse', uri: 'ytm:browse:' + id + (b.params ? ':' + s(b.params, 400) : '') };
              return { kind: k, uri: 'ytm:' + k + ':' + id };
            }
            if (ep.watchEndpoint && ep.watchEndpoint.videoId) return { kind: 'track', uri: 'ytm:track:' + s(ep.watchEndpoint.videoId, 40), list: s(ep.watchEndpoint.playlistId, 120) };
            if (ep.watchPlaylistEndpoint) return { kind: 'list', uri: 'ytm:list:' + s(ep.watchPlaylistEndpoint.playlistId, 120) };
            return null;
          }
          function runsLinks(runs, kind) {
            var out = [];
            (runs || []).forEach(function (r) {
              var t = target(r.navigationEndpoint);
              if (t && t.kind === kind) out.push({ uri: t.uri, name: s(r.text, 120) });
            });
            return out;
          }
          // A two-row card (album, playlist, artist, a song tile).
          function card(r) {
            var t = target(r.navigationEndpoint) || target(r.title && r.title.runs && r.title.runs[0] && r.title.runs[0].navigationEndpoint);
            if (!t) return null;
            var kind = t.kind === 'list' ? 'playlist' : t.kind;
            return { uri: t.uri, kind: kind, title: s(text(r.title), 200), subtitle: s(text(r.subtitle), 160), image: thumb(r.thumbnailRenderer) };
          }
          // Explore's and Moods & genres' buttons: a mood or genre, a chart, new releases.
          function navButton(r) {
            var t = target(r.clickCommand);
            return t ? { uri: t.uri, kind: 'mood', title: s(text(r.buttonText), 120), subtitle: '', image: '' } : null;
          }
          // A podcast episode row (Explore's popular episodes, a podcast's page).
          function episode(r) {
            var t = target(r.onTap) || target(r.title && r.title.runs && r.title.runs[0] && r.title.runs[0].navigationEndpoint);
            if (!t || t.kind !== 'track') return null;
            var show = r.secondTitle && r.secondTitle.runs && r.secondTitle.runs[0];
            return { uri: t.uri, uid: '', title: s(text(r.title), 200), artists: show ? [{ uri: '', name: s(show.text, 120) }] : [], album: null,
              image: thumb(r.thumbnail), durationMs: 0, explicit: false, playable: true };
          }
          function duration(v) {
            var m = /^(?:(\d+):)?(\d+):(\d+)$/.exec(String(v || '').trim());
            return m ? ((+(m[1] || 0) * 60 + +m[2]) * 60 + +m[3]) * 1000 : 0;
          }
          // A list row: a song when it plays, else a card (search results).
          function row(r) {
            var cols = r.flexColumns || [], first = cols[0] && cols[0].musicResponsiveListItemFlexColumnRenderer && cols[0].musicResponsiveListItemFlexColumnRenderer.text || {};
            var second = cols[1] && cols[1].musicResponsiveListItemFlexColumnRenderer && cols[1].musicResponsiveListItemFlexColumnRenderer.text || {};
            var play = r.overlay && r.overlay.musicItemThumbnailOverlayRenderer && r.overlay.musicItemThumbnailOverlayRenderer.content &&
              r.overlay.musicItemThumbnailOverlayRenderer.content.musicPlayButtonRenderer && r.overlay.musicItemThumbnailOverlayRenderer.content.musicPlayButtonRenderer.playNavigationEndpoint;
            var t = target(first.runs && first.runs[0] && first.runs[0].navigationEndpoint) || target(play);
            var video = r.playlistItemData && r.playlistItemData.videoId;
            if (video || t && t.kind === 'track') {
              var id = s(video || t.uri.slice(10), 40);
              var fixed = r.fixedColumns && r.fixedColumns[0] && r.fixedColumns[0].musicResponsiveListItemFixedColumnRenderer;
              var runs = [];
              for (var c = 1; c < cols.length; c++) { var cc = cols[c].musicResponsiveListItemFlexColumnRenderer; if (cc && cc.text && cc.text.runs) runs = runs.concat(cc.text.runs); }
              var artists = runsLinks(runs, 'artist');
              // Search rows lead with the kind ("Song • Artist • Album • 3:40").
              if (!artists.length) {
                var plain = (second.runs || []).filter(function (x) { return !/^\s*[•·&,]?\s*$/.test(x.text) && !/^(Song|Video|Episode|Podcast|Single|EP)$/.test(x.text) && !/^\d+:\d\d/.test(x.text); });
                if (plain[0]) artists = [{ uri: '', name: s(plain[0].text, 120) }];
              }
              var album = runsLinks(runs, 'album')[0] || null;
              var len = fixed ? text(fixed.text) : '';
              if (!len) runs.forEach(function (x) { if (/^\d+:\d\d(:\d\d)?$/.test(x.text)) len = x.text; });
              // The row's own thumbs-up (its menu's like button) says whether the song is liked.
              var top = r.menu && r.menu.menuRenderer && r.menu.menuRenderer.topLevelButtons || [], status = '';
              top.forEach(function (b) { if (b.likeButtonRenderer && b.likeButtonRenderer.likeStatus) status = b.likeButtonRenderer.likeStatus; });
              return { track: { uri: 'ytm:track:' + id, uid: s(r.playlistItemData && r.playlistItemData.playlistSetVideoId, 80), title: s(text(first), 200),
                liked: liked[id] !== undefined ? liked[id] : status ? status === 'LIKE' : undefined,
                artists: artists, album: album, image: thumb(r.thumbnail), durationMs: duration(len),
                explicit: !!(r.badges || []).some(function (b) { return b.musicInlineBadgeRenderer && /EXPLICIT/.test(b.musicInlineBadgeRenderer.icon && b.musicInlineBadgeRenderer.icon.iconType); }),
                playable: r.musicItemRendererDisplayPolicy !== 'MUSIC_ITEM_RENDERER_DISPLAY_POLICY_GREY_OUT' },
                list: t && t.list || '' };
            }
            var link = target(r.navigationEndpoint);
            if (!link) return null;
            return { card: { uri: link.uri, kind: link.kind, title: s(text(first), 200), subtitle: s(text(second), 160), image: thumb(r.thumbnail) } };
          }
          // Where a shelf's More or title goes: the whole list (Top songs, a discography, New releases).
          function moreOf(v, h) {
            var ep = v.bottomEndpoint || h && h.moreContentButton && h.moreContentButton.buttonRenderer && h.moreContentButton.buttonRenderer.navigationEndpoint ||
              h && h.title && h.title.runs && h.title.runs[0] && h.title.runs[0].navigationEndpoint;
            var t = target(ep);
            return t && t.kind !== 'track' && t.kind !== 'list' ? { uri: t.uri, kind: t.kind } : null;
          }
          // Walks any InnerTube answer: shelves of songs, carousels and grids
          // of cards, the page header, and the next continuation.
          function walk(data) {
            var out = { shelves: [], header: null, cont: '', lists: {}, top: [], chips: [] };
            function shelf(title, items, more) {
              var songs = [], cards = [];
              (items || []).forEach(function (it) {
                if (it.musicResponsiveListItemRenderer) { var r = row(it.musicResponsiveListItemRenderer); if (r && r.track) { songs.push(r.track); if (r.list) out.lists[r.track.uri] = r.list; } else if (r && r.card && r.card.title) cards.push(r.card); }
                else if (it.musicTwoRowItemRenderer) { var c = card(it.musicTwoRowItemRenderer); if (c && c.title) cards.push(c); }
                else if (it.musicNavigationButtonRenderer) { var m = navButton(it.musicNavigationButtonRenderer); if (m && m.title) cards.push(m); }
                else if (it.musicMultiRowListItemRenderer) { var e = episode(it.musicMultiRowListItemRenderer); if (e) songs.push(e); }
                else if (it.continuationItemRenderer) { var ce = it.continuationItemRenderer.continuationEndpoint; out.cont = ce && ce.continuationCommand && ce.continuationCommand.token || out.cont; }
              });
              if (songs.length || cards.length) out.shelves.push({ title: s(title, 120), more: more || null, tracks: songs, cards: cards });
            }
            (function visit(o, depth) {
              if (!o || typeof o !== 'object' || depth > 30) return;
              if (Array.isArray(o)) { for (var i = 0; i < o.length; i++) visit(o[i], depth + 1); return; }
              for (var k in o) {
                var v = o[k];
                if (!v || typeof v !== 'object') continue;
                if (k === 'musicShelfRenderer' || k === 'musicPlaylistShelfRenderer' || k === 'musicCarouselShelfRenderer' || k === 'gridRenderer') {
                  var h = v.header && (v.header.musicCarouselShelfBasicHeaderRenderer || v.header.gridHeaderRenderer || v.header.musicSideAlignedItemRenderer) || null;
                  shelf(text(v.title) || text(h && h.title), v.contents || v.items, moreOf(v, h));
                  var nc = v.continuations && v.continuations[0] && v.continuations[0].nextContinuationData;
                  if (nc) out.cont = nc.continuation;
                } else if (k === 'musicPlaylistShelfContinuation' || k === 'musicShelfContinuation' || k === 'gridContinuation') {
                  shelf('', v.contents || v.items);
                  var nc2 = v.continuations && v.continuations[0] && v.continuations[0].nextContinuationData;
                  if (nc2) out.cont = nc2.continuation;
                } else if (k === 'appendContinuationItemsAction') {
                  shelf('', v.continuationItems);
                } else if (k === 'chipCloudChipRenderer') {
                  var se = v.navigationEndpoint && v.navigationEndpoint.searchEndpoint;
                  if (se && se.params) out.chips.push({ name: s(text(v.text), 60), params: s(se.params, 200), selected: !!v.isSelected });
                } else if (k === 'itemSectionRenderer' || k === 'musicCardShelfRenderer') {
                  // Search answers: the top result card, then sections of rows.
                  if (k === 'musicCardShelfRenderer') {
                    var top = target(v.title && v.title.runs && v.title.runs[0] && v.title.runs[0].navigationEndpoint);
                    if (top && top.kind !== 'track' && top.kind !== 'list') out.top.push({ uri: top.uri, kind: top.kind, title: s(text(v.title), 200), subtitle: s(text(v.subtitle), 160), image: thumb(v.thumbnail) });
                  }
                  shelf(text(v.header && v.header.musicCardShelfHeaderBasicRenderer && v.header.musicCardShelfHeaderBasicRenderer.title), v.contents);
                  visit(v.contents, depth + 1);
                } else if (!out.header && /^music(Responsive|Immersive|Visual|Detail|EditablePlaylistDetail)HeaderRenderer$/.test(k)) {
                  out.header = v.header && v.header.musicResponsiveHeaderRenderer || v.header && v.header.musicDetailHeaderRenderer || v;
                  out.wide = /Immersive|Visual/.test(k);
                  // Your own playlist: its header is the editable one, with its privacy.
                  if (k === 'musicEditablePlaylistDetailHeaderRenderer') {
                    var eh = v.editHeader && v.editHeader.musicPlaylistEditHeaderRenderer;
                    out.editable = { privacy: s(eh && eh.privacy, 20) };
                  }
                } else visit(v, depth + 1);
              }
            })(data, 0);
            return out;
          }
          // The shelves of a page as the room's sections; a shelf of songs becomes song tiles.
          function sectionsOf(p, untitled) {
            return p.shelves.filter(function (sh) { return sh.title || untitled; }).map(function (sh) {
              var items = sh.cards.length ? sh.cards : sh.tracks.map(function (t) {
                return { uri: t.uri, kind: 'track', title: t.title, subtitle: t.artists.map(function (x) { return x.name; }).join(', '), image: t.image };
              });
              return { title: sh.title, more: sh.more || undefined, items: items.slice(0, sh.cards.length && sh.cards[0].kind === 'mood' ? 60 : 20) };
            }).filter(function (sh) { return sh.items.length; });
          }
          // What a page's Save or Follow acts on, by its uri: an album's playlist (the id its own
          // Save to library button likes) or an artist's channel (what its Subscribe button sends);
          // an artist's own Mix and Shuffle buttons.
          var saveIds = {}, headerPlays = {};
          function header(h, uri, wide, editable) {
            if (!h) return null;
            var sub = text(h.straplineTextOne) || text(h.subtitle) || text(h.secondSubtitle);
            var desc = h.description && (h.description.musicDescriptionShelfRenderer ? text(h.description.musicDescriptionShelfRenderer.description) : text(h.description));
            var out = { uri: uri, title: s(text(h.title), 200), subtitle: s(sub, 160), description: s(desc, 400),
              image: wide ? thumb(h.foregroundThumbnail, 544) : thumb(h.thumbnail, 544), banner: wide ? thumb(h.thumbnail, 1200) : '' };
            if (editable) { out.editable = true; out.privacy = editable.privacy || 'PRIVATE'; }
            var follow = h.subscriptionButton && h.subscriptionButton.subscribeButtonRenderer;
            if (follow && follow.channelId) { saveIds[uri] = s(follow.channelId, 80); out.saved = !!follow.subscribed; }
            (h.buttons || []).forEach(function (b) {
              var t = b.toggleButtonRenderer, like = t && t.defaultServiceEndpoint && t.defaultServiceEndpoint.likeEndpoint;
              if (!like || !like.target || !like.target.playlistId) return;
              saveIds[uri] = s(like.target.playlistId, 120);
              // The button's default sends what a press would do: LIKE when not saved yet.
              out.saved = typeof t.isToggled === 'boolean' ? t.isToggled : like.status !== 'LIKE';
            });
            var radio = h.startRadioButton && h.startRadioButton.buttonRenderer && h.startRadioButton.buttonRenderer.navigationEndpoint;
            var shuffle = h.playButton && h.playButton.buttonRenderer && h.playButton.buttonRenderer.navigationEndpoint;
            if (radio || shuffle) headerPlays[uri] = { radio: radio, shuffle: shuffle };
            return out;
          }
          function count(h) {
            var m = /([\d,.]+)\s+(?:songs|tracks|episodes)/i.exec(text(h && (h.secondSubtitle || h.subtitle)));
            return m ? parseInt(m[1].replace(/[,.]/g, ''), 10) : 0;
          }
          function browseId(uri) { return String(uri || '').split(':').slice(2).join(':'); }
          function browseBody(uri) {
            var parts = String(uri || '').split(':');
            return parts[3] ? { browseId: parts[2], params: parts.slice(3).join(':') } : { browseId: parts[2] };
          }
          function page(a, kind) {
            var id = browseId(a.uri), offset = Math.max(0, a.number | 0);
            var req = offset && conts[a.uri] ? fetchJson('/browse', { continuation: conts[a.uri] }) : fetchJson('/browse', browseBody(a.uri));
            return req.then(function (d) {
              var p = walk(d);
              conts[a.uri] = p.cont;
              var songs = [];
              p.shelves.forEach(function (sh) { songs = songs.concat(sh.tracks); });
              var tracks = kind === 'artist' ? (p.shelves.filter(function (sh) { return sh.tracks.length; })[0] || { tracks: [] }).tracks.slice(0, 10) : songs;
              var context = '';
              for (var u in p.lists) { context = p.lists[u]; break; }
              if (!context && kind === 'playlist' && /^VL/.test(id)) context = id.slice(2);
              if (!context && kind === 'show' && /^MPSP/.test(id)) context = id.slice(4);
              var h = offset ? null : header(p.header, a.uri, p.wide, p.editable);
              // An album's rows carry no cover or album link of their own.
              if (kind === 'album' && h) tracks.forEach(function (t) {
                t.album = t.album || { uri: a.uri, name: h.title };
                t.image = t.image || h.image.replace(/=w\d+-h\d+/, '=w240-h240');
              });
              if (h) h.radio = !!headerPlays[a.uri] || kind === 'playlist' || kind === 'album';
              var out = { header: h, contextUri: context ? 'ytm:list:' + context : '', tracks: tracks, offset: offset,
                total: offset ? offset + tracks.length + (p.cont ? 100 : 0) : Math.max(count(p.header), tracks.length + (p.cont ? 100 : 0)) };
              // An artist: every shelf of its page (albums, singles, videos, featured on, playlists, fans also like),
              // each with its own More. An album: other versions and more from the artist.
              if (kind === 'artist' || kind === 'album') out.sections = sectionsOf({ shelves: p.shelves.filter(function (sh) { return sh.cards.length; }) });
              if (kind === 'artist') {
                var top = p.shelves.filter(function (sh) { return sh.tracks.length; })[0];
                if (top && top.more) out.more = top.more;
              }
              return out;
            });
          }
          function rawItem(it) {
            return it && (it.playlistPanelVideoRenderer || it.playlistPanelVideoWrapperRenderer && it.playlistPanelVideoWrapperRenderer.primaryRenderer && it.playlistPanelVideoWrapperRenderer.primaryRenderer.playlistPanelVideoRenderer);
          }
          function current() {
            var st = store(), q = st && st.getState().queue || {}, items = q.items || [];
            var i = q.selectedItemIndex | 0;
            return { st: st, q: q, items: items, auto: q.automixItems || [], index: i };
          }
          // A queue item's id is its index in the page's queue (what Remove from queue names).
          function itemId(it) {
            var r = rawItem(it), we = r && r.navigationEndpoint && r.navigationEndpoint.watchEndpoint;
            return we && typeof we.index === 'number' ? String(we.index) : '';
          }
          function panelItem(it) {
            var r = rawItem(it);
            if (!r || !r.videoId) return null;
            var by = (r.longBylineText || r.shortBylineText || {}).runs || [];
            var artists = runsLinks(by, 'artist');
            if (!artists.length && by[0]) artists = [{ uri: '', name: s(by[0].text, 120) }];
            return { uri: 'ytm:track:' + s(r.videoId, 40), uid: itemId(it), title: s(text(r.title), 200), artists: artists,
              album: runsLinks(by, 'album')[0] || null, image: thumb(r.thumbnail), durationMs: duration(text(r.lengthText)), explicit: false, playable: true };
          }
          function navigate(endpoint) {
            app().dispatchEvent(new CustomEvent('yt-navigate', { bubbles: true, composed: true, detail: { endpoint: endpoint } }));
            return true;
          }
          // Likes and dislikes this page made; the page's own store learns of them only
          // through its own buttons.
          var liked = {}, disliked = {};
          function videoId(uri) { return /^ytm:track:/.test(uri || '') ? uri.slice(10) : ''; }
          // The playlist a page plays as (an album's OLAK list, a playlist without VL).
          function listOf(uri) {
            var id = browseId(uri);
            if (/^ytm:list:/.test(uri || '')) return Promise.resolve(uri.slice(9));
            if (/^ytm:playlist:/.test(uri || '')) return Promise.resolve(id.replace(/^VL/, ''));
            if (/^ytm:show:/.test(uri || '')) return Promise.resolve(id.replace(/^MPSP/, ''));
            if (/^ytm:album:/.test(uri || '')) return saveIds[uri] ? Promise.resolve(saveIds[uri])
              : page({ uri: uri, number: 0 }, 'album').then(function (p) { return saveIds[uri] || (p.contextUri || '').slice(9); });
            return Promise.resolve('');
          }
          // What a queue add sends: one song, or a whole album or playlist, as its menus do.
          function queueTarget(uri) {
            var video = videoId(uri);
            if (video) return Promise.resolve({ videoId: video, onEmptyQueue: { watchEndpoint: { videoId: video } } });
            return listOf(uri).then(function (list) { return list ? { playlistId: list, onEmptyQueue: { watchEndpoint: { playlistId: list } } } : null; });
          }
          function editPlaylist(uri, actions) {
            var id = browseId(uri).replace(/^VL/, '');
            if (!/^ytm:playlist:/.test(uri || '') || !id) return false;
            return fetchJson('/browse/edit_playlist', { playlistId: id, actions: actions }).then(function () { return true; });
          }
          // The Songs page: the library's Songs (FEmusic_liked_videos, the
          // page's own Library > Songs), read on as far as the page needs in
          // the library's own order (newest first) and whole for any other
          // order or a filter; kept five minutes.
          var libSongs = null, songOrder = null;
          function readSongs(n) {
            if (!libSongs || Date.now() - libSongs.at > 300000) libSongs = { at: Date.now(), list: [], lists: {}, cont: '', started: false, done: false, chain: Promise.resolve() };
            var L = libSongs;
            function more() {
              if (L.done || L.list.length >= n) return L;
              var req = L.started ? fetchJson('/browse', { continuation: L.cont }) : fetchJson('/browse', { browseId: 'FEmusic_liked_videos' });
              return req.then(function (d) {
                var p = walk(d), added = 0;
                L.started = true;
                p.shelves.forEach(function (sh) { sh.tracks.forEach(function (t) { L.list.push(t); added++; }); });
                for (var u in p.lists) L.lists[u] = p.lists[u];
                L.cont = p.cont;
                if (!p.cont || !added || L.list.length >= 20000) L.done = true;
                return more();
              });
            }
            L.chain = L.chain.catch(function () { }).then(more);
            return L.chain;
          }
          function songsQuery(t) {
            t = s(t, 200);
            var bar = t.indexOf('|'), sort = bar < 0 ? '' : t.slice(0, bar);
            return { sort: /^(added|title|artist|album)$/.test(sort) ? sort : 'added', filter: bar < 0 ? '' : t.slice(bar + 1).trim().toLowerCase() };
          }
          function orderedSongs(text, n) {
            var q = songsQuery(text), whole = q.sort !== 'added' || !!q.filter;
            return readSongs(whole ? Infinity : n).then(function (L) {
              if (!whole) return { list: L.list, done: L.done, lists: L.lists };
              var key = q.sort + '|' + q.filter;
              if (songOrder && songOrder.from === L && songOrder.key === key) return songOrder;
              function artist(t) { return t.artists[0] && t.artists[0].name || ''; }
              function album(t) { return t.album && t.album.name || ''; }
              var list = q.filter ? L.list.filter(function (t) {
                return t.title.toLowerCase().indexOf(q.filter) >= 0 || artist(t).toLowerCase().indexOf(q.filter) >= 0 || album(t).toLowerCase().indexOf(q.filter) >= 0;
              }) : L.list.slice();
              var cmp = function (x, y) { return x.localeCompare(y, undefined, { sensitivity: 'base', numeric: true }); };
              if (q.sort !== 'added') list.sort(q.sort === 'artist' ? function (x, y) { return cmp(artist(x), artist(y)) || cmp(album(x), album(y)) || cmp(x.title, y.title); }
                : q.sort === 'album' ? function (x, y) { return cmp(album(x), album(y)) || cmp(x.title, y.title); }
                : function (x, y) { return cmp(x.title, y.title); });
              songOrder = { from: L, key: key, list: list, done: true, lists: L.lists };
              return songOrder;
            });
          }
          var SONG_SORT_NAMES = [{ id: 'added', name: 'Date added' }, { id: 'title', name: 'Title' }, { id: 'artist', name: 'Artist' }, { id: 'album', name: 'Album' }];
          // Search's own filter chips (Songs, Videos, Albums, Artists, Featured and Community
          // playlists, Episodes, Podcasts, Profiles): their params, by a name the room keeps.
          var chipParams = {}, chipList = [];
          function chipId(name) { return name.toLowerCase().replace(/[^a-z]+/g, '_').replace(/^_|_$/g, '').slice(0, 30); }
          function readChips(p) {
            p.chips.forEach(function (c) {
              var id = chipId(c.name);
              if (!id || c.selected) return;
              chipParams[id] = c.params;
              if (!chipList.some(function (x) { return x.id === 'ytm:filter:' + id; })) chipList.push({ id: 'ytm:filter:' + id, name: c.name });
            });
          }
          // Library > Settings > Playback's Audio quality, as the page's settings list it.
          function audioSetting() {
            return fetchJson('/browse', { browseId: 'SPaccount_playback' }).then(function (d) {
              var found = null;
              (function visit(o, depth) {
                if (found || !o || typeof o !== 'object' || depth > 30) return;
                var m = o.settingSingleOptionMenuRenderer;
                if (m && m.items && (/audio quality/i.test(text(m.title)) || m.items.some(function (x) { return /^AUDIO_QUALITY/.test(x.settingMenuItemRenderer && x.settingMenuItemRenderer.value || ''); }))) { found = m; return; }
                for (var k in o) visit(o[k], depth + 1);
              })(d, 0);
              if (!found) throw new Error('no audio quality setting');
              return found.items.map(function (x) { return x.settingMenuItemRenderer; }).filter(Boolean);
            });
          }
          function setAudio(item) {
            return service(item.updateServiceEndpoint).then(function () {
              if (/^AUDIO_QUALITY_/.test(item.value || '') && w.ytcfg && w.ytcfg.set) w.ytcfg.set('AUDIO_QUALITY', item.value);
              return true;
            });
          }
          // Premium plays at the best quality it offers: once per profile, the setting is
          // moved to its highest choice (the last the page lists); a choice made later stays. A read
          // that fails is tried again when the page next loads.
          var qualityChecked = false;
          function bestQuality() {
            if (qualityChecked) return;
            qualityChecked = true;
            try { if (w.localStorage.getItem('exo.quality.v1')) return; } catch (e) { return; }
            audioSetting().then(function (items) {
              var best = items[items.length - 1];
              return best && !best.selected ? setAudio(best) : true;
            }).then(function () { try { w.localStorage.setItem('exo.quality.v1', '1'); } catch (e) { } }, function () { });
          }
          var LIBRARY = { playlists: 'FEmusic_liked_playlists', albums: 'FEmusic_liked_albums', artists: 'FEmusic_library_corpus_track_artists',
            subscriptions: 'FEmusic_library_corpus_artists', podcasts: 'FEmusic_library_non_music_audio_list', uploads: 'FEmusic_library_privately_owned_releases', history: 'FEmusic_history' };
          // The library's sort orders (its own sort menu's params).
          var LIBRARY_SORTS = { recent: '', added: 'ggMGKgQIABAB', az: 'ggMGKgQIARAA', za: 'ggMGKgQIARAB' };
          var views = {
            // Your library's songs, 200 at a time, in the order asked for.
            songs: function (a) {
              var offset = Math.max(0, a.number | 0);
              return orderedSongs(a.text, offset + 200).then(function (o) {
                return { contextUri: 'ytm:songs:library', tracks: o.list.slice(offset, offset + 200), offset: offset,
                  total: o.list.length + (o.done ? 0 : 200), sorts: SONG_SORT_NAMES, sort: songsQuery(a.text).sort };
              });
            },
            // Signed in with Premium is what the room plays for; Premium also gets the best audio quality.
            status: function () {
              var signedIn = !!cfg('LOGGED_IN'), premium = signedIn && !!cfg('IS_SUBSCRIBER');
              var ready = !!(document.querySelector('ytmusic-app') && app().networkManager);
              if (ready && premium) bestQuality();
              return { signedIn: signedIn, subscribed: premium, ready: ready };
            },
            // Home's shelves (the first answer and its next page).
            home: function () {
              return fetchJson('/browse', { browseId: 'FEmusic_home' }).then(function (d) {
                var p = walk(d);
                var more = p.cont ? fetchJson('/browse', { continuation: p.cont }).then(walk, function () { return { shelves: [] }; }) : Promise.resolve({ shelves: [] });
                return more.then(function (q) {
                  return { greeting: '', sections: sectionsOf({ shelves: p.shelves.concat(q.shelves) }).map(function (sh) { sh.items = sh.items.slice(0, 12); return sh; }).slice(0, 16) };
                });
              });
            },
            // Explore: New releases, Charts and Moods & genres, then its shelves.
            explore: function () {
              // A shelf of moods shows its first dozen here; its title opens them all.
              return fetchJson('/browse', { browseId: 'FEmusic_explore' }).then(function (d) {
                return { sections: sectionsOf(walk(d), true).map(function (sh) { if (sh.more && sh.items[0].kind === 'mood') sh.items = sh.items.slice(0, 12); return sh; }) };
              });
            },
            // Any other page (charts, new releases, a mood or genre, a discography): its header, songs and shelves.
            browse: function (a) {
              return fetchJson('/browse', browseBody(a.uri)).then(function (d) {
                var p = walk(d), tracks = [];
                p.shelves.forEach(function (sh) { if (!sh.cards.length) tracks = tracks.concat(sh.tracks); });
                return { header: header(p.header, a.uri, p.wide), tracks: tracks.slice(0, 200), sections: sectionsOf({ shelves: p.shelves.filter(function (sh) { return sh.cards.length; }) }, true) };
              });
            },
            // A search, or one of its filters (uri ytm:filter:<id>): the page's own chips and their params.
            search: function (a) {
              var q = s(a.text, 200).trim(), f = /^ytm:filter:/.test(a.uri || '') ? a.uri.slice(11) : '';
              if (!q) return { tracks: [], artists: [], albums: [], playlists: [] };
              function run(params) { return fetchJson('/search', params ? { query: q, params: params } : { query: q }); }
              var req = f && chipParams[f] ? run(chipParams[f]) : run('').then(function (d) { readChips(walk(d)); return f && chipParams[f] ? run(chipParams[f]) : d; });
              return req.then(function (d) {
                var p = walk(d), cap = f ? 100 : 10, out = { top: f ? [] : p.top.slice(0, 1), tracks: [], artists: [], albums: [], playlists: [], shows: [] };
                readChips(p);
                p.shelves.forEach(function (sh) {
                  out.tracks = out.tracks.concat(sh.tracks);
                  sh.cards.forEach(function (c) {
                    var key = c.kind === 'artist' ? 'artists' : c.kind === 'album' ? 'albums' : c.kind === 'playlist' ? 'playlists' : c.kind === 'show' ? 'shows' : '';
                    if (key && out[key].length < cap && !out[key].some(function (x) { return x.uri === c.uri; })) out[key].push(c);
                  });
                });
                out.tracks = out.tracks.slice(0, f ? 100 : 10);
                // The top result's own songs name no artist: they are its.
                var lead = out.top[0];
                if (lead && lead.kind === 'artist') out.tracks.forEach(function (t) { if (!t.artists.length) t.artists = [{ uri: lead.uri, name: lead.title }]; });
                out.filters = chipList.slice(0, 12);
                out.filter = f ? 'ytm:filter:' + f : '';
                return out;
              });
            },
            // Your library, one shelf at a time, in the order asked for ("filter|sort").
            library: function (a) {
              var filters = [{ id: 'playlists', name: 'Playlists' }, { id: 'albums', name: 'Albums' }, { id: 'artists', name: 'Artists' },
                { id: 'subscriptions', name: 'Subscriptions' }, { id: 'podcasts', name: 'Podcasts' }, { id: 'uploads', name: 'Uploads' }, { id: 'history', name: 'History' }];
              var parts = s(a.text, 40).split('|'), filter = LIBRARY[parts[0]] ? parts[0] : 'playlists';
              var sort = LIBRARY_SORTS[parts[1]] !== undefined ? parts[1] : 'recent', sortable = !/podcasts|uploads|history/.test(filter);
              var body = { browseId: LIBRARY[filter] };
              if (sortable && LIBRARY_SORTS[sort]) body.params = LIBRARY_SORTS[sort];
              return fetchJson('/browse', body).then(function (d) {
                var p = walk(d), items = [], seen = {};
                // History is songs, newest first: each once, as a song you play from the list.
                p.shelves.forEach(function (sh) {
                  items = items.concat(sh.cards, filter !== 'history' ? [] : sh.tracks.filter(function (t) { return !seen[t.uri] && (seen[t.uri] = 1); }).map(function (t) {
                    return { uri: t.uri, kind: 'track', title: t.title, subtitle: t.artists.map(function (x) { return x.name; }).join(', '), image: t.image };
                  }));
                });
                items = items.filter(function (c) { return /^(playlist|album|artist|show|browse|track)$/.test(c.kind); });
                return { items: items.slice(0, 500), filters: filters, filter: filter === 'playlists' ? '' : filter,
                  sorts: sortable ? [{ id: 'recent', name: 'Recent activity' }, { id: 'added', name: 'Recently added' }, { id: 'az', name: 'A to Z' }, { id: 'za', name: 'Z to A' }] : [],
                  sort: sortable ? sort : '', folder: null };
              });
            },
            playlist: function (a) { return page(a, 'playlist'); },
            album: function (a) { return page(a, 'album'); },
            artist: function (a) { return page(a, 'artist'); },
            show: function (a) { return page(a, 'show'); },
            // Up next as the page's queue has it: the list after the song, then autoplay's songs.
            queue: function () {
              var c = current(), list = c.items.map(panelItem);
              return { current: list[c.index] || null, queued: list.slice(c.index + 1).filter(Boolean),
                next: c.auto.map(panelItem).filter(Boolean).slice(0, 50).map(function (t) { t.uid = ''; return t; }), autoplay: c.q.autoplay !== false };
            },
            player: function () {
              var c = current(), state = c.st ? c.st.getState() : {}, it = c.items[c.index], now = panelItem(it);
              var id = now ? videoId(now.uri) : '';
              var like = state.likeStatus && state.likeStatus.videos && state.likeStatus.videos[id];
              var repeat = { NONE: 0, ALL: 1, ONE: 2 }[c.q.repeatMode] || 0;
              return { trackUri: now ? now.uri : '', contextUri: '', shuffle: !!c.q.shuffleEnabled, repeat: repeat,
                liked: id in liked ? liked[id] : like === 'LIKE', disliked: id in disliked ? disliked[id] : like === 'DISLIKE',
                canShuffle: !!now, canRepeat: !!now, autoplay: c.q.autoplay !== false,
                // A song with both a song and a music video: the page's own Song / Video switch.
                canVideo: !!(it && it.playlistPanelVideoWrapperRenderer), video: c.q.playbackContentMode === 'OMV_PREFERRED',
                image: now ? now.image.replace(/=w\d+-h\d+/, '=w544-h544') : '', title: now ? now.title : '', artists: now ? now.artists : [] };
            },
            // Lyrics as the page's own Lyrics tab has them (YouTube Music's web player has them unsynced).
            lyrics: function (a) {
              var id = videoId(a.uri);
              if (!id) return { lines: [], synced: false };
              return nextTab(id, /^MPLY/).then(function (found) {
                if (!found) return { lines: [], synced: false };
                return fetchJson('/browse', { browseId: found }).then(function (b) {
                  var body = '';
                  (function visit(o, depth) {
                    if (body || !o || typeof o !== 'object' || depth > 25) return;
                    if (o.musicDescriptionShelfRenderer) { body = text(o.musicDescriptionShelfRenderer.description); return; }
                    for (var k in o) visit(o[k], depth + 1);
                  })(b, 0);
                  return { synced: false, lines: body.split(/\r?\n/).slice(0, 400).map(function (l) { return { start: 0, text: s(l, 300) }; }) };
                });
              });
            },
            // The page's own Related tab for a song: you might also like, recommended playlists, similar artists.
            related: function (a) {
              var id = videoId(a.uri);
              if (!id) return { sections: [] };
              return nextTab(id, /^MPTR/).then(function (found) {
                return found ? fetchJson('/browse', { browseId: found }).then(function (b) { return { sections: sectionsOf(walk(b)) }; }) : { sections: [] };
              });
            },
            // The playlists a song can be saved to, as the page's own Save to playlist lists them.
            playlists: function () {
              return fetchJson('/playlist/get_add_to_playlist', { playlistId: 'LM', excludeWatchLater: true }).then(function (d) {
                var items = [];
                (function visit(o, depth) {
                  if (!o || typeof o !== 'object' || depth > 25) return;
                  var r = o.playlistAddToOptionRenderer;
                  if (r && r.playlistId && r.playlistId !== 'LM') { items.push({ uri: 'ytm:playlist:VL' + s(r.playlistId, 120), kind: 'playlist', title: s(text(r.title), 200), subtitle: '', image: '' }); return; }
                  for (var k in o) visit(o[k], depth + 1);
                })(d, 0);
                return { items: items.slice(0, 500) };
              });
            },
            // Audio quality, as Settings > Playback offers it on this account.
            audio: function () {
              return audioSetting().then(function (items) {
                var at = items.findIndex(function (x) { return x.selected; });
                if (at < 0) at = items.findIndex(function (x) { return x.value === cfg('AUDIO_QUALITY'); });
                return { qualities: items.map(function (x, i) { return { id: String(i), name: s(x.name, 60) }; }), quality: at < 0 ? '' : String(at) };
              });
            }
          };
          // The id of one of a song's /next tabs (Lyrics MPLY…, Related MPTR…).
          function nextTab(id, re) {
            return fetchJson('/next', { videoId: id }).then(function (d) {
              var found = '';
              (function visit(o, depth) {
                if (found || !o || typeof o !== 'object' || depth > 25) return;
                if (o.browseEndpoint && re.test(o.browseEndpoint.browseId || '')) { found = o.browseEndpoint.browseId; return; }
                for (var k in o) visit(o[k], depth + 1);
              })(d, 0);
              return found;
            });
          }
          function findItem(c, uid) {
            for (var i = 0; i < c.items.length; i++) if (itemId(c.items[i]) === uid) return i;
            return -1;
          }
          // The queue's own drop handler (a drag in its list) moves the song from one place to another.
          function dropAt(from, to) {
            var q = document.querySelector('ytmusic-player-queue');
            if (!q || typeof q.onDrop !== 'function' || !Array.isArray(q.data) || !q.data[from] || from === to) return false;
            var it = q.data[from], drag = { data: it.playlistPanelVideoWrapperRenderer || it.playlistPanelVideoRenderer }, kids = [];
            kids[to] = drag;
            drag.parentElement = { children: kids };
            q.onDrop({ detail: { dragEl: drag } });
            return true;
          }
          function removeQueued(c, uid) {
            var at = findItem(c, uid), r = at < 0 ? null : rawItem(c.items[at]);
            if (!r || at === c.index) return Promise.resolve(false);
            return service({ removeFromQueueEndpoint: { videoId: r.videoId, itemId: uid } }).then(function () { return true; });
          }
          var acts = {
            // A song in its list (album, playlist, mix), a whole list, or a
            // page (album, playlist, artist, podcast) from its first song.
            play: function (a) {
              // The Songs page: the song, in the library list its own row plays in.
              if (a.text === 'ytm:songs:library') {
                var song = videoId(a.uri), from = libSongs && libSongs.lists[a.uri];
                if (!song) return false;
                return navigate({ watchEndpoint: from ? { videoId: song, playlistId: from } : { videoId: song } });
              }
              var video = videoId(a.uri), list = /^ytm:list:/.test(a.text || '') ? a.text.slice(9) : '';
              if (video) return navigate({ watchEndpoint: list ? { videoId: video, playlistId: list, index: Math.max(0, a.number | 0) } : { videoId: video } });
              if (list) return navigate({ watchPlaylistEndpoint: { playlistId: list } });
              var pageUri = a.text || a.uri;
              if (!/^ytm:(playlist|album|artist|show):/.test(pageUri || '')) return false;
              return page({ uri: pageUri, number: 0 }, pageUri.split(':')[1]).then(function (p) {
                var first = p.tracks[0];
                if (p.contextUri) return navigate({ watchPlaylistEndpoint: { playlistId: p.contextUri.slice(9) } });
                return first ? navigate({ watchEndpoint: { videoId: videoId(first.uri) } }) : false;
              });
            },
            skipTo: function (a) {
              var c = current(), video = videoId(a.uri), all = c.items.concat(c.auto);
              for (var i = 0; i < all.length; i++) {
                var it = panelItem(all[i]), raw = rawItem(all[i]);
                if (it && it.uri === a.uri && (!a.uid || it.uid === a.uid) && raw && raw.navigationEndpoint) return navigate(raw.navigationEndpoint);
              }
              return video ? navigate({ watchEndpoint: { videoId: video } }) : false;
            },
            // The page's own like call (what its thumbs-up button sends); a
            // playlist, album or podcast is saved to the library the same way (an
            // album through its own playlist), and an artist followed with the
            // subscribe call its Subscribe button sends.
            like: function (a) {
              var video = videoId(a.uri), id = browseId(a.uri);
              if (/^ytm:artist:/.test(a.uri || '')) {
                var channel = saveIds[a.uri] || id.replace(/^MPLA/, '');
                if (!/^UC[\w-]+$/.test(channel)) return false;
                return fetchJson(a.on ? '/subscription/subscribe' : '/subscription/unsubscribe', { channelIds: [channel] }).then(function () { return true; });
              }
              return (video ? Promise.resolve('') : saveIds[a.uri] ? Promise.resolve(saveIds[a.uri]) : listOf(a.uri)).then(function (playlist) {
                var targetId = video ? { videoId: video } : playlist ? { playlistId: playlist } : null;
                if (!targetId) return false;
                return fetchJson(a.on ? '/like/like' : '/like/removelike', { target: targetId }).then(function () {
                  if (video) { liked[video] = !!a.on; if (a.on) disliked[video] = false; }
                  return true;
                });
              });
            },
            // Thumbs down (what its thumbs-down button sends); the page skips a disliked song itself.
            dislike: function (a) {
              var video = videoId(a.uri);
              if (!video) return false;
              return fetchJson(a.on ? '/like/dislike' : '/like/removelike', { target: { videoId: video } }).then(function () {
                disliked[video] = !!a.on;
                if (a.on) liked[video] = false;
                return true;
              });
            },
            shuffle: function (a) {
              var c = current();
              if (!!c.q.shuffleEnabled === !!a.on) return true;
              var b = document.querySelector('ytmusic-player-bar .shuffle, ytmusic-player-bar [aria-label*="huffle"]');
              if (!b) return false;
              b.click();
              return true;
            },
            repeat: function (a) {
              var want = Math.max(0, Math.min(2, a.number | 0)), b = document.querySelector('ytmusic-player-bar .repeat, ytmusic-player-bar [aria-label*="epeat"]');
              if (!b) return false;
              for (var i = 0; i < 3 && ({ NONE: 0, ALL: 1, ONE: 2 }[current().q.repeatMode] || 0) !== want; i++) b.click();
              return true;
            },
            // Add to queue and Play next: the queueAddEndpoint the page's own song, album and playlist menus carry.
            enqueue: function (a) {
              return queueTarget(a.uri).then(function (t) { return t ? service({ queueAddEndpoint: { queueTarget: t, queueInsertPosition: 'INSERT_AT_END' } }).then(function () { return true; }) : false; });
            },
            playNext: function (a) {
              return queueTarget(a.uri).then(function (t) { return t ? service({ queueAddEndpoint: { queueTarget: t, queueInsertPosition: 'INSERT_AFTER_CURRENT_VIDEO' } }).then(function () { return true; }) : false; });
            },
            // Remove from queue, as a queue item's own menu does it.
            dequeue: function (a) { return a.uid ? removeQueued(current(), a.uid) : false; },
            // Everything after the song, one Remove from queue at a time.
            clearQueue: function () {
              var c = current(), ids = c.items.slice(c.index + 1).map(itemId).filter(Boolean), chain = Promise.resolve(true);
              ids.forEach(function (id) { chain = chain.then(function () { return removeQueued(current(), id); }); });
              return chain.then(function () { return true; });
            },
            queueMove: function (a) {
              var c = current(), from = findItem(c, a.uid || ''), parts = String(a.text || '').split('|'), to = findItem(c, parts[2] || '');
              if (from < 0 || to < 0) return false;
              to = parts[0] === 'before' ? (to < from ? to : to - 1) : (to > from ? to : to + 1);
              return dropAt(from, to);
            },
            // Start radio (a song's, an album's or a playlist's Start mix; an artist's own Mix button).
            radio: function (a) {
              var video = videoId(a.uri), own = headerPlays[a.uri];
              if (video) return navigate({ watchEndpoint: { videoId: video, playlistId: 'RDAMVM' + video, params: 'wAEB' } });
              if (own && own.radio) return navigate(own.radio);
              return listOf(a.uri).then(function (list) { return list ? navigate({ watchPlaylistEndpoint: { playlistId: 'RDAMPL' + list, params: 'wAEB' } }) : false; });
            },
            // Shuffle play (an album's or playlist's menu; an artist's own Shuffle button).
            shufflePlay: function (a) {
              var own = headerPlays[a.uri];
              if (own && own.shuffle) return navigate(own.shuffle);
              return listOf(a.uri).then(function (list) { return list ? navigate({ watchPlaylistEndpoint: { playlistId: list, params: 'wAEB8gECKAE%3D' } }) : false; });
            },
            // The queue's own Autoplay switch.
            autoplay: function (a) {
              var st = store();
              if (!st) return false;
              st.dispatch({ type: 'SET_AUTOPLAY_ENABLED', payload: !!a.on });
              st.dispatch({ type: 'HAS_USER_CHANGED_DEFAULT_AUTOPLAY_MODE', payload: true });
              return true;
            },
            // The player's own Song / Video switch.
            video: function (a) {
              var c = current(), it = c.items[c.index];
              if (!c.st || !it || !it.playlistPanelVideoWrapperRenderer) return false;
              c.st.dispatch({ type: 'SET_PLAYBACK_CONTENT_MODE', payload: a.on ? 'OMV_PREFERRED' : 'ATV_PREFERRED' });
              return true;
            },
            // Your playlists, through the calls the page's own playlist editor sends (browse/edit_playlist).
            addToPlaylist: function (a) {
              var video = videoId(a.uri);
              return video ? editPlaylist(a.text, [{ action: 'ACTION_ADD_VIDEO', addedVideoId: video, dedupeOption: 'DEDUPE_OPTION_SKIP' }]) : false;
            },
            removeFromPlaylist: function (a) {
              var video = videoId(a.uri);
              return video && a.uid ? editPlaylist(a.text, [{ action: 'ACTION_REMOVE_VIDEO', setVideoId: a.uid, removedVideoId: video }]) : false;
            },
            // A row moved in your playlist (context): before the row named (body.before; none: to the end).
            movePlaylistTrack: function (a) {
              var next = s(body(a).before, 100);
              if (!a.uid) return false;
              var move = { action: 'ACTION_MOVE_VIDEO_BEFORE', setVideoId: a.uid };
              if (next) move.movedSetVideoIdSuccessor = next;
              return editPlaylist(a.text, [move]);
            },
            // New playlist: its name, description and privacy (body; private unless said), with a first song.
            createPlaylist: function (a) {
              var b = body(a), name = s(b.name, 150).trim(), video = videoId(a.uri);
              if (!name) return false;
              var req = { title: name, description: s(b.description, 1000), privacyStatus: PRIVACY.test(b.privacy) ? b.privacy : 'PRIVATE' };
              if (video) req.videoIds = [video];
              return fetchJson('/playlist/create', req).then(function (d) { return !!(d && d.playlistId); });
            },
            // Your playlist (context): its name, description and privacy (body), in one edit.
            editPlaylist: function (a) {
              var b = body(a), name = s(b.name, 150).trim();
              if (!name) return false;
              var edits = [{ action: 'ACTION_SET_PLAYLIST_NAME', playlistName: name },
                { action: 'ACTION_SET_PLAYLIST_DESCRIPTION', playlistDescription: s(b.description, 1000) }];
              if (PRIVACY.test(b.privacy)) edits.push({ action: 'ACTION_SET_PLAYLIST_PRIVACY', playlistPrivacy: b.privacy });
              return editPlaylist(a.text, edits);
            },
            deletePlaylist: function (a) {
              var id = browseId(a.text).replace(/^VL/, '');
              if (!/^ytm:playlist:/.test(a.text || '') || !id || id === 'LM' || id === 'SE') return false;
              return fetchJson('/playlist/delete', { playlistId: id }).then(function () { return true; });
            },
            // Audio quality (index: one of the audio view's choices), set the way Settings sets it.
            audio: function (a) {
              return audioSetting().then(function (items) {
                var item = items[a.number | 0];
                if (!item) return false;
                try { w.localStorage.setItem('exo.quality.v1', '1'); } catch (e) { }
                return setAudio(item);
              });
            }
          };
          var me = {
            v: 5,
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
          w.__exoYouTubeMusic = me;
          return me;
        })
        """;
}
