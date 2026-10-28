import { CopyLogs } from '../ui/ErrorNote'
import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties, type ReactNode } from 'react'
import { Bookmark, CHECK_PATH, ChevronLeft, ChevronRight, Close, Refresh, Search } from '../../brand/icons'
import { shouldReduceMotion } from '../../lib/motionPreference'
import { host, onHostEvent, type Game, type StoreCalendar, type StoreHome, type StoreTile } from '../../lib/host'
import { onPageActivityChanged, pageIsActive } from '../../lib/pageActivity'
import { report } from '../../lib/report'
import { useGlide } from '../../lib/glide'
import { GameOverlay } from '../../motion'
import { onScreen, pane, pressedArt, transition, visible } from '../../lib/viewTransition'
import { useTopOnChange } from '../../lib/topOnChange'
import { onStoreSearch, takeStoreSearch } from '../../lib/storeSearch'
import { Switch } from '../Switch'
import { Spinner } from '../ui/Spinner'
import { DictateButton } from '../ui/DictateButton'
import { SteamImage, StoreCard, useWarm } from './StoreCard'
import { CoverArt } from '../CoverArt'
import { StoreGamePage } from './StoreGamePage'
import { artSources, localDay, steamArt, steamSaleMarks, steamSaleNow, useOwned, useWishlist } from './storeData'

type Tab = 'home' | 'calendar' | 'wishlist'
/** A card at its place in the view; arrive: the view is showing its items for the first time. */
/** priced: the card shows its price (the Wishlist, where the price is the point); elsewhere it is on the game's page. */
type CardFn = (tile: StoreTile, index: number, arrive: boolean, priced?: boolean) => ReactNode

/** Views that have already arrived once this session (DESIGN.md › Motion: arrive plays the first time only). */
const arrived = new Set<string>()

function useArrive(view: string, ready: boolean): boolean {
  const [first] = useState(() => !arrived.has(view))
  useEffect(() => { if (ready) arrived.add(view) }, [view, ready])
  return first
}

function arriveStyle(i: number): CSSProperties {
  return { '--i': i } as CSSProperties
}

/**
 * The Store room, laid out like a launcher's storefront: the big games up
 * top, then the games picked for this person and Steam's charts with what
 * they play woven in. Search and a wishlist
 * sit above; a game opens its own page.
 */
/** A game's page's viewer (its trailer or screenshot, or their placeholder): where its card's art flies to and back from. */
const PAGE_ART = '.exo-game-overlay-stage .exo-sgp-stage'

const NO_GAMES: Game[] = []

/** What a hidden-unmounted Store comes back to (BootGate's roomScroll keeps the scroll). */
const remembered = { tab: 'home' as Tab, query: '', page: null as StoreTile | null, lastPage: null as StoreTile | null }

