import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore } from 'react'
import { host, onHostEvent, type Game, type StoreArt, type StoreGame, type StoreOffer, type StoreTile, type WishItem } from '../../lib/host'
import { report } from '../../lib/report'
import { titleIdentity } from '../../lib/utils'

const moneyFormats = new Map<string, Intl.NumberFormat>()

/** Cents in the price's currency, Steam's for the Windows region ("$14.99", "14,99 €"); 0 is Free. Untagged prices are US dollars. */
export function cents(value: number | null | undefined, currency?: string | null): string {
  if (value == null) return ''
  if (value === 0) return 'Free'
  const code = currency || 'USD'
  let format = moneyFormats.get(code)
  if (!format) {
    try { format = new Intl.NumberFormat(undefined, { style: 'currency', currency: code }) } catch { format = new Intl.NumberFormat(undefined, { style: 'currency', currency: 'USD' }) }
    moneyFormats.set(code, format)
  }
  return format.format(value / 100)
}

type SteamArt = 'poster' | 'hero' | 'logo' | 'header' | 'capsule'

const ART_FILES: Record<SteamArt, string> = {
  poster: 'library_600x900.jpg',
  hero: 'library_hero.jpg',
  logo: 'logo.png',
  header: 'header.jpg',
  capsule: 'capsule_616x353.jpg',
}

/** Steam's store art for an app by its classic name: the library poster, the hero banner, the logo, the header or the capsule. */
export function steamArt(appId: string, kind: SteamArt): string {
  return `https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/${appId}/${ART_FILES[kind]}`
}

/**
 * Sources for SteamImage, in order: each kind's current path from Steam,
 * then its classic name (older games, or when Steam's list was unreachable).
 * Kinds in wide show whole inside a poster frame.
 */
export function artSources(game: { appId: string; art?: StoreArt | null }, kinds: Exclude<SteamArt, 'logo'>[], wide: SteamArt[] = []) {
  const out: { src: string; wide?: boolean }[] = []
  for (const kind of kinds) {
    for (const src of [game.art?.[kind], steamArt(game.appId, kind)]) {
      if (src && !out.some(o => o.src === src)) out.push(wide.includes(kind) ? { src, wide: true } : { src })
    }
  }
  return out
}

/** GG.deals compares official stores and key shops; its search lands on the game, for people who buy keys. */
export function ggDealsUrl(title: string): string {
  const clean = title.replace(/[™®©]/g, '').replace(/\s+/g, ' ').trim()
  return `https://gg.deals/search/?title=${encodeURIComponent(clean)}`
}

/** "In your library": the Steam app the library owns, or the same title owned on any store. */
export function useOwned(games: readonly Game[]) {
  return useMemo(() => {
    const steam = new Set<string>()
    const titles = new Set<string>()
    for (const g of games) {
      if (!g.owned && !g.installed) continue
      const m = /^steam:(\d+)$/.exec(g.id)
      if (m) steam.add(m[1])
      titles.add(titleIdentity(g.title))
    }
    return (appId: string, title: string) => steam.has(appId) || titles.has(titleIdentity(title))
  }, [games])
}

/** "Very Positive · 92%": Steam's review summary, empty when the game has none. */
export function reviewText(review?: string | null, percent?: number | null): string {
  if (!review) return ''
  return percent != null ? `${review} · ${percent}%` : review
}

// A game's page, asked for once: a card on screen warms it, the click then draws the same answer at once.
const GAME_KEEP_MS = 10 * 60_000
/** Each entry holds a whole game's page; a long browse is capped or it would hold them all. Oldest-inserted go first. */
const KEEP_MAX_ENTRIES = 96
type Kept<T> = { at: number; answer: Promise<T>; value?: T }
const games = new Map<string, Kept<StoreGame>>()
const offers = new Map<string, Kept<{ ok: boolean; offers: StoreOffer[] }>>()

/** One answer per game, shared with any warm-up still on its way; a failed answer is never kept. */
function keep<T>(cache: Map<string, Kept<T>>, appId: string, fresh: boolean, ask: () => Promise<T>, ok: (value: T) => boolean): Promise<T> {
  const known = cache.get(appId)
  if (known && !fresh && Date.now() - known.at < GAME_KEEP_MS) return known.answer
  if (!cache.has(appId) && cache.size >= KEEP_MAX_ENTRIES) {
    const oldest = cache.keys().next().value
    if (oldest !== undefined) cache.delete(oldest)
  }
  const entry: Kept<T> = { at: Date.now(), answer: ask() }
  cache.set(appId, entry)
  // Only this entry is dropped: a newer one may have replaced it under the same key while this answer was on its way.
  const drop = () => { if (cache.get(appId) === entry) cache.delete(appId) }
  entry.answer.then(value => { if (ok(value)) entry.value = value; else drop() }, drop)
  return entry.answer
}

