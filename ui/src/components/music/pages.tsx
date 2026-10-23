import { useEffect, useId, useMemo, useState, type CSSProperties } from 'react'
import { Download, Play, More } from '../../brand/icons'
import { trackLength, type PlayerActOptions, type PlayerAction, type PlayerCard, type PlayerHeader, type PlayerLink, type PlayerTrack, type PlayerViewData } from '../../lib/player'
import type { MusicFeature } from '../../lib/musicAdapters'
import { Menu, type MenuItem } from '../ui/Menu'
import { Art, isLikedCard } from './Art'
import { ArtistPage } from './artist'
import { Confirm, copyLink, newPlaylist, openRadio, shareLink, useFlash } from './manage'
import { loadView, pageFor, runAction, usePlayer, useView, type Page } from './playerContext'

const arrive = (index: number) => ({ '--i': index }) as CSSProperties

/** A panel or list still on its first read: one quiet line, not a blank. */
export function Waiting() {
  return <p className="exo-music-empty" role="status">Loading…</p>
}

/** An action the service refused, under the control that asked. */
export function Failed({ when }: { when: boolean }) {
  return when ? <p className="exo-music-error" role="alert">Didn't work. Try again.</p> : null
}

export function HomePage({ data }: { data: PlayerViewData }) {
  const { has } = usePlayer()
  const recent = useView('recent', {}, !has('recent'))
  const sections = (data.sections ?? []).filter(section => section.items.length)
  const recents = recent.data?.items ?? []
  return (
    <>
      {data.greeting ? <h2 className="exo-music-greeting">{data.greeting}</h2> : null}
      {recents.length ? <RecentGrid cards={recents.slice(0, 8)} /> : null}
      {sections.map((section, index) => <Shelf key={`${index}:${section.title}`} title={section.title} cards={section.items} more={section.more} />)}
      {!sections.length && !recents.length && (recent.data || !has('recent')) ? <p className="exo-music-empty">Nothing on Home yet</p> : null}
    </>
  )
}

/**
 * Explore (YouTube Music), New and Radio (Apple Music), and any page that is shelves and songs
 * (charts, new releases, a mood or genre, a discography): the service's own shelves, one after another.
 */
export function BrowsePage({ page, data }: { page: Page; data: PlayerViewData }) {
  const { play } = usePlayer()
  // A page opened from a shelf names itself; the room's own pages are named down the left.
  const title = page.view === 'explore' ? 'Explore' : page.uri ? data.header?.title || page.title : ''
  const sections = (data.sections ?? []).filter(section => section.items.length)
  const empty = page.view === 'radio' ? 'No stations yet' : page.view === 'browse' && !page.uri ? 'Nothing new yet' : 'Nothing here yet'
  return (
    <>
      {title ? <h2 className="exo-music-greeting">{title}</h2> : null}
      {data.tracks?.length ? (
        <section className="exo-music-section">
          <TrackList tracks={data.tracks} onPlay={track => play(undefined, track)} showAlbum />
        </section>
      ) : null}
      {sections.map((section, index) => <Shelf key={`${index}:${section.title}`} title={section.title} cards={section.items} more={section.more} />)}
      {!sections.length && !data.tracks?.length ? <p className="exo-music-empty">{empty}</p> : null}
    </>
  )
}

/** Recently played: a compact grid of tiles above Home's shelves. */
function RecentGrid({ cards }: { cards: PlayerCard[] }) {
  const { open, playCard } = usePlayer()
  return (
    <section className="exo-music-section">
      <div className="exo-music-recent">
        {cards.map((card, index) => (
          <div key={card.uri} className="exo-music-recent-tile exo-motion-arrive" style={arrive(index)}>
            <button type="button" className="exo-music-recent-open" onClick={() => { const next = pageFor(card); if (next) open(next); else playCard(card) }}>
              <Art src={card.image} round={card.kind === 'artist'} liked={isLikedCard(card)} />
              <span className="exo-music-row-title">{card.title}</span>
            </button>
            <button type="button" className="exo-music-card-play is-small" aria-label={`Play ${card.title}`} onClick={() => playCard(card)}><Play size={14} /></button>
          </div>
        ))}
      </div>
    </section>
  )
}

const SEARCH_GROUPS = [['artists', 'Artists'], ['albums', 'Albums'], ['playlists', 'Playlists'], ['stations', 'Stations'], ['videos', 'Music videos'],
  ['shows', 'Podcasts'], ['audiobooks', 'Audiobooks']] as const

/**
 * Search: the best match, then songs and every kind of result, episodes last. One row of chips
 * narrows it: the service's own search filters where it has them (YouTube Music: a new search),
 * else one chip per kind of result already here.
 */