export function StoreRoom({ active, games, onOpenGame }: {
  active: boolean
  games: readonly Game[]
  /** "In your library": the game's name and its Steam app id. */
  onOpenGame: (name: string, appId?: string) => void
}) {
  const [tab, setTab] = useState<Tab>(remembered.tab)
  const [query, setQuery] = useState(remembered.query)
  const [page, setPage] = useState<StoreTile | null>(remembered.page)
  // The closing fade keeps showing the game that was open.
  const [lastPage, setLastPage] = useState<StoreTile | null>(remembered.lastPage)
  useEffect(() => { remembered.tab = tab; remembered.query = query; remembered.page = page; remembered.lastPage = lastPage },
    [tab, query, page, lastPage])
  if (page && page !== lastPage) setLastPage(page)
  // A search sent here from elsewhere (a friend's game you don't have): taken once the room is up, or at once when it is.
  useEffect(() => {
    const search = (text: string) => { setPage(null); setQuery(text) }
    const waiting = takeStoreSearch()
    if (waiting) search(waiting)
    return onStoreSearch(text => { takeStoreSearch(); search(text) })
  }, [])
  const shownPage = page ?? lastPage
  const owned = useOwned(games)
  const home = useHome()
  const wish = useWishlist()
  const hits = useSearch(query)
  const searching = query.trim().length >= 2
  const view = searching ? 'search' : tab
  const calendar = useCalendar(view === 'calendar')

  const scroller = useRef<HTMLDivElement>(null)
  useTopOnChange(scroller, `${view}|${hits.query}`)

  // The card's art flies into the page's viewer and back on close, the page coming up under it, as a game's page does
  // in Library. Flown into the whole window, the window's glass was captured on its own and stood in as a flat tint,
  // then blinked into glass as it landed. Opened from elsewhere, the page floats in.
  const artFrom = useRef<Element | null>(null)
  const openPage = useCallback((tile: StoreTile) => {
    const from = pressedArt()
    artFrom.current = from
    void transition(() => setPage(tile), { kind: 'morph', morph: { name: 'exo-store-art', from, to: () => visible(PAGE_ART) } })
  }, [])
  const closePage = () => {
    const to = artFrom.current
    void transition(() => setPage(null), { kind: 'morph', back: true, morph: { name: 'exo-store-art', from: visible(PAGE_ART), to: () => onScreen(to) ? to : null } })
  }

  // The library games a pick is like, for its Similar to band; the host names them by library id.
  // One array per set of ids while the library is the same, so a memoized card that did not change is not drawn again.
  const { byId, similarKept } = useMemo(() => ({ byId: new Map(games.map(g => [g.id, g])), similarKept: new Map<string, Game[]>() }), [games])
  const similar = useCallback((ids?: string[] | null) => {
    if (!ids?.length) return NO_GAMES
    const key = ids.join('|')
    let list = similarKept.get(key)
    if (!list) similarKept.set(key, list = ids.flatMap(id => byId.get(id) ?? []))
    return list
  }, [byId, similarKept])
  const openLibrary = useCallback((game: Game) => onOpenGame(game.title), [onOpenGame])
  // A game's wishlist mark follows the wishlist as it is now: the shelves, the banner and the calendar were read with the
  // wishlist of their moment, so a game added since showed none. Their own word stands only until the wishlist is read.
  const withWish = useCallback((tile: StoreTile): StoreTile => {
    const wished = wish.loaded ? wish.ids.has(tile.appId) : !!tile.wished
    return wished === !!tile.wished ? tile : { ...tile, wished }
  }, [wish.loaded, wish.ids])
  const card = useCallback<CardFn>((tile, index, arrive, priced) => (
    <StoreCard key={tile.appId} tile={withWish(tile)} owned={owned(tile.appId, tile.name)} similar={similar(tile.similarTo)} priced={priced} onOpen={openPage} onOpenGame={openLibrary} arrive={arrive ? index : undefined} />
  ), [owned, openPage, similar, openLibrary, withWish])

  const [openError, setOpenError] = useState<string | null>(null)
  // With nothing else to show, an error reads in the view itself, with Retry; over a page on screen, here.
  const status = viewStatus(view, { hits, home, calendar, wish, openError })
  const steamSale = steamSaleNow()

  return (
    <main className="exo-store" aria-label="Store" data-room-scroll>
      <h1 className="sr-only">Store</h1>
      <header className="exo-store-top">
        <StoreTools view={view} query={query} wished={wish.items.length} refreshing={home.refreshing}
          onTab={next => void transition(() => { setTab(next); setQuery(''); setOpenError(null) }, { kind: 'push', back: next === 'home', morph: pane(scroller.current) })} onQuery={setQuery} onRefresh={() => void home.load(true)}>
          <p className="exo-store-status" role="status">{status}</p>
          {/* Steam's sale on now or within the week, by the search. */}
          {steamSale ? <span className="exo-store-sale">{steamSale}</span> : null}
        </StoreTools>
      </header>

      <div ref={scroller} className="exo-store-scroll">
        <div key={view} className="exo-motion-switch">
          {view === 'search' ? <Results hits={hits} card={card} onClear={() => setQuery('')} />
            : view === 'wishlist' ? <Wishlist wish={wish} owned={owned} card={card} />
              : view === 'calendar' ? <Calendar cal={calendar} card={card} withWish={withWish} />
              : <Discover home={home.data} error={home.error} retrying={home.loading} onRetry={() => void home.load(false)} card={card} withWish={withWish} similar={similar} onOpen={openPage}
                  still={!!page || !active} shining={home.refreshing} />}
        </div>
      </div>

      <GameOverlay open={!!page} label={shownPage?.name ?? 'Game'} scrim={<button type="button" className="exo-game-overlay-scrim" aria-label="Close" tabIndex={-1} onClick={closePage} />}>
        {shownPage ? (
          <StoreGamePage
            key={shownPage.appId}
            seed={shownPage}
            onScreen={active}
            owned={owned(shownPage.appId, shownPage.name)}
            wished={wish.ids.has(shownPage.appId)}
            onWish={on => void wish.set(shownPage, on)}
            onOpenLibrary={() => { setPage(null); onOpenGame(shownPage.name, shownPage.appId) }}
          />
        ) : null}
      </GameOverlay>
    </main>
  )
}

