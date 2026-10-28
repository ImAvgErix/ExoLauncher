import { useCallback, useEffect, useState } from 'react'
import { Bookmark, Check, CHECK_PATH, Close, ExternalLink, Library, Play } from '../../brand/icons'
import { host, type StoreGame, type StoreOffer, type StoreOtherOffers, type StoreRecentReviews, type StoreTile } from '../../lib/host'
import { report } from '../../lib/report'
import { Spinner } from '../ui/Spinner'
import { Price } from './StoreCard'
import { ggDealsUrl, loadGame, loadOffers, loadReviews, peekGame, peekOffers, peekReviews, reviewText, steamArt, useDone } from './storeData'
import { TrailerPlayer } from './TrailerPlayer'
import { CopyLogs, ErrorNote } from '../ui/ErrorNote'

/** GG.deals: the browser. */
function open(url: string): Promise<unknown> {
  return host.openUrl(url).catch((e: unknown) => report(e, 'Store.open'))
}

/** Steam's row from the card alone: its link, and the price the shelf already showed. */
function seedOffer(seed: StoreTile): StoreOffer {
  return { store: '1', name: 'Steam', url: `https://store.steampowered.com/app/${seed.appId}`, final: seed.final, initial: seed.initial,
    discount: seed.discount, currency: seed.currency, priceText: seed.priceText, wasText: seed.wasText }
}

type Shown = { kind: 'trailer'; index: number } | { kind: 'shot'; index: number }

/**
 * A game's page in the Store: a viewer for its trailers and screenshots with Details under it; beside it its logo and
 * tags, Get on each official launcher that sells it, Wishlist and GG.deals, About and the system requirements against
 * this PC, on the window's own glass with no art behind it (DESIGN.md › Store). One fixed layout for every game: each block has its place and
 * its size from the first frame (placeholders until the details land), so nothing moves when they do; what arrives
 * fades in where it belongs. Steam's part comes first; the other launchers fill in when they answer.
 */
export function StoreGamePage({ seed, onScreen, owned, wished, onWish, onOpenLibrary }: {
  seed: StoreTile
  /** The Store room is on screen: a trailer plays only then. */
  onScreen: boolean
  owned: boolean
  wished: boolean
  onWish: (on: boolean) => void
  onOpenLibrary: () => void
}) {
  const game = useStoreGame(seed.appId)
  const { offers: others } = useOtherOffers(seed.appId)
  const recent = useRecentReviews(seed.appId)
  const name = game.data?.name ?? seed.name
  const data = game.data
  return (
    <section className="exo-sgp" aria-labelledby="exo-sgp-title" tabIndex={-1} data-autofocus>
      <div className="exo-sgp-body">
        <div className="exo-sgp-left">
          <Media game={game} onScreen={onScreen} />
          <Facts game={data} recent={recent} />
        </div>
        <div className="exo-sgp-side">
          <div className="exo-sgp-head">
            <HeroLogo key={seed.appId} appId={seed.appId} name={name} />
            <h2 id="exo-sgp-title" className="sr-only">{name}</h2>
            <Pills tags={data?.tags ?? null} />
          </div>
          <BuyPanel game={data} loading={!data && !game.error} others={others} seed={seed} owned={owned} wished={wished} onWish={onWish} onOpenLibrary={onOpenLibrary} />
          <About text={data ? data.description ?? '' : null} />
          <Specs specs={data ? data.specs ?? [] : null} fit={data?.fit ?? null} />
        </div>
      </div>
    </section>
  )
}

/** The media's place: the viewer once the details land, its frames shining until then, or why they did not with Retry. */
function Media({ game, onScreen }: { game: ReturnType<typeof useStoreGame>; onScreen: boolean }) {
  return (
    <div className="exo-sgp-main">
      {game.data ? <MediaViewer game={game.data} onScreen={onScreen} /> : null}
      {game.error ? (
        <div className="exo-store-error" role="alert">
          <p>{game.error}</p>
          <button type="button" className="exo-set-btn" onClick={game.retry}>Retry</button>
          <CopyLogs error={game.error} />
        </div>
      ) : null}
      {!game.data && !game.error ? <MediaSkeleton /> : null}
    </div>
  )
}

