import { memo, useCallback, useEffect, useId, useMemo, useRef, useState, type CSSProperties } from 'react'
import { Bookmark } from '../../brand/icons'
import type { Game, StoreArt, StoreTile } from '../../lib/host'
import { CoverArt } from '../CoverArt'
import { artSources, cents, reviewText, warmGame, watchCard } from './storeData'

/**
 * Steam art with fallbacks: the first source that loads wins. A wide
 * fallback inside a poster frame is shown whole over a blurred copy of
 * itself instead of being cropped.
 */
export function SteamImage({ sources, className, eager = false, onMissing, onLoad }: {
  sources: { src: string; wide?: boolean }[]
  className?: string
  eager?: boolean
  onMissing?: () => void
  /** The picture is in (the banner fades a new game in only then). */
  onLoad?: () => void
}) {
  const [index, setIndex] = useState(0)
  const pick = sources[index]
  const gone = !pick
  useEffect(() => { if (gone) onMissing?.() }, [gone, onMissing])
  if (!pick) return null
  const img = (
    <img
      className={`exo-store-img${pick.wide ? ' is-contained' : ''}${className ? ' ' + className : ''}`}
      src={pick.src}
      alt=""
      loading={eager ? 'eager' : 'lazy'}
      decoding="async"
      draggable={false}
      onError={() => setIndex(i => i + 1)}
      onLoad={onLoad}
    />
  )
  return pick.wide ? <><img className="exo-store-img is-backdrop" src={pick.src} alt="" aria-hidden="true" decoding="async" />{img}</> : img
}

/** Sale chip, price and the old price, in Steam's own regional text when it gave one; nothing when there is no price. */
export function Price({ final, initial, discount, currency, priceText, wasText, free = false }: {
  final?: number | null
  initial?: number | null
  discount: number
  currency?: string | null
  priceText?: string | null
  wasText?: string | null
  free?: boolean
}) {
  if (free) return <span className="exo-store-price"><span className="exo-store-now">Free</span></span>
  if (final == null || (final === 0 && !initial)) return null
  return (
    <span className="exo-store-price">
      {discount > 0 ? <span className="exo-store-save">-{discount}%</span> : null}
      <span className="exo-store-now">{priceText ?? cents(final, currency)}</span>
      {discount > 0 && initial ? <s className="exo-store-was">{wasText ?? cents(initial, currency)}</s> : null}
    </span>
  )
}

/**
 * Warms the game's page so the click opens it whole: once the card has stayed on
 * screen a moment (in the background, a few games at a time; not with watch
 * false), and at once when the pointer rests on it or it takes focus.
 */
export function useWarm(appId: string, art?: StoreArt | null, watch = true) {
  const timer = useRef(0)
  useEffect(() => () => window.clearTimeout(timer.current), [])
  return useMemo(() => ({
    ref: watch ? (card: HTMLElement | null) => card ? watchCard(card, appId, art) : undefined : undefined,
    onPointerEnter: () => {
      window.clearTimeout(timer.current)
      timer.current = window.setTimeout(() => void warmGame(appId, art), 60)
    },
    onPointerLeave: () => window.clearTimeout(timer.current),
    onFocus: () => void warmGame(appId, art),
  }), [appId, art, watch])
}

/**
 * One game on a shelf: its library poster (its name on black when Steam has
 * no art) and name, with a coming game's release date or a sale's discount
 * under it (price and score are on its page). A pick made for this person
 * says why on its art: the wishlist's bookmark in a corner, or a Similar to
 * band across the top with the icons of the games you play that it is like,
 * which opens them as covers (SimilarPanel). arrive is its place when the view
 * arrives (DESIGN.md › Motion).
 */