/** The line in the top bar: the view on screen's error, once that view has something to show (before, the view says it itself). */
function viewStatus(view: string, s: {
  hits: ReturnType<typeof useSearch>
  home: ReturnType<typeof useHome>
  calendar: ReturnType<typeof useCalendar>
  wish: ReturnType<typeof useWishlist>
  openError: string | null
}): string | null {
  if (view === 'search') return s.hits.items ? s.hits.error : null
  if (view === 'home') return s.home.data ? s.home.error ?? s.openError : null
  if (view === 'calendar') return s.calendar.data ? s.calendar.error : null
  return s.wish.loaded ? s.wish.error : null
}

function Discover({ home, error, retrying, onRetry, card, withWish, similar, onOpen, still, shining }: {
  home: StoreHome | null
  similar: (ids?: string[] | null) => Game[]
  error: string | null
  retrying: boolean
  onRetry: () => void
  card: CardFn
  withWish: (tile: StoreTile) => StoreTile
  onOpen: (t: StoreTile) => void
  still: boolean
  /** The refresh you asked for: the banner and the shelves shine where they are until it lands (DESIGN.md › Waits). */
  shining: boolean
}) {
  const arrive = useArrive('home', !!home)
  const hero = useMemo(() => home?.hero?.map(withWish), [home, withWish])
  if (!home) return error ? <Unreachable message={error} busy={retrying} onRetry={onRetry} /> : <Skeleton />
  return (
    <>
      {hero?.length ? <Hero items={hero} similar={similar} onOpen={onOpen} still={still} shining={shining} arrive={arrive} /> : null}
      {(home.shelves ?? []).map(s => s.items.length
        ? <Row key={s.id} id={s.id} title={s.title} count={s.items.length} shining={shining}>{s.items.map((t, i) => card(t, i, arrive))}</Row>
        : null)}
    </>
  )
}

/** Nothing to show and the store did not answer: what happened, and Retry. */
function Unreachable({ message, busy, onRetry }: { message: string; busy: boolean; onRetry: () => void }) {
  return (
    <div className="exo-store-empty exo-store-error" role="alert">
      <p>{message}</p>
      <button type="button" className="exo-set-btn" disabled={busy} onClick={onRetry}>{busy ? <Spinner label="Trying again" /> : null}Retry</button>
      <CopyLogs error={message} />
    </div>
  )
}

/** "Coming Oct 23" for Steam's dates ("Oct 23", "Feb 2027", "Q1 2027", "2027"); a developer's own words ("To be announced") as they are. */
function heroWhen(when?: string | null): string {
  if (!when) return 'Coming soon'
  return /^(?:[A-Z][a-z]{2} \d{1,2}(?:, \d{4})?|[A-Z][a-z]{2} \d{4}|Q[1-4] \d{4}|\d{4})$/.test(when) ? `Coming ${when}` : when
}

/**
 * The big games: a wide banner with the game's logo, turning every eight
 * seconds (held under the pointer or keyboard focus, a game page, another
 * room or while Exo is in the background; a pick starts the eight seconds
 * again), and the list beside it. The turn is the end of the time line under
 * the pick, so the two never disagree; under Reduce motion it does not turn.
 * The next game's art loads while this one shows.
 */
function Hero({ items, similar, onOpen, still, shining, arrive }: { items: StoreTile[]; similar: (ids?: string[] | null) => Game[]; onOpen: (t: StoreTile) => void; still: boolean; shining: boolean; arrive: boolean }) {
  const [index, setIndex] = useState(0)
  const [hovered, setHovered] = useState(false)
  const [focused, setFocused] = useState(false)
  const [foreground, setForeground] = useState(pageIsActive)
  useEffect(() => onPageActivityChanged(() => setForeground(pageIsActive())), [])
  const paused = hovered || focused || still || !foreground
  const count = items.length
  const at = Math.min(index, count - 1)
  const turns = count > 1 && !shouldReduceMotion()
  useEffect(() => {
    const next = items[(at + 1) % count]
    for (const src of [artSources(next, ['hero', 'header', 'capsule'])[0]?.src, steamArt(next.appId, 'logo')]) {
      if (src) new Image().src = src
    }
  }, [at, items, count])
  const current = items[at]
  const warm = useWarm(current.appId, current.art)
  return (
    <section className="exo-store-hero" aria-label="Featured" onMouseEnter={() => setHovered(true)} onMouseLeave={() => setHovered(false)}
      onFocus={e => setFocused(e.target.matches(':focus-visible'))}
      onBlur={e => { if (!e.currentTarget.contains(e.relatedTarget)) setFocused(false) }}>
      <HeroLight tile={current} />
      <button type="button" className={`exo-store-hero-stage${shining ? ' exo-shine' : ''}`} data-vt-art onClick={() => onOpen(current)} {...warm} aria-label={`Open ${current.name}`}>
        <HeroArt tile={current} />
        <span className="exo-store-hero-copy">
          <HeroTags tile={current} like={similar(current.similarTo)[0]} />
          <HeroLogo key={current.appId} tile={current} />
          <span className="exo-store-hero-cta">See the game</span>
        </span>
      </button>
      <ol className="exo-store-hero-list">
        {items.map((t, i) => (
          <li key={t.appId} className={arrive ? 'exo-motion-arrive' : undefined} style={arrive ? arriveStyle(i) : undefined}>
            <HeroPick tile={t} on={i === at} onPick={() => i === at ? onOpen(t) : setIndex(i)} />
            {/* The featured game's time, filling until the next one (held while you point at the banner); its end turns the banner. */}
            {i === at && turns ? <span key={at} className={`exo-store-hero-time${paused ? ' is-held' : ''}`} aria-hidden="true" data-exo-timer=""
              onAnimationEnd={() => setIndex((at + 1) % count)} /> : null}
          </li>
        ))}
      </ol>
    </section>
  )
}