/**
 * The game's logo; its name only when Steam has no logo. Keyed by game, so it never flashes the name first.
 * Its slot is one fixed height whatever the logo's shape, so the tags under it sit in the same place on every page.
 */
function HeroLogo({ appId, name }: { appId: string; name: string }) {
  const [failed, setFailed] = useState(false)
  return (
    <div className="exo-sgp-mark">
      {failed
        ? <p className="exo-sgp-name" aria-hidden="true">{name}</p>
        : <img className="exo-sgp-logo exo-motion-switch" src={steamArt(appId, 'logo')} alt="" onError={() => setFailed(true)} />}
    </div>
  )
}

/**
 * Get on Steam first, then each other official launcher as it answers (one
 * reserved row while they are asked), Wishlist, GG.deals, and the library
 * game when it is owned.
 */
function BuyPanel({ game, loading, others, seed, owned, wished, onWish, onOpenLibrary }: {
  game: StoreGame | null
  loading: boolean
  others: StoreOffer[] | null
  seed: StoreTile
  owned: boolean
  wished: boolean
  onWish: (on: boolean) => void
  onOpenLibrary: () => void
}) {
  const [added, flashAdded] = useDone()
  const [opening, setOpening] = useState(false)
  const soon = game ? !!game.comingSoon : !!seed.soon
  // Steam's row draws at once from the card that opened the page; Steam's own
  // details replace it (and fill in the price) when they land.
  const steam = game?.offers ?? (loading ? [seedOffer(seed)] : [])
  return (
    <div className="exo-sgp-buy">
      {steam.map(offer => <OfferRow key={offer.store} offer={offer} primary free={!!game?.free} soon={soon} />)}
      {/* The other launchers: one row of its own whatever they answer, so nothing under it moves. */}
      <div className="exo-sgp-others">
        {others === null ? <span className="exo-sgp-offer is-loading exo-shine" role="status" aria-label="Checking other stores" /> : null}
        {(others ?? []).map(offer => <OfferRow key={offer.store} offer={offer} primary={false} free={false} soon={soon} />)}
      </div>
      <div className="exo-sgp-tools">
        <button type="button" className={`exo-game-tool${wished ? ' is-on' : ''}`} aria-pressed={wished}
          onClick={() => { if (!wished) flashAdded(); onWish(!wished) }}>
          {/* On the wishlist: a green check that stays (drawn in when you add it), not the bookmark. */}
          {wished
            ? <svg className={`exo-motion-check exo-store-check ${added ? 'is-held' : 'is-static'}`} viewBox="0 0 24 24" aria-hidden="true"><path d={CHECK_PATH} pathLength="1" /></svg>
            : <Bookmark size={14} />}
          <span className="exo-store-stack">
            <span className={wished ? 'is-hidden' : undefined}>Wishlist</span>
            <span className={wished ? undefined : 'is-hidden'}>On wishlist</span>
          </span>
        </button>
        <button type="button" className="exo-game-tool" disabled={opening}
          onClick={() => { setOpening(true); open(ggDealsUrl(game?.name ?? seed.name)).finally(() => setOpening(false)) }}>
          {opening ? <Spinner label="Opening GG.deals" /> : <ExternalLink size={14} />}GG.deals
        </button>
        {owned ? <button type="button" className="exo-game-tool" onClick={onOpenLibrary}><Library size={14} />In your library</button> : null}
      </div>
    </div>
  )
}

/** Get on a launcher: busy (and not pressable again) while the launcher or browser opens; a failure reads right under it. */
function OfferRow({ offer, primary, free, soon }: { offer: StoreOffer; primary: boolean; free: boolean; soon: boolean }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const label = soon ? `Wishlist on ${offer.name}` : `Get on ${offer.name}`
  const go = () => {
    if (busy) return
    setBusy(true)
    setError(null)
    const failed = `${offer.name}'s page could not be opened.`
    host.storeOpen(offer.url)
      .then(res => { if (!res.ok) setError(res.message ?? failed) })
      .catch((e: unknown) => { report(e, 'Store.openInStore'); setError(failed) })
      .finally(() => setBusy(false))
  }
  return (
    <>
      <button type="button" className={`exo-sgp-offer${primary ? ' is-primary' : ' exo-motion-switch'}`} disabled={busy} onClick={go}>
        <span className="exo-sgp-offer-name">{primary ? label : offer.name}</span>
        {soon ? null : <Price final={offer.final} initial={offer.initial} discount={offer.discount} currency={offer.currency} priceText={offer.priceText} wasText={offer.wasText} free={free} />}
        {busy ? <Spinner label={`Opening ${offer.name}`} /> : <ExternalLink size={14} />}
      </button>
      {error ? <ErrorNote className="exo-store-error" message={error} /> : null}
    </>
  )
}