const peek = <T,>(cache: Map<string, Kept<T>>, appId: string) => {
  const known = cache.get(appId)
  return known?.value !== undefined && Date.now() - known.at < GAME_KEEP_MS ? known.value : null
}

/** Steam's page for the game. */
export const loadGame = (appId: string, fresh = false) => keep(games, appId, fresh, () => host.storeGame(appId), g => g.ok)
/** Steam's page if it already arrived, so the game page draws whole from its first frame. */
export const peekGame = (appId: string) => peek(games, appId)
/** The other official launchers' offers; kept only when CheapShark answered, so a slow answer is asked again next time. */
export const loadOffers = (appId: string) => keep(offers, appId, false, () => host.storeGameOffers(appId), res => res.ok).then(res => (res.ok ? res.offers ?? [] : null))
export const peekOffers = (appId: string) => peek(offers, appId)?.offers ?? null

const images = new Set<string>()
/** Only what a duplicate preload test needs; capped so a long browse does not grow it. */
const IMAGES_MAX_ENTRIES = 2048
function preloadImage(src?: string | null) {
  if (!src || images.has(src)) return
  if (images.size >= IMAGES_MAX_ENTRIES) {
    const oldest = images.keys().next().value
    if (oldest !== undefined) images.delete(oldest)
  }
  images.add(src)
  const image = new Image()
  image.decoding = 'async'
  image.src = src
}

/** Everything a game's page shows first: Steam's page, the hero, the logo and the first picture. The other launchers' prices
 * are asked for only when the page opens (CheapShark limits how often it is asked; pointing at cards must not use that up). */
export function warmGame(appId: string, art?: StoreArt | null): Promise<unknown> {
  preloadImage(artSources({ appId, art }, ['hero', 'header', 'capsule'])[0]?.src)
  preloadImage(steamArt(appId, 'logo'))
  return Promise.allSettled([
    loadGame(appId).then(g => { if (g.ok) preloadImage(g.media[0]?.thumb ?? g.screenshots[0]) }),
  ])
}

// Cards warm once they have stayed on screen a moment (most of the card, half a second: a shelf scrolled past or a
// room still being built warms nothing), three games at a time, each once. All at once, the thirty cards of the home
// page's first screen kept the host busy for seconds, and a room opened the first time waits for its host to go quiet.
// Capped like the page cache it feeds: past it a card may warm again, which the cache still shares.
const WARMED_MAX_ENTRIES = 1024
const DWELL_MS = 500
const warmed = new Set<string>()
const waiting: (() => Promise<unknown>)[] = []
const dwelling = new Map<HTMLElement, number>()
let warming = 0
function pump() {
  while (warming < 3 && waiting.length) {
    const next = waiting.shift()!
    warming++
    void next().finally(() => { warming--; pump() })
  }
}
function settle(card: HTMLElement) {
  window.clearTimeout(dwelling.get(card))
  dwelling.delete(card)
}
const seen = typeof IntersectionObserver === 'undefined' ? null : new IntersectionObserver(entries => {
  for (const entry of entries) {
    const card = entry.target as HTMLElement
    if (!entry.isIntersecting) { settle(card); continue }
    if (dwelling.has(card)) continue
    dwelling.set(card, window.setTimeout(() => {
      settle(card)
      seen?.unobserve(card)
      const appId = card.dataset.warm
      if (!appId || warmed.has(appId) || !card.isConnected) return
      if (warmed.size >= WARMED_MAX_ENTRIES) {
        const oldest = warmed.keys().next().value
        if (oldest !== undefined) warmed.delete(oldest)
      }
      warmed.add(appId)
      const art = cardArt.get(card)
      waiting.push(() => warmGame(appId, art))
      pump()
    }, DWELL_MS))
  }
}, { threshold: 0.6 })
const cardArt = new WeakMap<HTMLElement, StoreArt | null | undefined>()

/** Watches a card: once it has stayed on screen a moment, its game page is warmed in the background. */
export function watchCard(card: HTMLElement, appId: string, art?: StoreArt | null) {
  if (!seen || warmed.has(appId)) return () => {}
  card.dataset.warm = appId
  cardArt.set(card, art)
  seen.observe(card)
  return () => { settle(card); seen.unobserve(card) }
}