export function SearchPage({ query, data, onFilter }: { query: string; data: PlayerViewData; onFilter: (filter: string) => void }) {
  const [only, setOnly] = useState('')
  const [said, setSaid] = useState(query)
  if (said !== query) { setSaid(query); setOnly('') }
  const top = data.top?.[0]
  const filters = data.filters ?? []
  const filter = data.filter ?? ''
  const groups = SEARCH_GROUPS.filter(([key]) => data[key]?.length)
  const chips = <SearchChips data={data} groups={groups} only={only} onOnly={setOnly} onFilter={onFilter} />
  if (!top && !groups.length && !hasSongs(data)) return <>{filters.length ? chips : null}<p className="exo-music-empty">No results for “{query}”</p></>
  const whole = !!filter || !!only
  return (
    <>
      {chips}
      <SearchTop top={only ? undefined : top} tracks={!only || only === 'tracks' ? data.tracks : undefined} whole={whole}
        songsTitle={filters.find(option => option.id === filter)?.name ?? 'Songs'} />
      {groups.filter(([key]) => !only || only === key).map(([key, name]) => (
        <Shelf key={key} title={name} cards={data[key] ?? []} grid={!!only} />
      ))}
      {!only || only === 'episodes' ? <Songs title="Episodes" tracks={data.episodes} whole={whole} /> : null}
    </>
  )
}

const hasSongs = (data: PlayerViewData) => !!data.tracks?.length || !!data.episodes?.length

/** Search's first results: Spotify's layout, the top result beside the first four songs; narrowed, the songs alone. */
function SearchTop({ top, tracks, whole, songsTitle }: { top?: PlayerCard; tracks?: PlayerTrack[]; whole: boolean; songsTitle: string }) {
  if (top && tracks?.length && !whole) {
    return (
      <div className="exo-music-search-head">
        <TopResult card={top} />
        <Songs title="Songs" tracks={tracks} whole={false} limit={4} />
      </div>
    )
  }
  return (
    <>
      {top ? <TopResult card={top} /> : null}
      <Songs title={songsTitle} tracks={tracks} whole={whole} showAlbum />
    </>
  )
}

/** Search's songs or episodes: the first six, or limit (all once narrowed), each played on its own. */
function Songs({ title, tracks, whole, showAlbum = false, limit = 6 }: { title: string; tracks?: PlayerTrack[]; whole: boolean; showAlbum?: boolean; limit?: number }) {
  const { play } = usePlayer()
  if (!tracks?.length) return null
  return (
    <section className="exo-music-section">
      <h2>{title}</h2>
      <TrackList tracks={whole ? tracks : tracks.slice(0, limit)} onPlay={track => play(undefined, track)} showAlbum={showAlbum} />
    </section>
  )
}

/** The service's own search filters (a new search), else one chip per kind of result already here. */
function SearchChips({ data, groups, only, onOnly, onFilter }: {
  data: PlayerViewData; groups: ReadonlyArray<readonly [string, string]>; only: string; onOnly: (id: string) => void; onFilter: (filter: string) => void
}) {
  const filters = data.filters ?? []
  if (filters.length) return <Chips options={filters.map(option => [option.id, option.name] as const)} picked={data.filter ?? ''} onPick={onFilter} />
  const kinds = [...(data.tracks?.length ? [['tracks', 'Songs'] as const] : []), ...groups, ...(data.episodes?.length ? [['episodes', 'Episodes'] as const] : [])]
  return kinds.length > 1 ? <Chips options={kinds} picked={only} onPick={onOnly} /> : null
}

/** One row of chips, the one picked pressed; all: an All chip first. */
export function Chips({ options, picked, onPick, label = 'Show results', all = true }: {
  options: ReadonlyArray<readonly [string, string]>; picked: string; onPick: (id: string) => void; label?: string; all?: boolean
}) {
  return (
    <div className="exo-shelf-chips exo-music-chips" role="group" aria-label={label}>
      {[...(all ? [['', 'All'] as const] : []), ...options].map(([id, name]) => (
        <button key={id} type="button" className="exo-shelf-chip" aria-pressed={picked === id} onClick={() => onPick(id)}>{name}</button>
      ))}
    </div>
  )
}

const KINDS: Record<string, string> = { artist: 'Artist', album: 'Album', playlist: 'Playlist', show: 'Podcast', episode: 'Episode', track: 'Song', station: 'Station' }

/** The search's best match, large, above the rest. */
function TopResult({ card }: { card: PlayerCard }) {
  const { open, playCard } = usePlayer()
  const kind = KINDS[card.kind] ?? ''
  return (
    <section className="exo-music-section">
      <h2>Top result</h2>
      <div className="exo-music-top exo-motion-arrive">
        <button type="button" className="exo-music-top-open" onClick={() => { const next = pageFor(card); if (next) open(next); else playCard(card) }}>
          <Art src={card.image} round={card.kind === 'artist'} size={96} />
          <span className="exo-music-row-text">
            <span className="exo-music-top-title">{card.title}</span>
            <span className="exo-music-row-sub">{[kind, card.subtitle].filter(Boolean).join(' · ')}</span>
          </span>
        </button>
        <button type="button" className="exo-music-card-play" aria-label={`Play ${card.title}`} onClick={() => playCard(card)}><Play size={16} /></button>
      </div>
    </section>
  )
}

