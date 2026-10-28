/**
 * Exo Launcher shell — the Library, rooms, and the game sheet.
 * CTA strings (Play | Install | Update) and cancelInstall live via GamePage + host.
 */
import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { AppUpdate, Chats, Close, Download, Gamepad, Gpu, Key, Library, Minus, Mouse, MusicNotes, Play, Refresh, Settings, StoreBag } from '../brand/icons'
import {
  host,
  onHostEvent,
  hostQuiet,
  resolvePrimaryAction,
  shelfAction,
  type Game,
  type InstallProgress,
  type InstallResponse,
  type PausedDownload,
  type LaunchResponse,
  type LauncherSettings,
  type LibraryFolder,
  type StoreStatus,
} from '../lib/host'
import { sortGames } from '../lib/utils'
import {
  beginGameRead,
  cardForExactId,
  findLibraryGame,
  findLibraryGameByTitle,
  isInstallingGame,
  librarySortMode,
  materializeVariant,
  mergedCoverUrl,
  mergeExactGame,
  mergeExactGameKeepingRunState,
  mergeHostGames,
  mergeLibraryUpdate,
  preferredVariantId,
  reuseGame,
  reuseList,
  setExactRunState,
  statusBelongsToSelection,
  transferForGame,
} from '../lib/library'
import { addPortableFolder, addPortableProgram } from '../lib/portable'
import { onMouseNotice } from '../lib/mouseState'
import { startMouseRelay } from '../lib/mouseRelay'
import { notify as notice } from '../lib/notify'
import { report } from '../lib/report'
import { applyTextTheme, readCachedTextTheme } from '../lib/exoTheme'
import { preloadUpscalerStatuses } from '../lib/upscalerCache'
import { preloadInitialCoverArt, preloadLibraryArtwork } from './CoverArt'
import type { Command } from './blocks/command-menu-1'
import { runPaletteExtra, usePaletteExtras } from './shell/paletteExtras'
import { ExoIsland } from './shell/ExoIsland'
import { useWhatsNew } from './shell/useWhatsNew'
import { bootGate, BootSplash, EJECTABLE_ROOMS, Room } from './shell/BootGate'
import { ErrorBoundary } from './shell/ErrorBoundary'
import type { PcLink } from './gpu/PcRoom'
import { LibraryPane } from './shell/LibraryPane'
import { ROOM_ORDER, TitleBar, type View } from './shell/TitleBar'
import { MUSIC_LINK, savedMusicService, saveMusicService, type MusicService } from '../lib/music'
import { ShellNotices, type DepPrompt } from './shell/ShellNotices'
import { NoticeStack } from './shell/NoticeStack'
import { ConfirmHost } from './ui/Dialog'
import { DiscordNotices } from './shell/DiscordNotices'
import { playOffline, StoreOutageNotices } from './shell/StoreOutageNotices'
import { onReportedFailure, useActionStatus, type ActionStatus } from './shell/useActionStatus'
import { holdsBoot, useAppUpdate } from './shell/useAppUpdate'
import { useRunStatePoll } from './shell/useRunStatePoll'
import { useLiveLibraryRefresh } from './shell/useLiveLibraryRefresh'
import { useShellLayout } from './shell/useShellLayout'
import { useShelves } from './shell/useShelves'
import type { SettingsSectionId } from '../lib/settingsSections'
import { closeCaptures, openCaptures, useCapturesSheet } from '../lib/capture'
import { runNoteCommand, useNoteCommands, watchNotesOpen } from '../lib/notes'

// Rooms and heavy panes load as their own chunks the first time they render.
// Fallbacks are null/quiet shells — the app never flashes a spinner for these.
const SettingsPanel = lazy(() => import('./SettingsPanel').then((m) => ({ default: m.SettingsPanel })))
const FriendsRoom = lazy(() => import('./FriendsRoomRoute').then((m) => ({ default: m.FriendsRoom })))
const NativeMusicRoom = lazy(() => import('./music/NativeMusicRoom').then((m) => ({ default: m.NativeMusicRoom })))
const DevicesRoom = lazy(() => import('./DevicesRoom').then((m) => ({ default: m.DevicesRoom })))
const StoreRoom = lazy(() => import('./store/StoreRoom').then((m) => ({ default: m.StoreRoom })))
const CommandMenu = lazy(() => import('./blocks/command-menu-1'))
const GameSheet = lazy(() => import('./shell/GameSheet').then((m) => ({ default: m.GameSheet })))
const PcRoom = lazy(() => import('./gpu/PcRoom').then((m) => ({ default: m.PcRoom })))
const ActivateKeySheet = lazy(() => import('./ActivateKeySheet').then((m) => ({ default: m.ActivateKeySheet })))
const WhatsNewSheet = lazy(() => import('./shell/WhatsNewSheet').then((m) => ({ default: m.WhatsNewSheet })))
const CapturesOverlay = lazy(() => import('./captures/CapturesSheet').then((m) => ({ default: m.CapturesOverlay })))
import { useWishlistSaleCount } from './store/storeData'
import { openDiscord, setPendingOpen, type DiscordNotice } from '../lib/discord'
import { requestStoreSearch } from '../lib/storeSearch'
import { onScreen, pressedArt, transition, visible, type Morph } from '../lib/viewTransition'

/**
 * What loads behind the splash besides the Library: Settings, and Devices (Exo Mouse, whose game-linked profiles and
 * battery notice must work with the room never opened). Store, Discord's page, Music and PC load the first time
 * they're opened (openRoom), so a room never opened costs nothing; Discord's connection itself starts with Exo.
 * It starts once the splash's mark has landed, so the intro never shares its frames with that work, and settles when
 * everything is ready or after the cap.
 */
/** When Music was last opened, so a PC that uses it gets its room (and its service) started with Exo. */
const MUSIC_USED_KEY = 'exo.music.usedAt'

/**
 * Music was opened in the last week: its room opens with Exo, out of sight, so Spotify is ready when you get there (a
 * cold start took about 7 s). A PC that doesn't use Music still never runs it (its eight processes cost about 570 MB).
 */
function musicUsedLately() {
  try { return Date.now() - Number(localStorage.getItem(MUSIC_USED_KEY) ?? 0) < 7 * 24 * 3600_000 } catch { return false }
}

function useBootPreload(booting: boolean, settings: LauncherSettings | null, musicService: MusicService, setOpenedRooms: (update: (rooms: ReadonlySet<View>) => ReadonlySet<View>) => void) {
  const preloaded = useRef<Promise<unknown> | null>(null)
  // The preloaded rooms stay live (their effects read their data) until a while after the splash.
  const [warming, setWarming] = useState(false)
  useEffect(() => {
    if (booting || !warming) return
    const timer = window.setTimeout(() => setWarming(false), BOOT_WARM_AFTER_MS)
    return () => clearTimeout(timer)
  }, [booting, warming])
  useEffect(() => {
    if (!booting || settings?.onboardingComplete !== true || preloaded.current) return
    preloaded.current = new Promise(resolve => window.setTimeout(() => {
      setWarming(true)
      setOpenedRooms(current => new Set<View>([...current, 'devices', 'settings', ...(musicUsedLately() ? ['music' as const] : [])]))
      resolve(Promise.race([
        Promise.allSettled([
          import('./DevicesRoom'),
          import('./SettingsPanel').then(module => module.preloadSettingsRoom()),
          import('./shell/GameSheet'),
          import('./gpu/PcRoom'),
          import('./blocks/command-menu-1'),
        ]),
        new Promise<void>(done => window.setTimeout(done, BOOT_PRELOAD_CAP_MS)),
      ]))
    }, BOOT_PRELOAD_AFTER_MS))
  }, [booting, settings?.onboardingComplete, musicService, setOpenedRooms])
  return { preloaded, warming }
}

/** The rooms built the first time they're opened, each with its code. */
const ROOM_IDLE_MS = 3 * 60 * 1000

const LAZY_ROOMS: ReadonlyMap<View, (() => Promise<unknown>) | null> = new Map<View, (() => Promise<unknown>) | null>([
  ['store', () => import('./store/StoreRoom')],
  ['friends', () => import('./FriendsRoomRoute')],
  ['music', () => import('./music/NativeMusicRoom')],
  ['system', () => import('./gpu/PcRoom')],
])
/** The most a room opened the first time waits to be whole; Music starts its service's engine first. */
const ROOM_OPEN_CAP_MS = 2500
const MUSIC_OPEN_CAP_MS = 4500
/** How long the host has to stay quiet before a room's reads count as all back. */
const ROOM_QUIET_MS = 150

// A frame, or 50 ms where none comes (a hidden window draws none).
const nextFrame = () => new Promise<void>(resolve => { requestAnimationFrame(() => resolve()); window.setTimeout(resolve, 50) })
const wait = (ms: number) => new Promise<void>(resolve => window.setTimeout(resolve, Math.max(0, ms)))

/**
 * A room opened the first time, built out of sight: its code, then what it asked the host for, then the pictures on
 * its first screen, decoded, so it shows whole at once instead of filling in after; never longer than its cap.
 */
async function roomReady(room: View) {
  const since = performance.now()
  const until = since + (room === 'music' ? MUSIC_OPEN_CAP_MS : ROOM_OPEN_CAP_MS)
  const code = LAZY_ROOMS.get(room)
  if (code) await Promise.race([code().catch(() => { }), wait(until - performance.now())])
  // React draws it and its effects send their first reads; then each answer draws and may ask for more.
  await nextFrame()
  await nextFrame()
  await hostQuiet(ROOM_QUIET_MS, since, until)
  await nextFrame()
  const box = document.querySelector(`[data-room="${room}"]`)
  const first = [...(box?.querySelectorAll('img') ?? [])].filter(img => {
    const at = img.getBoundingClientRect()
    return at.width > 0 && at.bottom > 0 && at.top < window.innerHeight && at.right > 0 && at.left < window.innerWidth
  }).slice(0, 48)
  // A lazy picture out of sight never starts loading: on the first screen it loads now, so it's there when the room is.
  for (const img of first) if (img.loading === 'lazy') img.loading = 'eager'
  await Promise.race([Promise.allSettled(first.map(img => img.decode())), wait(until - performance.now())])
}

const NO_FOLDERS: LibraryFolder[] = []
const NO_IDS: string[] = []

