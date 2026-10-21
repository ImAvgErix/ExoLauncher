import { createContext, useContext, useEffect, useState } from 'react'
import { report } from '../../lib/report'
import type { MusicAdapter, MusicFeature } from '../../lib/musicAdapters'
import type { PlayerAction, PlayerActOptions, PlayerCard, PlayerView, PlayerViewData, PlayerViewOptions } from '../../lib/player'

/** A page of the player's middle column. edit: a playlist just made, opened with its details to fill in. */
export type Page = { view: 'home' | 'search' | 'songs' | 'collection' | 'playlist' | 'album' | 'artist' | 'show' | 'explore' | 'browse' | 'radio' | 'discography';
  uri?: string; title?: string; edit?: boolean }

/** The playlist sheet: a new playlist (with the song it starts from, in the folder it goes in), or one of yours to edit. */
export type PlaylistEdit = { uri?: string; title?: string; description?: string; privacy?: string; image?: string; track?: string; folder?: string }

export type Player = {
  adapter: MusicAdapter
  has: (feature: MusicFeature) => boolean
  open: (page: Page) => void
  /** A context (playlist, album, artist, list) from its start, or a track in it; query: the Songs page's order it plays in. */
  play: (context: string | undefined, track?: { uri: string }, index?: number, query?: string) => void
  playCard: (card: PlayerCard) => void
  playingUri?: string
  /** Goes up after an action changed your library or settings (like, save, add, remove, audio). */
  revision: number
  /** Opens the playlist sheet (services with the playlists feature). */
  editPlaylist: (edit: PlaylistEdit) => void
  /** A playlist of yours was deleted: a page of it closes. */
  deleted: (uri: string) => void
  /** A song's credits, in the side panel. */
  showCredits: (uri: string) => void
}

export const PlayerContext = createContext<Player | null>(null)

export function usePlayer() {
  const player = useContext(PlayerContext)
  if (!player) throw new Error('usePlayer outside the music player')
  return player
}

export function pageFor(card: PlayerCard): Page | null {
  if (card.kind === 'collection') return { view: 'collection', uri: card.uri, title: card.title }
  if (card.kind === 'playlist' || card.kind === 'album' || card.kind === 'artist' || card.kind === 'show') return { view: card.kind, uri: card.uri, title: card.title }
  if (card.kind === 'episodes') return { view: 'playlist', uri: card.uri, title: card.title }
  if (card.kind === 'browse' || card.kind === 'mood') return { view: 'browse', uri: card.uri, title: card.title }
  if (card.kind === 'discography') return { view: 'discography', uri: card.uri, title: card.title }
  return null
}

/** Ask the service for one view; the room's message on failure. A service that is still starting
    can't answer for a moment, so a view is asked again (three tries, 1.5 s apart) before the room says so. */
export async function loadView(adapter: MusicAdapter, view: PlayerView, options: PlayerViewOptions = {}) {
  let message = `${adapter.name} could not load this page.`
  for (let attempt = 0; attempt < 3; attempt++) {
    if (attempt) await new Promise(resolve => window.setTimeout(resolve, 1500))
    try {
      const result = await adapter.view(view, options)
      if (result.ok && result.data) return { data: result.data }
      message = result.message ?? message
    } catch (error) {
      if (attempt === 2) report(error, 'MusicPlayer.load')
    }
  }
  return { error: message }
}

/** Actions that change your library or the service's settings: what shows them reads again (Player.revision). */
const CHANGES: ReadonlySet<PlayerAction> = new Set<PlayerAction>(['like', 'dislike', 'addToPlaylist', 'removeFromPlaylist', 'audio', 'library', 'suggestLess',
  'createPlaylist', 'editPlaylist', 'deletePlaylist', 'movePlaylistTrack', 'createFolder', 'renameFolder', 'deleteFolder', 'moveToFolder',
  'playlistImage', 'privacy', 'collaborative', 'pin', 'played', 'download'])
const changed = new Set<() => void>()
export function onPlayerChanged(listener: () => void) {
  changed.add(listener)
  return () => { changed.delete(listener) }
}

export async function runAction(adapter: MusicAdapter, action: PlayerAction, options: PlayerActOptions = {}) {
  return (await runActionFor(adapter, action, options)).ok
}

/** An action and what it answered: a create's new uri, a refusal's message. */
export async function runActionFor(adapter: MusicAdapter, action: PlayerAction, options: PlayerActOptions = {}) {
  try {
    const result = await adapter.act(action, options)
    if (result.ok && CHANGES.has(action)) changed.forEach(listener => listener())
    return result
  } catch (error) {
    report(error, 'MusicPlayer.act')
    return { ok: false, message: undefined, uri: null }
  }
}

/**
 * Pages already seen this session, per service: a page opened again shows at
 * once from here while its fresh copy loads underneath (the service answers in
 * 0.3 to 1 s). The oldest fall out past 60.
 */
const seen = new Map<string, PlayerViewData>()
/** Pages only: live state (the queue, devices, Jam, friends) always loads fresh. */
const PAGES: ReadonlySet<PlayerView> = new Set<PlayerView>(['home', 'playlist', 'album', 'artist', 'show', 'collection', 'library', 'lyrics', 'explore', 'browse',
  'radio', 'credits', 'related', 'discography'])
function remember(key: string, data: PlayerViewData) {
  seen.delete(key)
  seen.set(key, data)
  if (seen.size > 60) seen.delete(seen.keys().next().value as string)
}

/**
 * One view, read again whenever its options change. The previous answer stays
 * until the next one arrives, so a page never empties while it reloads.
 */
export function useView(view: PlayerView, options: PlayerViewOptions = {}, skip = false) {
  const { adapter } = usePlayer()
  const key = JSON.stringify([view, options])
  const seenKey = adapter.name + key
  const [state, setState] = useState<{ key: string; data: PlayerViewData | null; error: string | null }>({ key: '', data: null, error: null })
  useEffect(() => {
    if (skip) return
    let live = true
    const [name, args] = JSON.parse(key) as [PlayerView, PlayerViewOptions]
    void loadView(adapter, name, args).then(result => {
      if (!live) return
      if (result.data && PAGES.has(name)) remember(adapter.name + key, result.data)
      // A failed read keeps this view's own last answer, never the previous page's or song's
      // (a panel's live state, read again, keeps what it last had).
      setState(previous => result.data ? { key, data: result.data, error: null }
        : { key, data: previous.key === key || !PAGES.has(name) ? previous.data : seen.get(adapter.name + key) ?? null, error: result.error ?? null })
    })
    return () => { live = false }
  }, [adapter, key, skip])
  const cached = state.key === key ? undefined : seen.get(seenKey)
  return { data: cached ?? state.data, error: state.key === key ? state.error : null, fresh: state.key === key || !!cached }
}