// The wishlist, read once for everyone who shows it: the Store room and the Store tab's sale count.
// loaded: the host has answered with the list once (until then it is not known to be empty).
type WishState = { items: WishItem[]; loaded: boolean; error: string | null }
let wish: WishState = { items: [], loaded: false, error: null }
let wishKey = ''
const wishListeners = new Set<() => void>()
let offWishChanged: (() => void) | null = null
const unreadable = 'Your wishlist could not be read. Try again.'

/**
 * The list the host answered (null keeps the one on screen) and what went wrong, if anything.
 * A new list only when something on it changed, so cards that show it render again only then.
 */
function setWish(items: WishItem[] | null, error: string | null) {
  const key = items ? JSON.stringify(items) : wishKey
  const next = { items: key === wishKey ? wish.items : items ?? wish.items, loaded: wish.loaded || items !== null, error }
  if (next.items === wish.items && next.loaded === wish.loaded && next.error === wish.error) return
  wishKey = key
  wish = next
  for (const listener of wishListeners) listener()
}

function readWishlist() {
  host.storeWishlist('get')
    .then(res => setWish(res.message ? null : res.items, res.message ?? null))
    .catch((e: unknown) => { report(e, 'Store.wishlist'); setWish(null, unreadable) })
}

function subscribeWishlist(listener: () => void) {
  wishListeners.add(listener)
  if (!offWishChanged) {
    offWishChanged = onHostEvent('store.changed', e => { if (e?.what === 'wishlist') readWishlist() })
    readWishlist()
  }
  return () => {
    wishListeners.delete(listener)
    if (wishListeners.size === 0) { offWishChanged?.(); offWishChanged = null }
  }
}

const wishSnapshot = () => wish

/**
 * The wishlist: add or remove a game (shown at once, and put back with the
 * reason when the host could not save it), check its prices now, read it again.
 */
export function useWishlist() {
  const { items, loaded, error } = useSyncExternalStore(subscribeWishlist, wishSnapshot)
  const [checking, setChecking] = useState(false)
  // Answers come back in any order: only the newest change's list is shown, so a quick second toggle isn't undone by the first one's answer.
  const newest = useRef(0)
  const [checked, flashChecked] = useDone()
  const set = useCallback(async (tile: StoreTile, on: boolean) => {
    const mine = ++newest.current
    const before = wish.items
    setWish(on
      ? [{ ...tile, addedAt: new Date().toISOString() }, ...before.filter(w => w.appId !== tile.appId)]
      : before.filter(w => w.appId !== tile.appId), null)
    // A failed change puts back this game alone: another toggle made while it was on its way stays.
    const undo = (message: string) => {
      const was = before.find(w => w.appId === tile.appId)
      const rest = wish.items.filter(w => w.appId !== tile.appId)
      const at = was ? before.indexOf(was) : 0
      setWish(was ? [...rest.slice(0, at), was, ...rest.slice(at)] : rest, message)
    }
    try {
      const res = await host.storeWishlist(on ? 'add' : 'remove', { appId: tile.appId, name: tile.name })
      if (res.message) undo(res.message)
      else if (mine === newest.current) setWish(res.items, null)
    } catch (e) {
      report(e, 'Store.wish')
      undo('The wishlist could not be changed. Try again.')
    }
  }, [])
  const check = useCallback(() => {
    const failed = 'Prices could not be checked. Try again.'
    setChecking(true)
    host.storeWishlist('check')
      .then(res => {
        if (res.message) { setWish(null, res.message); return }
        setWish(res.items, res.ok ? null : failed)
        if (res.ok) flashChecked()
      })
      .catch((e: unknown) => { report(e, 'Store.check'); setWish(null, failed) })
      .finally(() => setChecking(false))
  }, [flashChecked])
  const ids = useMemo(() => new Set(items.map(w => w.appId)), [items])
  return { items, loaded, ids, set, check, checking, checked, error, reload: readWishlist }
}

/** Wishlisted games on sale that the library does not own: the wishlist's On sale now, and a count for the Store tab. */
export function useWishlistSaleCount(games: readonly Game[]): number {
  const { items } = useSyncExternalStore(subscribeWishlist, wishSnapshot)
  const owned = useOwned(games)
  return useMemo(() => items.filter(w => w.discount > 0 && !owned(w.appId, w.name)).length, [items, owned])
}

/** Done (DESIGN.md › Motion), short: true for 1.6 s after flash(), while the check draws on the control that did it. */
export function useDone(): [boolean, () => void] {
  const [done, setDone] = useState(false)
  const timer = useRef(0)
  useEffect(() => () => window.clearTimeout(timer.current), [])
  const flash = useCallback(() => {
    window.clearTimeout(timer.current)
    setDone(true)
    timer.current = window.setTimeout(() => setDone(false), 1600)
  }, [])
  return [done, flash]
}
