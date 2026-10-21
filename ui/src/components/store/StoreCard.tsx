import { memo, useCallback, useEffect, useMemo, useRef, useState, type CSSProperties } from 'react'
import { ThumbsUp } from '../../brand/icons'
import type { StoreArt, StoreTile } from '../../lib/host'
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

/** Sale chip, price and (unless compact) the old price, in Steam's own regional text when it gave one; nothing when there is no price. */
export function Price({ final, initial, discount, currency, priceText, wasText, free = false, compact = false }: {
  final?: number | null
  initial?: number | null
  discount: number
  currency?: string | null
  priceText?: string | null
  wasText?: string | null
  free?: boolean
  compact?: boolean
}) {
  if (free) return <span className="exo-store-price"><span className="exo-store-now">Free</span></span>
  if (final == null || (final === 0 && !initial)) return null
  return (
    <span className="exo-store-price">
      {discount > 0 ? <span className="exo-store-save">-{discount}%</span> : null}
      <span className="exo-store-now">{priceText ?? cents(final, currency)}</span>
      {!compact && discount > 0 && initial ? <s className="exo-store-was">{wasText ?? cents(initial, currency)}</s> : null}
    </span>
  )
}

/**
 * Warms the game's page so the click opens it whole: as soon as the card is on
 * screen (in the background, a few games at a time), and at once when the
 * pointer rests on it or it takes focus.
 */
export function useWarm(appId: string, art?: StoreArt | null) {
  const timer = useRef(0)
  useEffect(() => () => window.clearTimeout(timer.current), [])
  return useMemo(() => ({
    ref: (card: HTMLElement | null) => card ? watchCard(card, appId, art) : undefined,
    onPointerEnter: () => {
      window.clearTimeout(timer.current)
      timer.current = window.setTimeout(() => void warmGame(appId, art), 60)
    },
    onPointerLeave: () => window.clearTimeout(timer.current),
    onFocus: () => void warmGame(appId, art),
  }), [appId, art])
}

/**
 * One game on a shelf: its library poster (its name on black when Steam has
 * no art), name, and one quiet line: price, or the release date, with the
 * review score. arrive is its place when the view arrives (DESIGN.md › Motion).
 */
export const StoreCard = memo(function StoreCard({ tile, owned, onOpen, arrive }: {
  tile: StoreTile
  owned: boolean
  onOpen: (tile: StoreTile) => void
  arrive?: number
}) {
  const [missing, setMissing] = useState(false)
  const onMissing = useCallback(() => setMissing(true), [])
  const warm = useWarm(tile.appId, tile.art)
  const score = reviewText(tile.review, tile.reviewPercent)
  return (
    <button
      type="button"
      className={`exo-store-card${arrive != null ? ' exo-motion-arrive' : ''}`}
      style={arrive != null ? { '--i': arrive } as CSSProperties : undefined}
      onClick={() => onOpen(tile)}
      {...warm}
      aria-label={[tile.name, owned ? 'in your library' : '', score].filter(Boolean).join(', ')}
    >
      <span className="exo-store-poster" data-vt-art>
        {missing
          ? <span className="exo-store-poster-name" aria-hidden="true">{tile.name}</span>
          : <SteamImage sources={artSources(tile, ['poster', 'header'], ['header'])} onMissing={onMissing} />}
        {owned ? <span className="exo-store-badge">In library</span> : null}
      </span>
      <span className="exo-store-card-title">{tile.name}</span>
      <span className="exo-store-meta" aria-hidden="true">
        {tile.soon
          ? <span className="exo-store-when">{tile.when ?? 'Coming soon'}</span>
          : <Price compact free={tile.free} final={tile.final} initial={tile.initial} discount={tile.discount} currency={tile.currency} priceText={tile.priceText} />}
        {tile.reviewPercent != null && tile.review ? <span className="exo-store-score"><ThumbsUp size={11} filled />{tile.reviewPercent}%</span> : null}
      </span>
    </button>
  )
})
