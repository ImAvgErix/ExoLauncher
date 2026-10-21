import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties, type ReactNode } from 'react'
import { host, onHostEvent } from '../../lib/host'
import { report } from '../../lib/report'
import { MUSIC_LINK, takeMusicLink, type MusicPlayback } from '../../lib/music'
import { pane, pressedArt, transition, visible, type Morph } from '../../lib/viewTransition'
import type { MusicAdapter } from '../../lib/musicAdapters'
import type { PlayerCard, PlayerOption, PlayerState, PlayerView, PlayerViewData, PlayerViewOptions } from '../../lib/player'
import { ChevronLeft, Devices, Equalizer, Explore, Heart, Home, Jam, More, Movie, New, Next, NowPlaying, Pause, Pin, Play, Plus, Previous, Queue, Radio, Repeat, RepeatOne, Search, Shuffle, Songs, ThumbsDown, Users } from '../../brand/icons'
import { Menu, type MenuItem } from '../ui/Menu'
import { useGlassTone, useLens } from '../../lib/glass'
import { Art, isLikedCard } from './Art'
import { DiscographyPage } from './artist'
import { Confirm, copyLink, createFolder, NameField, newPlaylist, shareLink, useFlash } from './manage'
import { BrowsePage, HomePage, ListPage, SearchPage, Waiting } from './pages'
import { SongsPage } from './SongsPage'
import { CreditsPanel, DevicesPanel, FriendsPanel, JamPanel, NowPlayingPanel, QueuePanel } from './panels'
import { useMusicShortcuts } from './shortcuts'
import { SoundPanel } from './SoundPanel'
import { PlaylistSheet } from './PlaylistSheet'
import { PlayerContext, loadView, onPlayerChanged, pageFor, runAction, usePlayer, useView, type Page, type Player, type PlaylistEdit } from './playerContext'
import { useTopOnChange } from '../../lib/topOnChange'

type Panel = 'now' | 'queue' | 'devices' | 'friends' | 'jam' | 'sound' | 'credits' | null

/** Ctrl+N and Ctrl+Shift+N reach the library, which knows where a new playlist or folder goes. */
const CREATE = 'exo-music-create'

/**
 * Exo's own music player, the same for every service: the library down the
 * left, the page in the middle, a panel on the right when asked for, and
 * one player strip along the bottom. The service (lib/musicAdapters.ts) runs
 * hidden behind it; the song's clock, seek and volume stay in the title
 * bar's mini player.
 */