/** Liked Songs, a playlist, an album, a podcast or an artist: a header, then its songs, then the page's facts and shelves. */
export function ListPage({ page, data }: { page: Page; data: PlayerViewData }) {
  const { adapter, has, play } = usePlayer()
  const [more, setMore] = useState<PlayerTrack[]>([])
  const [removed, setRemoved] = useState<Set<string>>(() => new Set())
  // Your playlist in the order you just gave it, until the page reads again.
  const [order, setOrder] = useState<{ from: PlayerTrack[] | undefined; tracks: PlayerTrack[] } | null>(null)
  const loaded = [...(data.tracks ?? []), ...more]
  const tracks = (order && order.from === data.tracks ? order.tracks : loaded).filter(track => !removed.has(track.uid || track.uri))
  const context = data.contextUri || page.uri
  const total = data.total ?? 0
  const editable = data.header?.editable ? data.header.uri : undefined

  // One place up or down in your playlist, as each service moves a row: Spotify by the row it goes
  // before or after (name), Apple Music by the whole order (from, to), YouTube Music by the row it
  // goes before (before; none: the end).
  async function move(track: PlayerTrack, by: number) {
    const from = tracks.indexOf(track), to = from + by, other = tracks[to]
    if (!editable || from < 0 || !other) return false
    const before = tracks[by < 0 ? to : to + 1]?.uid ?? ''
    const ok = await runAction(adapter, 'movePlaylistTrack', {
      uri: track.uri, uid: track.uid, context: editable, name: `${by < 0 ? 'before' : 'after'}|${other.uri}|${other.uid}`,
      text: JSON.stringify({ from, to, before }),
    })
    if (ok) {
      const next = tracks.slice()
      next.splice(to, 0, next.splice(from, 1)[0])
      setOrder({ from: data.tracks, tracks: next })
    }
    return ok
  }

  // Moving needs a service that edits playlists and nothing removed since the list was read; a row
  // goes down onto the last one read only once the whole list is here (the row after it is unknown).
  const canMove = !!editable && has('playlists') && !removed.size
  return (
    <>
      {data.header ? <PageHeader page={page} header={data.header} canPlay={!!tracks.length} onPlay={() => play(context)} /> : null}
      <section className="exo-music-section">
        {page.view === 'artist' && tracks.length ? <Heading title="Top songs" more={data.more} /> : null}
        <TrackList tracks={tracks} onPlay={(track, index) => play(context, track, index)} showAlbum={page.view !== 'album' && page.view !== 'show'}
          numbered={page.view === 'album'} liked={page.view === 'collection'} whole={tracks.length >= total}
          playlist={editable} onMove={canMove ? move : undefined}
          onRemoved={track => setRemoved(previous => new Set(previous).add(track.uid || track.uri))} />
        {page.view === 'artist' ? null : (
          <>
            <ShowMore page={page} data={data} shown={tracks.length} more={more.length} onMore={list => { setMore(previous => [...previous, ...list]); setOrder(null) }} />
            <Facts facts={data.header ? pageFacts(data.header, total || tracks.length) : undefined} />
          </>
        )}
      </section>
      {page.view === 'artist' ? <ArtistPage page={page} data={data} /> : <PageShelves data={data} />}
    </>
  )
}

/** The next 100 songs (50 episodes) of a long list. */
function ShowMore({ page, data, shown, more, onMore }: {
  page: Page; data: PlayerViewData; shown: number; more: number; onMore: (tracks: PlayerTrack[]) => void
}) {
  const { adapter } = usePlayer()
  const first = data.tracks?.length ?? 0, total = data.total ?? 0, offset = first + more
  const [loading, setLoading] = useState(false)
  const [failed, setFailed] = useState(false)
  const pageSize = page.view === 'show' ? 50 : 100
  async function load() {
    setLoading(true)
    setFailed(false)
    const result = await loadView(adapter, page.view, { uri: page.uri, offset })
    if (result.data?.tracks) onMore(result.data.tracks)
    else setFailed(true)
    setLoading(false)
  }
  return (
    <>
      {shown < total && first >= Math.min(pageSize, total) ? (
        <button type="button" className="button button--secondary button--sm exo-music-more" disabled={loading} onClick={() => void load()}>
          {loading ? 'Loading' : 'Show more'}
        </button>
      ) : null}
      <Failed when={failed} />
    </>
  )
}

/** Under an album's or a list's songs: its other albums ("More by" its artist) and the page's own shelves, each with its More. */
function PageShelves({ data }: { data: PlayerViewData }) {
  const by = data.header?.artists?.[0]?.name
  return (
    <>
      <Shelf title={by ? `More by ${by}` : 'Albums'} cards={data.albums ?? []} />
      <Shelf title="Singles and EPs" cards={data.singles ?? []} />
      <Shelf title="Fans also like" cards={data.related ?? []} />
      {(data.sections ?? []).map((section, index) => <Shelf key={`${index}:${section.title}`} title={section.title} cards={section.items} more={section.more} />)}
    </>
  )
}

/**
 * One-line facts under a list: the service's own (Apple Music's release date, length, label and
 * copyright), else an album's day, song count, length, label and copyright lines (Spotify's).
 */
