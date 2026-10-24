import { useEffect, useRef, useState, type ReactNode } from 'react'
import { host } from '../../lib/host'
import { report } from '../../lib/report'
import { performanceMode, type DiscordFavoriteGif, type DiscordGif } from '../../lib/discord'
import { Icons } from './icons'
import { useDismiss } from '../../lib/dismiss'

type Tab = 'trending' | 'favorites'
type Shown = { key: string; url: string; src: string; width: number; height: number; video: boolean; title?: string }

/** Two columns at their own heights (Discord's masonry): each GIF goes under the shorter one. */
function columns(list: Shown[]): [Shown[], Shown[]] {
  const out: [Shown[], Shown[]] = [[], []]
  const height = [0, 0]
  for (const gif of list) {
    const side = height[0] <= height[1] ? 0 : 1
    out[side].push(gif)
    height[side] += gif.width && gif.height ? gif.height / gif.width : 1
  }
  return out
}

/**
 * One GIF in the grid. Only the ones in or near view load and play (a GIF video each is a decoder);
 * scrolled away they pause and keep their frame. In Performance mode they play only under the pointer.
 */
function GifMedia({ gif }: { gif: Shown }) {
  const video = useRef<HTMLVideoElement>(null)
  const [near, setNear] = useState(false)
  const [seen, setSeen] = useState(false)
  const [hover, setHover] = useState(false)
  useEffect(() => {
    const element = video.current
    if (!element) return
    const watch = new IntersectionObserver(([entry]) => {
      setNear(entry.isIntersecting)
      if (entry.isIntersecting) setSeen(true)
    }, { root: element.closest('.exo-dc-gif-grid'), rootMargin: '200px 0px' })
    watch.observe(element)
    return () => watch.disconnect()
  }, [])
  const playing = near && (hover || !performanceMode())
  useEffect(() => {
    const element = video.current
    if (!element || !seen) return
    if (playing) void element.play().catch(() => { })
    else element.pause()
  }, [playing, seen])
  const shape = gif.width && gif.height ? { aspectRatio: `${gif.width} / ${gif.height}` } : undefined
  if (!gif.video) return <img src={gif.src} alt="" loading="lazy" decoding="async" draggable={false} style={shape} />
  // Once near, it loads at least its first frame, so a GIF held still (Performance mode) still shows.
  return <video ref={video} src={seen ? gif.src : undefined} loop muted playsInline preload={seen ? 'auto' : 'none'} style={shape}
    onPointerEnter={() => setHover(true)} onPointerLeave={() => setHover(false)} />
}

const fromSearch = (gif: DiscordGif): Shown => ({ key: gif.url, url: gif.url, src: gif.src, width: gif.width, height: gif.height, video: true, title: gif.title })
const fromFavorite = (gif: DiscordFavoriteGif): Shown => ({
  key: gif.url, url: gif.url, src: gif.src, width: gif.width, height: gif.height,
  video: gif.format === 2 || /\.(mp4|webm)(\?|$)/i.test(gif.src),
})

/**
 * Discord's GIF picker (through Discord): Trending and your Favorites, and search over both
 * tabs. Favorites are your Discord account's own, the same list Discord shows on every device;
 * the star adds or removes one. Picking a GIF sends its link, as Discord does.
 */
export function GifPicker({ tabs, onPick, onClose }: { tabs?: ReactNode; onPick: (url: string) => void; onClose: () => void }) {
  const [query, setQuery] = useState('')
  const [tab, setTab] = useState<Tab>('trending')
  const [gifs, setGifs] = useState<DiscordGif[]>([])
  const [favorites, setFavorites] = useState<DiscordFavoriteGif[] | null>(null)
  const [loading, setLoading] = useState(true)
  const box = useRef<HTMLDivElement>(null)
  const searching = query.trim().length > 0

  useEffect(() => {
    host.discordGifFavorites('list').then(result => setFavorites(result.ok ? result.gifs ?? [] : []))
      .catch((error: unknown) => { report(error, 'GifPicker.favorites'); setFavorites([]) })
  }, [])

  useEffect(() => {
    if (tab === 'favorites' && !searching) return
    let live = true
    setLoading(true)
    const timer = window.setTimeout(() => {
      host.discordGifs(query.trim() || undefined).then(result => { if (live) setGifs(result.gifs ?? []) })
        .catch((error: unknown) => report(error, 'GifPicker.load'))
        .finally(() => { if (live) setLoading(false) })
    }, searching ? 300 : 0)
    return () => { live = false; window.clearTimeout(timer) }
  }, [query, tab, searching])

  useDismiss(box, onClose)
  useEffect(() => {
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') onClose() }
    document.addEventListener('keydown', escape)
    return () => document.removeEventListener('keydown', escape)
  }, [onClose])

  const starred = new Set((favorites ?? []).map(gif => gif.url))
  const toggle = (gif: Shown) => {
    const on = starred.has(gif.url)
    // Shown at once; Discord's answer (the saved list) replaces it.
    setFavorites(list => on ? (list ?? []).filter(item => item.url !== gif.url) : [{ url: gif.url, src: gif.src, format: gif.video ? 2 : 1, width: gif.width, height: gif.height }, ...(list ?? [])])
    host.discordGifFavorites(on ? 'remove' : 'add', { url: gif.url, src: gif.src, width: gif.width, height: gif.height })
      .then(result => { if (result.ok && result.gifs) setFavorites(result.gifs) })
      .catch((error: unknown) => report(error, 'GifPicker.star'))
  }
  const onFavorites = tab === 'favorites' && !searching
  const shown = onFavorites ? (favorites ?? []).map(fromFavorite) : gifs.map(fromSearch)
  const empty = onFavorites ? 'Star a GIF to keep it here.' : searching ? 'No GIFs found.' : 'Nothing trending right now.'
  const settled = onFavorites ? favorites !== null : !loading

  return (
    <div className="exo-dc-picker is-gif" ref={box} role="dialog" aria-label="GIFs">
      {tabs}
      <header className="exo-dc-picker-top">
        <input className="exo-dc-picker-search" placeholder="Search GIFs" value={query} autoFocus onChange={event => setQuery(event.target.value)} aria-label="Search GIFs" />
        <div className="exo-dc-picker-tabs" role="tablist" aria-label="GIFs">
          <button type="button" role="tab" aria-selected={tab === 'trending'} onClick={() => { setTab('trending'); setQuery('') }}>Trending</button>
          <button type="button" role="tab" aria-selected={tab === 'favorites'} onClick={() => { setTab('favorites'); setQuery('') }}>
            Favorites{favorites?.length ? <span className="exo-dc-picker-count">{favorites.length}</span> : null}
          </button>
        </div>
      </header>
      <div className="exo-dc-gif-grid">
        {columns(shown).map((column, side) => <div key={side} className="exo-dc-gif-col">{column.map(gif => (
          <span key={gif.key} className="exo-dc-gif">
            <button type="button" className="exo-dc-gif-pick" aria-label={gif.title || 'GIF'} onClick={() => onPick(gif.url)}>
              <GifMedia gif={gif} />
            </button>
            <button type="button" className={`exo-dc-gif-star${starred.has(gif.url) ? ' is-on' : ''}`} aria-pressed={starred.has(gif.url)}
              aria-label={starred.has(gif.url) ? 'Remove from favorites' : 'Add to favorites'} onClick={() => toggle(gif)}>
              <Icons.Star size={16} filled={starred.has(gif.url)} />
            </button>
          </span>
        ))}</div>)}
        {settled && shown.length === 0 ? <p className="exo-dc-note">{empty}</p> : null}
      </div>
    </div>
  )
}