export function MusicPlayer({ adapter, active, nowPlaying }: { adapter: MusicAdapter; active: boolean; nowPlaying: number }) {
  const [stack, setStack] = useState<Page[]>([{ view: 'home' }])
  const [sheet, setSheet] = useState<PlaylistEdit | null>(null)
  const [panel, setPanel] = useState<Panel>(null)
  // The title bar's mini player opens Now playing, in the very render the room shows, so its cover can fly there.
  const [nowAsked, setNowAsked] = useState(0)
  if (nowAsked !== nowPlaying) {
    setNowAsked(nowPlaying)
    setPanel('now')
  }
  const [credits, setCredits] = useState('')
  const searchField = useRef<HTMLInputElement>(null)
  const [query, setQuery] = useState('')
  const [player, setPlayer] = useState<PlayerState | null>(null)
  // The song as the host last sent it, and when it arrived (this page's clock), so lyrics run on time.
  const [live, setLive] = useState<{ playback: MusicPlayback; at: number } | null>(null)
  const playback = live?.playback ?? null
  const [tick, setTick] = useState(0)
  const page = stack[stack.length - 1]

  // The cover pressed to open a page flies into that page's header once it lands (Main).
  const artFrom = useRef<Element | null>(null)
  const open = useCallback((next: Page) => {
    artFrom.current = pressedArt('.exo-music-art')
    setQuery('')
    setStack(previous => [...previous, next])
  }, [])
  // Only the latest read lands: an older one (a retry, a slow answer) never puts back a shuffle or heart already changed.
  const reads = useRef(0)
  const readPlayer = useCallback(async () => {
    const read = ++reads.current
    const result = await loadView(adapter, 'player')
    if (read === reads.current && result.data && typeof result.data.trackUri === 'string') setPlayer(result.data as PlayerState)
  }, [adapter])

  const [revision, setRevision] = useState(0)
  useEffect(() => onPlayerChanged(() => setRevision(n => n + 1)), [])
  // The heart follows a like made anywhere (a song's menu, a page's Save).
  useEffect(() => { void readPlayer() }, [readPlayer, revision])
  useEffect(() => {
    // An answer older than an event already here (by the host's publish order) is dropped.
    const accept = (next: MusicPlayback | null) => {
      const at = performance.now()
      setLive(previous => !next ? previous
        : previous?.playback.seq !== undefined && next.seq !== undefined && next.seq <= previous.playback.seq ? previous
          : { playback: next, at })
    }
    void host.musicPlayback().then(r => accept(r.playback)).catch(() => { })
    // The song changed or paused: shuffle, repeat and like follow it, and an open queue re-reads.
    let timer = 0
    const stop = onHostEvent('music.playback', data => {
      accept(data as MusicPlayback)
      window.clearTimeout(timer)
      timer = window.setTimeout(() => { void readPlayer(); setTick(n => n + 1) }, 250)
    })
    return () => { stop(); window.clearTimeout(timer) }
  }, [readPlayer])

  const play = useCallback((context: string | undefined, track?: { uri: string }, index?: number, order?: string) => {
    void runAction(adapter, 'play', { context, uri: track?.uri, index, query: order }).then(() => void readPlayer())
  }, [adapter, readPlayer])
  const playCard = useCallback((card: PlayerCard) => {
    void runAction(adapter, 'play', card.kind === 'track' ? { uri: card.uri } : { context: card.uri }).then(() => void readPlayer())
  }, [adapter, readPlayer])
  const showCredits = useCallback((uri: string) => { setCredits(uri); setPanel('credits') }, [])
  // A Spotify link opened from Discord: a Jam joins in its panel, a song plays, a page opens.
  const [join, setJoin] = useState<string | null>(null)
  useEffect(() => {
    const take = () => {
      const uri = takeMusicLink()
      const [, kind, id] = uri?.split(':') ?? []
      if (!uri || !id) return
      if (kind === 'socialsession') { setJoin(id); setPanel('jam') }
      else if (kind === 'track' || kind === 'episode') { play(undefined, { uri }); setPanel('now') }
      else if (kind === 'playlist' || kind === 'album' || kind === 'artist' || kind === 'show') open({ view: kind, uri })
    }
    take()
    window.addEventListener(MUSIC_LINK, take)
    return () => window.removeEventListener(MUSIC_LINK, take)
  }, [open, play])
  // A playlist of yours deleted: back to Home if a page of it is in the way back.
  const deleted = useCallback((uri: string) => setStack(previous => previous.some(p => p.uri === uri) ? [{ view: 'home' }] : previous), [])
  const value = useMemo<Player>(() => ({
    adapter, has: feature => adapter.features.has(feature), open, play, playCard, playingUri: player?.trackUri, revision,
    editPlaylist: setSheet, deleted, showCredits,
  }), [adapter, open, play, playCard, player?.trackUri, revision, deleted, showCredits])

  const toggle = (action: 'like' | 'dislike' | 'shuffle' | 'repeat') => {
    if (!player) return
    const options = action === 'like' || action === 'dislike'
      ? (player.trackUri && (action === 'like' || adapter.features.has('dislike')) ? { uri: player.trackUri, on: action === 'like' ? !player.liked : !player.disliked } : null)
      : action === 'shuffle' ? (player.canShuffle ? { on: !player.shuffle } : null)
        : player.canRepeat ? { index: (player.repeat + 1) % 3 } : null
    if (options) void runAction(adapter, action, options).then(() => void readPlayer())
  }
  const goTo = (view: 'home' | 'songs') => { setQuery(''); setStack([{ view }]) }
  useMusicShortcuts(active, adapter.shortcuts, live, {
    shuffle: () => toggle('shuffle'),
    repeat: () => toggle('repeat'),
    like: () => toggle('like'),
    dislike: () => toggle('dislike'),
    create: what => { if (adapter.features.has(what === 'folder' ? 'folders' : 'playlists')) window.dispatchEvent(new CustomEvent(CREATE, { detail: what })) },
    search: () => searchField.current?.focus(),
    home: () => goTo('home'),
    songs: () => goTo('songs'),
    back: () => setStack(previous => previous.length > 1 ? previous.slice(0, -1) : previous),
    panel: name => { if (name !== 'devices' || adapter.features.has('devices')) setPanel(name) },
    closePanel: () => setPanel(null),
  })

  const search = query.trim()
  return (
    <PlayerContext.Provider value={value}>
      <main className="exo-music" aria-label={adapter.name}>
        <h1 className="sr-only">{adapter.name}</h1>
        <nav className="exo-music-side" aria-label="Your library">
          {NAV.filter(([view]) => view === 'home' || view === 'songs' || value.has(view)).map(([view, Icon, name]) => (
            <button key={view} type="button" className="exo-music-row exo-music-nav" aria-current={!search && stack.length === 1 && page.view === view}
              onClick={() => {
              const go = () => { setQuery(''); setStack([{ view }]) }
              // Songs draws at once; every other page pushes when its data lands (Main).
              if (view === 'songs' || page.view === 'songs') void transition(go, { kind: 'push', back: view !== 'songs', morph: pane(visible('.exo-music-main')) })
              else go()
            }}>
              <Icon size={16} /> {name}
            </button>
          ))}
          <label className="exo-music-search">
            <Search size={15} />
            <input ref={searchField} type="search" value={query} placeholder="Search" aria-label={`Search ${adapter.name}`}
              onChange={event => setQuery(event.target.value)} onKeyDown={event => { if (event.key === 'Escape') setQuery('') }} />
          </label>
          <Library current={search ? undefined : page.uri} />
        </nav>
        <Main page={page} query={search} depth={stack.length} artFrom={artFrom} canBack={!search && stack.length > 1}
          onBack={() => setStack(previous => previous.length > 1 ? previous.slice(0, -1) : previous)} />
        {panel ? (
          <aside className="exo-music-panel exo-motion-switch" key={panel} aria-label={panelName(panel)}>
            {{
              now: () => <NowPlayingPanel player={player} playback={playback} receivedAt={live?.at ?? 0} />,
              queue: () => <QueuePanel tick={tick} />,
              devices: () => <DevicesPanel />,
              friends: () => <FriendsPanel />,
              sound: () => <SoundPanel />,
              jam: () => <JamPanel join={join} onJoin={() => setJoin(null)} />,
              credits: () => <CreditsPanel uri={credits} />,
            }[panel]()}
          </aside>
        ) : null}
        <PlayerBar playback={playback} player={player} panel={panel} onPanel={next => {
          const toggle = () => setPanel(current => current === next ? null : next)
          if (next !== 'now') return toggle()
          // Now playing: the strip's cover flies up into the panel, and back down when it closes.
          const strip = () => visible('.exo-music-bar-art')
          const big = () => visible('.exo-music-now > .exo-music-art')
          const opening = panel !== 'now'
          void transition(toggle, { kind: 'morph', back: !opening, morph: { name: 'exo-music-now', from: opening ? strip() : big(), to: opening ? big : strip } })
        }}
          onChanged={() => void readPlayer()} />
        {sheet ? <PlaylistSheet edit={sheet} onClose={() => setSheet(null)} /> : null}
      </main>
    </PlayerContext.Provider>
  )
}