/**
 * The banner's art: the new game fades in over the last one once its picture has loaded, never over an empty frame,
 * and the last one goes when the fade ends, so two games never show through each other. Each layer is shown whole.
 */
function HeroArt({ tile }: { tile: StoreTile }) {
  const [layers, setLayers] = useState<StoreTile[]>([tile])
  const [loaded, setLoaded] = useState<string | null>(null)
  if (layers[layers.length - 1].appId !== tile.appId) setLayers([layers[layers.length - 1], tile])
  const show = (appId: string) => setLoaded(appId)
  return (
    <span className="exo-store-hero-arts">
      {layers.map((t, i) => (
        <span key={t.appId} className={`exo-store-hero-art${i > 0 ? (loaded === t.appId ? ' is-in' : ' is-waiting') : ''}`}
          onTransitionEnd={i > 0 ? event => { if (event.target === event.currentTarget) setLayers([t]) } : undefined}>
          <SteamImage eager sources={artSources(t, ['hero', 'header', 'capsule'])} onLoad={() => show(t.appId)} onMissing={() => show(t.appId)} />
        </span>
      ))}
    </span>
  )
}

/** Above the logo: when a coming game is out, and why it is here (the wishlist's bookmark, or the game you play it is like). */
function HeroTags({ tile, like }: { tile: StoreTile; like?: Game }) {
  if (!tile.soon && !tile.wished && !like) return null
  return (
    <span className="exo-store-hero-tags">
      {tile.soon ? <span className="exo-store-hero-tag">{heroWhen(tile.when)}</span> : null}
      {tile.wished ? <span className="exo-store-hero-tag"><Bookmark size={12} filled />On your wishlist</span> : null}
      {!tile.wished && like ? <span className="exo-store-hero-tag">Similar to<CoverArt game={like} className="exo-store-similar-icon" />{like.title}</span> : null}
    </span>
  )
}

/**
 * The featured game's light on the black around the banner (DESIGN.md › Immersive: art gets the light), as Continue's
 * art lights the Library: its own picture, far out of focus and dim, behind the banner and the list. Still while it
 * shows (the blur never animates); a new game's light fades in over the last one.
 */
function HeroLight({ tile }: { tile: StoreTile }) {
  const src = artSources(tile, ['hero', 'header', 'capsule'])[0]?.src
  return src ? <span className="exo-store-hero-light" aria-hidden="true"><img key={tile.appId} src={src} alt="" decoding="async" draggable={false} /></span> : null
}

function HeroPick({ tile, on, onPick }: { tile: StoreTile; on: boolean; onPick: () => void }) {
  // The list's games warm when pointed at, not all at once for being on screen: the banner's own game already has.
  const warm = useWarm(tile.appId, tile.art, false)
  return (
    <button type="button" className={`exo-store-hero-pick${on ? ' is-on' : ''}`} onClick={onPick} {...warm} aria-current={on}>
      <span className="exo-store-hero-pick-art" data-vt-art><SteamImage eager sources={artSources(tile, ['capsule', 'header'])} /></span>
      <span className="exo-store-hero-pick-name">{tile.name}</span>
    </button>
  )
}

