import { host } from './host'
import type { PlayerAction, PlayerActOptions, PlayerRoomState, PlayerView, PlayerViewData, PlayerViewOptions } from './player'

/** What a service can do beyond play, browse and its queue. */
export type MusicFeature = 'devices' | 'jam' | 'friends' | 'recent' | 'addToPlaylist' | 'enqueue' | 'playNext' | 'shows' | 'audio' | 'editQueue'
  /** explore: Explore (YouTube Music); browse and radio: the New and Radio pages (Apple Music); history: History under the queue;
      autoplay: the queue's Autoplay switch; dequeue: removing from Up Next; station: Create station; mix: Start mix and Shuffle play;
      songRadio: a song's or artist's radio playlist (Spotify); library: Add to library; suggestLess, dislike: Suggest less, thumbs down;
      playlists: making, editing, moving the songs of and deleting your playlists; cover: a playlist's own picture; folders: Your
      library's folders and pins (Spotify); credits: a song's credits; discography: an artist's every release; speed: playback speed;
      video: the Song/Video switch; related: Related beside lyrics; quality: the service's own audio quality choice;
      queueLists: Play next and Add to queue on a whole album, playlist or podcast. */
  | 'explore' | 'browse' | 'radio' | 'history' | 'autoplay' | 'dequeue' | 'station' | 'mix' | 'songRadio' | 'library' | 'suggestLess' | 'dislike'
  | 'playlists' | 'cover' | 'folders' | 'credits' | 'discography' | 'speed' | 'video' | 'related' | 'quality' | 'queueLists'

/** Whose keyboard shortcuts the room takes: Spotify's desktop app's, the Music app's, or YouTube Music's (with the Music app's). */
export type MusicShortcuts = 'spotify' | 'music' | 'youtube'

/**
 * One service behind Exo's own player: its hidden engine's lifecycle and its
 * pages and actions. Every adapter answers the same shapes (lib/player.ts).
 */
export type MusicAdapter = {
  name: string
  /** What the service calls the songs you like. */
  likedName: string
  features: ReadonlySet<MusicFeature>
  shortcuts: MusicShortcuts
  start: () => Promise<{ ok: boolean; state: PlayerRoomState; message?: string }>
  /** Starts the service's own sign-in (its window or the browser, never its full page or Spotify's window). */
  signIn: () => Promise<{ ok: boolean; message?: string }>
  /** Signed in without the subscription the room plays on: where to get it (Spotify Premium); none: sign in with another account. */
  premiumUrl?: string
  /** The room closed; an engine that sleeps while idle may now. */
  leave: () => Promise<unknown>
  view: (view: PlayerView, options?: PlayerViewOptions) => Promise<{ ok: boolean; data?: PlayerViewData; message?: string }>
  /** uri: what a create made (a new playlist or folder), where the service says. */
  act: (action: PlayerAction, options?: PlayerActOptions) => Promise<{ ok: boolean; message?: string; uri?: string | null }>
  /** The service's own web link for a song, album, playlist, artist or podcast, to copy and share (else the item's own url). */
  shareUrl?: (uri: string) => string | null
  /** A service that names a new playlist itself (Spotify's "My Playlist #n") makes it at once and opens it to edit. */
  newPlaylistName?: (count: number) => string
}

/** A Spotify uri's web link (what Spotify's own Copy link copies), or null for what has none (a folder, Liked Songs). */
function spotifyShareUrl(uri: string): string | null {
  const parts = uri.split(':')
  const [kind, id] = parts[1] === 'user' && parts[3] === 'playlist' ? ['playlist', parts[4]] : [parts[1], parts[2]]
  return id && /^[A-Za-z0-9]+$/.test(id) && ['track', 'album', 'artist', 'playlist', 'show', 'episode'].includes(kind)
    ? `https://open.spotify.com/${kind}/${id}` : null
}