export const StoreCard = memo(function StoreCard({ tile, owned, similar = NONE, priced = false, onOpen, onOpenGame, arrive }: {
  tile: StoreTile
  owned: boolean
  /** Show the price under the name (the Wishlist); elsewhere only a coming game's date or a sale's discount. */
  priced?: boolean
  /** The library games this pick is like, closest first. */
  similar?: readonly Game[]
  onOpen: (tile: StoreTile) => void
  /** A game of yours from the Similar to panel, opened in the Library. */
  onOpenGame?: (game: Game) => void
  arrive?: number
}) {
  const [missing, setMissing] = useState(false)
  const onMissing = useCallback(() => setMissing(true), [])
  const warm = useWarm(tile.appId, tile.art)
  const score = reviewText(tile.review, tile.reviewPercent)
  const like = tile.wished ? NONE : similar
  return (
    <div className={`exo-store-card-slot${arrive != null ? ' exo-motion-arrive' : ''}`} style={arrive != null ? { '--i': arrive } as CSSProperties : undefined}>
    <button
      type="button"
      className="exo-store-card"
      onClick={() => onOpen(tile)}
      {...warm}
      aria-label={[tile.name, owned ? 'in your library' : '', tile.wished ? 'on your wishlist' : '', score].filter(Boolean).join(', ')}
    >
      <span className="exo-store-poster" data-vt-art>
        {missing
          ? <span className="exo-store-poster-name" aria-hidden="true">{tile.name}</span>
          : <SteamImage sources={artSources(tile, ['poster', 'header'], ['header'])} onMissing={onMissing} />}
        {owned ? <span className="exo-store-badge">In library</span> : null}
        {tile.wished ? <span className="exo-glass exo-store-mark" aria-hidden="true"><Bookmark size={14} filled /></span> : null}
      </span>
      <span className="exo-store-card-title">{tile.name}</span>
      {/* Price and score are on the game's page; a card says only what its shelf is about: when it comes, or the sale.
          The Wishlist is about the price, so its cards show it. */}
      {tile.soon || tile.discount > 0 || (priced && tile.final != null) ? (
        <span className="exo-store-meta" aria-hidden="true">
          {tile.soon ? <span className="exo-store-when">{tile.when ?? 'Coming soon'}</span>
            : priced ? <Price free={tile.free} final={tile.final} initial={tile.initial} discount={tile.discount} currency={tile.currency} priceText={tile.priceText} wasText={tile.wasText} />
            : <span className="exo-store-save">-{tile.discount}%</span>}
        </span>
      ) : null}
    </button>
    {like.length ? <SimilarTo games={like} onOpenGame={onOpenGame} /> : null}
    </div>
  )
})

const NONE: readonly Game[] = []

/**
 * Similar to, across the top of a pick's art: the icons of the games of yours it is like. Pressed, it opens a popover
 * (large glass, light dismiss) with those games as covers, each opening in the Library.
 */
function SimilarTo({ games, onOpenGame }: { games: readonly Game[]; onOpenGame?: (game: Game) => void }) {
  const id = useId()
  const anchor = `--exo-like-${id.replace(/[^a-z0-9]/gi, '')}` as const
  const panel = useRef<HTMLDivElement>(null)
  return (
    <>
      <button type="button" className="exo-glass exo-store-similar" style={{ anchorName: anchor }} popoverTarget={id}
        aria-label={`Similar to ${games.map(g => g.title).join(', ')}`}>
        Similar to
        <span className="exo-store-similar-icons" aria-hidden="true">
          {games.map(g => <CoverArt key={g.id} game={g} className="exo-store-similar-icon" />)}
        </span>
      </button>
      <div ref={panel} id={id} popover="auto" className="exo-glass is-panel exo-store-like-pop" style={{ positionAnchor: anchor } as CSSProperties}
        role="dialog" aria-label="Similar to your games">
        <p className="exo-store-like-head">Similar to</p>
        <div className="exo-store-like-covers">
          {games.map(g => (
            <button key={g.id} type="button" className="exo-store-like-game" onClick={() => { panel.current?.hidePopover(); onOpenGame?.(g) }}>
              <CoverArt game={g} className="exo-store-like-cover" />
              <span className="exo-store-card-title">{g.title}</span>
            </button>
          ))}
        </div>
      </div>
    </>
  )
}