/** Steam's most voted tags, as quiet pills, in one row of their own. (Steam Deck's rating is left out: Exo plays games on Windows.) */
function Pills({ tags }: { tags: string[] | null }) {
  return (
    <ul aria-label="Tags" className="exo-sgp-pills">
      {(tags ?? []).map(t => <li key={t} className="exo-sgp-pill exo-motion-switch">{t}</li>)}
    </ul>
  )
}

/** Lines where words will be, shining until the details land. */
function Placeholder({ lines }: { lines: number }) {
  return <span className="exo-sgp-placeholder" aria-hidden="true">{Array.from({ length: lines }, (_, i) => <i key={i} className="exo-shine" />)}</span>
}

/** The game's description: what the game is. Its card takes the room the column leaves; a long one scrolls inside it. */
function About({ text }: { text: string | null }) {
  return (
    <section className="exo-sgp-card exo-sgp-about" aria-labelledby="exo-sgp-about-title">
      <h3 id="exo-sgp-about-title" className="exo-sgp-h">About</h3>
      {text === null ? <Placeholder lines={4} /> : <p className="exo-sgp-text exo-motion-switch">{text || 'Steam has no description for this game.'}</p>}
    </section>
  )
}

/** "Very Positive · 94% of 1,025", or a dash when Steam gives no score. */
function scoreText(word: string | null | undefined, percent: number | null | undefined, count: number | undefined): string {
  const score = reviewText(word, percent)
  return score ? `${score}${count ? ` of ${count.toLocaleString()}` : ''}` : '—'
}

/** Steam's controller support in a few words, as its own page pairs them: "Full · Xbox and PlayStation". */
function controllerText(game: StoreGame): string {
  const support = game.controller === 'full' ? 'Full' : game.controller === 'partial' ? 'Partial' : null
  if (!support) return game.playStation ? 'PlayStation' : '—'
  return `${support} · Xbox${game.playStation ? ' and PlayStation' : ''}`
}

/** A short list of languages whole; a long one by its count, which is the useful part of it. */
function languagesText(languages: string[] | undefined): string {
  if (!languages?.length) return '—'
  return languages.length <= 3 ? languages.join(', ') : `${languages.length} languages`
}

/**
 * Two rows of facts under the media, the same ten on every page: reviews, release, developer, publisher and genres;
 * then the last 30 days' reviews (under the all-time score, asked for once the page opens), controller, languages,
 * the account a game needs besides Steam, and its DRM.
 */
function Facts({ game, recent }: { game: StoreGame | null; recent: StoreRecentReviews | null | undefined }) {
  // A fact is null (a placeholder) until the page lands.
  const fact = (text: (g: StoreGame) => string) => (game ? text(game) : null)
  const cells: [string, string | null][] = [
    ['Reviews', fact(g => scoreText(g.reviewWord, g.reviewPercent, g.reviewCount))],
    [game?.comingSoon ? 'Release' : 'Released', fact(g => g.released || '—')],
    ['Developer', fact(g => g.developers.join(', ') || '—')],
    ['Publisher', fact(g => g.publishers.join(', ') || '—')],
    ['Genres', fact(g => g.genres.slice(0, 4).join(', ') || '—')],
    ['Recent reviews', recent === undefined ? null : scoreText(recent?.word, recent?.percent, recent?.count)],
    ['Controller', fact(controllerText)],
    ['Languages', fact(g => languagesText(g.languages))],
    ['Other account', fact(g => g.account || '—')],
    ['DRM', fact(g => g.drm || '—')],
  ]
  return (
    <section className="exo-sgp-facts" aria-label="Details">
      <dl>
        {cells.map(([k, v]) => (
          <div key={k}>
            <dt>{k}</dt>
            {v === null ? <dd><Placeholder lines={1} /></dd> : <dd className="exo-motion-switch">{v}</dd>}
          </div>
        ))}
      </dl>
    </section>
  )
}