/** The game's logo over the banner; its name only when Steam has no logo. Keyed by game, so a failure never carries over. */
function HeroLogo({ tile }: { tile: StoreTile }) {
  const [failed, setFailed] = useState(false)
  return failed
    ? <span className="exo-glass exo-store-hero-name">{tile.name}</span>
    : <img className="exo-store-hero-logo exo-motion-switch" src={steamArt(tile.appId, 'logo')} alt="" onError={() => setFailed(true)} />
}

/** A shelf: one horizontal row, scrolled by its arrows (a page at a time) or the wheel. Arrows change only when an edge is reached or left. */
function Row({ id, title, count, shining, children }: { id: string; title: string; count: number; shining: boolean; children: ReactNode }) {
  const track = useRef<HTMLDivElement>(null)
  const [edges, setEdges] = useState({ start: true, end: false })
  const measure = useCallback(() => {
    const el = track.current
    if (!el) return
    const start = el.scrollLeft < 4
    const end = el.scrollLeft + el.clientWidth >= el.scrollWidth - 4
    setEdges(e => e.start === start && e.end === end ? e : { start, end })
  }, [])
  useEffect(() => {
    measure()
    const el = track.current
    if (!el) return
    const observer = new ResizeObserver(measure)
    observer.observe(el)
    return () => observer.disconnect()
  }, [measure, count])
  const page = (dir: 1 | -1) => track.current?.scrollBy({ left: dir * track.current.clientWidth * 0.9, behavior: shouldReduceMotion() ? 'auto' : 'smooth' })
  return (
    <section className={`exo-store-row${shining ? ' exo-shine' : ''}`} aria-labelledby={`exo-store-${id}`}>
      <div className="exo-shelf-head">
        <h2 id={`exo-store-${id}`} className="exo-shelf-title">{title}</h2>
        <span className="exo-store-arrows">
          <button type="button" className="exo-set-btn" aria-label={`Back in ${title}`} disabled={edges.start} onClick={() => page(-1)}><ChevronLeft size={14} /></button>
          <button type="button" className="exo-set-btn" aria-label={`More ${title}`} disabled={edges.end} onClick={() => page(1)}><ChevronRight size={14} /></button>
        </span>
      </div>
      <div ref={track} className="exo-store-track" onScroll={measure}>{children}</div>
    </section>
  )
}

/** The last answer stays up, shining, while a newer query is on its way (DESIGN.md › Waits). */
function Results({ hits, card, onClear }: { hits: ReturnType<typeof useSearch>; card: CardFn; onClear: () => void }) {
  if (!hits.items) return hits.error ? <Unreachable message={hits.error} busy={false} onRetry={hits.retry} /> : <GridSkeleton label="Searching the store" />
  if (hits.items.length === 0) return (
    <div className={`exo-store-empty${hits.pending ? ' exo-shine' : ''}`}>
      <p>Nothing on the store matches “{hits.query}”</p>
      <button type="button" className="exo-set-btn" onClick={onClear}>Clear search</button>
    </div>
  )
  return <div className={`exo-store-grid${hits.pending ? ' exo-shine' : ''}`}>{hits.items.map((t, i) => card(t, i, true))}</div>
}

/** On sale now leaves out games the library already owns; they stay with the rest. Empty only once the host has said so. */
function Wishlist({ wish, owned, card }: {
  wish: ReturnType<typeof useWishlist>
  owned: (appId: string, title: string) => boolean
  card: CardFn
}) {
  const { items, check: onCheck, checking, checked } = wish
  const arrive = useArrive('wishlist', wish.loaded)
  if (!wish.loaded) return wish.error ? <Unreachable message={wish.error} busy={false} onRetry={wish.reload} /> : <GridSkeleton label="Loading your wishlist" />
  if (items.length === 0) return <div className="exo-store-empty"><p>Your wishlist is empty</p></div>
  const sale = items.filter(w => w.discount > 0 && !owned(w.appId, w.name))
  const rest = items.filter(w => !sale.includes(w))
  return (
    <>
      <div className="exo-shelf-head">
        {/* The Wishlist chip already names the list and its count; a heading only when it splits by sale. */}
        {sale.length ? <h2 className="exo-shelf-title">On sale now<span className="exo-shelf-count">{sale.length}</span></h2> : <span />}
        <button type="button" className="exo-set-btn" disabled={checking} onClick={onCheck} aria-label="Check prices">
          {checking ? <Spinner label="Checking prices" /> : null}
          <span className="exo-store-stack">
            <span className={checked && !checking ? 'is-hidden' : undefined}>Check prices</span>
            {checked && !checking ? <svg className="exo-motion-check exo-store-check" viewBox="0 0 24 24" aria-hidden="true"><path d={CHECK_PATH} pathLength="1" /></svg> : null}
          </span>
        </button>
      </div>
      <div className="exo-store-grid">{(sale.length ? sale : items).map((t, i) => card(t, i, arrive, true))}</div>
      {sale.length && rest.length ? (
        <>
          <div className="exo-shelf-head"><h2 className="exo-shelf-title">Not on sale<span className="exo-shelf-count">{rest.length}</span></h2></div>
          <div className="exo-store-grid">{rest.map((t, i) => card(t, sale.length + i, arrive, true))}</div>
        </>
      ) : null}
    </>
  )
}

