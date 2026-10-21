import { useEffect, useRef, useState, type RefObject } from 'react'
import { Play, Search } from '../../brand/icons'
import type { PlayerOption, PlayerTrack } from '../../lib/player'
import { Menu } from '../ui/Menu'
import { TrackList, Waiting } from './pages'
import { loadView, usePlayer } from './playerContext'

const ROW = 56
/** Rows drawn above and below the ones in view. */
const OVERSCAN = 12

type SongList = {
  /** The "sort|filter" this list answers. */
  key: string
  tracks: PlayerTrack[]
  total: number
  contextUri: string
  sorts: PlayerOption[]
  sort: string
  /** Spotify: these are Liked Songs, so a row's menu removes. */
  liked: boolean
}

/** The unfiltered list per service while Exo runs: Songs opens again at once. */
const kept = new WeakMap<object, SongList>()

/**
 * Every song in your library, one list: Spotify's Liked Songs, Apple Music's
 * library songs, YouTube Music's library Songs. The service orders and
 * filters it (so the same order plays on), 200 rows a call as the list
 * scrolls; only the rows in view are drawn. A new order or filter keeps the
 * list on screen until its first rows have landed.
 */
export function SongsPage({ scroller }: { scroller: RefObject<HTMLDivElement | null> }) {
  const { adapter, play } = usePlayer()
  const [list, setList] = useState<SongList | null>(() => kept.get(adapter) ?? null)
  const [sort, setSort] = useState(() => kept.get(adapter)?.sort ?? '')
  const [filter, setFilter] = useState('')
  const [settled, setSettled] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [removed, setRemoved] = useState<ReadonlySet<string>>(() => new Set())
  const [range, setRange] = useState({ start: 0, end: 30 })
  const [scrolled, setScrolled] = useState(false)
  const rows = useRef<HTMLDivElement>(null)
  const loadingMore = useRef(false)
  const query = `${sort}|${settled.trim()}`
  // The list's own key: its order and filter, and the chip it is narrowed to.
  const listKey = `${query}#`
  const ready = list !== null

  useEffect(() => {
    const timer = window.setTimeout(() => setSettled(filter), 250)
    return () => window.clearTimeout(timer)
  }, [filter])

  // The first rows of this order and filter.
  useEffect(() => {
    let live = true
    void loadView(adapter, 'songs', { query, offset: 0 }).then(result => {
      if (!live) return
      const data = result.data
      if (!data) { setError(result.error ?? `${adapter.name} could not load your songs.`); return }
      const tracks = data.tracks ?? []
      setError(null)
      setList(previous => {
        if (previous && previous.key !== listKey) scroller.current?.scrollTo({ top: 0 })
        return {
          key: listKey, tracks, total: Math.max(data.total ?? 0, tracks.length), contextUri: data.contextUri ?? '',
          sorts: data.sorts ?? [], sort: data.sort ?? '', liked: !!data.liked,
        }
      })
    })
    return () => { live = false }
  }, [adapter, query, listKey, scroller])

  useEffect(() => {
    if (list && !list.key.slice(list.key.indexOf('|') + 1, list.key.indexOf('#'))) kept.set(adapter, list)
  }, [adapter, list])

  // Which rows are in view, measured against the room's scrolling column.
  useEffect(() => {
    const box = scroller.current
    if (!box) return
    let frame = 0
    const measure = () => {
      frame = 0
      const el = rows.current
      if (!el) return
      const top = el.getBoundingClientRect().top - box.getBoundingClientRect().top + box.scrollTop
      const from = box.scrollTop - top
      const start = Math.max(0, Math.floor(from / ROW) - OVERSCAN)
      const end = Math.max(start, Math.ceil((from + box.clientHeight) / ROW) + OVERSCAN)
      setRange(current => current.start === start && current.end === end ? current : { start, end })
      if (box.scrollTop > 0) setScrolled(true)
    }
    const schedule = () => { if (!frame) frame = requestAnimationFrame(measure) }
    measure()
    box.addEventListener('scroll', schedule, { passive: true })
    const sizes = new ResizeObserver(schedule)
    sizes.observe(box)
    return () => { box.removeEventListener('scroll', schedule); sizes.disconnect(); cancelAnimationFrame(frame) }
  }, [scroller, ready])

  // The next rows, once the view comes near the end of what has loaded.
  useEffect(() => {
    if (!list || loadingMore.current || list.tracks.length >= list.total || range.end < list.tracks.length - OVERSCAN) return
    loadingMore.current = true
    const { key } = list
    const offset = list.tracks.length
    void loadView(adapter, 'songs', { query: key.slice(0, key.indexOf('#')), offset }).then(result => {
      loadingMore.current = false
      const data = result.data
      if (!data) { setError(result.error ?? `${adapter.name} could not load your songs.`); return }
      setList(previous => {
        if (!previous || previous.key !== key || previous.tracks.length !== offset) return previous
        const tracks = [...previous.tracks, ...(data.tracks ?? [])]
        return { ...previous, tracks, total: data.tracks?.length ? Math.max(data.total ?? 0, tracks.length) : tracks.length }
      })
    })
  }, [adapter, list, range.end])

  const tracks = list ? (removed.size ? list.tracks.filter(track => !removed.has(track.uid || track.uri)) : list.tracks) : []
  const total = list ? Math.max(tracks.length, list.total - removed.size) : 0
  const sorts = list?.sorts ?? []
  const playFrom = (track: PlayerTrack, index: number) => list && play(list.contextUri, track, index, list.key.slice(0, list.key.indexOf('#')))

  return (
    <section className="exo-music-section exo-music-songs" aria-busy={!list || list.key !== listKey}>
      <header className="exo-music-songs-head">
        <h2>Songs</h2>
        <button type="button" className="button button--primary button--sm" disabled={!tracks[0]?.playable}
          onClick={() => tracks[0] && playFrom(tracks[0], 0)}><Play size={14} /> Play</button>
        <label className="exo-music-search exo-music-songs-filter">
          <Search size={15} />
          <input type="search" value={filter} placeholder="Filter" aria-label="Filter your songs"
            onChange={event => setFilter(event.target.value)} onKeyDown={event => { if (event.key === 'Escape') setFilter('') }} />
        </label>
        {sorts.length ? (
          <Menu label="Sort" anchor="--exo-music-songs-sort" offset={4} triggerClassName="exo-music-sort"
            trigger={sorts.find(option => option.id === list?.sort)?.name ?? 'Sort'}
            items={sorts.map(option => ({ id: option.id, label: option.name }))} checked={list?.sort ?? ''} onAction={setSort} />
        ) : null}
      </header>
      {error ? <p className="exo-music-error" role="alert">{error}</p> : list ? null : <Waiting />}
      {list && !total ? <p className="exo-music-empty">{settled.trim() ? `No songs match “${settled.trim()}”` : 'No songs in your library yet'}</p> : null}
      <div ref={rows}>
        {total ? (
          <TrackList tracks={tracks} onPlay={playFrom} showAlbum liked={list?.liked}
            window={{ start: range.start, end: range.end, total, row: ROW, arrive: !scrolled }}
            onRemoved={track => setRemoved(previous => new Set(previous).add(track.uid || track.uri))} />
        ) : null}
      </div>
    </section>
  )
}