type Spec = NonNullable<StoreGame['specs']>[number]

/** This PC's part against the requirements: a check when it meets them, a cross when it is below. */
function Mark({ meets }: { meets: Spec['meets'] }) {
  if (!meets) return null
  return (
    <span className="exo-sgp-check" role="img" aria-label={meets === 'below' ? 'Below Minimum' : meets === 'recommended' ? 'Meets Recommended' : 'Meets Minimum'}>
      {meets === 'below' ? <Close size={12} /> : <Check size={12} />}
    </span>
  )
}

const VERDICT = {
  recommended: { text: 'Runs at Recommended', tone: 'is-good' },
  minimum: { text: 'Meets Minimum', tone: '' },
  below: { text: 'Below Minimum', tone: 'is-bad' },
} as const

/** The parts every page lists, in this order: the same rows on every page, a dash where Steam names none. */
const PARTS = ['OS', 'Processor', 'Graphics', 'Memory', 'Storage', 'DirectX', 'Network']

/**
 * The system requirements against this PC, a row per part: this PC's part with a check or a cross, then Steam's
 * Minimum and Recommended, and one verdict at the top. Steam's words stay where Exo could not read a part, and that
 * part is not judged. The same seven rows and four columns on every page.
 */
function Specs({ specs, fit }: { specs: Spec[] | null; fit: StoreGame['fit'] }) {
  const both = specs?.some(s => s.recommended) ?? true
  const verdict = fit ? VERDICT[fit] : null
  const tone = (s: Spec) => s.meets === 'recommended' || (!both && s.meets === 'minimum') ? 'is-good' : s.meets === 'below' ? 'is-bad' : s.meets ? '' : 'is-unknown'
  return (
    <section className="exo-sgp-card exo-sgp-specs" aria-labelledby="exo-sgp-specs-title">
      <div className="exo-sgp-card-head">
        <h3 id="exo-sgp-specs-title" className="exo-sgp-h">System requirements</h3>
        {verdict ? <p className={`exo-sgp-verdict exo-motion-switch ${verdict.tone}`}>{fit === 'minimum' && !both ? 'Meets the requirements' : verdict.text}</p> : null}
      </div>
      <div className="exo-sgp-spec-grid" role="table" aria-label="System requirements">
        <div className="exo-sgp-spec is-head" role="row">
          <span role="columnheader" />
          <span role="columnheader">This PC</span>
          <span role="columnheader">Minimum</span>
          <span role="columnheader">Recommended</span>
        </div>
        {PARTS.map(part => {
          const s = specs?.find(x => x.label === part)
          return (
            <div key={part} className="exo-sgp-spec" role="row">
              <span role="rowheader" className="exo-sgp-spec-label">{part}</span>
              {specs === null ? (
                <><span role="cell"><Placeholder lines={1} /></span><span role="cell" /><span role="cell" /></>
              ) : (
                <>
                  <span role="cell" className={`exo-sgp-spec-mine exo-motion-switch ${s ? tone(s) : 'is-unknown'}`}>{s?.mine ? <><Mark meets={s.meets} />{s.mine}</> : null}</span>
                  <span role="cell" className="exo-sgp-spec-need exo-motion-switch"><em>Minimum</em>{s?.minimum ?? '—'}</span>
                  <span role="cell" className="exo-sgp-spec-need exo-motion-switch"><em>Recommended</em>{s?.recommended ?? s?.minimum ?? '—'}</span>
                </>
              )}
            </div>
          )
        })}
      </div>
    </section>
  )
}