/** A calendar day as a person says it: Today, Tomorrow, Yesterday, else the weekday and date. */
function dayLabel(iso: string): string {
  const [year, month, date] = iso.split('-').map(Number)
  const day = new Date(year, month - 1, date)
  const today = new Date()
  today.setHours(0, 0, 0, 0)
  const away = Math.round((day.getTime() - today.getTime()) / 86_400_000)
  if (away === 0) return 'Today'
  if (away === 1) return 'Tomorrow'
  if (away === -1) return 'Yesterday'
  return day.toLocaleDateString(undefined, { weekday: 'long', month: 'short', day: 'numeric' })
}

/**
 * The calendar: what came out in the last two weeks and what is dated in the next six, a day at a time, the wishlist
 * first and then games like the ones played most. The filters are for this view only and do nothing to the lists.
 */
function Calendar({ cal, card, withWish }: { cal: ReturnType<typeof useCalendar>; card: CardFn; withWish: (tile: StoreTile) => StoreTile }) {
  const [hideOwned, setHideOwned] = useState(true)
  const [wishOnly, setWishOnly] = useState(false)
  const [hideEarly, setHideEarly] = useState(false)
  const arrive = useArrive('calendar', !!cal.data)
  if (!cal.data) return cal.error ? <Unreachable message={cal.error} busy={cal.loading} onRetry={() => void cal.load()} /> : <GridSkeleton label="Loading the calendar" />
  const shown = (cal.data.days ?? [])
    .map(d => ({ ...d, entries: d.entries.filter(e => !(hideOwned && e.owned) && !(wishOnly && !withWish({ ...e.tile, wished: e.wished }).wished) && !(hideEarly && e.earlyAccess)) }))
    .filter(d => d.entries.length > 0)
  // Coming up first, from today; then what came out lately, the newest first.
  const now = new Date()
  const today = localDay(now)
  // Steam's sales on their first and last day ahead, as far as the calendar reaches (a day of their own when no game
  // falls on it); not over a calendar the filters emptied, which says so instead.
  const marks = shown.length ? steamSaleMarks(today, localDay(new Date(now.getFullYear(), now.getMonth(), now.getDate() + 42))) : new Map<string, string[]>()
  const markedOnly = [...marks.keys()].filter(date => !shown.some(d => d.date === date)).map(date => ({ date, entries: [] }))
  const ahead: typeof shown = [...shown.filter(d => d.date >= today), ...markedOnly].sort((a, b) => a.date.localeCompare(b.date))
  const past = shown.filter(d => d.date < today).reverse()
  let before = 0
  const place = (list: typeof shown) => list.map(d => { const start = before; before += d.entries.length; return { ...d, start } })
  const groups = [{ id: 'ahead', title: 'Coming up', days: place(ahead) }, { id: 'past', title: 'Out recently', days: place(past) }].filter(g => g.days.length)
  return (
    <>
      <div className="exo-cal-filters" role="group" aria-label="Calendar filters">
        <span className="exo-cal-filter"><span id="exo-cal-owned">Hide games I own</span><Switch checked={hideOwned} labelledBy="exo-cal-owned" onChange={setHideOwned} /></span>
        <span className="exo-cal-filter"><span id="exo-cal-wish">Wishlist only</span><Switch checked={wishOnly} labelledBy="exo-cal-wish" onChange={setWishOnly} /></span>
        <span className="exo-cal-filter"><span id="exo-cal-early">Hide Early Access</span><Switch checked={hideEarly} labelledBy="exo-cal-early" onChange={setHideEarly} /></span>
      </div>
      {groups.length === 0 ? (
        <div className="exo-store-empty">
          <p>{(cal.data.days ?? []).length ? 'Nothing matches these filters' : 'No dated releases to show right now'}</p>
          {(cal.data.days ?? []).length ? <button type="button" className="exo-set-btn" onClick={() => { setHideOwned(false); setWishOnly(false); setHideEarly(false) }}>Show everything</button> : null}
        </div>
      ) : groups.map(group => (
        <section key={group.id} aria-labelledby={`exo-cal-${group.id}`}>
          <div className="exo-shelf-head"><h2 id={`exo-cal-${group.id}`} className="exo-shelf-title">{group.title}</h2></div>
          {/* Days flow side by side, each as wide as its games, so a day with one game never takes a whole row. */}
          <div className="exo-cal-days">
            {group.days.map(day => (
              <section key={day.date} className="exo-cal-day" aria-labelledby={`exo-cal-${day.date}`}>
                <h3 id={`exo-cal-${day.date}`} className="exo-cal-date">{dayLabel(day.date)}</h3>
                {marks.get(day.date)?.map(m => <p key={m} className="exo-cal-mark">{m}</p>)}
                {day.entries.length ? (
                  <div className="exo-cal-games">
                    {day.entries.map((e, i) => (
                      <div key={e.tile.appId} className="exo-cal-item">
                        {/* The day says when; the card needs no date of its own. */}
                        {card({ ...e.tile, wished: e.wished, soon: false }, Math.min(day.start + i, 24), arrive)}
                        {e.earlyAccess ? <p className="exo-cal-why">Early Access</p> : null}
                      </div>
                    ))}
                  </div>
                ) : null}
              </section>
            ))}
          </div>
        </section>
      ))}
    </>
  )
}