/** music.youtube.com's links, from the room's ids (YouTubeMusicNative.cs). */
function youtubeShareUrl(uri: string) {
  const [, kind, id] = /^ytm:(\w+):([\w-]+)/.exec(uri) ?? []
  if (!id) return null
  if (kind === 'track') return `https://music.youtube.com/watch?v=${id}`
  if (kind === 'list') return `https://music.youtube.com/playlist?list=${id}`
  if (kind === 'playlist' && id.startsWith('VL')) return `https://music.youtube.com/playlist?list=${id.slice(2)}`
  if (kind === 'artist' && id.startsWith('UC')) return `https://music.youtube.com/channel/${id}`
  return kind === 'album' || kind === 'show' || kind === 'playlist' ? `https://music.youtube.com/browse/${id}` : null
}

/**
 * The room's playlist details as the web services' hosts take them: the playlist as context and the
 * typed words as JSON (text). A move already carries both (the row's neighbours are only known on the page).
 */
function webOptions(action: PlayerAction, options: PlayerActOptions = {}): PlayerActOptions {
  const words = () => JSON.stringify({ name: options.name, description: options.description ?? '', privacy: options.privacy })
  if (action === 'createPlaylist') return { uri: options.uri, text: words() }
  if (action === 'editPlaylist') return { context: options.uri, text: words() }
  if (action === 'deletePlaylist') return { context: options.uri }
  return options
}

/** The Spotify app, hidden, through its own platform APIs (SpotifyNative.cs). */
export const spotifyAdapter: MusicAdapter = {
  name: 'Spotify',
  likedName: 'Liked Songs',
  features: new Set(['devices', 'jam', 'friends', 'recent', 'addToPlaylist', 'enqueue', 'playNext', 'shows', 'audio', 'editQueue', 'playlists', 'cover',
    'folders', 'credits', 'songRadio', 'discography', 'speed']),
  shortcuts: 'spotify',
  premiumUrl: 'https://www.spotify.com/premium/',
  shareUrl: spotifyShareUrl,
  newPlaylistName: count => `My Playlist #${count + 1}`,
  start: () => host.spotifyStart(),
  signIn: () => host.spotifySignIn(),
  leave: () => host.spotifyLeave(),
  view: (view, options) => host.spotifyView(view, options),
  // Spotify deletes your playlist as its own app does: by taking it out of your library.
  act: (action, options) => action === 'deletePlaylist' ? host.spotifyAct('like', { uri: options?.uri, on: false }) : host.spotifyAct(action, options),
}

/** music.youtube.com on Exo's embedded profile, hidden, through its own InnerTube client (YouTubeMusicNative.cs). */
export const youtubeAdapter: MusicAdapter = {
  name: 'YouTube Music',
  likedName: 'Liked Music',
  features: new Set(['enqueue', 'playNext', 'editQueue', 'addToPlaylist', 'shows', 'explore', 'mix', 'dislike', 'autoplay', 'video', 'related',
    'playlists', 'quality', 'queueLists']),
  shortcuts: 'youtube',
  shareUrl: youtubeShareUrl,
  start: () => host.ytmusicStart(),
  signIn: () => host.musicSignIn('youtube'),
  leave: () => Promise.resolve(),
  view: (view, options) => host.ytmusicView(view, options),
  act: (action, options) => host.ytmusicAct(action, webOptions(action, options)),
}

/** music.apple.com on Exo's embedded profile, hidden, through the page's own MusicKit (AppleMusicNative.cs). */
export const appleAdapter: MusicAdapter = {
  name: 'Apple Music',
  likedName: 'Favorites',
  features: new Set(['recent', 'addToPlaylist', 'enqueue', 'playNext', 'browse', 'radio', 'history', 'autoplay', 'dequeue', 'station', 'library',
    'suggestLess', 'playlists', 'credits', 'quality', 'queueLists']),
  shortcuts: 'music',
  start: () => host.applemusicStart(),
  signIn: () => host.musicSignIn('apple'),
  leave: () => Promise.resolve(),
  view: (view, options) => host.applemusicView(view, options),
  act: (action, options) => host.applemusicAct(action, webOptions(action, options)),
}