/** The big viewer (a trailer, or a screenshot) and the strip to pick from: trailers first. */
function MediaViewer({ game, onScreen }: { game: StoreGame; onScreen: boolean }) {
  const [shown, setShown] = useState<Shown | null>(game.media.some(m => m.hls) ? { kind: 'trailer', index: 0 } : game.screenshots.length ? { kind: 'shot', index: 0 } : null)
  if (!shown) return null
  const trailer = shown.kind === 'trailer' ? game.media[shown.index] : null
  return (
    <div className="exo-sgp-media">
      <div className="exo-sgp-stage-box">
        <div className="exo-sgp-stage">
          {trailer?.hls
            ? <TrailerPlayer key={trailer.hls} av1={trailer.av1} hls={trailer.hls} poster={trailer.thumb} onScreen={onScreen} />
            : <img key={game.screenshots[shown.index]} className="exo-store-img exo-motion-switch" src={game.screenshots[shown.index]} alt="" decoding="async" />}
        </div>
      </div>
      <div className="exo-sgp-strip" role="listbox" aria-label="Trailers and screenshots">
        {game.media.map((m, i) => m.hls ? (
          <button key={m.hls} type="button" role="option" aria-selected={shown.kind === 'trailer' && shown.index === i} aria-label={`Trailer ${i + 1}`}
            className="exo-sgp-thumb is-trailer" onClick={() => setShown({ kind: 'trailer', index: i })}>
            <img src={m.thumb} alt="" loading="lazy" decoding="async" /><Play size={16} />
          </button>
        ) : null)}
        {game.screenshots.map((s, i) => (
          <button key={s} type="button" role="option" aria-selected={shown.kind === 'shot' && shown.index === i} aria-label={`Screenshot ${i + 1}`}
            className="exo-sgp-thumb" onClick={() => setShown({ kind: 'shot', index: i })}>
            <img src={thumbOf(s)} alt="" loading="lazy" decoding="async" />
          </button>
        ))}
      </div>
    </div>
  )
}

/** The viewer's place while the page loads: its black frames, shining (DESIGN.md › Waits). */
function MediaSkeleton() {
  return (
    <div className="exo-sgp-media is-skeleton" role="status" aria-label="Loading the game">
      <div className="exo-sgp-stage-box" aria-hidden="true"><div className="exo-sgp-stage exo-shine" /></div>
      <div className="exo-sgp-strip" aria-hidden="true">
        {Array.from({ length: 8 }, (_, i) => <span key={i} className="exo-sgp-thumb exo-shine" />)}
      </div>
    </div>
  )
}

/** Steam's own small copy of a screenshot (600x338) for its thumbnail: the strip decoded eight full 1920x1080 ones as a page opened. */
const thumbOf = (shot: string) => shot.replace(/\.1920x1080\.jpg/i, '.600x338.jpg')

/** The game's page (already warmed while its card was on screen, so it draws whole at once); Retry asks the host again. */
function useStoreGame(appId: string) {
  const [state, setState] = useState<{ data: StoreGame | null; error: string | null }>(() => ({ data: peekGame(appId), error: null }))
  const [attempt, setAttempt] = useState(0)
  useEffect(() => {
    let live = true
    loadGame(appId, attempt > 0)
      .then(g => { if (live) setState(g.ok ? { data: g, error: null } : { data: null, error: g.message ?? 'The store could not be reached.' }) })
      .catch((e: unknown) => { report(e, 'Store.game'); if (live) setState({ data: null, error: 'The store could not be reached.' }) })
    return () => { live = false }
  }, [appId, attempt])
  const retry = useCallback(() => {
    setState({ data: null, error: null })
    setAttempt(n => n + 1)
  }, [])
  return { ...state, retry }
}

/**
 * The other launchers' offers, asked for alongside Steam's page; null while they
 * are on their way, empty when there are none or they could not be asked.
 */
function useOtherOffers(appId: string) {
  const [answer, setAnswer] = useState<StoreOtherOffers | null>(() => peekOffers(appId))
  useEffect(() => {
    let live = true
    loadOffers(appId)
      .then(res => { if (live) setAnswer(res.ok ? res : { ok: false, offers: [] }) })
      .catch((e: unknown) => { report(e, 'Store.offers'); if (live) setAnswer({ ok: false, offers: [] }) })
    return () => { live = false }
  }, [appId])
  return { offers: answer ? answer.offers ?? [] : null }
}

/** Steam's Recent reviews for the open page: undefined while asked, null when Steam did not answer. */
function useRecentReviews(appId: string) {
  const [recent, setRecent] = useState<StoreRecentReviews | null | undefined>(() => peekReviews(appId) ?? undefined)
  useEffect(() => {
    let live = true
    loadReviews(appId)
      .then(res => { if (live) setRecent(res.ok ? res : null) })
      .catch((e: unknown) => { report(e, 'Store.reviews'); if (live) setRecent(null) })
    return () => { live = false }
  }, [appId])
  return recent
}