function pageFacts(header: PlayerHeader, count: number) {
  if (header.facts?.length) return header.facts
  if (!header.date && !header.label && !header.copyright?.length) return undefined
  const day = header.date && header.date.length === 10 ? new Date(`${header.date}T12:00:00`).toLocaleDateString(undefined, { dateStyle: 'long' }) : header.date
  const minutes = header.durationMs ? Math.round(header.durationMs / 60_000) : 0
  return [[day, `${count} ${count === 1 ? 'song' : 'songs'}`, minutes ? `${minutes} min` : ''].filter(Boolean).join(' · '),
    ...(header.label ? [header.label] : []), ...(header.copyright ?? [])]
}

export function Facts({ facts }: { facts?: string[] }) {
  return facts?.length ? <ul className="exo-music-facts">{facts.map(fact => <li key={fact}>{fact}</li>)}</ul> : null
}

/** A section's name, with Show all beside it where the section has a page of its own. */
function Heading({ title, more }: { title: string; more?: PlayerLink }) {
  const { open } = usePlayer()
  if (!title) return null
  const next = more ? pageFor({ ...more, title, subtitle: '', image: '' }) : null
  return (
    <div className="exo-music-section-head">
      <h2>{title}</h2>
      {next ? <button type="button" className="exo-music-link exo-music-all" onClick={() => open(next)}>Show all</button> : null}
    </div>
  )
}

function PageHeader({ page, header, canPlay, onPlay }: { page: Page; header: PlayerHeader; canPlay: boolean; onPlay: () => void }) {
  const { editPlaylist } = usePlayer()
  const [failed, setFailed] = useState(false)
  const artist = page.view === 'artist'
  // A playlist just made opens with its details to fill in, as Spotify's own does.
  const [asked, setAsked] = useState(false)
  useEffect(() => {
    if (!page.edit || !header.owned || asked) return
    setAsked(true)
    editPlaylist({ uri: header.uri, title: header.title, description: header.description, image: header.image })
  }, [page.edit, header, asked, editPlaylist])
  return (
    <header className={`exo-music-header${artist && header.banner ? ' is-banner' : ''}`}>
      {artist && header.banner ? <Art className="exo-music-banner" src={header.banner} /> : null}
      {header.image || !artist ? <Art src={header.image} round={artist} liked={page.view === 'collection'} size={176} /> : null}
      <div className="exo-music-header-text">
        <h2>{header.title || page.title}</h2>
        {header.subtitle ? <p className="exo-music-header-sub">{header.subtitle}</p> : null}
        {header.badges?.length ? <p className="exo-music-header-badges">{header.badges.map(badge => <span key={badge} className="exo-music-badge">{badge}</span>)}</p> : null}
        {header.description ? <p className="exo-music-header-desc">{header.description}</p> : null}
        <HeaderActions page={page} header={header} canPlay={canPlay} onPlay={onPlay} onFailed={setFailed} />
        <Failed when={failed} />
      </div>
    </header>
  )
}

/**
 * What Save does on a page, if anything: albums, podcasts and artists are liked; someone else's playlist
 * goes to your library (Apple Music, a catalog one) or is saved (Spotify, where the page says whether it
 * is; YouTube Music, never an automatic one: Liked Music, Episodes for later).
 */
function saveAction(page: Page, header: PlayerHeader, has: (feature: MusicFeature) => boolean): PlayerAction | null {
  if (page.view === 'album' || page.view === 'show' || page.view === 'artist') return 'like'
  if (page.view !== 'playlist' || header.editable || header.owned) return null
  if (has('library')) return header.uri.includes(':library-') ? null : 'library'
  return header.saved !== undefined || has('playlists') && !/:VL(LM|SE)$/.test(header.uri) ? 'like' : null
}

/** Play, then what the page offers: its station, Save or Favorite, and its More. */
function HeaderActions({ page, header, canPlay, onPlay, onFailed }: {
  page: Page; header: PlayerHeader; canPlay: boolean; onPlay: () => void; onFailed: (failed: boolean) => void
}) {
  const { adapter, has } = usePlayer()
  const [saved, setSaved] = useState(!!header.saved)
  // A page shown from the cache first: the fresh copy's Save or Follow replaces the cached one.
  const [said, setSaid] = useState(header.saved)
  if (said !== header.saved) { setSaid(header.saved); setSaved(!!header.saved) }
  const artist = page.view === 'artist'
  const save = saveAction(page, header, has)
  const run = async (ok: Promise<boolean>, then?: () => void) => {
    onFailed(false)
    if (await ok) then?.()
    else onFailed(true)
  }
  // Apple Music's words: Favorite an artist, add an album to your library.
  const words = artist ? (has('station') ? ['Favorite', 'Favorited'] : ['Follow', 'Following']) : has('library') ? ['Add to library', 'In library'] : ['Save', 'Saved']
  return (
    <div className="exo-music-header-actions">
      <button type="button" className="button button--primary" disabled={!canPlay} onClick={onPlay}><Play size={16} /> Play</button>
      {header.station && has('station') ? (
        <button type="button" className="button button--secondary" onClick={() => void run(runAction(adapter, 'station', { uri: header.station }))}>Station</button>
      ) : null}
      {save ? (
        <button type="button" className="button button--secondary" aria-pressed={saved}
          onClick={() => void run(runAction(adapter, save, { uri: header.uri, on: !saved }), () => setSaved(!saved))}>
          {words[saved ? 1 : 0]}
        </button>
      ) : null}
      <PageMenu page={page} header={header} />
    </div>
  )
}