/** The Library's folders and hidden games, from settings: a change shows at once, then is saved. */
function useLibraryFolders(
  settings: LauncherSettings | null,
  setSettings: (update: (current: LauncherSettings | null) => LauncherSettings | null) => void,
  persist: (patch: Partial<LauncherSettings>) => void,
) {
  const folders = settings?.libraryFolders ?? NO_FOLDERS
  const hidden = settings?.hiddenGames ?? NO_IDS
  const save = useCallback((next: LibraryFolder[]) => {
    setSettings(current => current ? { ...current, libraryFolders: next } : current)
    persist({ libraryFolders: next })
  }, [setSettings, persist])
  const saveHidden = useCallback((next: string[]) => {
    setSettings(current => current ? { ...current, hiddenGames: next } : current)
    persist({ hiddenGames: next })
  }, [setSettings, persist])
  return [folders, save, hidden, saveHidden] as const
}

/** The splash always plays in full (the mark lands by 0.76 s, then holds a beat), however fast Exo is ready. The page's
 * thread is idle through it, so a longer hold is only waiting: 2.2 s left the Library usable at 2.9 s with its data in at
 * 0.3 s, and 1.4 s at 2.1 s once only the Library, Settings and Devices loaded behind it. */
const MIN_BOOT_SPLASH_MS = 900
/** Settings and Devices start loading as the mark settles; they are light now that the other rooms wait to be opened. */
const BOOT_PRELOAD_AFTER_MS = 450
/** The most the splash waits for Exo's own rooms; a slow one finishes behind the library. */
const BOOT_PRELOAD_CAP_MS = 2500
/** How long the preloaded rooms stay live after the splash, so slower reads (Discord, a store) land. */
const BOOT_WARM_AFTER_MS = 8000
/** The most the splash waits for the first screen of covers, so the library never arrives as blank tiles. */
const BOOT_COVERS_CAP_MS = 1500
/** Covers on the library's first screen at a full-HD window. */
const BOOT_COVERS = 40
/** Running install percentages repaint at most this often; terminal phases land at once. */
const PROGRESS_FLUSH_MS = 250

type LaunchStatusEvent = { message?: string; phase?: string; ok?: boolean; gameId?: string }

/** A native room surface must yield to every dialog owned by the Exo document. */
function isSurfaceActive(view: View, room: View, blockers: readonly boolean[]): boolean {
  return view === room && !blockers.some(Boolean)
}

function commandActionUnavailable(game: Game, requested?: 'play' | 'install'): string | null {
  if (requested === 'play' && (game.isRunning || game.canStop || !game.installed)) {
    return game.installed ? 'The selected game is already running.' : 'Install this game first.'
  }
  if (requested === 'install' && (game.installed || resolvePrimaryAction(game) !== 'install')) {
    return game.installed ? 'This game is already installed.' : 'Exo cannot install this game yet. Install it from its store app.'
  }
  return null
}

/** The game a running download belongs to; its status messages wait for it to finish. */
function activeTransferId(progress: InstallProgress | null): string | null {
  return progress?.isActive ? progress.gameId : null
}

/** A status that belongs to no card becomes a notice; card messages stay in the sheet. */
function shellNotice(status: ActionStatus): { message: string; tone: ActionStatus['tone']; sticky: boolean } | null {
  if (status.gameId !== null || !status.message) return null
  return { message: status.message, tone: status.tone, sticky: status.sticky }
}

applyTextTheme(readCachedTextTheme())

/** Keeps the latest value and applies it at most every `ms`; `now` applies it at once. */
function latestEvery<T>(apply: (value: T) => void, ms: number) {
  let latest: T | undefined
  let timer = 0
  let flushedAt = 0
  const flush = () => {
    timer = 0
    flushedAt = performance.now()
    apply(latest as T)
  }
  return {
    push(value: T, now: boolean) {
      latest = value
      if (now) {
        window.clearTimeout(timer)
        flush()
      } else if (!timer) {
        timer = window.setTimeout(flush, Math.max(0, ms - (performance.now() - flushedAt)))
      }
    },
    // Clearing the handle too: a stale handle made every later push think a
    // flush was already scheduled, freezing progress on its last value.
    cancel: () => {
      window.clearTimeout(timer)
      timer = 0
    },
  }
}


/**
 * Startup milestone: every card on the first screen shows its art (or has no
 * art to show). Checked every 50 ms for at most 10 s; a hidden window never
 * paints, so it simply stops.
 */
function logFirstCoversPainted(bootAt: number) {
  const deadline = performance.now() + 10_000
  const check = () => {
    const tiles = [...document.querySelectorAll<HTMLElement>('.exo-tile-media')].filter((tile) => {
      const rect = tile.getBoundingClientRect()
      return rect.width > 0 && rect.bottom > 0 && rect.top < window.innerHeight
    })
    const waiting = tiles.some((tile) => {
      const image = tile.querySelector('img')
      return !!image && !(image.complete && image.naturalWidth > 0 && image.style.opacity === '1')
    })
    if (tiles.length > 0 && !waiting) {
      requestAnimationFrame(() => void host.log({
        level: 'info',
        message: `PERF library-covers-painted after ${Date.now() - bootAt} ms covers=${tiles.length}`,
      }).catch(() => { }))
    } else if (performance.now() < deadline && !document.hidden) {
      window.setTimeout(check, 50)
    }
  }
  check()
}

/**
 * Coming back to the Library from another room: every card on screen should
 * still show its art. A second later, any that do not reach app.log with what
 * they were drawing, so a blank cover in the field leaves evidence.
 */
function logBlankCoversAfterRoom(from: View) {
  window.setTimeout(() => {
    if (document.hidden) return
    const pane = document.querySelector<HTMLElement>('main.exo-library-pane')
    if (!pane) return
    const view = pane.getBoundingClientRect()
    const tiles = [...pane.querySelectorAll<HTMLElement>('.exo-tile-hit[data-game-id]')].filter((tile) => {
      const rect = tile.getBoundingClientRect()
      return rect.width > 0 && rect.bottom > view.top && rect.top < view.bottom
    })
    const blank = tiles.flatMap((tile) => {
      const image = tile.querySelector<HTMLImageElement>('img.exo-cover-front:not([data-under])')
      if (!image || (image.complete && image.naturalWidth > 0 && image.style.opacity === '1')) return []
      const origin = image.src.startsWith('https://thumbs.') ? 'thumb' : image.src.startsWith('https://covers.') ? 'file' : 'remote'
      return [`${tile.dataset.gameId}:${origin}:${image.complete ? `w${image.naturalWidth}` : 'loading'}`]
    })
    if (!blank.length) return
    void host.log({
      level: 'warn',
      message: `Library covers blank 1 s after leaving ${from}: ${blank.length} of ${tiles.length} (${blank.slice(0, 8).join(', ')})`,
    }).catch(() => { })
  }, 1000)
}

/** A download that ended while Exo was in the background: Windows says so too, but never over a game. */
function notifyAway(p: InstallProgress, games: Game[]) {
  const away = document.hidden || document.documentElement.dataset.exoInactive === 'true'
  if (!away || games.some(g => g.isRunning)) return
  const title = findLibraryGame(games, p.gameId)?.title ?? 'Exo'
  const line = p.status || (p.phase === 'completed' ? 'Ready to play.' : p.phase === 'failed' ? 'The download failed.' : 'The download was canceled.')
  void host.appNotify(title, line).catch(() => { })
}


/** A Continue banner (landscape art): it flies as its whole card, title plate included. */
const isBanner = (art: Element | null | undefined) => !!art?.closest('.exo-continue-card')

/** A card whose game page is open keeps its slot, empty, while its art is on the page (styles/art.css › away). */
function setAway(art: Element | null | undefined, away: boolean) {
  const card = art?.closest('.exo-continue-card') ?? art
  card?.toggleAttribute('data-exo-away', away)
}