/**
 * The calendar from the host, asked for each time the tab shows (the host keeps Steam's lists half an hour, so this is
 * cheap and a wishlist change reorders it); the last answer stays up while a newer one is on its way, and a slow answer
 * that a newer one replaced is dropped.
 */
function useCalendar(active: boolean) {
  const [data, setData] = useState<StoreCalendar | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const latest = useRef(0)
  const load = useCallback(async () => {
    const mine = ++latest.current
    setLoading(true)
    try {
      const res = await host.storeCalendar()
      if (mine !== latest.current) return
      if (res.ok) { setData(res); setError(null) } else setError(res.message ?? 'The store could not be reached.')
    } catch (e) {
      report(e, 'Store.calendar')
      if (mine === latest.current) setError('The store could not be reached. Check your connection and try again.')
    } finally {
      if (mine === latest.current) setLoading(false)
    }
  }, [])
  useEffect(() => { if (active) void load() }, [active, load])
  useEffect(() => onHostEvent('store.changed', e => { if (active && e?.what === 'wishlist') void load() }), [active, load])
  return { data, error, loading, load }
}

/** A poster on its way: the card's own shape, its art and its name, as clear skeleton cards that shine. */
function GhostCard() {
  return <span className="exo-store-card is-skeleton"><span className="exo-store-ghost exo-skeleton exo-shine" /><span className="exo-store-ghost-name exo-skeleton exo-shine" /></span>
}

/** The home page on its way (DESIGN.md › Waits): the banner, the list beside it and two shelves as skeleton cards, shining. */
function Skeleton() {
  return (
    <div role="status" aria-label="Loading the store">
      <div className="exo-store-hero is-skeleton" aria-hidden="true">
        <span className="exo-store-ghost-hero exo-skeleton exo-shine" />
        <span className="exo-store-hero-list">
          {Array.from({ length: 6 }, (_, i) => <span key={i} className="exo-store-ghost-pick exo-skeleton exo-shine" />)}
        </span>
      </div>
      {[0, 1].map(row => (
        <div key={row} className="exo-store-row" aria-hidden="true">
          <div className="exo-shelf-head"><span className="exo-store-ghost-title exo-skeleton exo-shine" /></div>
          <div className="exo-store-track">{Array.from({ length: 10 }, (_, i) => <GhostCard key={i} />)}</div>
        </div>
      ))}
    </div>
  )
}

/** A grid's first answer on its way: its posters as skeleton cards, shining, no banner. */
function GridSkeleton({ label }: { label: string }) {
  return (
    <div className="exo-store-grid" role="status" aria-label={label}>
      {Array.from({ length: 12 }, (_, i) => <GhostCard key={i} />)}
    </div>
  )
}

/** The last home page shown, kept while Exo runs: the room built again (after it sat unused) shows it at once. */
let keptHome: StoreHome | null = null

/**
 * The last home page at once, then whatever the host refreshes, quietly behind it (stale while it revalidates); read
 * each time the room is built (the host answers from memory) and when the host says the home page changed.
 * loading counts every read in flight; refreshing is only the one the user asked for, which the room shows.
 */