/** What a press on More plays straight away. */
const HEADER_PLAYS: Record<string, PlayerAction> = { shuffle: 'shufflePlay', mix: 'radio', next: 'playNext', queue: 'enqueue' }

/** The artist a page's radio starts from: the artist page's own, or an album's first artist. */
function radioArtist(page: Page, header: PlayerHeader) {
  if (page.view === 'artist') return { uri: header.uri, name: header.title }
  return page.view === 'album' ? header.artists?.find(a => a.uri) ?? null : null
}

/**
 * A page's More, as each service's own has it: Shuffle play and Start mix, the queue; for your
 * playlist Edit details, public or private, collaborative and Delete; Download wherever the service
 * offers it; artist radio; Copy link.
 */
function pageItems(page: Page, header: PlayerHeader, has: (feature: MusicFeature) => boolean, link: boolean): MenuItem[] {
  const own = page.view === 'playlist' && !!(header.owned ?? header.editable) && has('playlists')
  const lists = page.view === 'album' || page.view === 'playlist' || page.view === 'show'
  const mix = has('mix') && (!!header.radio || page.view === 'artist')
  const offered: Array<[boolean, MenuItem]> = [
    [mix, { id: 'shuffle', label: 'Shuffle play' }],
    [mix, { id: 'mix', label: 'Start mix' }],
    [lists && has('queueLists') && has('playNext'), { id: 'next', label: 'Play next' }],
    [lists && has('queueLists') && has('enqueue'), { id: 'queue', label: 'Add to queue' }],
    [own, { id: 'edit', label: 'Edit details' }],
    [own && header.public !== undefined, { id: 'privacy', label: header.public ? 'Make private' : 'Make public' }],
    [own && header.collaborative !== undefined, { id: 'collab', label: header.collaborative ? 'Stop letting others add songs' : 'Let others add songs' }],
    [typeof header.downloaded === 'boolean' || header.downloaded === 'downloading', { id: 'download', label: header.downloaded ? 'Remove download' : 'Download' }],
    [has('songRadio') && !!radioArtist(page, header), { id: 'radio', label: 'Go to artist radio' }],
    [link, { id: 'link', label: 'Copy link' }],
    [own, { id: 'delete', label: 'Delete' }],
  ]
  return offered.filter(([shown]) => shown).map(([, item]) => item)
}

/** What a page's More changes on the service, as a promise of whether it took. */
function pageAction(adapter: ReturnType<typeof usePlayer>['adapter'], open: (page: Page) => void, page: Page, header: PlayerHeader, id: string) {
  const from = radioArtist(page, header)
  if (HEADER_PLAYS[id]) return runAction(adapter, HEADER_PLAYS[id], { uri: header.uri })
  if (id === 'privacy') return runAction(adapter, 'privacy', { uri: header.uri, on: !header.public })
  if (id === 'collab') return runAction(adapter, 'collaborative', { uri: header.uri, on: !header.collaborative })
  if (id === 'download') return runAction(adapter, 'download', { uri: header.uri, on: !header.downloaded })
  return id === 'radio' && from ? openRadio(adapter, open, from.uri, `${from.name} Radio`) : Promise.resolve(false)
}

function PageMenu({ page, header }: { page: Page; header: PlayerHeader }) {
  const { adapter, has, open, editPlaylist, deleted } = usePlayer()
  const [word, flash] = useFlash()
  const [confirming, setConfirming] = useState(false)
  const menuId = useId().replace(/[^a-z0-9]/gi, '')
  const link = shareLink(adapter, header)
  const items = pageItems(page, header, has, !!link)
  if (!items.length) return null

  async function choose(id: string) {
    if (id === 'edit') { editPlaylist({ uri: header.uri, title: header.title, description: header.description, privacy: header.privacy, image: header.image }); return }
    if (id === 'delete') { setConfirming(true); return }
    if (id === 'link' && link) { const ok = await copyLink(link); flash(ok ? 'Link copied' : "Couldn't copy the link", !ok); return }
    if (!await pageAction(adapter, open, page, header, id)) flash("Didn't work. Try again.", true)
  }
  async function remove() {
    setConfirming(false)
    if (await runAction(adapter, 'deletePlaylist', { uri: header.uri })) deleted(header.uri)
    else flash("Didn't work. Try again.", true)
  }
  return (
    <>
      <Menu label={`More for ${header.title || page.title}`} anchor={`--exo-music-head-${menuId}`} offset={4} triggerClassName="exo-ghost-btn exo-music-page-more"
        trigger={<More size={16} />} items={items} onAction={id => void choose(id)} />
      {header.downloaded === true || header.downloaded === 'downloading'
        ? <span className="exo-music-state is-on"><Download size={14} /> {header.downloaded === true ? 'Downloaded' : 'Downloading'}</span> : null}
      {word ? <span className={`exo-music-state${word.failed ? ' is-failed' : ' is-on'}`} role="status">{word.text}</span> : null}
      {confirming ? <Confirm text={`Delete “${header.title}”?`} action="Delete" onCancel={() => setConfirming(false)} onConfirm={() => void remove()} /> : null}
    </>
  )
}

