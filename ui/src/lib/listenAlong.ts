import { useEffect, useState, useSyncExternalStore } from 'react'
import type { DiscordFriend, DiscordRich } from './discord'
import { host, onHostEvent } from './host'
import { savedMusicService, type MusicService } from './music'
import { appleAdapter, spotifyAdapter, youtubeAdapter } from './musicAdapters'
import { notify } from './notify'
import { report } from './report'

/**
 * Listen along: a Discord friend's song (their Listening activity: Spotify's own, Exo's for any of its services, or
 * another app's) played here on the listener's own service and account, at the same moment, song after song, in a
 * call or not. Spotify's activity names its track, which a Spotify listener plays as it is; any other song is found by
 * the listener's own search and MusicMatch.cs. Only the song's name and artists leave Exo, to the listener's service.
 *
 * How it can fail: the friend pauses or goes offline (their activity goes: the music pauses after a moment and picks
 * up when it comes back, and listening along ends after 15 minutes without it); the song is not on this service (said,
 * and the next song is tried); the listener plays something else mid-song (listening along stops, as Discord's does;
 * the song after a followed one ending on its own is the service's autoplay, not a choice); clocks a second or two
 * apart (only a jump of more than 2.5 s seeks); the listener switches services (it stops).
 */
export type ListenFriend = { id: string; name: string; avatar: string | null }
export type ListenAlong =
  | { status: 'off' }
  | { status: 'finding' | 'playing' | 'paused' | 'missing'; friend: ListenFriend; title: string; artist: string }

/** A friend's activity that can be listened along to: a song, and when it started. */
export function canListenAlong(rich: DiscordRich | null | undefined): rich is DiscordRich & { details: string; start: number } {
  return !!rich && rich.type === 2 && !!rich.details && typeof rich.start === 'number'
}

const adapters = { spotify: spotifyAdapter, youtube: youtubeAdapter, apple: appleAdapter }

/** Your Discord friends listening to a song now, kept current from their presence; signedIn: Discord is, in Exo.
    enabled false: nothing is asked or listened to (the Jam box while there is no call, music or listening along). */
export function useListeningFriends(enabled = true) {
  const [friends, setFriends] = useState<DiscordFriend[] | null>(null)
  const [signedIn, setSignedIn] = useState(true)
  useEffect(() => {
    if (!enabled) return
    let live = true
    host.discordStatus().then(async status => {
      if (!live) return
      setSignedIn(status.signedIn)
      if (status.signedIn) { const result = await host.discordFriends(); if (live) setFriends(result.friends) }
    }).catch((error: unknown) => { report(error, 'listenAlong.friends'); if (live) setFriends([]) })
    const off = onHostEvent('discord.presence', event =>
      setFriends(list => list?.map(friend => friend.id === event.userId ? { ...friend, status: event.status, rich: event.rich } : friend) ?? null))
    return () => { live = false; off() }
  }, [enabled])
  const listening = (friends ?? []).flatMap(friend =>
    friend.kind === 'friend' && friend.status !== 'offline' && canListenAlong(friend.rich) ? [{ friend, rich: friend.rich }] : [])
  return { listening, loaded: friends !== null, signedIn }
}

/** The same song by name, as two services or a service and its media session write it. */
export function sameSong(a: string | null | undefined, b: string | null | undefined) {
  const fold = (text: string) => text.toLowerCase().replace(/[^\p{L}\p{N}]+/gu, '')
  const [x, y] = [fold(a ?? ''), fold(b ?? '')]
  return !!x && !!y && (x.includes(y) || y.includes(x))
}

const OFF: ListenAlong = { status: 'off' }
let state: ListenAlong = OFF
const watchers = new Set<() => void>()

export function useListenAlong(): ListenAlong {
  return useSyncExternalStore(watch => { watchers.add(watch); return () => { watchers.delete(watch) } }, () => state, () => OFF)
}

function show(next: ListenAlong) {
  state = next
  watchers.forEach(watch => watch())
}

/** The followed song: its key (Spotify's track id, else name and artists), when it started, and what plays here for it. */
type Follow = { friend: ListenFriend; key: string; start: number; end: number | null; uri: string | null; title: string; paused: boolean }
let follow: Follow | null = null
let unsubscribe: (() => void) | null = null
let gone = 0
let ended = 0
let turn = 0
const found = new Map<string, { uri: string; title: string } | null>()

export function listenAlong(friend: ListenFriend, rich: DiscordRich) {
  stopListening()
  const service = savedMusicService()
  const now: Follow = { friend, key: '', start: 0, end: null, uri: null, title: '', paused: false }
  follow = now
  const offPresence = onHostEvent('discord.presence', event => { if (follow === now && event.userId === friend.id) void apply(now, event.rich ?? null) })
  const offPlayback = onHostEvent('music.playback', playback => {
    // Another song mid-song is the listener's own choice; after the followed one ends it is the service's autoplay.
    if (follow !== now || !now.uri || playback?.status !== 'playing' || sameSong(playback.title, now.title)) return
    if (now.end !== null && Date.now() < now.end - 5000) stopListening('You played another song, so listening along stopped.')
  })
  const offService = () => { if (follow === now) stopListening() }
  window.addEventListener('exo-music-service', offService)
  unsubscribe = () => { offPresence(); offPlayback(); window.removeEventListener('exo-music-service', offService) }
  notify({ id: 'listen-along', title: friend.name, text: `Listening along on ${adapters[service].name}.`, tone: 'good' })
  void adapters[service].start().then(started => {
    if (follow !== now) return
    if (started.ok && started.state === 'running') void apply(now, rich)
    else stopListening(`Sign in to ${adapters[service].name} in Music to listen along.`)
  }, () => { if (follow === now) stopListening(`${adapters[service].name} did not open. Try again.`) })
}