function useHome() {
  const [data, setData] = useState<StoreHome | null>(keptHome)
  const [error, setError] = useState<string | null>(null)
  const [inFlight, setInFlight] = useState(0)
  const [refreshing, setRefreshing] = useState(false)
  const load = useCallback(async (refresh: boolean) => {
    setInFlight(n => n + 1)
    if (refresh) setRefreshing(true)
    try {
      const res = await host.storeHome(refresh)
      // The same page again (the room showing) keeps the one on screen, so its cards do not render again.
      if (res.ok) {
        setData(d => d && d.at === res.at ? d : res)
        if (keptHome?.at !== res.at) keptHome = res
        setError(null)
      } else setError(res.message ?? 'The store could not be reached.')
    } catch (e) {
      report(e, 'Store.home')
      setError('The store could not be reached. Check your connection and try again.')
    } finally {
      setInFlight(n => n - 1)
      if (refresh) setRefreshing(false)
    }
  }, [])
  useEffect(() => { void load(false) }, [load])
  useEffect(() => onHostEvent('store.changed', e => { if (e?.what === 'home') void load(false) }), [load])
  return { data, error, loading: inFlight > 0, refreshing, load }
}

/**
 * Search as you type, a quarter second after the last key; the newest query
 * wins (the host cancels the typed one before it, and nothing else: a friend's
 * game looked up meanwhile never ends it), and the last answer stays up until
 * it lands. A new query clears the last one's error.
 */
function useSearch(query: string) {
  const [result, setResult] = useState<{ items: StoreTile[]; query: string } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [attempt, setAttempt] = useState(0)
  useEffect(() => {
    const q = query.trim()
    setError(null)
    if (q.length < 2) { setResult(null); return }
    let live = true
    const timer = setTimeout(() => {
      host.storeSearch(q, true)
        .then(res => {
          // A newer query replaced this one: live is false and the answer is dropped.
          if (!live) return
          if (res.ok) setResult({ items: res.items, query: q })
          else setError(res.message ?? 'Search did not work.')
        })
        .catch((e: unknown) => { report(e, 'Store.search'); if (live) setError('Search could not reach the store. Try again.') })
    }, 250)
    return () => { live = false; clearTimeout(timer) }
  }, [query, attempt])
  const retry = useCallback(() => setAttempt(n => n + 1), [])
  const q = query.trim()
  return { items: result?.items ?? null, query: result?.query ?? '', error, retry, pending: q.length >= 2 && !error && result?.query !== q }
}

/** The Store's own controls, at the top of the room: Discover or Wishlist, search, and refresh. */
function StoreTools({ view, query, wished, refreshing, onTab, onQuery, onRefresh, children }: {
  children: ReactNode
  view: string
  query: string
  wished: number
  /** The refresh you asked for: its glyph turns until it lands. */
  refreshing: boolean
  onTab: (tab: Tab) => void
  onQuery: (query: string) => void
  onRefresh: () => void
}) {
  const chipGlide = useGlide<HTMLDivElement>()
  const searchField = useRef<HTMLInputElement>(null)
  return (
    <>
      <div ref={chipGlide} className="exo-shelf-chips exo-glide" role="group" aria-label="Store view">
        <button type="button" className="exo-shelf-chip" aria-pressed={view === 'home'} onClick={() => onTab('home')}>Discover</button>
        <button type="button" className="exo-shelf-chip" aria-pressed={view === 'calendar'} onClick={() => onTab('calendar')}>Calendar</button>
        <button type="button" className="exo-shelf-chip" aria-pressed={view === 'wishlist'} onClick={() => onTab('wishlist')}>
          Wishlist<span className="exo-store-chip-count">{wished || ''}</span>
        </button>
      </div>
      {children}
      <label className="exo-store-search">
        <Search size={15} />
        <input ref={searchField} type="search" placeholder="Search the store" value={query} onChange={e => onQuery(e.target.value)} aria-label="Search the store" />
        <button type="button" aria-label="Clear search" tabIndex={query ? 0 : -1} className={query ? undefined : 'is-hidden'} onClick={() => onQuery('')}><Close size={13} /></button>
        <DictateButton target={searchField} />
      </label>
      <button
        type="button"
        className={`exo-set-btn exo-store-refresh${view === 'home' ? '' : ' is-hidden'}`}
        disabled={refreshing || view !== 'home'}
        onClick={onRefresh}
        aria-label={refreshing ? 'Refreshing the store' : 'Refresh the store'}
        aria-busy={refreshing}
      >
        <Refresh size={14} className={refreshing ? 'exo-spin' : undefined} />
      </button>
    </>
  )
}