/** A row of cards, Show all beside its name where it has a page; grid: every card in rows (a discography, a search narrowed to one kind). */
export function Shelf({ title, cards, more, grid = false }: { title: string; cards: PlayerCard[]; more?: PlayerLink; grid?: boolean }) {
  const { open, playCard } = usePlayer()
  if (!cards.length) return null
  const heading = <Heading title={title} more={more} />
  // Moods and genres, charts, new releases: named buttons, not art.
  if (cards.every(card => card.kind === 'mood')) {
    return (
      <section className="exo-music-section">
        {heading}
        <div className="exo-music-moods">
          {cards.map(card => (
            <button key={card.uri} type="button" className="exo-music-mood" onClick={() => open({ view: 'browse', uri: card.uri, title: card.title })}>{card.title}</button>
          ))}
        </div>
      </section>
    )
  }
  return (
    <section className="exo-music-section">
      {heading}
      <div className={grid ? 'exo-music-grid' : 'exo-music-shelf'}>
        {cards.map((card, index) => (
          <div key={card.uri} className="exo-music-card exo-motion-arrive" style={arrive(index)}>
            <button type="button" className="exo-music-card-open" onClick={() => { const next = pageFor(card); if (next) open(next); else playCard(card) }}>
              <Art src={card.image} round={card.kind === 'artist'} liked={isLikedCard(card)} />
              <span className="exo-music-card-title">{card.title}</span>
              <span className="exo-music-card-sub">{card.subtitle || (card.kind === 'artist' ? 'Artist' : '')}</span>
            </button>
            <button type="button" className="exo-music-card-play" aria-label={`Play ${card.title}`} onClick={() => playCard(card)}><Play size={16} /></button>
          </div>
        ))}
      </div>
    </section>
  )
}

/** Playlists a song can be added to, read once per service and again after your library changes; a failed read is asked again. */
const playlistsCache = new WeakMap<object, { revision: number; pending: Promise<PlayerCard[] | null> }>()
function usePlaylists(enabled: boolean) {
  const { adapter, revision } = usePlayer()
  const [list, setList] = useState<PlayerCard[]>([])
  useEffect(() => {
    if (!enabled) return
    let live = true
    let cached = playlistsCache.get(adapter)
    if (!cached || cached.revision !== revision) {
      const pending = loadView(adapter, 'playlists').then(result => result.data?.items ?? null)
      const entry = { revision, pending }
      void pending.then(items => { if (!items && playlistsCache.get(adapter) === entry) playlistsCache.delete(adapter) })
      playlistsCache.set(adapter, cached = entry)
    }
    void cached.pending.then(items => { if (live && items) setList(items) })
    return () => { live = false }
  }, [adapter, enabled, revision])
  return list
}

const episode = (track: PlayerTrack) => /^spotify:episode:/.test(track.uri)

/** A track row's look: arriving, playing, unplayable. */
function rowClass(track: PlayerTrack, moves: boolean, playingUri?: string) {
  return ['exo-music-track', moves && 'exo-motion-arrive', track.uri === playingUri && 'is-playing', !track.playable && 'is-unplayable'].filter(Boolean).join(' ')
}

/** An episode's day, and Played or the minutes left once you have started it. */
function EpisodeState({ track }: { track: PlayerTrack }) {
  const day = track.date ? new Date(`${track.date}T12:00:00`).toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: track.date.slice(0, 4) === String(new Date().getFullYear()) ? undefined : 'numeric' }) : ''
  const left = track.progressMs && track.durationMs > track.progressMs ? Math.max(1, Math.round((track.durationMs - track.progressMs) / 60_000)) : 0
  const state = track.played ? 'Played' : left ? `${left} min left` : ''
  if (!day && !state) return null
  return <span className={track.played ? 'exo-music-episode is-played' : 'exo-music-episode'}>{[day, state].filter(Boolean).join(' · ')}{track.artists.length ? ' · ' : ''}</span>
}

/** A row's second line: an episode's day and state, then the artists (pages a click away, outside the queue). */
function TrackSub({ track, links }: { track: PlayerTrack; links: boolean }) {
  const { open } = usePlayer()
  return (
    <span className="exo-music-row-sub">
      {episode(track) ? <EpisodeState track={track} /> : null}
      {track.artists.map((artist, i) => (
        <span key={`${artist.uri}:${artist.name}`}>{i ? ', ' : ''}
          {artist.uri && links ? <button type="button" className="exo-music-link" onClick={() => open({ view: 'artist', uri: artist.uri, title: artist.name })}>{artist.name}</button> : artist.name}
        </span>
      ))}
    </span>
  )
}

/** A row's length, or for a moment the word an action answered with. */
function TrackTime({ track, done }: { track: PlayerTrack; done: { word: string; failed?: boolean } | null }) {
  return (
    <span className={`exo-music-track-time${done ? done.failed ? ' is-failed' : ' is-done' : ''}`}>
      {done ? done.word : track.durationMs ? trackLength(track.durationMs) : ''}
    </span>
  )
}