export function LauncherApp() {
  const [games, setGames] = useState<Game[]>([])
  const storeSales = useWishlistSaleCount(games)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [selectedVariantId, setSelectedVariantId] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [libraryError, setLibraryError] = useState<string | null>(null)
  const [booting, setBooting] = useState(true)
  const [bootRevealing, setBootRevealing] = useState(false)
  const coldStart = useRef(true)
  const bootAt = useRef(Date.now())
  const bootPreparationStarted = useRef(false)
  const [busy, setBusy] = useState(false)
  const actionPending = useRef(false)
  const dependencyPending = useRef(false)
  const usableLogged = useRef(false)
  const transferRequests = useRef(new Set<string>())
  const [pendingTransfers, setPendingTransfers] = useState<string[]>([])
  /** The card whose Play or Install is starting (Continue, Friends are playing and a card's menu say Starting…). */
  const [startingId, setStartingId] = useState<string | null>(null)
  const progressVersion = useRef(0)
  const [progress, setProgress] = useState<InstallProgress | null>(null)
  // Direct writes (a failed start, a cleared prompt) cancel a queued throttled
  // progress event so it cannot bring back a stale "downloading" state.
  const progressFlushRef = useRef<{ cancel: () => void } | null>(null)
  const writeProgress = useCallback((next: InstallProgress | null | ((p: InstallProgress | null) => InstallProgress | null)) => {
    progressFlushRef.current?.cancel()
    setProgress(next)
  }, [])
  const [transferFailures, setTransferFailures] = useState<Record<string, string>>({})
  // Games whose transfer the user (or a hold) just stopped: a Cancel or Pause is announced as a `cancelled` event and then
  // the call itself answers ok false ("Canceled."), which is not a failure to leave a red alert for.
  const stoppedTransfers = useRef(new Set<string>())
  const [view, setView] = useState<View>('library')
  const settingsReturnView = useRef<View>('library')
  const [musicService, setMusicService] = useState<MusicService>(savedMusicService)
  /** The mini player's presses: each opens the Music room on Now playing, its cover flying there. */
  const [nowPlaying, setNowPlaying] = useState(0)
  useEffect(() => {
    let live = true
    // A new service's room opens fresh, not on a Now playing asked of the last one.
    const changed = (event: Event) => { setMusicService((event as CustomEvent<MusicService>).detail); setNowPlaying(0) }
    window.addEventListener('exo-music-service', changed)
    void host.webClientPreferences().then(async options => {
      if (!live) return
      if (options.musicService) saveMusicService(options.musicService)
      else {
        const service = savedMusicService()
        await host.setWebClientPreferences({ musicService: service })
        if (live) saveMusicService(service)
      }
    }).catch((e: unknown) => report(e, 'LauncherApp.webClientPreferences'))
    return () => { live = false; window.removeEventListener('exo-music-service', changed) }
  }, [])
  const [activateKeyOpen, setActivateKeyOpen] = useState(false)
  const addingFolder = useRef(false)
  const [openedRooms, setOpenedRooms] = useState<ReadonlySet<View>>(() => new Set<View>(['library']))
  const openedRef = useRef(openedRooms)
  openedRef.current = openedRooms
  // The room being built for its first showing (roomReady): its tab says so, and it runs out of sight meanwhile.
  const [opening, setOpening] = useState<View | null>(null)
  const openingRef = useRef<View | null>(null)
  const goToViewRef = useRef<(next: View, morph?: Morph, alongside?: () => void) => Promise<void>>(() => Promise.resolve())
  const [settings, setSettings] = useState<LauncherSettings | null>(null)
  /** Launch order from the latest library read; Sort > Last launched follows it. */
  const [recent, setRecent] = useState<string[] | undefined>()
  /** Where Settings opens next; a new object each time so the same section can be asked twice. */
  const [settingsSection, setSettingsSection] = useState<{ id: SettingsSectionId } | null>(null)
  const sourceSaves = useRef(Promise.resolve())
  const sourceSaveVersions = useRef(new Map<string, number>())
  const confirmedSources = useRef(new Map<string, string | undefined>())
  const [settingsError, setSettingsError] = useState<string | null>(null)
  const [stores, setStores] = useState<StoreStatus[]>([])
  const [storeMatrixReady, setStoreMatrixReady] = useState(false)
  const [queuedIds, setQueuedIds] = useState<string[]>([])
  const [commandOpen, setCommandOpen] = useState(false)
  const [depPrompt, setDepPrompt] = useState<DepPrompt | null>(null)
  const [activeGameId, setActiveGameId] = useState<string | null>(null)
  const [libraryPane, setLibraryPane] = useState<'shelf' | 'game'>('shelf')
  /** Where a link opens the PC room: a game's graphics, or its shaders. */
  const [gpuJump, setGpuJump] = useState<{ tab: PcLink } | null>(null)
  const [overlayMotion, setOverlayMotion] = useState<'pointer' | 'instant'>('pointer')
  const overlayOpenedAtRef = useRef(0)
  // Tracked handle for the post-launch busy unlock: an orphaned timer firing
  // mid-action would release the CTAs while an RPC is still in flight.
  const busyUnlockTimerRef = useRef<number | null>(null)

  function clearBusyUnlockTimer() {
    if (busyUnlockTimerRef.current !== null) {
      window.clearTimeout(busyUnlockTimerRef.current)
      busyUnlockTimerRef.current = null
    }
  }

  useEffect(
    () => () => {
      if (busyUnlockTimerRef.current !== null) window.clearTimeout(busyUnlockTimerRef.current)
    },
    [],
  )
  const selectedIdRef = useRef<string | null>(null)
  const gamesRef = useRef<Game[]>([])
  const libraryMainRef = useRef<HTMLElement>(null)
  selectedIdRef.current = selectedId
  gamesRef.current = games

  const layout = useShellLayout(settings)
  const status = useActionStatus(activeTransferId(progress), selectedIdRef)
  const setStatus = status.set
  const notify = status.notify
  useEffect(() => onReportedFailure(message => setStatus(message, null, false, 'bad')), [setStatus])
  const clearTransferFailure = useCallback((gameId: string) => {
    setTransferFailures(current => {
      if (!(gameId in current)) return current
      const next = { ...current }
      delete next[gameId]
      return next
    })
  }, [])
  const update = useAppUpdate()
  const checkUpdate = update.check
  const shelves = useShelves(games, progress, settings, recent ?? settings?.recent)

  const selectCard = useCallback((id: string | null) => {
    selectedIdRef.current = id
    setSelectedId(id)
    setSelectedVariantId(null)
  }, [])

  /** The art a game sheet opened from, where it flies back to on close; its slot stays empty meanwhile (away). */
  const gameArtFrom = useRef<Element | null>(null)
  /** The one way out of the details overlay. Every close path goes through here
   * so the shelf can never stay inert behind an overlay that already unmounted. */
  const closeGamePage = useCallback(() => {
    setLibraryPane('shelf')
    selectCard(null)
    setAway(gameArtFrom.current, false)
  }, [selectCard])

  const closeGameSheet = useCallback(() => {
    const id = selectedIdRef.current
    const card = gameArtFrom.current
    void transition(closeGamePage, {
      kind: 'morph',
      back: true,
      morph: {
        name: 'exo-game-art',
        from: visible('.exo-game-detail-cover'),
        to: () => onScreen(card) ? card : id ? visible(`[data-game-id="${CSS.escape(id)}"] [data-vt-art]`) : null,
      },
    })
  }, [closeGamePage])
  const viewRequestedAt = useRef(0)
  const goToView = useCallback((next: View, morph?: Morph, alongside?: () => void): Promise<void> => {
    viewRequestedAt.current = performance.now()
    closeGamePage()
    if (next === view) {
      // Back to the room you are in while another is still being built: that is a change of mind, not a wait.
      if (openingRef.current !== null) { openingRef.current = null; setOpening(null) }
      return Promise.resolve()
    }
    // A room opened the first time is built out of sight and shows once it's whole; asking for another room meanwhile
    // goes there instead.
    if (LAZY_ROOMS.has(next) && !openedRef.current.has(next)) {
      openingRef.current = next
      setOpening(next)
      setOpenedRooms(current => new Set<View>([...current, next]))
      return roomReady(next).then(() => {
        if (openingRef.current !== next) return
        openingRef.current = null
        setOpening(null)
        return goToViewRef.current(next, morph, alongside)
      })
    }
    openingRef.current = null
    setOpening(null)
    if (next === 'settings') settingsReturnView.current = view
    // Room to room slides toward the chosen tab. Settings floats in with its own entrance (a crossfade
    // over it would fade it twice, its words over the room's art) and leaves with a fade.
    const room = next !== 'settings' && view !== 'settings'
    return transition(() => { setView(next); alongside?.() }, { kind: room ? 'room' : next === 'settings' ? 'none' : 'fade', back: room && ROOM_ORDER.indexOf(next) < ROOM_ORDER.indexOf(view), morph })
  }, [closeGamePage, view])
  goToViewRef.current = goToView
  // A game's Shaders opens the PC room.
  const openGpu = useCallback(() => goToView('system'), [goToView])
  // A newer driver or app found in the background: said once, with where to update it.
  useEffect(() => onHostEvent('versions.new', found => notice({
    id: `versions-${found.id}`,
    title: `${found.name} ${found.version}`,
    text: found.installed ? `Out now. You have ${found.installed}.` : 'Out now.',
    actions: [{
      label: 'Open', primary: true, onPress: () => {
        if (found.page === 'devices') { void goToView('devices'); return }
        setGpuJump({ tab: found.page })
        void goToView('system')
      },
    }],
  })), [goToView])
  const openNowPlaying = useCallback(() => void goToView('music',
    { name: 'exo-music-now', from: visible('.exo-mini-cover'), to: () => visible('.exo-music-now > .exo-music-art') },
    () => setNowPlaying(n => n + 1)), [goToView])

  // The Captures sheet opens over any room. It stands in for a game page it was
  // opened from (two sheets would fight over focus). A new snip opens in it,
  // straight in the editor (one taken over a game waits there, the game kept in
  // front); Send to Discord opens the Discord room with the file copied.
  const capturesSheet = useCapturesSheet()
  useEffect(() => { if (capturesSheet.open) closeGamePage() }, [capturesSheet.open, capturesSheet.seq, closeGamePage])
  const sendToDiscord = useCallback(() => {
    closeCaptures()
    goToView('friends')
    notice({ text: 'Copied. Paste it into a chat with Ctrl+V.', tone: 'good' })
  }, [goToView])
  useEffect(() => onHostEvent('discord.open', open => { setPendingOpen(open); goToView('friends') }), [goToView])
  // A Discord banner clicked: its conversation (a thread in its channel), in the Discord room.
  const openNotice = useCallback((notice: DiscordNotice) => {
    openDiscord({ guildId: notice.guildId ?? null, channelId: notice.channelId, parentId: notice.parentId, name: notice.parentId ? notice.place?.replace(/^#/, '') : undefined })
    goToView('friends')
  }, [goToView])
  useEffect(() => onHostEvent('capture.saved', ({ item, edit }) => {
    if (edit) openCaptures({ itemId: item.id, edit: true })
  }), [])
  // A text file Windows opened with Exo: in Captures › Notes.
  useEffect(() => watchNotesOpen(message => setStatus(message, null, false, 'bad')), [setStatus])

  /** The gear opens Settings and, pressed again, returns where Escape would. */
  // Settings floats over the room it was opened from, which stays on screen (dimmed) under it.
  const under = view === 'settings' ? settingsReturnView.current : view
  // A room left open but out of sight gives its tree back after three minutes. Its
  // code chunk stays loaded and its scroll is kept (BootGate's roomScroll), so coming
  // back is fast and lands where it was. Devices is not ejectable: its iframe is the
  // mouse's game-profile engine — game.runState pushes are dropped while it is gone,
  // and a hidden room's effects do not run, so no code there could notice.
  const underRef = useRef(under)
  underRef.current = under
  const viewRef = useRef(view)
  viewRef.current = view
  const roomHiddenAt = useRef(new Map<View, number>())
  useEffect(() => {
    const shown = new Set<View>([underRef.current, viewRef.current])
    for (const room of EJECTABLE_ROOMS) {
      if (shown.has(room)) roomHiddenAt.current.delete(room)
      else if (openedRef.current.has(room)) roomHiddenAt.current.set(room, roomHiddenAt.current.get(room) ?? Date.now())
    }
  }, [under, view])
  useEffect(() => {
    const sweep = setInterval(() => {
      const now = Date.now()
      setOpenedRooms(current => {
        let next: Set<View> | null = null
        for (const room of EJECTABLE_ROOMS) {
          const since = roomHiddenAt.current.get(room)
          if (since == null || now - since < ROOM_IDLE_MS || !current.has(room)) continue
          if (room === underRef.current || room === viewRef.current) continue
          next ??= new Set(current)
          next.delete(room)
          roomHiddenAt.current.delete(room)
        }
        return next ?? current
      })
    }, 30_000)
    return () => clearInterval(sweep)
  }, [])
  const toggleSettings = useCallback(() => {
    if (view === 'settings') void transition(() => setView(settingsReturnView.current))
    else void goToView('settings')
  }, [goToView, view])

  const openGamePage = useCallback((
    id: string,
    motion: 'pointer' | 'instant' = 'pointer',
    options?: { game?: Game },
  ) => {
    // The ref is fresher than the closure right after a library refresh.
    const library = gamesRef.current
    const existing =
      findLibraryGame(library, id) ??
      (options?.game ? findLibraryGameByTitle(library, options.game.title) : undefined)
    selectCard(existing?.id ?? id)
    if (existing && existing.id !== id && existing.variants?.some((variant) => variant.id === id)) {
      setSelectedVariantId(id)
    }
    setOverlayMotion(motion)
    overlayOpenedAtRef.current = performance.now()
    setLibraryPane('game')
    setStatus(null, null)
  }, [selectCard, setStatus])

  /** A friend's game you don't have: the Store, searching for it. */
  const findInStore = useCallback((title: string) => { requestStoreSearch(title); void goToView('store') }, [goToView])

  /** The Store's "In your library": the Library, on that game's page. The Steam app wins over the title. */
  const openLibraryGameFromStore = useCallback((title: string, appId?: number | string) => {
    const library = gamesRef.current
    const game = (appId != null && appId !== '' ? findLibraryGame(library, `steam:${appId}`) : undefined)
      ?? findLibraryGameByTitle(library, title)
    goToView('library')
    if (game) openGamePage(game.id, 'instant')
  }, [goToView, openGamePage])


  const libraryRequest = useRef(0)
  const loadLibrary = useCallback(async (force = false, silent = false, full = false, quietErrors = false) => {
    const request = ++libraryRequest.current
    if (coldStart.current) {
      bootAt.current = Date.now()
      setLoading(true)
    }
    if (!silent) {
      setStatus(null, null)
      setLibraryError(null)
    }
    try {
      const res = await host.getLibrary(force, full)
      if (request !== libraryRequest.current) return
      const previous = gamesRef.current
      const prevById = new Map(previous.map((g) => [g.id, g]))
      const incoming = res.games.map((g) => {
        const old = prevById.get(g.id)
        return reuseGame(old, { ...g, coverUrl: mergedCoverUrl(g, old) })
      })
      const merged = reuseList(previous, mergeHostGames(previous, incoming, selectedIdRef.current))
      gamesRef.current = merged
      setGames(merged)
      if (res.recent) {
        const next = res.recent
        setRecent(current => current?.join('\u001f') === next.join('\u001f') ? current : next)
      }
      if (res.stores?.length) setStores(res.stores)
      if (res.queuedGameIds) setQueuedIds(res.queuedGameIds)
      if (res.progress?.isActive) writeProgress(res.progress)
    } catch (e) {
      if (quietErrors || request !== libraryRequest.current) return
      // The Library shows it (a banner over saved games, or in place of an empty shelf).
      setLibraryError(e instanceof Error ? e.message : 'Could not load the library.')
    } finally {
      setLoading(false)
    }
  }, [setStatus, writeProgress])
  const rescanLiveLibrary = useCallback(() => loadLibrary(true, true, true, true), [loadLibrary])
  // Library › Refresh: the host reads every game again (sizes, stores, time
  // played, last launched, Riot hours, achievements, covers); then the grid
  // takes the result. A forced read asked beside it shares its first part, the
  // stores read again (the host runs one forced scan for both), so it answers
  // when the stores have been read: the covers shine until then, while the art
  // that follows lands on its own.
  const refreshEverything = useCallback(() => {
    const all = host.libraryRefreshAll()
    return { stores: rescanLiveLibrary(), done: all.then(() => rescanLiveLibrary()) }
  }, [rescanLiveLibrary])
  useLiveLibraryRefresh(rescanLiveLibrary)

  const loadSettings = useCallback(async () => {
    try {
      const s = await host.getSettings()
      setSettings(s)
      setSettingsError(null)
    } catch (error) {
      setSettingsError(error instanceof Error ? error.message : 'Settings could not be loaded')
    }
  }, [])

  // Saves answer in any order: only the newest one's answer is taken (an older one would put back what the newer already changed),
  // and a save that failed has the settings read again, so a folder or hidden game that did not stick does not stay on screen.
  const settingsSeq = useRef(0)
  const persistSettings = useCallback((patch: Partial<LauncherSettings>) => {
    const mine = ++settingsSeq.current
    void host.setSettings(patch).then((saved) => {
      if (saved && mine === settingsSeq.current) setSettings(saved)
    }).catch((e: unknown) => {
      report(e, 'LauncherApp.persistSettings')
      if (mine === settingsSeq.current) void loadSettings()
    })
  }, [loadSettings])
  /** PC › Display › HDR saves through this and hears whether it took. */
  const saveSettings = useCallback(async (patch: Partial<LauncherSettings>) => {
    try {
      setSettings(await host.setSettings(patch))
      return true
    } catch (e) {
      report(e, 'LauncherApp.saveSettings')
      return false
    }
  }, [])

  useEffect(() => {
    void loadSettings()
    void loadLibrary()
    void host
      .storesMatrix()
      .then(setStores)
      .catch((e: unknown) => report(e, 'LauncherApp.storesMatrix'))
      .finally(() => setStoreMatrixReady(true))

    const offLaunch = onHostEvent('launch.status', (data) => {
      const d = (data ?? {}) as LaunchStatusEvent
      // Detail rail owns status for the selected game — avoid a second sticky banner.
      if (d.message) setStatus(d.message, d.gameId ?? null)
      if (!d.gameId || (d.phase !== 'running' && d.phase !== 'stopped')) return
      const gameId = d.gameId
      if (d.phase === 'running') {
        // The host recorded the launch; Last launched moves it up now, not at the next library read.
        setRecent(current => current?.[0]?.toLowerCase() === gameId.toLowerCase() ? current
          : [gameId, ...(current ?? []).filter(id => id.toLowerCase() !== gameId.toLowerCase())])
        // Last launched sorts by lastPlayedUtc: stamp the launch on its card now.
        const launched = gameId.toLowerCase()
        const now = new Date().toISOString()
        setGames(items => items.map(g => g.id.toLowerCase() === launched || g.variants?.some(v => v.id.toLowerCase() === launched)
          ? { ...g, lastPlayedUtc: now } : g))
      }
      if (d.phase === 'stopped') {
        // The native Stop result is emitted only after the exact verified
        // game process is gone. Release the CTA immediately; the follow-up
        // discovery scan is reconciliation, not part of the user action.
        // Counting it as a read drops any run-state poll already in flight.
        beginGameRead(gameId)()
        setGames((items) => setExactRunState(items, gameId, false, false))
      }
      const current = beginGameRead(gameId)
      void host.getGame(gameId).then((result) => {
        if (!result.ok || !result.game) return
        const game = result.game
        setGames((items) => current() ? mergeExactGame(items, game) : mergeExactGameKeepingRunState(items, game))
      }).catch((e: unknown) => report(e, 'LauncherApp.launchStatus.getGame'))
    })
    // Exo Mouse: a game profile came on, the battery is low, the signal is weak.
    startMouseRelay()
    const offMouse = onMouseNotice(notify)
    const offSale = onHostEvent('store.sale', (sale) => {
      const price = sale.price ?? (sale.final != null && (sale.currency ?? "USD") === "USD" ? `$${(sale.final / 100).toFixed(2)}` : "a lower price")
      notify(`${sale.name} is on sale on Steam: ${price} (-${sale.discount}%).`)
    })
    // One of Exo's parts (a store tool, a model) didn't come twice; Exo keeps trying by itself.
    const offParts = onHostEvent('components.failed', ({ feature }) => notice({ tone: 'bad', text: `${feature} couldn't be set up. Exo tries again later.` }))
    const offQueue = onHostEvent('install.queue', (data) => {
      const queue = data as { queuedGameIds?: string[]; pausedDownloads?: PausedDownload[]; held?: string | null }
      if (Array.isArray(queue?.queuedGameIds)) setQueuedIds(queue.queuedGameIds)
    })
    const progressFlush = latestEvery(setProgress, PROGRESS_FLUSH_MS)
    progressFlushRef.current = progressFlush
    // A transfer that ended (done, failed or stopped): its status line, the away notice, the library again when it finished.
    const transferEnded = (p: InstallProgress) => {
      if (p.status) setStatus(p.status, p.gameId)
      notifyAway(p, gamesRef.current)
      if (p.phase === 'completed') void loadLibrary(true, true)
    }
    const offProgress = onHostEvent('install.progress', (data) => {
      progressVersion.current++
      const p = data as InstallProgress
      progressFlush.push(p, !p?.isActive)
      if (p?.phase === 'cancelled' && p.gameId) stoppedTransfers.current.add(p.gameId)
      if (p?.phase === 'failed') {
        setTransferFailures(current => ({ ...current, [p.gameId]: p.status || 'Install failed.' }))
      } else if (p?.gameId && (p.isActive || p.phase === 'completed' || p.phase === 'cancelled')) {
        clearTransferFailure(p.gameId)
      }
      if (!p?.isActive && (p?.phase === 'completed' || p?.phase === 'failed' || p?.phase === 'cancelled')) transferEnded(p)
    })
    const offCovers = onHostEvent('library.updated', (data) => {
      const d = data as { games?: Game[] }
      if (!Array.isArray(d?.games)) return
      // A pushed full snapshot can arrive while an older cached RPC response is
      // still in flight. That response must not remove a newly discovered game.
      libraryRequest.current++
      if (d.games.length === 0) {
        gamesRef.current = []
        setGames([])
        return
      }
      setGames((prev) => mergeLibraryUpdate(prev, d.games!, selectedIdRef.current))
    })
    return () => {
      progressFlush.cancel()
      offLaunch()
      offProgress()
      offQueue()
      offSale()
      offParts()
      offMouse()
      offCovers()
    }
  }, [loadLibrary, loadSettings, setStatus, notify, clearTransferFailure])

  // The key popover closes once the store's redeem page opens, so the host's
  // confirmation is heard here, where it can still open the new game.
  useEffect(() => onHostEvent('keys.found', (data) => {
    const payload = (data ?? {}) as { ok?: boolean; gameId?: string; message?: string }
    if (payload.ok && payload.gameId) {
      const gameId = payload.gameId
      setActivateKeyOpen(false)
      setStatus(payload.message ?? 'Key activated.', null)
      void loadLibrary(true, true).then(() => openGamePage(gameId, 'instant'))
      return
    }
    setStatus(payload.message ?? 'No new game was confirmed.', null, true)
  }), [loadLibrary, openGamePage, setStatus])

  const { preloaded, warming } = useBootPreload(booting, settings, musicService, setOpenedRooms)

  useEffect(() => {
    if (
      !booting ||
      bootPreparationStarted.current ||
      settings?.onboardingComplete !== true ||
      loading
    ) return
    bootPreparationStarted.current = true
    const prepare = async () => {
      const wait = Math.max(0, MIN_BOOT_SPLASH_MS - (Date.now() - bootAt.current))
      const firstScreen = gamesRef.current.slice(0, BOOT_COVERS)
      await Promise.all([
        preloaded.current,
        new Promise<void>((resolve) => window.setTimeout(resolve, wait)),
        Promise.race([
          preloadInitialCoverArt(firstScreen, BOOT_COVERS).catch(() => { }),
          new Promise<void>((resolve) => window.setTimeout(resolve, BOOT_COVERS_CAP_MS)),
        ]),
      ])
      coldStart.current = false
      setBootRevealing(true)
    }
    void prepare()
  }, [booting, loading, settings?.onboardingComplete, preloaded])

  useEffect(() => {
    if (booting) return
    if (!usableLogged.current) {
      usableLogged.current = true
      performance.mark('exo.library.usable')
      void host.log({ level: 'info', message: `Library usable after ${Date.now() - bootAt.current} ms` }).catch(() => { })
      logFirstCoversPainted(bootAt.current)
    }
    let cancelled = false
    const artworkCancellation = new AbortController()
    const timer = window.setTimeout(() => {
      const visible = gamesRef.current.slice(0, 24)
      const tasks = [
        () => preloadLibraryArtwork(visible, 6, artworkCancellation.signal),
        // Each status walks a game folder on disk; warm only the first
        // cards. Any other sheet reads its status when it opens.
        () => preloadUpscalerStatuses(visible.slice(0, 8), 2, 2000),
      ]
      void (async () => {
        for (const task of tasks) {
          if (cancelled || document.hidden) break
          try { await task() } catch { /* Optional warming cannot block the usable library. */ }
        }
      })()
    }, 1000)
    return () => { cancelled = true; artworkCancellation.abort(); clearTimeout(timer) }
  }, [booting])

  useEffect(() => {
    if (view !== 'music') return
    try { localStorage.setItem(MUSIC_USED_KEY, String(Date.now())) } catch { /* the warm start is a convenience */ }
  }, [view])

  // Warm the actual rooms independently. Registry/account reads and artwork
  // downloads must not stand between opening Exo and a ready music player.
  // A provider switch re-prepares the rooms only, not the library warming.
  useEffect(() => {
    if (booting) return
    const rooms = window.setTimeout(() => {
      if (document.hidden) return
      void host.prepareRooms(musicService).catch(() => { })
    }, 100)
    return () => clearTimeout(rooms)
  }, [booting, musicService])

  // A library refresh can make another exact source the deterministic card
  // default (for example after installing that source). Keep the open detail
  // rail attached to the same real source instead of leaving selectedId pointed
  // at a now-hidden variant.
  useEffect(() => {
    if (!selectedId) return
    const card = cardForExactId(games, selectedId)
    if (!card || card.id === selectedId) return
    const retainedVariant = selectedVariantId && card.variants?.some((variant) => variant.id === selectedVariantId)
      ? selectedVariantId
      : selectedId
    setSelectedId(card.id)
    setSelectedVariantId(retainedVariant)
  }, [games, selectedId, selectedVariantId])

  useRunStatePoll(games, selectedId, selectedVariantId, busy, setGames)

  const previousView = useRef<View>(view)
  const settingsOpens = useRef(0)
  useEffect(() => {
    const from = previousView.current
    previousView.current = view
    if (view === 'library' && from !== 'library') logBlankCoversAfterRoom(from)
    if (view === 'settings' && from !== 'settings') {
      // Click to the first frame that draws Settings (the frame after this commit).
      const requestedAt = viewRequestedAt.current
      const open = ++settingsOpens.current
      requestAnimationFrame(() => window.setTimeout(() => void host.log({
        level: 'info',
        message: `PERF settings-open paintMs=${Math.round(performance.now() - requestedAt)} open=${open} from=${from}`,
      }).catch(() => { })))
    }
  }, [view])

  useEffect(() => {
    setOpenedRooms((current) => {
      if (current.has(view)) return current
      const next = new Set(current)
      next.add(view)
      return next
    })
  }, [view])

  const selectedCard = useMemo(() => {
    if (!selectedId) return null
    return cardForExactId(games, selectedId) ?? findLibraryGame(games, selectedId) ?? null
  }, [games, selectedId])

  // Cards stay canonical in the grid; the detail rail materializes exactly one
  // real source so every bridge action carries that source's own id/target.
  const selected = useMemo(
    () => selectedCard ? materializeVariant(selectedCard, selectedVariantId ?? preferredVariantId(selectedCard, settings?.preferredGameSources)) : null,
    [selectedCard, selectedVariantId, settings],
  )

  // Ctrl+K finds hidden games too, so one can still be opened and played.
  const commandPaletteGames = useMemo(
    () => sortGames([...shelves.library, ...shelves.hidden], 'name', []),
    [shelves.library, shelves.hidden],
  )

  const paletteExtras = usePaletteExtras(commandOpen)
  const noteCommands = useNoteCommands(commandOpen)
  const commands = useMemo<Command[]>(() => {
    const nav: Command[] = [
      { id: 'nav-library', group: 'Go to', title: 'Library', icon: Library },
      { id: 'nav-store', group: 'Go to', title: 'Store', icon: StoreBag },
      { id: 'nav-friends', group: 'Go to', title: 'Discord', icon: Chats },
      { id: 'nav-music', group: 'Go to', title: 'Music', icon: MusicNotes },
      { id: 'nav-system', group: 'Go to', title: 'PC', icon: Gpu },
      { id: 'nav-devices', group: 'Go to', title: 'Devices', icon: Mouse },
      { id: 'nav-settings', group: 'Go to', title: 'Settings', icon: Settings },
    ]
    const seenTitles = new Set<string>()
    const gameCommands: Command[] = commandPaletteGames.flatMap((game) => {
      const key = game.title.trim().toLowerCase()
      if (seenTitles.has(key)) return []
      seenTitles.add(key)
      return [{
        id: `game-${game.id}`,
        group: 'Library',
        title: game.title,
        // Choosing a game opens its page: the row is the game, not Play or Install.
        icon: Gamepad,
      }]
    })
    const actions: Command[] = [
      { id: 'act-add-game', group: 'Actions', title: 'Add a game', icon: Gamepad },
      { id: 'act-activate-key', group: 'Actions', title: 'Activate a key', icon: Key },
      { id: 'act-refresh', group: 'Actions', title: 'Refresh library', icon: Refresh },
      { id: 'act-update', group: 'Actions', title: 'Check for updates', icon: AppUpdate },
      { id: 'act-minimize', group: 'Actions', title: 'Minimize window', icon: Minus },
      { id: 'act-close', group: 'Actions', title: 'Close window', icon: Close },
      // Both act on the open game sheet, and show only while one is open.
      ...(selected ? [
        { id: 'act-play', group: 'Actions', title: `Play ${selected.title}`, icon: Play },
        { id: 'act-install', group: 'Actions', title: `Install ${selected.title}`, icon: Download },
      ] : []),
    ]
    return [...nav, ...gameCommands, ...actions, ...noteCommands, ...paletteExtras]
  }, [commandPaletteGames, selected, noteCommands, paletteExtras])

  const NAV_COMMANDS: Record<string, View> = {
    'nav-library': 'library',
    'nav-store': 'store',
    'nav-friends': 'friends',
    'nav-music': 'music',
    'nav-system': 'system',
    'nav-devices': 'devices',
    'nav-settings': 'settings',
  }

  const ACTION_COMMANDS: Record<string, () => void> = {
    'act-add-game': () => void addGameFromLibrary(),
    // The key popover sits under the Library's Add button, and a refresh reports in the Library.
    'act-activate-key': () => void goToView('library').then(() => setActivateKeyOpen(true)),
    'act-refresh': () => { goToView('library'); void loadLibrary(true) },
    'act-update': () => void checkUpdate().then(result => setStatus(result.message, null, false, result.ok ? 'info' : 'bad')),
    'act-minimize': () => void host.minimize(),
    'act-close': () => void host.close(),
    'act-play': () => {
      if (selected) void runPrimary(false, selected, 'play')
    },
    'act-install': () => {
      if (selected) void runPrimary(false, selected, 'install')
    },
  }

  function runCommand(command: Command) {
    const view = NAV_COMMANDS[command.id]
    if (view) {
      goToView(view)
      return
    }
    const action = ACTION_COMMANDS[command.id]
    if (action) {
      action()
      return
    }
    if (command.id.startsWith('game-')) { openGamePage(command.id.slice(5), 'instant'); return }
    if (runNoteCommand(command.id)) return
    const room = runPaletteExtra(command, message => setStatus(message, null, false, 'bad'))
    if (room === 'friends' || room === 'music') goToView(room)
  }

  function selectSource(id: string) {
    const card = selectedCard
    if (!card?.variants?.some(variant => variant.id === id)) return
    setSelectedVariantId(id)
    setStatus(null, id)
    const key = card.canonicalTitleKey
    if (!key) return
    if (!confirmedSources.current.has(key)) confirmedSources.current.set(key, settings?.preferredGameSources?.[key])
    const version = (sourceSaveVersions.current.get(key) ?? 0) + 1
    sourceSaveVersions.current.set(key, version)
    setSettings(current => current ? { ...current, preferredGameSources: { ...current.preferredGameSources, [key]: id } } : current)
    // Serialize saves so a slower old choice can never overwrite a newer one on disk.
    // The bridge atomically merges only this game's entry with other preferences.
    sourceSaves.current = sourceSaves.current.then(async () => {
      try {
        const saved = await host.setSettings({ preferredGameSources: { [key]: id } })
        confirmedSources.current.set(key, saved.preferredGameSources?.[key] ?? id)
      } catch (error) {
        if (sourceSaveVersions.current.get(key) !== version) return
        const previous = confirmedSources.current.get(key)
        setSettings(current => {
          if (!current) return current
          const preferences = { ...current.preferredGameSources }
          if (previous) preferences[key] = previous
          else delete preferences[key]
          return { ...current, preferredGameSources: preferences }
        })
        const openCard = selectedIdRef.current ? cardForExactId(gamesRef.current, selectedIdRef.current) : undefined
        if (openCard?.canonicalTitleKey === key) setSelectedVariantId(previous ?? card.selectedVariantId ?? card.id)
        setStatus(`Could not save the game source: ${error instanceof Error ? error.message : 'Try again.'}`, previous ?? card.id, true)
      }
    })
  }

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault()
        setCommandOpen((open) => !open)
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  // Ctrl+1..5 are the rooms in tab order, Ctrl+, is Settings, also from a text box (no
  // text edit uses them, and Discord's message box holds focus); a hosted app's window
  // takes its own keys before they reach the page.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (!e.ctrlKey || e.altKey || e.shiftKey || e.metaKey || e.defaultPrevented || e.isComposing) return
      if (commandOpen || activateKeyOpen) return
      if (!/^[1-9,]$/.test(e.key)) return
      const next = e.key === ',' ? 'settings' : ROOM_ORDER[Number(e.key) - 1]
      if (!next) return
      e.preventDefault()
      if (next !== view) goToView(next)
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [commandOpen, activateKeyOpen, goToView, view])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.defaultPrevented || e.isComposing || commandOpen || activateKeyOpen || depPrompt || e.key !== 'Escape') return
      if (view === 'settings') {
        e.preventDefault()
        void transition(() => setView(settingsReturnView.current))
        // Focus sat inside the room that just hid; hand it back to the gear that opened it.
        document.querySelector<HTMLElement>('.exo-settings-btn')?.focus({ preventScroll: true })
        return
      }
      if (libraryPane === 'game') {
        // Closes while a launch runs too: it goes on behind the page (GameSheet).
        closeGameSheet()
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [view, libraryPane, commandOpen, activateKeyOpen, depPrompt, closeGameSheet, busy])

  function pendingTransfer(gameId: string, kind: 'install' | 'update'): InstallProgress {
    const status = kind === 'update' ? 'Starting update…' : 'Starting install…'
    return { gameId, phase: 'preparing', percent: null, status, canCancel: true, isActive: true }
  }

  function settleTransfer(res: InstallResponse, ok: boolean, gameId: string, version: number) {
    if (version !== progressVersion.current) return
    if (res.progress) { writeProgress(res.progress); return }
    writeProgress((p) => (p?.gameId === gameId ? {
      ...p, isActive: false, canCancel: false,
      percent: res.handoffOnly ? null : p.percent, phase: res.handoffOnly ? 'idle' : ok ? 'completed' : 'failed'
    } : p))
  }

  /** True when the host asked for a dependency first; the prompt is up and the CTA is free. */
  function promptForDeps(res: LaunchResponse | InstallResponse, action: 'play' | 'install' | 'update', gameId: string): boolean {
    if (!res.needsDependencies || !res.missingDependencies?.length) return false
    setDepPrompt({ action, gameId, deps: res.missingDependencies })
    setStatus(res.message || 'Install required', gameId)
    if (action === 'play') setBusy(false)
    else writeProgress(p => p?.gameId === gameId ? null : p)
    return true
  }

  /** `scope` is the game for a status the open sheet shows, or null for one the notice stack shows. */
  async function runPlay(game: Game, skipDeps: boolean, scope: string | null) {
    setStatus('Preparing launch…', scope)
    const res = await host.launch(game.id, { skipDeps })
    if (promptForDeps(res, 'play', game.id)) return
    // The store's servers are down: its own offline mode, asked first, then Play again.
    if (res.offline) {
      setStatus(res.message, scope, true)
      const answer = await playOffline(res.offline, `Play ${game.title} offline?`, () => setStatus('Restarting in offline mode…', scope))
      if (answer?.ok) return runPlay(game, skipDeps, scope)
      if (answer) setStatus(answer.message || 'Could not switch to offline mode.', scope, true)
      setBusy(false)
      return
    }
    setStatus(res.message || (res.ok ? 'Game started.' : 'Could not start the game.'), scope, !res.ok)
    if (!res.ok) {
      setBusy(false)
      return
    }
    clearBusyUnlockTimer()
    busyUnlockTimerRef.current = window.setTimeout(() => {
      busyUnlockTimerRef.current = null
      setBusy(false)
    }, 1500)
  }

  async function runTransfer(game: Game, kind: 'install' | 'update', skipDeps: boolean, scope: string | null = game.id) {
    clearTransferFailure(game.id)
    stoppedTransfers.current.delete(game.id)
    setStatus(kind === 'update' ? 'Starting update…' : 'Starting install…', scope)
    const version = progressVersion.current
    writeProgress(p => p?.isActive ? p : pendingTransfer(game.id, kind))
    const res = kind === 'update'
      ? await host.update(game.id, { skipDeps })
      : await host.install(game.id, undefined, game.title, { skipDeps })
    if (res.queuedGameIds) setQueuedIds(res.queuedGameIds)
    if (promptForDeps(res, kind, game.id)) return res
    if (res.queued) {
      setStatus(res.message || 'Queued.', scope)
      writeProgress((p) => (p?.gameId === game.id ? null : p))
      return res
    }
    const done = kind === 'update' ? 'Update' : 'Install'
    const stopped = stoppedTransfers.current.delete(game.id)
    if (!res.ok && !stopped) setTransferFailures(current => ({ ...current, [game.id]: res.message || `${done} failed` }))
    setStatus(res.message || (res.handoffOnly ? 'Finish this in the store app.' : res.ok ? `${done} complete` : `${done} failed`), scope)
    settleTransfer(res, res.ok, game.id, version)
    return res
  }


  // Play (or Install) straight from the Library's Continue and Friends are playing: the sheet's primary action, for that card.
  const runPrimaryRef = useRef<typeof runPrimary | null>(null)
  runPrimaryRef.current = runPrimary
  const playFromLibrary = useCallback((game: Game) => {
    const shortcut = shelfAction(game)
    if (shortcut) void runPrimaryRef.current?.(false, game, shortcut.action)
  }, [])

  /** A Riot game's menu: Play as another saved account (the Riot Client switches first). */
  const playAs = useCallback(async (game: Game, account: { key: string; name: string }) => {
    setStatus(`Signing in as ${account.name}…`, null)
    try {
      const result = await host.accountsSwitch('riot', account.key)
      if (!result.ok) {
        setStatus(result.message ?? 'Could not switch the Riot account.', null, false, 'bad')
        return
      }
    } catch {
      setStatus('Could not switch the Riot account.', null, false, 'bad')
      return
    }
    playFromLibrary(game)
  }, [setStatus, playFromLibrary])

  /** Started from Continue, Friends are playing or a card's menu, no sheet shows the status (null), so it goes to the notice stack. */
  function statusScope(game: Game): string | null {
    return overlayOpen && statusBelongsToSelection(game.id, selected) ? game.id : null
  }

  async function runPrimary(skipDeps = false, target?: Game, requestedAction?: 'play' | 'install') {
    const game = target ?? selected
    if (!game || busy || actionPending.current || transferRequests.current.has(game.id)) return
    const scope = statusScope(game)
    const resolvedAction = resolvePrimaryAction(game)
    const unavailable = commandActionUnavailable(game, requestedAction)
    if (unavailable) {
      setStatus(unavailable, scope)
      return
    }
    const nextAction = resolvedAction === 'none' ? resolvedAction : requestedAction ?? resolvedAction
    if (nextAction === 'none') {
      setStatus(
        game.installed
          ? 'Exo cannot launch this copy. Open it from its store app.'
          : 'Exo cannot install this game yet. Install it from its store app.',
        scope,
      )
      return
    }
    if (nextAction === 'play') { actionPending.current = true; setBusy(true) }
    else {
      transferRequests.current.add(game.id)
      setPendingTransfers([...transferRequests.current])
    }
    clearBusyUnlockTimer()
    setStatus(null, game.id)
    setDepPrompt(null)
    setStartingId(game.id)
    try {
      if (nextAction === 'play') await runPlay(game, skipDeps, scope)
      else await runTransfer(game, nextAction, skipDeps, scope)
    } catch (e) {
      setStatus(e instanceof Error ? e.message : 'Something went wrong. Try again.', scope, scope === null, 'bad')
      if (nextAction !== 'play') setTransferFailures(current => ({ ...current, [game.id]: e instanceof Error ? e.message : nextAction === 'update' ? 'Update failed.' : 'Install failed.' }))
      if (nextAction === 'play') setBusy(false)
      writeProgress((p) => (p?.gameId === game.id && p.isActive ? { ...p, isActive: false, canCancel: false, phase: 'failed' } : p))
    } finally {
      if (nextAction === 'play') actionPending.current = false
      transferRequests.current.delete(game.id)
      setPendingTransfers([...transferRequests.current])
      setStartingId(current => (current === game.id ? null : current))
    }
  }

  const openGameFolder = useCallback(async (game: Game) => {
    try {
      const result = await host.openFolder(game.id)
      if (!result.ok) setStatus(result.message ?? 'Game folder not found.', null, false, 'bad')
    } catch (error) {
      setStatus(error instanceof Error ? error.message : 'Could not open the game folder.', null, false, 'bad')
    }
  }, [setStatus])

  const turnOnHdrForGames = useCallback(() => saveSettings({ hdrForGames: true }), [saveSettings])


  async function onPrimary() {
    if (selected?.canStop) {
      await onStopGame(selected)
      return
    }
    await runPrimary(false)
  }

  async function onStopGame(target?: Game) {
    const game = target ?? selected
    if (!game || !game.canStop || busy || actionPending.current || (progress?.isActive && progress.gameId === game.id)) return
    actionPending.current = true
    setBusy(true)
    clearBusyUnlockTimer()
    setStatus(`Closing ${game.title}…`, game.id)
    try {
      const result = await host.stop(game.id)
      setStatus(result.message ?? (result.ok ? 'Game closed.' : 'Could not close the game.'), game.id, !result.ok)
      if (result.ok) {
        beginGameRead(game.id)()
        setGames((items) => setExactRunState(items, game.id, false, false))
        const current = beginGameRead(game.id)
        void host.getGame(game.id).then((refreshed) => {
          if (!refreshed.ok || !refreshed.game) return
          const next = refreshed.game
          setGames((items) => current() ? mergeExactGame(items, next) : mergeExactGameKeepingRunState(items, next))
        }).catch((e: unknown) => report(e, 'LauncherApp.stop.getGame'))
      }
    } catch (error) {
      setStatus(error instanceof Error ? error.message : 'Could not close the game.', game.id, true)
    } finally {
      actionPending.current = false
      setBusy(false)
    }
  }

  async function onOfferMissingDeps() {
    if (!depPrompt || dependencyPending.current) return
    const prompt = depPrompt
    dependencyPending.current = true
    try {
      // All of them in one install: Windows asks once.
      const result = await host.runtimesInstall('missing', prompt.deps.map(dep => dep.id))
      const failed = result.results.find(item => item.outcome === 'failed')
      if (failed) throw new Error(`${failed.name}: ${failed.message}`)
      if (!result.ok) throw new Error(result.message || 'The runtimes could not be installed. Try again.')
      if (result.results.some(item => item.outcome === 'restart')) {
        setDepPrompt((prev) => (prev === prompt ? { ...prev, awaitingContinue: true } : prev))
        setStatus('Restart Windows to finish installing, then Check and retry.', prompt.gameId)
        return
      }
      await onContinueAfterDeps()
    } catch (error) {
      setStatus(error instanceof Error ? error.message : 'The runtime could not be installed. Try again.', prompt.gameId, true)
    } finally { dependencyPending.current = false }
  }

  async function onContinueAfterDeps(skipChecks = false) {
    if (!depPrompt) return
    const gameId = depPrompt.gameId
    const game = findLibraryGame(gamesRef.current, gameId)
    setDepPrompt(null)
    if (!game) { setStatus('The original game is no longer in your library.', gameId); return }
    await runPrimary(skipChecks, materializeVariant(game, gameId))
  }

  async function onCancel() {
    try {
      const id = progress?.isActive ? progress.gameId : null
      if (!id) return
      const res = await host.cancelInstall(id)
      if (!res.ok) throw new Error(res.message || 'Could not cancel. Try again.')
      setStatus(res.message ?? 'Cancel requested.', progress?.gameId ?? selectedIdRef.current)
    } catch (e) {
      setStatus(e instanceof Error ? e.message : 'Could not cancel. Try again.', progress?.gameId ?? selectedIdRef.current)
    }
  }

  async function onDequeue() {
    // The queued job may be another store copy of this card (the pill shows it as this one).
    const id = selected ? queuedIds.find(queued => isInstallingGame(selected, queued)) ?? selected.id : null
    if (!id) return
    try {
      const res = await host.dequeue(id)
      if (res.queuedGameIds) setQueuedIds(res.queuedGameIds)
      setStatus(res.message ?? (res.ok ? 'Removed from the queue.' : 'Could not remove this game from the queue.'), id, !res.ok)
    } catch (e) {
      setStatus(e instanceof Error ? e.message : 'Could not remove this game from the queue.', id, true)
    }
  }

  // The top bar's busy line is for work the user started (and app updates), never the boot behind the splash.
  // One predicate (useShelves) decides what the library shows, so an empty
  // library never claims there are owned games somewhere else.
  const emptyLibrary = !booting && !loading && shelves.library.length + shelves.hidden.length === 0
  const displayedGame = selected
  // Inert only while a stage is actually on screen. A pane that went inert for
  // an overlay with nothing to show left the room unresponsive.
  const overlayOpen = libraryPane === 'game' && !!displayedGame

  /** Keep the active card selected while its fixed-size details panel opens. */
  const openCard = useCallback((game: Game) => {
    setActiveGameId(game.id)
    // The card's art moves to the middle of the screen and back on close, its slot left empty meanwhile: a grid
    // tile's cover flies into the page's cover, a Continue banner opens out into the whole sheet.
    // A Continue banner flies as its whole card, its title plate and buttons with it (named apart from its art they
    // stayed behind, covered by the flying picture), into the page's cover as a grid card does. Flying into the whole
    // sheet cross-faded the card's glass with the sheet's, which flickered; the sheet comes up under its own cover.
    const art = pressedArt()
    const from = isBanner(art) ? art?.closest('.exo-continue-card') ?? art : art
    setAway(gameArtFrom.current, false)
    gameArtFrom.current = from
    void transition(() => { openGamePage(game.id, 'pointer', { game }); setAway(from, true) }, {
      kind: 'morph',
      morph: { name: 'exo-game-art', from, to: () => visible('.exo-game-detail-cover') },
    })
  }, [openGamePage])
  // The card menu's Properties: the game's page, with its Properties over it.
  const [propertiesId, setPropertiesId] = useState<string | null>(null)
  const openProperties = useCallback((game: Game) => {
    openCard(game)
    setPropertiesId(game.id)
  }, [openCard])
  useEffect(() => { if (!overlayOpen) setPropertiesId(null) }, [overlayOpen])
  // Stable while nothing is downloading, so idle progress events leave the shelves alone.
  const activeProgress = progress?.isActive ? progress : null
  const transferFor = useCallback((game: Game) => transferForGame(activeProgress, game), [activeProgress])

  async function finishOnboarding(refreshLibrary = false) {
    const next = await host.setSettings({ onboardingComplete: true })
    if (!next?.onboardingComplete) {
      throw new Error('Could not save settings. Try again.')
    }
    setSettings(next)
    if (refreshLibrary) await loadLibrary(true)
  }

  async function addFolderDuringOnboarding() {
    const result = await addPortableFolder()
    if (result.cancelled) return false
    if (!result.ok) {
      throw new Error(result.message || 'The folder could not be added.')
    }
    return true
  }

  /** Library > Add a game (its .exe) or Add a game folder: one at a time. */
  const addFromLibrary = useCallback(async (add: typeof addPortableFolder) => {
    if (addingFolder.current) return
    addingFolder.current = true
    try {
      const result = await add()
      if (result.cancelled) return
      setStatus(result.message ?? (result.ok ? 'Game added.' : 'Could not add the game.'), null, false, result.ok ? 'info' : 'bad')
      if (result.ok) await loadLibrary(true, true)
    } catch (error) {
      setStatus(error instanceof Error ? error.message : 'Could not open the picker.', null, false, 'bad')
    } finally { addingFolder.current = false }
  }, [loadLibrary, setStatus])
  const addGameFromLibrary = useCallback(() => addFromLibrary(addPortableProgram), [addFromLibrary])

  const [libraryFolders, saveFolders, hiddenGames, saveHiddenGames] = useLibraryFolders(settings, setSettings, persistSettings)

  const sortLibrary = useCallback((next: Parameters<typeof librarySortMode>[0]) => persistSettings({ sortMode: librarySortMode(next) }), [persistSettings])
  const activateKey = useCallback(() => setActivateKeyOpen(true), [])
  const closeActivateKey = useCallback(() => setActivateKeyOpen(false), [])
  const retryLibrary = useCallback(() => { void loadLibrary(true) }, [loadLibrary])
  const openStoreSettings = useCallback(() => {
    setSettingsSection({ id: 'accounts' })
    goToView('settings')
  }, [goToView])
  // Discord's settings open in the Discord room, from wherever they were asked for.
  useEffect(() => {
    const open = () => goToView('friends')
    window.addEventListener('exo-discord-settings', open)
    return () => window.removeEventListener('exo-discord-settings', open)
  }, [goToView])
  // What's New, once after an update over a ready Library with nothing else open.
  const [whatsNew, closeWhatsNew] = useWhatsNew(settings, [booting, view !== 'library', overlayOpen, commandOpen, activateKeyOpen, !!depPrompt], persistSettings)
  // A Spotify link opened elsewhere (Discord) shows in the Music room, which takes it from there.
  useEffect(() => {
    const open = () => goToView('music')
    window.addEventListener(MUSIC_LINK, open)
    return () => window.removeEventListener(MUSIC_LINK, open)
  }, [goToView])
  const updateStores = useCallback(async (next: StoreStatus[]) => {
    setStores(next)
    await loadLibrary(true, true)
  }, [loadLibrary])
  const runCommandRef = useRef(runCommand)
  runCommandRef.current = runCommand
  const selectCommand = useCallback((command: Command) => runCommandRef.current(command), [])
  const keyStatus = useCallback((message: string, fail?: boolean) => setStatus(message, null, !!fail), [setStatus])
  // Library › Select › Uninstall: each game as its page's Uninstall does it, one after another, then one line for all.
  const uninstallGames = useCallback(async (picked: Game[]) => {
    const [doing, done] = picked.every(game => game.store === 'local') ? ['Removing', 'Removed.'] : ['Uninstalling', 'Uninstalled.']
    setStatus(`${doing} ${picked.length === 1 ? picked[0].title : `${picked.length} games`}…`, null)
    let failed: string | null = null
    let handoff = false
    let queued = false
    for (const game of picked) {
      const r = await host.uninstall(game.id).catch((error: unknown) => ({ ok: false, message: error instanceof Error ? error.message : undefined, queued: false, handoffOnly: false }))
      if (!r.ok) failed ??= r.message ?? `Could not uninstall ${game.title}.`
      handoff ||= !!r.handoffOnly
      queued ||= !!r.queued
    }
    setStatus(failed ?? (handoff ? 'Finish uninstalling in the store app.' : queued ? 'Queued.' : done), null, !!failed)
    void loadLibrary(true, true)
  }, [setStatus, loadLibrary])

  // This component is not compiled (it mirrors state into refs), so every
  // render would hand each room and sheet new elements and re-render them,
  // hidden rooms included, four times a second during a download. Elements
  // that only change with their own inputs keep their identity instead.
  const dialogBlockers = [overlayOpen, commandOpen, activateKeyOpen, !!depPrompt, booting, capturesSheet.open]
  const friendsActive = isSurfaceActive(view, 'friends', dialogBlockers)
  const musicActive = isSurfaceActive(view, 'music', dialogBlockers)
  const devicesActive = isSurfaceActive(view, 'devices', dialogBlockers)
  const sortMode = settings ? librarySortMode(settings.sortMode) : null
  // The Exo Island: a Discord call outside its room.
  const openDiscordRoom = useCallback(() => goToView('friends'), [goToView])
  const openSearch = useCallback(() => setCommandOpen(true), [])
  const island = useMemo(() => (
    <ExoIsland inDiscord={view === 'friends'} onOpenDiscord={openDiscordRoom} />
  ), [view, openDiscordRoom])
  const titleBar = useMemo(() => (
    <TitleBar
      view={view}
      opening={opening}
      onView={goToView}
      onNowPlaying={openNowPlaying}
      onToggleSettings={toggleSettings}
      musicService={musicService}
      storeSales={storeSales}
      island={island}
      onSearch={openSearch}
    />
  ), [view, opening, goToView, openNowPlaying, toggleSettings, musicService, storeSales, island, openSearch])
  const settingsRoom = useMemo(() => settings && (
    <Room mounted={openedRooms.has('settings')} warm={warming} active={view === 'settings'} label="Settings" className="exo-set-room" onBackdrop={toggleSettings}>
      <SettingsPanel
        active={view === 'settings'}
        section={settingsSection}
        settings={settings}
        stores={stores}
        updateBusy={update.busy}
        updateError={update.error}
        updatePercent={update.percent}
        updateStatus={update.status}
        updateAvailable={update.available}
        onCheckUpdate={checkUpdate}
        onInstallUpdate={update.install}
        onSettings={setSettings}
        onStores={updateStores}
      />
    </Room>
  ), [openedRooms, warming, view, settingsSection, settings, stores, update.busy, update.error, update.percent, update.status, update.available, update.install, checkUpdate, updateStores, toggleSettings])
  const friendsRoom = useMemo(() => (
    <Room name="friends" mounted={openedRooms.has('friends')} warm={warming || opening === 'friends'} active={under === 'friends'} covered={view === 'settings'} label="Discord" className="relative z-10 flex min-h-0 flex-1 flex-col">
      <FriendsRoom active={friendsActive} />
    </Room>
  ), [under, view, openedRooms, warming, opening, friendsActive])
  const musicRoom = useMemo(() => (
    <Room name="music" mounted={openedRooms.has('music')} warm={warming || opening === 'music'} active={under === 'music'} covered={view === 'settings'} label="Music" className="relative z-10 flex min-h-0 flex-1 flex-col">
      <NativeMusicRoom key={musicService} service={musicService} active={musicActive} shown={view === 'music' && !booting} nowPlaying={nowPlaying} />
    </Room>
  ), [under, openedRooms, warming, opening, view, musicService, musicActive, booting, nowPlaying])
  const storeRoom = useMemo(() => (
    <Room name="store" mounted={openedRooms.has('store')} warm={warming || opening === 'store'} active={under === 'store'} covered={view === 'settings'} label="Store" className="relative z-10 flex min-h-0 flex-1 flex-col">
      <StoreRoom active={view === 'store'} games={games} onOpenGame={openLibraryGameFromStore} />
    </Room>
  ), [under, openedRooms, warming, opening, view, games, openLibraryGameFromStore])
  const devicesRoom = useMemo(() => (
    <Room mounted={openedRooms.has('devices')} warm={warming} active={under === 'devices'} covered={view === 'settings'} label="Devices" className="relative z-10 flex min-h-0 flex-1 flex-col">
      <DevicesRoom active={devicesActive} />
    </Room>
  ), [under, view, openedRooms, warming, devicesActive])
  const systemRoom = useMemo(() => (
    <Room name="system" mounted={openedRooms.has('system')} warm={warming || opening === 'system'} active={under === 'system'} covered={view === 'settings'} label="PC" className="relative z-10 flex min-h-0 flex-1 flex-col">
      <Suspense fallback={null}><PcRoom active={view === 'system'} jump={gpuJump} games={games} settings={settings} onSave={saveSettings} /></Suspense>
    </Room>
  ), [under, openedRooms, warming, opening, view, gpuJump, games, settings, saveSettings])
  const libraryRoom = useMemo(() => sortMode && (
    <Room mounted={openedRooms.has('library')} active={under === 'library'} covered={view === 'settings'} label="Library" className="exo-under-bar relative z-10 flex min-h-0 flex-1">
      <LibraryPane
        mainRef={libraryMainRef}
        active={under === 'library'}
        overlayOpen={overlayOpen}
        empty={emptyLibrary}
        error={libraryError}
        games={shelves.library}
        hidden={shelves.hidden}
        hiddenIds={hiddenGames}
        onHidden={saveHiddenGames}
        playing={shelves.playing}
        selectedId={selectedId}
        activeGameId={activeGameId}
        queuedIds={queuedIds}
        sort={sortMode}
        reduceMotion={layout.reduceMotion}
        transferFor={transferFor}
        onActiveGameChange={setActiveGameId}
        onOpen={openCard}
        onPlay={playFromLibrary}
        onFindInStore={findInStore}
        onPlayAs={playAs}
        startingId={startingId}
        onOpenFolder={openGameFolder}
        onProperties={openProperties}
        onSort={sortLibrary}
        folders={libraryFolders}
        onFolders={saveFolders}
        onAddGame={addGameFromLibrary}
        onActivateKey={activateKey}
        onRefresh={refreshEverything}
        onRetry={retryLibrary}
        onStoreSettings={openStoreSettings}
        onUninstall={uninstallGames}
      />
    </Room>
  ), [under, view, openedRooms, overlayOpen, emptyLibrary, libraryError, shelves, selectedId, activeGameId, queuedIds, sortMode, layout.reduceMotion,
    transferFor, openCard, playFromLibrary, findInStore, playAs, startingId, openGameFolder, openProperties, sortLibrary, libraryFolders, saveFolders, hiddenGames, saveHiddenGames, addGameFromLibrary,
    activateKey, refreshEverything, retryLibrary, openStoreSettings, uninstallGames])
  const activateKeySheet = useMemo(() => (
    <Suspense fallback={null}><ActivateKeySheet open={activateKeyOpen} onClose={closeActivateKey} onStatus={keyStatus} /></Suspense>
  ), [activateKeyOpen, closeActivateKey, keyStatus])
  const commandMenu = useMemo(() => (
    <Suspense fallback={null}><CommandMenu
      open={commandOpen}
      onOpenChange={setCommandOpen}
      commands={commands}
      groupOrder={['Go to', 'Library', 'Actions', 'Notes', 'Music', 'Discord']}
      onSelect={selectCommand}
      inputPlaceholder="Search commands and games"
      className="pointer-events-none absolute inset-0 z-[60] h-full w-full bg-transparent"
    /></Suspense>
  ), [commandOpen, commands, selectCommand])

  if (holdsBoot(update)) {
    return (
      <div className="exo-app">
        <BootSplash checkingUpdate />
      </div>
    )
  }

  const gate = bootGate({
    settings,
    settingsError,
    storeMatrixReady,
    stores,
    onRetrySettings: () => void loadSettings(),
    onSettings: setSettings,
    onStores: setStores,
    onComplete: finishOnboarding,
    onAddFolder: addFolderDuringOnboarding,
  })
  if (gate || !settings) return gate

  return (
    <div className="exo-app">
      {booting && <BootSplash revealing={bootRevealing} reduceMotion={layout.reduceMotion} onComplete={() => setBooting(false)} />}
      {titleBar}

      <ShellNotices
        transferFailures={Object.entries(transferFailures).filter(([id, message]) => !overlayOpen || !selected || !cardForExactId([selected], id) || status.gameId !== id || status.message !== message)
          .map(([gameId, message]) => ({ gameId, message, title: findLibraryGame(games, gameId)?.title ?? gameId }))}
        onDismissTransferFailure={clearTransferFailure}
        depPrompt={depPrompt}
        onSkipDeps={() => void onContinueAfterDeps(true)}
        onInstallDeps={() => void onOfferMissingDeps()}
        onContinueAfterDeps={() => void onContinueAfterDeps()}
        onDismissDeps={() => setDepPrompt(null)}
        update={update}
        showUpdate={view !== 'settings'}
        notice={shellNotice(status)}
        onDismissNotice={() => setStatus(null, null)}
        passing={status.notice}
      />

      <DiscordNotices onOpen={openNotice} />
      <StoreOutageNotices />
      <NoticeStack />
      <ConfirmHost />

      {settingsRoom}
      {friendsRoom}
      {musicRoom}
      {storeRoom}
      {systemRoom}
      {devicesRoom}
      {libraryRoom}
      <ErrorBoundary
        label="Game sheet"
        onReset={closeGamePage}
        className="fixed inset-x-0 bottom-0 top-[var(--exo-titlebar-h,52px)] z-[80] bg-black/80"
      >
        <Suspense fallback={null}><GameSheet
          game={displayedGame}
          open={overlayOpen}
          instant={overlayMotion === 'instant'}
          openedAt={overlayOpenedAtRef.current}
          statusMsg={status.message}
          statusGameId={status.gameId}
          busy={busy || pendingTransfers.includes(displayedGame?.id ?? '')}
          progress={progress}
          onPrimary={() => void onPrimary()}
          onStop={() => void onStopGame()}
          onCancel={() => void onCancel()}
          queued={!!displayedGame && queuedIds.some(id => isInstallingGame(displayedGame, id))}
          onOpenSystem={(page) => {
            if (busy || !displayedGame) return
            closeGamePage()
            setGpuJump({ tab: page })
            openGpu()
          }}
          onDequeue={() => void onDequeue()}
          hdrForGames={settings.hdrForGames === true}
          onHdrForGames={turnOnHdrForGames}
          propertiesOpen={!!displayedGame && propertiesId === displayedGame.id}
          onPropertiesOpen={open => setPropertiesId(open && displayedGame ? displayedGame.id : null)}
          onClose={() => { if (!busy) closeGameSheet() }}
          onSelectSource={selectSource}
          onStatusFor={(gameId, message, sticky) => setStatus(message, gameId, !!sticky)}
          onUninstalled={() => {
            closeGamePage()
            void loadLibrary(true, true)
          }}
        /></Suspense>
      </ErrorBoundary>
      <Suspense fallback={null}><CapturesOverlay games={games} onDiscord={sendToDiscord} /></Suspense>
      {activateKeySheet}
      {commandMenu}
      <Suspense fallback={null}><WhatsNewSheet releases={whatsNew} update={update} onClose={closeWhatsNew} /></Suspense>
    </div>
  )
}