/** The room's own pages, down the left: Explore (YouTube Music), New and Radio (Apple Music) where the service has them. */
const NAV = [['home', Home, 'Home'], ['songs', Songs, 'Songs'], ['explore', Explore, 'Explore'], ['browse', New, 'New'], ['radio', Radio, 'Radio']] as const

function panelName(panel: Exclude<Panel, null>) {
  return { now: 'Now playing', queue: 'Queue', devices: 'Devices', friends: 'Friend activity', jam: 'Jam', sound: 'Sound', credits: 'Credits' }[panel]
}

function viewFor(page: Page, query: string, filter: string): [PlayerView, PlayerViewOptions] {
  if (query) return ['search', filter ? { query, uri: filter } : { query }]
  if (page.view === 'home' || page.view === 'browse' && !page.uri || page.view === 'radio') return [page.view, {}]
  return [page.view, { uri: page.uri }]
}

/** The middle column. The page on screen stays until the next one has loaded, then the new one fades in. */
function Main({ page, query, depth, artFrom, canBack, onBack }: {
  page: Page
  query: string
  /** How deep the page is, so a page further in pushes forward and one back pushes back. */
  depth: number
  artFrom: { current: Element | null }
  canBack: boolean
  onBack: () => void
}) {
  const [settled, setSettled] = useState(query)
  useEffect(() => {
    const timer = window.setTimeout(() => setSettled(query), query ? 300 : 0)
    return () => window.clearTimeout(timer)
  }, [query])
  const target: Page = settled ? { view: 'search' } : page
  // The Songs page reads its own list, a page at a time.
  const songs = target.view === 'songs'
  // A search filter the service offers (Songs, Albums, …) stays while the words change.
  const [filter, setFilter] = useState('')
  const [view, options] = viewFor(target, settled, settled ? filter : '')
  const { adapter } = usePlayer()
  const { data, error, fresh } = useView(view, options, songs)
  const [shown, setShown] = useState<{ page: Page; query: string; data: PlayerViewData } | null>(null)
  const scroller = useRef<HTMLDivElement>(null)
  const shownDepth = useRef(depth)
  useEffect(() => {
    if (!fresh || !data) return
    const land = () => setShown(previous => previous?.data === data ? previous : { page: target, query: settled, data })
    const from = artFrom.current
    artFrom.current = null
    const back = depth < shownDepth.current
    shownDepth.current = depth
    // A page replacing a page pushes; the first page, search results and the same page read again (the room
    // coming back) land as they are.
    const same = !!shown && shown.page.view === target.view && shown.page.uri === target.uri && shown.query === settled
    if (!shown || shown.data === data || same || settled || shown.query) return land()
    const cover: Morph[] = from && !back ? [{ name: 'exo-music-art', from, to: () => visible('.exo-music-header > .exo-music-art:not(.exo-music-banner)', scroller.current ?? document) }] : []
    void transition(land, { kind: 'push', back, morph: [pane(scroller.current), ...cover] })
    // target is derived from page and settled, which the data's key already covers.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [data, fresh])
  const shownKey = songs ? 'songs' : shown ? `${shown.page.view}:${shown.page.uri ?? ''}:${shown.query}` : ''
  // A page that failed to load shows its error, not the last page under it.
  const stale = !!error && shownKey !== `${target.view}:${target.uri ?? ''}:${settled}`
  useTopOnChange(scroller, shownKey)
  return (
    <div className="exo-music-main" ref={scroller} aria-busy={!songs && !fresh}>
      {canBack ? <button type="button" className="exo-ghost-btn exo-music-back" onClick={onBack} aria-label="Back"><ChevronLeft size={18} /></button> : null}
      {error && !songs ? <p className="exo-music-error" role="alert">{error}</p> : null}
      {/* The first page while the service starts (Spotify can take a while after Exo opens): say so, not a black page. */}
      {!songs && !shown && !error ? <p className="exo-music-wait" role="status">Loading {adapter.name}…</p> : null}
      {songs ? (
        <div key="songs" className="exo-motion-switch"><SongsPage scroller={scroller} /></div>
      ) : shown && !stale ? (
        <div key={shownKey} className="exo-motion-switch"><PageBody {...shown} onFilter={setFilter} /></div>
      ) : null}
    </div>
  )
}

function PageBody({ page, query, data, onFilter }: { page: Page; query: string; data: PlayerViewData; onFilter: (filter: string) => void }) {
  if (query) return <SearchPage query={query} data={data} onFilter={onFilter} />
  if (page.view === 'home') return <HomePage data={data} />
  if (page.view === 'explore' || page.view === 'browse' || page.view === 'radio') return <BrowsePage page={page} data={data} />
  if (page.view === 'discography') return <DiscographyPage page={page} data={data} />
  return <ListPage page={page} data={data} />
}

/**
 * The service sends a filtered list's own sub-filters (none for Artists), so
 * the top-level chips come from the unfiltered list and stay on screen.
 */
function useTopFilters(filters: PlayerOption[] | undefined, unfiltered: boolean): PlayerOption[] {
  const [top, setTop] = useState<PlayerOption[]>([])
  if (unfiltered && filters?.length && filters.map(option => option.id).join() !== top.map(option => option.id).join()) setTop(filters)
  return top.length ? top : filters ?? []
}

/**
 * Your library's head: Create where the service makes playlists (a menu with a folder too where it
 * has folders), and one menu of what to show (the service's own filters) and how to sort it.
 */
function LibraryHead({ filters, picked, sorts, selected, onPick, onSort, onCreate }: {
  filters: PlayerOption[]
  picked: string
  sorts: PlayerOption[]
  selected: string
  onPick: (id: string) => void
  onSort: (id: string) => void
  onCreate: (what: 'playlist' | 'folder') => void
}) {
  const { has } = usePlayer()
  const shown = filters.find(option => option.id === picked)?.name
  const order = sorts.find(option => option.id === selected)?.name
  const items = [
    ...(filters.length ? [{ id: 'show:', label: 'All', section: 'Show', on: !picked }] : []),
    ...filters.map(option => ({ id: 'show:' + option.id, label: option.name, section: 'Show', on: picked === option.id })),
    ...sorts.map(option => ({ id: 'sort:' + option.id, label: option.name, section: 'Sort by', on: selected === option.id })),
  ]
  return (
    <div className="exo-music-side-head">
      <h2 className="exo-music-side-title">Your library</h2>
      {has('folders') ? (
        <Menu label="Create" anchor="--exo-music-create" offset={4} triggerClassName="exo-ghost-btn exo-music-create" trigger={<Plus size={16} />}
          items={[{ id: 'playlist', label: 'Create playlist' }, { id: 'folder', label: 'Create folder' }]} onAction={id => onCreate(id === 'folder' ? 'folder' : 'playlist')} />
      ) : has('playlists') ? (
        <button type="button" className="exo-ghost-btn exo-music-create" aria-label="New playlist" onClick={() => onCreate('playlist')}><Plus size={16} /></button>
      ) : null}
      {items.length ? (
        <Menu label="Show and sort your library" anchor="--exo-music-sort" offset={4} triggerClassName="exo-music-sort"
          trigger={[shown, order].filter(Boolean).join(' · ') || 'All'}
          items={items} onAction={id => id.startsWith('show:') ? onPick(id.slice(5)) : onSort(id.slice(5))} />
      ) : null}
    </div>
  )
}

/**
 * Your library as the service keeps it: its own filters as chips, its own
 * sort orders, folders that open in place, and (Spotify) each row's own
 * menu. The list stays while a new filter loads.
 */
function Library({ current }: { current?: string }) {
  const player = usePlayer()
  const { adapter, has, open, playCard, revision } = player
  const [filter, setFilter] = useState('')
  const [sort, setSort] = useState('')
  const [folders, setFolders] = useState<PlayerCard[]>([])
  const [retry, setRetry] = useState(0)
  // A folder being named in place ('new' for a new one), and a delete waiting on its confirm.
  const [naming, setNaming] = useState<string | null>(null)
  const [confirming, setConfirming] = useState<PlayerCard | null>(null)
  const [failed, setFailed] = useState<string | null>(null)
  const [copied, flash] = useFlash()
  const folder = folders[folders.length - 1]
  // offset only asks again: after a save, follow or add (revision) and on Retry.
  const { data, error } = useView('library', { uri: folder?.uri, query: `${filter}|${sort}`, offset: revision + retry * 1000 })
  const items = data?.items ?? []
  const filters = useTopFilters(data?.filters, !filter && !folder)
  const into = folder?.uri
  const made = useRef(create)
  useEffect(() => { made.current = create })
  useEffect(() => {
    const onCreate = (event: Event) => made.current((event as CustomEvent<'playlist' | 'folder'>).detail)
    window.addEventListener(CREATE, onCreate)
    return () => window.removeEventListener(CREATE, onCreate)
  }, [])

  async function act(action: Parameters<typeof runAction>[1], options: Parameters<typeof runAction>[2]) {
    setFailed(null)
    if (!await runAction(adapter, action, options)) setFailed(`${adapter.name} did not take that. Try again.`)
  }
  function create(what: 'playlist' | 'folder') {
    if (what === 'folder') { setNaming('new'); return }
    setFailed(null)
    void newPlaylist(player, items.filter(card => card.owned && card.kind === 'playlist').length, into)
      .then(ok => { if (!ok) setFailed(`${adapter.name} could not make a playlist. Try again.`) })
  }
  function choose(card: PlayerCard, id: string) {
    const link = shareLink(adapter, card)
    if (id === 'play') playCard(card)
    else if (id === 'pin') void act('pin', { uri: card.uri, on: !card.pinned })
    else if (id === 'rename') setNaming(card.uri)
    else if (id === 'delete') setConfirming(card)
    else if (id === 'link' && link) void copyLink(link).then(ok => flash(ok ? 'Link copied' : "Couldn't copy the link", !ok))
    else if (id === 'out') void act('moveToFolder', { uri: card.uri, context: data?.folder?.parent || undefined })
    else if (id.startsWith('move:')) void act('moveToFolder', { uri: card.uri, context: id.slice(5) })
  }
  function newFolder(name: string) {
    void createFolder(adapter, name, into).then(ok => { if (!ok) setFailed(`${adapter.name} could not make a folder. Try again.`) })
  }
  function remove(card: PlayerCard) {
    setConfirming(null)
    void act(card.kind === 'folder' ? 'deleteFolder' : 'like', card.kind === 'folder' ? { uri: card.uri } : { uri: card.uri, on: false })
  }
  return (
    <>
      <LibraryHead filters={filters} {...chosen(data, filter, sort)}
        onPick={id => { setFolders([]); setFilter(id); setSort('') }} onSort={setSort} onCreate={create} />
      <LibraryNote error={error} loaded={!!data} failed={failed} copied={copied} onRetry={() => setRetry(n => n + 1)} />
      <ul className="exo-music-library">
        {folder ? <FolderBack title={data?.folder?.title || folder.title} onBack={() => setFolders(previous => previous.slice(0, -1))} /> : null}
        {naming === 'new' ? (
          <li className="exo-music-library-name">
            <Art folder />
            <NameField value="New Folder" label="Folder name" onDone={name => { setNaming(null); if (name) newFolder(name) }} />
          </li>
        ) : null}
        {items.map((card, index) => (
          <LibraryRow key={card.uri} card={card} index={index} current={current === card.uri}
            menu={has('folders') ? libraryMenu(card, items, !!folder, !!shareLink(adapter, card)) : null} renaming={naming === card.uri} confirming={confirming?.uri === card.uri}
            onOpen={() => {
              if (card.kind === 'folder') { setFolders(previous => [...previous, card]); return }
              const next = pageFor(card)
              if (next) open(next)
              else if (card.kind === 'track') playCard(card)
            }}
            onChoose={id => choose(card, id)}
            onRenamed={name => { setNaming(null); if (name) void act('renameFolder', { uri: card.uri, name }) }}
            onRemove={() => remove(card)} onCancel={() => setConfirming(null)} />
        ))}
      </ul>
    </>
  )
}

/** The filter and sort on show: the ones picked here, else the service's own. */
const chosen = (data: PlayerViewData | null, filter: string, sort: string) =>
  ({ picked: filter || data?.filter || '', sorts: data?.sorts ?? [], selected: sort || data?.sort || '' })

/** The open folder's name, a click from where it sits. */
function FolderBack({ title, onBack }: { title: string; onBack: () => void }) {
  return (
    <li>
      <button type="button" className="exo-music-row" onClick={onBack}>
        <ChevronLeft size={16} />
        <span className="exo-music-row-title">{title}</span>
      </button>
    </li>
  )
}

/** The library's state line: a read that failed (with Retry), the first read, or what the last action said. */
function LibraryNote({ error, loaded, failed, copied, onRetry }: {
  error: string | null
  loaded: boolean
  failed: string | null
  copied: { text: string; failed: boolean } | null
  onRetry: () => void
}) {
  if (error) {
    return (
      <p className="exo-music-error exo-music-retry" role="alert">{error}
        <button type="button" className="button button--secondary button--sm" onClick={onRetry}>Retry</button>
      </p>
    )
  }
  if (!loaded) return <Waiting />
  if (failed) return <p className="exo-music-error" role="alert">{failed}</p>
  return copied ? <p className={copied.failed ? 'exo-music-error' : 'exo-music-done'} role="status">{copied.text}</p> : null
}

/**
 * One row of your library: a page opens, a folder opens in place, a song (History) plays; its More
 * menu (Spotify), a rename field in its place, a delete's confirm under it.
 */
function LibraryRow({ card, index, current, menu, renaming, confirming, onOpen, onChoose, onRenamed, onRemove, onCancel }: {
  card: PlayerCard
  index: number
  current: boolean
  menu: MenuItem[] | null
  renaming: boolean
  confirming: boolean
  onOpen: () => void
  onChoose: (id: string) => void
  onRenamed: (name: string | null) => void
  onRemove: () => void
  onCancel: () => void
}) {
  const { has, playCard } = usePlayer()
  const folder = card.kind === 'folder'
  return (
    <li className={`exo-music-library-item${index < 16 ? ' exo-motion-arrive' : ''}`} style={{ '--i': index } as CSSProperties}>
      {renaming ? (
        <div className="exo-music-library-name">
          <Art folder />
          <NameField value={card.title} label="Folder name" onDone={onRenamed} />
        </div>
      ) : (
        <button type="button" className="exo-music-row exo-music-library-row" aria-current={current} onClick={onOpen}
          onDoubleClick={() => { if (!folder) playCard(card) }}>
          <Art src={card.image} round={card.kind === 'artist'} liked={isLikedCard(card)} folder={folder} />
          <span className="exo-music-row-text">
            <span className="exo-music-row-title">{card.title}</span>
            <span className="exo-music-row-sub">{card.pinned ? <><Pin size={12} className="exo-music-pin" filled /><span className="sr-only">Pinned, </span></> : null}{kindLabel(card, has('shows'))}</span>
          </span>
        </button>
      )}
      {menu && !renaming ? (
        <Menu label={`More for ${card.title}`} anchor={`--exo-lib-${index}`} offset={4} triggerClassName="exo-ghost-btn exo-music-library-more"
          trigger={<More size={16} />} items={menu} filter={{ section: 'Move to folder', label: 'Find a folder' }} onAction={onChoose} />
      ) : null}
      {confirming ? <Confirm text={deleteText(card)} action={folder || card.owned ? 'Delete' : 'Remove'} onConfirm={onRemove} onCancel={onCancel} /> : null}
    </li>
  )
}

/** A library row's menu, as Spotify's sidebar has it: Play, Pin, Rename, Copy link, Move to folder, Delete or Remove. */
function libraryMenu(card: PlayerCard, items: PlayerCard[], inFolder: boolean, link: boolean): MenuItem[] {
  const own = card.kind === 'folder' || card.owned
  const listed = card.kind !== 'collection' && card.kind !== 'episodes'
  const folders = card.kind === 'playlist' || card.kind === 'folder'
    ? [...items.filter(other => other.kind === 'folder' && other.uri !== card.uri).map(other => ({ id: `move:${other.uri}`, label: other.title, section: 'Move to folder' })),
      ...(inFolder ? [{ id: 'out', label: 'Out of this folder', section: 'Move to folder' }] : [])]
    : []
  return [
    ...(card.kind !== 'folder' ? [{ id: 'play', label: 'Play' }] : []),
    ...(listed ? [{ id: 'pin', label: card.pinned ? 'Unpin' : 'Pin' }] : []),
    ...(card.kind === 'folder' ? [{ id: 'rename', label: 'Rename' }] : []),
    ...(link ? [{ id: 'link', label: 'Copy link' }] : []),
    ...folders,
    ...(own ? [{ id: 'delete', label: 'Delete' }] : listed ? [{ id: 'delete', label: 'Remove from your library' }] : []),
  ]
}

function deleteText(card: PlayerCard) {
  if (card.kind === 'folder') return `Delete “${card.title}” and the playlists in it?`
  return card.owned ? `Delete “${card.title}”?` : `Remove “${card.title}” from your library?`
}

function kindLabel(card: PlayerCard, podcasts: boolean) {
  const kind = card.kind === 'collection' ? 'Playlist' : card.kind === 'show' && podcasts ? 'Podcast' : card.kind === 'folder' ? 'Folder'
    : card.kind[0]?.toUpperCase() + card.kind.slice(1)
  return card.subtitle && card.kind !== 'artist' ? `${kind} · ${card.subtitle}` : kind
}

function IconToggle({ label, on, disabled, className, onPress, children }: {
  label: string; on?: boolean; disabled?: boolean; className?: string; onPress: () => void; children: ReactNode
}) {
  return <button type="button" className={`exo-ghost-btn exo-music-toggle${on ? ' is-on' : ''}${className ? ' ' + className : ''}`} aria-label={label} aria-pressed={on} disabled={disabled} onClick={onPress}>{children}</button>
}

/** The playing song's heart, and YouTube Music's thumbs down beside it. */
function SongVerdict({ player, change }: { player: PlayerState; change: (...args: Parameters<MusicAdapter['act']>) => void }) {
  const { adapter, has } = usePlayer()
  return (
    <>
      <IconToggle label={player.liked ? `Remove from ${adapter.likedName}` : `Save to ${adapter.likedName}`} on={player.liked}
        onPress={() => change('like', { uri: player.trackUri, on: !player.liked })}><Heart size={16} filled={player.liked} /></IconToggle>
      {has('dislike') ? (
        <IconToggle label={player.disliked ? 'Remove dislike' : 'Dislike'} on={player.disliked}
          onPress={() => change('dislike', { uri: player.trackUri, on: !player.disliked })}><ThumbsDown size={16} filled={player.disliked} /></IconToggle>
      ) : null}
    </>
  )
}

/** The room's own controls; the song's clock, seek and volume stay in the title bar's mini player. */
function PlayerBar({ playback, player, panel, onPanel, onChanged }: {
  playback: MusicPlayback | null
  player: PlayerState | null
  panel: Panel
  onPanel: (panel: Exclude<Panel, null>) => void
  onChanged: () => void
}) {
  const { adapter, has } = usePlayer()
  // A control the player refused reads in Bad on the artist line for a moment, where the song is.
  const [failed, setFailed] = useState<string | null>(null)
  useEffect(() => {
    if (!failed) return
    const timer = window.setTimeout(() => setFailed(null), 4500)
    return () => window.clearTimeout(timer)
  }, [failed])
  const change = (...args: Parameters<MusicAdapter['act']>) => {
    setFailed(null)
    void runAction(adapter, ...args).then(ok => { if (!ok) setFailed(`${adapter.name} did not take that. Try again.`); onChanged() })
  }
  const glass = useRef<HTMLElement>(null)
  useLens(glass)
  useGlassTone(glass, 'music')
  return (
    <footer ref={glass} className="exo-glass exo-music-bar">
      <div className="exo-music-bar-song">
        <Art className="exo-music-bar-art" src={player?.image || playback?.artworkDataUrl || undefined} />
        <span className="exo-music-row-text">
          <span className="exo-music-row-title">{playback?.title || player?.title || 'Nothing playing'}</span>
          {/* The badge rides the artist line, so the title never gives up room when it appears. */}
          <span className="exo-music-bar-sub">
            {failed ? <span className="exo-music-row-sub is-failed" role="alert">{failed}</span> : <span className="exo-music-row-sub">{playback?.artist}</span>}
            <QualityBadge />
          </span>
        </span>
        {player?.trackUri ? <SongVerdict player={player} change={change} /> : null}
      </div>
      <BarControls playback={playback} player={player} change={change} onFail={setFailed} />
      <BarSide panel={panel} onPanel={onPanel}>
        {/* YouTube Music's Song / Video switch: the song's music video plays instead (its picture stays with the hidden player). */}
        {player?.canVideo && has('video') ? (
          <IconToggle label={player.video ? 'Play the song' : 'Play the music video'} on={player.video}
            onPress={() => change('video', { on: !player.video })}><Movie size={16} /></IconToggle>
        ) : null}
      </BarSide>
    </footer>
  )
}

/** Lossless, when Spotify streams at that quality. */
function QualityBadge() {
  const { has, revision } = usePlayer()
  const { data } = useView('audio', { offset: revision }, !has('audio'))
  return (data as { quality?: number } | null)?.quality === 5 ? <span className="exo-music-badge">Lossless</span> : null
}

function BarControls({ playback, player, change, onFail }: {
  playback: MusicPlayback | null
  player: PlayerState | null
  change: (...args: Parameters<MusicAdapter['act']>) => void
  onFail: (message: string | null) => void
}) {
  const playing = playback?.status === 'playing'
  const control = (action: 'toggle' | 'next' | 'previous') => {
    onFail(null)
    void host.musicControl(action).then(
      result => { if (!result.ok) onFail(result.message ?? 'The player could not respond.') },
      error => { report(error, 'MusicPlayer.control'); onFail('The player could not respond.') })
  }
  const repeat = player?.repeat ?? 0
  return (
      <div className="exo-music-bar-controls">
        <IconToggle label="Shuffle" on={player?.shuffle} disabled={!player?.canShuffle} onPress={() => change('shuffle', { on: !player?.shuffle })}><Shuffle size={16} /></IconToggle>
        <IconToggle label="Previous" disabled={!playback?.canPrev} onPress={() => control('previous')}><Previous size={18} /></IconToggle>
        <button type="button" className="exo-music-bar-play" aria-label={playing ? 'Pause' : 'Play'} disabled={!playback || playback.status === 'none'} onClick={() => control('toggle')}>
          {playing ? <Pause size={18} /> : <Play size={18} />}
        </button>
        <IconToggle label="Next" disabled={!playback?.canNext} onPress={() => control('next')}><Next size={18} /></IconToggle>
        <IconToggle label={repeat === 2 ? 'Repeat this song' : repeat === 1 ? 'Repeat' : 'Repeat off'} on={repeat > 0} disabled={!player?.canRepeat}
          onPress={() => change('repeat', { index: (repeat + 1) % 3 })}>{repeat === 2 ? <RepeatOne size={16} /> : <Repeat size={16} />}</IconToggle>
      </div>
  )
}

function BarSide({ panel, onPanel, children }: { panel: Panel; onPanel: (panel: Exclude<Panel, null>) => void; children?: ReactNode }) {
  const { has } = usePlayer()
  // Spotify's own panels: one button each, or one menu of them on a narrow window (system.css).
  const extra = ([['jam', <Jam size={16} />], ['friends', <Users size={16} />], ['devices', <Devices size={16} />]] as const)
    .filter(([name]) => has(name))
  return (
      <div className="exo-music-bar-side">
        {children}
        <IconToggle label="Now playing" on={panel === 'now'} onPress={() => onPanel('now')}><NowPlaying size={16} /></IconToggle>
        <IconToggle label="Queue" on={panel === 'queue'} onPress={() => onPanel('queue')}><Queue size={16} /></IconToggle>
        {extra.map(([name, icon]) => (
          <IconToggle key={name} className="exo-music-bar-wide" label={panelName(name)} on={panel === name} onPress={() => onPanel(name)}>{icon}</IconToggle>
        ))}
        {extra.length ? (
          <span className="exo-music-bar-narrow">
            <Menu label="More panels" anchor="--exo-music-panels" offset={4}
              triggerClassName={`exo-ghost-btn exo-music-toggle${extra.some(([name]) => name === panel) ? ' is-on' : ''}`} trigger={<More size={16} />}
              items={extra.map(([name]) => ({ id: name, label: panelName(name), on: panel === name }))} onAction={id => onPanel(id as Exclude<Panel, null>)} />
          </span>
        ) : null}
        <IconToggle label="Sound" on={panel === 'sound'} onPress={() => onPanel('sound')}><Equalizer size={16} /></IconToggle>
      </div>
  )
}