/** A long list draws only the rows in view: rows start..end of total, each row px tall; arrive plays only on the first view. */
export type TrackWindow = { start: number; end: number; total: number; row: number; arrive: boolean }

export function TrackList({ tracks, onPlay, showAlbum = false, numbered = false, liked = false, whole = true, playlist, onRow, onRemoved, onMove, window: view }: {
  tracks: PlayerTrack[]
  window?: TrackWindow
  onPlay: (track: PlayerTrack, index: number) => void
  showAlbum?: boolean
  numbered?: boolean
  /** Liked Songs: the menu removes instead of saving. */
  liked?: boolean
  /** Every row of the list is here (a row may move down onto the last one). */
  whole?: boolean
  /** Your own playlist: the menu can remove a song from it. */
  playlist?: string
  /** Queue rows skip to the item instead of starting a context. */
  onRow?: (track: PlayerTrack) => void
  onRemoved?: (track: PlayerTrack) => void
  /** Your own playlist: a song moves one place up (-1) or down (1). */
  onMove?: (track: PlayerTrack, by: number) => Promise<boolean>
}) {
  const player = usePlayer()
  const { adapter, has, open, playingUri, showCredits } = player
  const [done, setDone] = useState<{ uri: string; word: string; failed?: boolean } | null>(null)
  // Likes made from this list's menus, by song, over what the service said.
  const [likes, setLikes] = useState<Readonly<Record<string, boolean>>>({})
  const playlists = usePlaylists(has('addToPlaylist') && !onRow)
  const menuBase = useId().replace(/[^a-z0-9]/gi, '')
  // A row's key is its own: its uid, else its song and which time the song repeats, so removing one
  // never remounts (and replays the arrival of) every row under it.
  const keys = useMemo(() => {
    const seen = new Map<string, number>()
    return tracks.map(track => {
      if (track.uid) return track.uid
      const n = (seen.get(track.uri) ?? 0) + 1
      seen.set(track.uri, n)
      return `${track.uri}#${n}`
    })
  }, [tracks])
  const isLiked = (track: PlayerTrack) => likes[track.uri] ?? track.liked ?? liked
  // Suggest less and the library, as changed from this list's menus over what the service said.
  const [marks, setMarks] = useState<Readonly<Record<string, { less?: boolean; library?: boolean }>>>({})
  const isLess = (track: PlayerTrack) => marks[track.uri]?.less ?? !!track.disliked
  const inLibrary = (track: PlayerTrack) => marks[track.uri]?.library ?? track.inLibrary

  // Menu items that open a page or a panel instead of changing anything.
  function go(track: PlayerTrack, id: string) {
    const artist = track.artists[0]
    if (id === 'credits') showCredits(track.uri)
    else if (id === 'album' && track.album) open({ view: 'album', uri: track.album.uri, title: track.album.name })
    else if (id === 'artist' && artist?.uri) open({ view: 'artist', uri: artist.uri, title: artist.name })
    else return false
    return true
  }

  // Each action and the word its row shows once it lands (none: it opened something).
  function actions(track: PlayerTrack): Record<string, () => Promise<{ ok: boolean; word: string }>> {
    const act = (action: PlayerAction, options: PlayerActOptions, word: string, then?: () => void) => async () => {
      const ok = await runAction(adapter, action, { uri: track.uri, ...options })
      if (ok) then?.()
      return { ok, word }
    }
    const wasLiked = isLiked(track), less = !isLess(track), keep = !inLibrary(track)
    const mark = (change: { less?: boolean; library?: boolean }) => setMarks(previous => ({ ...previous, [track.uri]: { ...previous[track.uri], ...change } }))
    const link = shareLink(adapter, track)
    return {
      mix: act('radio', {}, 'Mix'),
      next: act('playNext', {}, 'Plays next'),
      queue: act('enqueue', {}, 'Queued'),
      station: act('station', {}, 'Station'),
      like: act('like', { on: !wasLiked }, wasLiked ? 'Removed' : 'Saved', () => setLikes(previous => ({ ...previous, [track.uri]: !wasLiked }))),
      less: act('suggestLess', { on: less }, less ? 'Noted' : 'Undone', () => { mark({ less }); if (less) setLikes(previous => ({ ...previous, [track.uri]: false })) }),
      library: act('library', { on: keep }, keep ? 'Added' : 'Removed', () => mark({ library: keep })),
      played: act('played', { on: !track.played }, track.played ? 'Unplayed' : 'Played'),
      remove: act('removeFromPlaylist', { context: playlist, uid: track.uid }, 'Removed'),
      radio: async () => ({ ok: await openRadio(adapter, open, track.uri, `${track.title} Radio`), word: '' }),
      link: async () => ({ ok: !!link && await copyLink(link), word: 'Copied' }),
      newPlaylist: async () => ({ ok: await newPlaylist(player, playlists.length, undefined, track.uri), word: adapter.newPlaylistName ? 'Added' : '' }),
      up: async () => ({ ok: !!onMove && await onMove(track, -1), word: 'Moved' }),
      down: async () => ({ ok: !!onMove && await onMove(track, 1), word: 'Moved' }),
    }
  }

  async function choose(track: PlayerTrack, id: string) {
    if (go(track, id)) return
    const wasLiked = isLiked(track)
    const run = id.startsWith('add:') ? async () => ({ ok: await runAction(adapter, 'addToPlaylist', { uri: track.uri, context: id.slice(4) }), word: 'Added' })
      : actions(track)[id]
    if (!run) return
    const { ok, word } = await run()
    // Gone from this list: removed from the playlist, unliked from Liked Songs, or a library song taken out of the library.
    const leaves = id === 'remove' || id === 'like' && liked && wasLiked || id === 'library' && word === 'Removed' && track.uri.includes(':library-songs:')
    if (ok && leaves && onRemoved) { onRemoved(track); return }
    if (ok && !word) return
    setDone(ok ? { uri: track.uri, word } : { uri: track.uri, word: "Didn't work", failed: true })
    window.setTimeout(() => setDone(current => current?.uri === track.uri ? null : current), 2400)
  }

  function menu(track: PlayerTrack, index: number): MenuItem[] {
    const show = episode(track)
    const moves = !!onMove && !!track.uid
    const offered: Array<[boolean, MenuItem]> = [
      [has('mix'), { id: 'mix', label: 'Start mix' }],
      [has('playNext'), { id: 'next', label: 'Play next' }],
      [has('enqueue'), { id: 'queue', label: 'Add to queue' }],
      [true, { id: 'like', label: `${isLiked(track) ? 'Remove from' : 'Save to'} ${show && has('shows') ? 'Your Episodes' : adapter.likedName}` }],
      [has('suggestLess'), { id: 'less', label: isLess(track) ? 'Undo suggest less' : 'Suggest less' }],
      [has('library'), { id: 'library', label: inLibrary(track) ? 'Remove from library' : 'Add to library' }],
      [has('station'), { id: 'station', label: 'Create station' }],
      [show && track.played !== undefined, { id: 'played', label: track.played ? 'Mark as unplayed' : 'Mark as played' }],
      [!!playlist && !!track.uid, { id: 'remove', label: 'Remove from this playlist' }],
      [moves && index > 0, { id: 'up', label: 'Move up' }],
      [moves && index < tracks.length - (whole ? 1 : 2), { id: 'down', label: 'Move down' }],
      [!show && has('songRadio'), { id: 'radio', label: 'Go to song radio' }],
      [!!track.artists[0]?.uri, { id: 'artist', label: 'Go to artist' }],
      [!!track.album && showAlbum, { id: 'album', label: 'Go to album' }],
      [!show && has('credits'), { id: 'credits', label: 'Show credits' }],
      [!!shareLink(adapter, track), { id: 'link', label: show ? 'Copy episode link' : 'Copy song link' }],
      [has('playlists'), { id: 'newPlaylist', label: 'New playlist', section: 'Add to playlist' }],
    ]
    return [...offered.filter(([shown]) => shown).map(([, item]) => item),
      ...playlists.filter(list => list.uri !== playlist).map(list => ({ id: `add:${list.uri}`, label: list.title, section: 'Add to playlist' }))]
  }

  const first = view ? view.start : 0
  return (
    <ol className="exo-music-tracks" style={view ? { position: 'relative', height: view.total * view.row } : undefined}>
      {(view ? tracks.slice(view.start, view.end) : tracks).map((track, at) => {
        const index = first + at
        const items = onRow ? [] : menu(track, index)
        const moves = !view || view.arrive && index < 16
        const start = () => onRow ? onRow(track) : onPlay(track, index)
        return (
          <li key={keys[index]}
            style={view ? { ...arrive(index), position: 'absolute', top: index * view.row, left: 0, right: 0, height: view.row } : arrive(index)}
            className={rowClass(track, moves, playingUri)}
            onDoubleClick={() => { if (track.playable) start() }}>
            <button type="button" className="exo-music-track-play" aria-label={`Play ${track.title}`} disabled={!track.playable} onClick={start}>
              {numbered ? <span className="exo-music-track-index">{index + 1}</span> : <Art src={track.image} />}
              <Play size={14} />
            </button>
            <span className="exo-music-row-text">
              <span className="exo-music-row-title">{track.title}{track.explicit ? <span className="exo-music-explicit" aria-label="Explicit">E</span> : null}</span>
              <TrackSub track={track} links={!onRow} />
            </span>
            {showAlbum && track.album && !onRow ? (
              <button type="button" className="exo-music-link exo-music-track-album" onClick={() => open({ view: 'album', uri: track.album!.uri, title: track.album!.name })}>{track.album.name}</button>
            ) : <span />}
            {items.length ? (
              <Menu label={`More for ${track.title}`} anchor={`--exo-track-${menuBase}-${index}`} offset={4}
                triggerClassName="exo-ghost-btn exo-music-track-more" trigger={<More size={16} />} items={items}
                filter={{ section: 'Add to playlist', label: 'Find a playlist' }} onAction={id => void choose(track, id)} />
            ) : <span />}
            <TrackTime track={track} done={done?.uri === track.uri ? done : null} />
          </li>
        )
      })}
    </ol>
  )
}