/** Stops following; the song playing keeps playing. why: a notice saying so. */
export function stopListening(why?: string) {
  if (!follow) return
  follow = null
  turn++
  window.clearTimeout(gone)
  window.clearTimeout(ended)
  unsubscribe?.()
  unsubscribe = null
  show(OFF)
  if (why) notify({ id: 'listen-along', text: why })
}

function apply(now: Follow, rich: DiscordRich | null) {
  window.clearTimeout(gone)
  window.clearTimeout(ended)
  if (!canListenAlong(rich)) return away(now)
  const key = rich.syncId ?? `${rich.details}|${rich.state ?? ''}`
  return key === now.key ? catchUp(now, rich) : next(now, rich, key)
}

/** Their activity went: between songs that is a moment, so the music pauses only when it stays gone. */
function away(now: Follow) {
  gone = window.setTimeout(() => {
    if (follow !== now) return
    now.paused = true
    if (state.status !== 'off') show({ ...state, status: 'paused' })
    void host.musicControl('pause').catch(() => null)
  }, 4000)
  ended = window.setTimeout(() => { if (follow === now) stopListening(`${now.friend.name} stopped listening.`) }, 15 * 60_000)
}

const shown = (now: Follow, rich: DiscordRich & { details: string }) =>
  ({ status: 'playing' as const, friend: now.friend, title: rich.details, artist: rich.state ?? '' })

/** The same song: catch up after their pause or seek; smaller differences are clocks. */
async function catchUp(now: Follow, rich: DiscordRich & { details: string; start: number }) {
  const jumped = Math.abs(rich.start - now.start) > 2500
  now.start = rich.start
  if (!now.uri || !now.paused && !jumped) return
  if (now.paused) await host.musicControl('play').catch(() => null)
  now.paused = false
  await seek(now)
  if (follow === now) show(shown(now, rich))
}

/** A new song: found on this service, played, and moved to where the friend is in it. */
async function next(now: Follow, rich: DiscordRich & { details: string; start: number }, key: string) {
  const mine = ++turn
  const current = () => follow === now && mine === turn
  Object.assign(now, { key, start: rich.start, end: rich.end && rich.end > rich.start ? rich.end : null, uri: null, title: '', paused: false })
  show({ ...shown(now, rich), status: 'finding' })
  const track = await find(rich).catch(() => null)
  if (!current()) return
  const played = track ? await adapters[savedMusicService()].act('play', { uri: track.uri }).catch(() => ({ ok: false })) : { ok: false }
  if (!current()) return
  if (!track || !played.ok) {
    show({ ...shown(now, rich), status: 'missing' })
    void host.musicControl('pause').catch(() => null)
    return
  }
  Object.assign(now, { uri: track.uri, title: track.title })
  await playing(track.title)
  if (!current()) return
  await seek(now)
  show(shown(now, rich))
}

/** Where the friend is in the song now; a start under 3 s plays from the top. */
async function seek(now: Follow) {
  const at = Date.now() - now.start
  if (at > 3000 && (now.end === null || Date.now() < now.end - 3000)) await host.musicControl('seek', { positionMs: at }).catch(() => null)
}

/** Waits (at most 5 s) until the service says the song plays, so the seek lands in it and not in the one before. */
function playing(title: string) {
  return new Promise<void>(resolve => {
    const done = () => { window.clearTimeout(timer); off(); resolve() }
    const off = onHostEvent('music.playback', playback => { if (playback?.status === 'playing' && sameSong(playback.title, title)) done() })
    const timer = window.setTimeout(done, 5000)
  })
}

/** The friend's song on the listener's service: Spotify's own track for a Spotify listener, else the best search result. */
async function find(rich: DiscordRich & { details: string; start: number }) {
  const service = savedMusicService()
  if (service === 'spotify' && rich.name === 'Spotify' && rich.syncId && /^[A-Za-z0-9]{22}$/.test(rich.syncId))
    return { uri: `spotify:track:${rich.syncId}`, title: rich.details }
  const key = `${service}|${rich.details}|${rich.state ?? ''}`
  if (found.has(key)) return found.get(key) ?? null
  const answer = await search(service, rich)
  if (found.size > 200) found.clear()
  // A search that failed is not remembered: the next try may find it.
  if (answer !== undefined) found.set(key, answer)
  return answer ?? null
}

/** The best of the service's search results for the song, null when none is it, undefined when the search failed. */
async function search(service: MusicService, rich: DiscordRich & { details: string; start: number }) {
  // Spotify's activity joins artists with "; "; others write one line.
  const artists = (rich.state ?? '').split(/;\s*/).filter(Boolean)
  const result = await adapters[service].view('search', { query: `${rich.details.replace(/\s*[([].*$/, '')} ${artists[0] ?? ''}`.trim() })
  if (!result.ok) return undefined
  const tracks = (result.data?.tracks ?? []).filter(track => track.playable !== false && track.uri).slice(0, 20)
  if (!tracks.length) return null
  const durationMs = rich.end && rich.end > rich.start ? rich.end - rich.start : null
  const match = await host.musicMatch({ title: rich.details, artists, durationMs },
    tracks.map(track => ({ title: track.title, artists: track.artists.map(artist => artist.name), durationMs: track.durationMs || null })))
  const track = match.ok && typeof match.index === 'number' && match.index >= 0 ? tracks[match.index] : null
  return track ? { uri: track.uri, title: track.title } : null
}
