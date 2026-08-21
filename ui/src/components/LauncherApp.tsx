/**
 * Exo Launcher shell — installed library, pinned row, search discovers installs.
 * CTA strings (Play | Download | Install | Update) and cancelInstall live via GamePage + host.
 */
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type CSSProperties } from 'react'
import { ChevronLeft, ChevronRight, Loader2, Settings } from '../brand/icons'
import { ExoMark } from '../brand/ExoMark'
import {
  host,
  onHostEvent,
  resolvePrimaryAction,
  type Game,
  type GameVariant,
  type InstallProgress,
  type LauncherSettings,
  type MissingDependency,
  type ProfileResponse,
  type StoreStatus,
} from '../lib/host'
import { smartSearchScore, sortGames, titleIdentity } from '../lib/utils'
import { pickNow, retainNow } from '../lib/now'
import { addPortableFolder } from '../lib/portable'
import { CACHE_KEYS, writeCache } from '../lib/cache'
import { applyTitlebarIdentity } from '../lib/titlebarIdentity'
import { installGamepadNavigation } from '../lib/gamepadNavigation'
import { preloadUpscalerStatuses } from '../lib/upscalerCache'
import { BannerIn, GameOverlay } from '../motion'
import { preloadInitialCoverArt, steamAppId } from './CoverArt'
import { AppAmbient } from './AppAmbient'
import { BrowseShelf } from './BrowseShelf'
import { GamePage } from './GamePage'
import { GameCard } from './GameCard'
import { NowStage } from './NowStage'
import { OnboardingPanel } from './OnboardingPanel'
import { SettingsPanel } from './SettingsPanel'
import { FriendsRoom } from './FriendsRoom'
import { ProfileRoom } from './ProfileRoom'
import { WindowChrome } from './WindowChrome'

type View = 'library' | 'friends' | 'profile' | 'settings'
const MIN_BOOT_SPLASH_MS = 120

const NAV_TABS: Array<{ id: View; label: string }> = [
  { id: 'library', label: 'Library' },
  { id: 'friends', label: 'Friends' },
]

type CatalogHit = {
  id: string
  title: string
  store: string
  coverUrl?: string | null
  coverSource?: string | null
  owned?: boolean
  installed?: boolean
  canInstall?: boolean
  source?: string
  launchTarget?: string | null
}

function mergedCoverUrl(incoming: Game, previous?: Game): string | null {
  const incomingRevision = incoming.artRevision ?? 0
  const previousRevision = previous?.artRevision ?? 0
  if (incomingRevision > previousRevision) return incoming.coverUrl ?? null
  if (incoming.coverUrl) return incoming.coverUrl
  if (previous?.coverUrl) return previous.coverUrl
  return null
}

function hitToGame(hit: CatalogHit, library: Game[] = []): Game {
  const existing = findLibraryGame(library, hit.id)
  const entitlementState = existing?.entitlementState ?? (hit.owned ? 'owned' : 'unknown')
  const entitlementBlocked = entitlementState === 'notOwned' || entitlementState === 'unverified'
  const owned = !entitlementBlocked && !!(hit.owned || existing?.owned)
  const installed = !!(hit.installed || existing?.installed)
  const canInstall = !installed && owned && !!(hit.canInstall || existing?.canInstall)
  return {
    id: hit.id,
    title: hit.title,
    store: hit.store,
    installed,
    owned,
    entitlementState,
    canInstall,
    primaryAction: entitlementBlocked ? 'none' : installed ? 'play' : canInstall ? 'install' : 'none',
    coverUrl: hit.coverUrl ?? existing?.coverUrl,
    coverSource: hit.coverSource ?? existing?.coverSource,
    status: existing?.status ?? (installed ? 'Ready' : owned ? 'Owned' : 'Catalog'),
    deps: [],
    launchNote: existing?.launchNote ?? '',
    launchTarget: hit.launchTarget ?? existing?.launchTarget,
    updateAvailable: existing?.updateAvailable,
  }
}

function findLibraryGame(games: Game[], id: string): Game | undefined {
  const needle = id.toLowerCase()
  return (
    games.find(
      (game) =>
        game.id.toLowerCase() === needle ||
        game.variants?.some((variant) => variant.id.toLowerCase() === needle),
    ) ?? games.find((game) => sameSteamApp(game, id))
  )
}

function findLibraryGameByTitle(games: Game[], title: string): Game | undefined {
  const key = titleIdentity(title)
  if (!key) return undefined
  return games.find((game) => isSearchableLibraryGame(game) && titleIdentity(game.title) === key)
}

function sameSteamApp(game: Game, id: string): boolean {
  const app = id.match(/^steam:(\d+)/i)?.[1]
  if (!app) return false
  return steamAppId(game) === app
}

function isSearchableLibraryGame(game: Game): boolean {
  if (game.isAddPortable || game.id === 'local:add') return false
  return !!(game.installed || game.owned || game.canInstall)
}

function libraryPresenceKeys(games: Game[]): Set<string> {
  const keys = new Set<string>()
  for (const game of games) {
    if (!isSearchableLibraryGame(game)) continue
    keys.add(game.id.toLowerCase())
    const app = steamAppId(game)
    if (app) keys.add(`steam:${app}`)
    const titleKey = titleIdentity(game.title)
    if (titleKey) keys.add(`title:${titleKey}`)
    for (const variant of game.variants ?? []) keys.add(variant.id.toLowerCase())
  }
  return keys
}

function catalogHitIsPresent(hit: CatalogHit, present: Set<string>): boolean {
  if (hit.installed) return true
  if (present.has(hit.id.toLowerCase())) return true
  const titleKey = titleIdentity(hit.title)
  if (titleKey && present.has(`title:${titleKey}`)) return true
  const app = hit.id.match(/^steam:(\d+)/i)?.[1]
  if (app && present.has(`steam:${app}`)) return true
  return false
}

function isInstallingGame(game: Game, installingId: string | null): boolean {
  if (!installingId) return false
  return (
    game.id === installingId ||
    !!game.variants?.some((variant) => variant.id === installingId) ||
    sameSteamApp(game, installingId)
  )
}

function transferForGame(
  progress: InstallProgress | null,
  game: Game,
): { percent: number | null } | null {
  if (!progress?.isActive || !progress.gameId) return null
  if (!isInstallingGame(game, progress.gameId)) return null
  return { percent: progress.percent ?? null }
}

function mergeHostGames(prev: Game[], incoming: Game[], selectedId: string | null): Game[] {
  const nextIds = new Set(incoming.map((game) => game.id.toLowerCase()))
  const retained = prev.filter((game) => {
    if (nextIds.has(game.id.toLowerCase())) return false
    if (!selectedId) return false
    if (game.id === selectedId || game.variants?.some((variant) => variant.id === selectedId)) return true
    return sameSteamApp(game, selectedId)
  })
  return retained.length ? [...incoming, ...retained] : incoming
}

function BootSplash() {
  return (
    <div className="exo-boot" role="status" aria-label="Preparing library and game tools">
      <ExoMark size={56} alive />
    </div>
  )
}

function cardForExactId(games: Game[], id: string | null): Game | null {
  if (!id) return null
  return games.find((game) =>
    game.id === id || game.variants?.some((variant) => variant.id === id),
  ) ?? null
}

function materializeVariant(card: Game, variantId: string | null): Game {
  // Prefer the source the user picked. If they haven't picked one yet, surface
  // a running source so Stop is one click without hunting the store picker.
  const preferredId =
    variantId ??
    card.variants?.find((item) => item.canStop || item.isRunning)?.id ??
    null
  const variant = preferredId
    ? card.variants?.find((item) => item.id === preferredId)
    : undefined
  if (!variant) return card
  return {
    ...card,
    id: variant.id,
    store: variant.store,
    installed: variant.installed,
    owned: variant.owned,
    entitlementState: variant.entitlementState,
    updateAvailable: variant.updateAvailable,
    canInstall: variant.canInstall,
    primaryAction: variant.primaryAction,
    path: variant.path,
    launchTarget: variant.launchTarget,
    playtimeMinutes: variant.playtimeMinutes,
    lastPlayedUtc: variant.lastPlayedUtc,
    status: variant.status,
    isRunning: variant.isRunning,
    canStop: variant.canStop,
    selectedVariantId: variant.id,
  }
}

function keepPlaytime(next?: number | null, prev?: number | null): number | null | undefined {
  const n = typeof next === 'number' && next > 0 ? next : null
  const p = typeof prev === 'number' && prev > 0 ? prev : null
  if (n != null && p != null) return Math.max(n, p)
  return n ?? p ?? next ?? prev
}

function keepLastPlayed(next?: string | null, prev?: string | null): string | null | undefined {
  if (!next) return prev ?? next
  if (!prev) return next
  const nextTime = Date.parse(next)
  const prevTime = Date.parse(prev)
  if (Number.isNaN(nextTime)) return prev
  if (Number.isNaN(prevTime)) return next
  return nextTime >= prevTime ? next : prev
}

function variantFromGame(game: Game, previous?: GameVariant): GameVariant {
  return {
    id: game.id,
    store: game.store,
    installed: game.installed,
    owned: game.owned,
    entitlementState: game.entitlementState,
    updateAvailable: game.updateAvailable,
    canInstall: game.canInstall,
    primaryAction: game.primaryAction,
    path: game.path,
    launchTarget: game.launchTarget,
    playtimeMinutes: keepPlaytime(game.playtimeMinutes, previous?.playtimeMinutes),
    lastPlayedUtc: keepLastPlayed(game.lastPlayedUtc, previous?.lastPlayedUtc),
    status: game.status,
    isRunning: game.isRunning,
    canStop: game.canStop,
  }
}

/** Keep a grouped card intact when `game.get` refreshes one of its exact sources. */
function mergeExactGame(items: Game[], refreshed: Game): Game[] {
  return items.map((item) => {
    if (item.id === refreshed.id) {
      // Preserve pin/cover when a single-source refresh omits them or races unpin.
      return {
        ...refreshed,
        coverUrl: refreshed.coverUrl || item.coverUrl || null,
        isFavorite: refreshed.isFavorite ?? item.isFavorite,
        playtimeMinutes: keepPlaytime(refreshed.playtimeMinutes, item.playtimeMinutes),
        lastPlayedUtc: keepLastPlayed(refreshed.lastPlayedUtc, item.lastPlayedUtc),
        variants: refreshed.variants?.length ? refreshed.variants : item.variants,
      }
    }
    if (!item.variants?.some((variant) => variant.id === refreshed.id)) return item
    const variants = item.variants.map((variant) =>
      variant.id === refreshed.id ? variantFromGame(refreshed, variant) : variant,
    )
    const anyRunning = variants.some((variant) => variant.isRunning)
    const anyStop = variants.some((variant) => variant.canStop)
    return {
      ...item,
      variants,
      isRunning: anyRunning,
      canStop: anyStop,
      playtimeMinutes: item.id === refreshed.id
        ? keepPlaytime(refreshed.playtimeMinutes, item.playtimeMinutes)
        : item.playtimeMinutes,
    }
  })
}

function gameMatchesFavoriteIds(game: Game, favoriteIds: Set<string> | null): boolean {
  if (!favoriteIds) return !!game.isFavorite
  if (favoriteIds.has(game.id.toLowerCase())) return true
  if (game.variants?.some((v) => favoriteIds.has(v.id.toLowerCase()))) return true
  return !!game.isFavorite
}

function statusBelongsToSelection(statusGameId: string | null, selected: Game | null): boolean {
  if (!statusGameId || !selected) return false
  if (statusGameId === selected.id) return true
  if (selected.selectedVariantId && statusGameId === selected.selectedVariantId) return true
  if (selected.variants?.some((v) => v.id === statusGameId)) return true
  return sameSteamApp(selected, statusGameId)
}

/** Update one exact source's transient run state without collapsing its card. */
function setExactRunState(items: Game[], id: string, isRunning: boolean, canStop: boolean): Game[] {
  return items.map((item) => {
    const variants = item.variants?.map((variant) =>
      variant.id === id ? { ...variant, isRunning, canStop } : variant,
    )
    if (item.id === id) return { ...item, isRunning, canStop, variants }
    if (variants?.some((variant) => variant.id === id)) {
      const anyRunning = variants.some((variant) => !!variant.isRunning)
      const anyStop = variants.some((variant) => !!variant.canStop)
      return { ...item, variants, isRunning: anyRunning, canStop: anyStop }
    }
    return item
  })
}

export function LauncherApp() {
  useEffect(() => installGamepadNavigation(), [])

  const [games, setGames] = useState<Game[]>([])
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [selectedVariantId, setSelectedVariantId] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [libraryError, setLibraryError] = useState<string | null>(null)
  const [booting, setBooting] = useState(true)
  const coldStart = useRef(true)
  const bootAt = useRef(Date.now())
  const bootPreparationStarted = useRef(false)
  const [busy, setBusy] = useState(false)
  const [statusMsg, setStatusMsg] = useState<string | null>(null)
  const [statusGameId, setStatusGameId] = useState<string | null>(null)
  // Messages that ask the user to do something must not vanish on a timer.
  const [statusSticky, setStatusSticky] = useState(false)
  const [progress, setProgress] = useState<InstallProgress | null>(null)
  const [view, setView] = useState<View>('library')
  const [settings, setSettings] = useState<LauncherSettings | null>(null)
  const [settingsError, setSettingsError] = useState<string | null>(null)
  const [stores, setStores] = useState<StoreStatus[]>([])
  const [storeMatrixReady, setStoreMatrixReady] = useState(false)
  const [queuedIds, setQueuedIds] = useState<string[]>([])
  const [query, setQuery] = useState('')
  const [depPrompt, setDepPrompt] = useState<{
    action: 'play' | 'install' | 'update'
    deps: MissingDependency[]
    awaitingContinue?: boolean
  } | null>(null)
  const [updateBanner, setUpdateBanner] = useState<string | null>(null)
  const [updateLatest, setUpdateLatest] = useState<string | null>(null)
  const [updateBusy, setUpdateBusy] = useState(false)
  const [updatePercent, setUpdatePercent] = useState(0)
  const [activeGameId, setActiveGameId] = useState<string | null>(null)
  const [libraryPane, setLibraryPane] = useState<'shelf' | 'game'>('shelf')
  const [overlayMotion, setOverlayMotion] = useState<'pointer' | 'instant'>('pointer')
  const [catalogHits, setCatalogHits] = useState<CatalogHit[]>([])
  const [catalogSearching, setCatalogSearching] = useState(false)
  const [authMsg, setAuthMsg] = useState<string | null>(null)
  const [selfAvatarImage, setSelfAvatarImage] = useState<string | null>(null)
  const [selfName, setSelfName] = useState<string | null>(null)
  const selfAvatarGameIdRef = useRef<string | null>(null)
  const selfAvatarImageRef = useRef<string | null>(null)
  const lastProfileRef = useRef<ProfileResponse | null>(null)
  const askedProfileAfterLibrary = useRef(false)
  const searchGen = useRef(0)
  const searchInputRef = useRef<HTMLInputElement>(null)
  const selectedIdRef = useRef<string | null>(null)
  const holdNowId = useRef<string | null>(null)
  const nowIdRef = useRef<string | null>(null)
  const gamesRef = useRef<Game[]>([])
  const libraryMainRef = useRef<HTMLElement>(null)
  const pinnedTrackRef = useRef<HTMLDivElement>(null)

  selectedIdRef.current = selectedId
  gamesRef.current = games
  const selfInitial = (selfName?.trim()?.[0] ?? 'E').toUpperCase()
  const actionLocked = busy || !!progress?.isActive
  const lockedGameId = progress?.isActive ? progress.gameId : statusGameId ?? selectedId

  function isCardActionLocked(game: Game): boolean {
    if (!actionLocked || !lockedGameId) return false
    // Exact source installs/updates use a variant id; keep that card interactive
    // for status while other cards stay locked.
    if (lockedGameId === game.id) return false
    if (game.variants?.some((variant) => variant.id === lockedGameId)) return false
    return true
  }

  const selectCard = useCallback((id: string | null) => {
    selectedIdRef.current = id
    setSelectedId(id)
    setSelectedVariantId(null)
  }, [])

  function openGamePage(id: string, motion: 'pointer' | 'instant' = 'pointer') {
    holdNowId.current = nowIdRef.current ?? holdNowId.current
    const catalogHit = catalogHits.find((item) => item.id === id)
    const existing =
      findLibraryGame(games, id) ??
      (catalogHit ? findLibraryGameByTitle(games, catalogHit.title) : undefined)
    if (existing) {
      selectedIdRef.current = existing.id
      selectCard(existing.id)
      if (existing.id !== id && existing.variants?.some((variant) => variant.id === id)) {
        setSelectedVariantId(id)
      }
    } else {
      const hit = catalogHits.find((item) => item.id === id)
      if (hit) {
        const card = hitToGame(hit, games)
        selectedIdRef.current = card.id
        setGames((prev) => (prev.some((game) => game.id === card.id) ? prev : [...prev, card]))
      } else {
        selectedIdRef.current = id
      }
      selectCard(id)
    }
    setOverlayMotion(motion)
    // Search is a temporary discovery mode. Once a title is opened, return
    // to the normal library surface so a stale query cannot hide the rest of
    // the collection when the overlay closes.
    searchGen.current += 1
    setQuery('')
    setCatalogHits([])
    setCatalogSearching(false)
    setLibraryPane('game')
    setView('library')
    setActionStatus(null, null)
  }

  function setActionStatus(
    message: string | null,
    gameId = selectedIdRef.current,
    sticky = false,
  ) {
    setStatusMsg(message)
    setStatusGameId(message ? gameId ?? null : null)
    setStatusSticky(Boolean(message) && sticky)
  }

  const loadLibrary = useCallback(async (force = false) => {
    if (coldStart.current) {
      bootAt.current = Date.now()
      setLoading(true)
    }
    setActionStatus(null, null)
    setLibraryError(null)
    try {
      const res = await host.getLibrary(force)
      const favoriteIds = res.favorites
        ? new Set(res.favorites.map((id) => id.toLowerCase()))
        : null
      const previous = gamesRef.current
      const prevById = new Map(previous.map((g) => [g.id, g]))
      const incoming = res.games.map((g) => {
        const old = prevById.get(g.id)
        const coverUrl = mergedCoverUrl(g, old)
        // Host OverlayUserPrefs is authoritative; also match variant ids so a
        // pin on steam:X still marks the grouped card after a scan.
        const isFavorite = gameMatchesFavoriteIds(g, favoriteIds)
        return { ...g, coverUrl, isFavorite }
      })
      const merged = mergeHostGames(previous, incoming, selectedIdRef.current)
      gamesRef.current = merged
      setGames(merged)
      if (res.stores?.length) setStores(res.stores)
      if (res.queuedGameIds) setQueuedIds(res.queuedGameIds)
      if (res.progress?.isActive) setProgress(res.progress)
      writeCache(CACHE_KEYS.library, res.games)
    } catch (e) {
      const message = e instanceof Error ? e.message : 'Library load failed'
      setLibraryError(message)
      setActionStatus(message, null)
    } finally {
      setLoading(false)
    }
  }, [])

  const loadSettings = useCallback(async () => {
    try {
      const s = await host.getSettings()
      setSettings(s)
      setSettingsError(null)
    } catch (error) {
      setSettingsError(error instanceof Error ? error.message : 'Settings could not be loaded')
    }
  }, [])

  // Titlebar identity is the Exo profile the user authored, not a store persona.
  // The chip is their uploaded picture, or initials. Library covers stay off it.
  const applyIdentity = useCallback((self: ProfileResponse) => {
    lastProfileRef.current = self
    const next = applyTitlebarIdentity(
      self,
      {
        avatarGameId: selfAvatarGameIdRef.current,
        avatarImageUrl: selfAvatarImageRef.current,
      },
      gamesRef.current.length > 0,
    )
    if (!next) return
    if (next.cacheable) writeCache(CACHE_KEYS.profile, self)
    setSelfName(next.name)
    selfAvatarGameIdRef.current = next.avatarGameId
    selfAvatarImageRef.current = next.avatarImageUrl
    setSelfAvatarImage(next.avatarImageUrl)
  }, [])

  useEffect(() => {
    if (settings?.onboardingComplete !== true) return

    const load = () => {
      void host.profileGet().then(applyIdentity).catch(() => {})
    }
    load()
    // profile.set / setLook / setShowcase / image calls all push this with the
    // whole profile, so the chip repaints without another round trip.
    const offProfile = onHostEvent('profile.updated', (data) => {
      const next = data as ProfileResponse | null
      if (next?.ok) applyIdentity(next)
      else load()
    })
    return () => {
      offProfile()
    }
  }, [applyIdentity, settings?.onboardingComplete])

  useEffect(() => {
    if (games.length === 0 || askedProfileAfterLibrary.current) return
    askedProfileAfterLibrary.current = true
    // Re-resolve a game-backed avatar after the library arrives without making
    // a second profile RPC. If the first read is still in flight it will apply
    // against the now-populated games ref when it settles.
    const cached = lastProfileRef.current
    if (cached) applyIdentity(cached)
  }, [games.length, applyIdentity])

  useEffect(() => {
    void loadSettings()
    void loadLibrary()
    void host
      .storesMatrix()
      .then(setStores)
      .catch(() => {})
      .finally(() => setStoreMatrixReady(true))
    // Network update discovery is not first-paint work. Give the cached shell
    // and library a quiet beat before starting it.
    const updateTimer = window.setTimeout(() => {
      void host
        .checkUpdate()
        .then((r) => {
          if (r.updateAvailable && r.message) {
            const ver = r.latest ?? ''
            if (ver && sessionStorage.getItem('exo.launcher.dismissed-update') === ver) return
            setUpdateBanner(r.message)
            setUpdateLatest(ver || null)
          }
        })
        .catch(() => {})
    }, 900)

    const offUpdate = onHostEvent('app.updateProgress', (data) => {
      const d = data as { percent?: number }
      if (typeof d?.percent === 'number' && d.percent >= 0) {
        setUpdatePercent(Math.min(100, Math.round(d.percent)))
      }
    })

    const offLaunch = onHostEvent('launch.status', (data) => {
      const d = data as { message?: string; phase?: string; ok?: boolean; gameId?: string }
      // Detail rail owns status for the selected game — avoid a second sticky banner.
      if (d?.message) setActionStatus(d.message, d.gameId ?? null)
      if (
        d?.phase === 'running' ||
        d?.phase === 'stopped' ||
        d?.phase === 'stopFailed' ||
        d?.phase === 'failed' ||
        d?.phase === 'handoff' ||
        d?.phase === 'needsDeps' ||
        d?.ok === false
      ) {
        setBusy(false)
      }
      if ((d?.phase === 'running' || d?.phase === 'stopped') && d.gameId) {
        if (d.phase === 'stopped') {
          // The native Stop result is emitted only after the exact verified
          // game process is gone. Release the CTA immediately; the follow-up
          // discovery scan is reconciliation, not part of the user action.
          setGames((items) => setExactRunState(items, d.gameId!, false, false))
        }
        void host.getGame(d.gameId).then((result) => {
          if (!result.ok || !result.game) return
          setGames((items) => mergeExactGame(items, result.game!))
        }).catch(() => {})
      }
    })
    const offProgress = onHostEvent('install.progress', (data) => {
      const p = data as InstallProgress
      setProgress(p)
      // Do not mirror every install.progress status into sticky statusMsg when detail owns that game.
      if (p?.isActive && p.status) {
        const sel = selectedIdRef.current
        if (!sel || sel !== p.gameId) setActionStatus(p.status, p.gameId)
      }
      if (!p?.isActive) {
        setBusy(false)
        if (p?.phase === 'completed' || p?.phase === 'failed' || p?.phase === 'cancelled') {
          if (p.status) setActionStatus(p.status, p.gameId)
          if (p.phase === 'completed') void loadLibrary(true)
        }
      }
    })
    const offCovers = onHostEvent('library.updated', (data) => {
      const d = data as { games?: Game[] }
      if (!Array.isArray(d?.games)) return
      writeCache(CACHE_KEYS.library, d.games)
      if (d.games.length === 0) {
        setGames([])
        return
      }
      // Never wipe a good cover with null during cache warm.
      // Trust host isFavorite so unpin cannot be re-applied by a stale local pin.
      setGames((prev) => {
        const prevById = new Map(prev.map((g) => [g.id, g]))
        const incoming = d.games!.map((g) => {
          const old = prevById.get(g.id)
          const coverUrl = mergedCoverUrl(g, old)
          return {
            ...old,
            ...g,
            coverUrl,
            isFavorite: !!g.isFavorite,
            playtimeMinutes: keepPlaytime(g.playtimeMinutes, old?.playtimeMinutes),
            lastPlayedUtc: keepLastPlayed(g.lastPlayedUtc, old?.lastPlayedUtc),
            isRunning: typeof g.isRunning === 'boolean' ? g.isRunning : !!old?.isRunning,
            canStop: typeof g.canStop === 'boolean' ? g.canStop : !!old?.canStop,
          }
        })
        return mergeHostGames(prev, incoming, selectedIdRef.current)
      })
    })
    return () => {
      window.clearTimeout(updateTimer)
      offLaunch()
      offProgress()
      offCovers()
      offUpdate()
    }
  }, [loadLibrary, loadSettings])

  useEffect(() => {
    if (
      !booting ||
      bootPreparationStarted.current ||
      settings?.onboardingComplete !== true ||
      loading
    ) return
    bootPreparationStarted.current = true
    const prepare = async () => {
      const ordered = [
        ...games.filter((game) => game.isFavorite),
        ...games.filter((game) => !game.isFavorite && game.installed),
        ...games.filter((game) => !game.isFavorite && !game.installed),
      ]
      await Promise.allSettled([
        preloadInitialCoverArt(ordered, 10),
        preloadUpscalerStatuses(games),
      ])
      const wait = Math.max(0, MIN_BOOT_SPLASH_MS - (Date.now() - bootAt.current))
      if (wait > 0) await new Promise<void>((resolve) => window.setTimeout(resolve, wait))
      coldStart.current = false
      setBooting(false)
    }
    void prepare()
  }, [booting, games, loading, settings?.onboardingComplete])

  useEffect(() => {
    if (!progress?.isActive || !progress.gameId) return
    setView('library')
    const card = cardForExactId(games, progress.gameId)
    setSelectedId(card?.id ?? progress.gameId)
    setSelectedVariantId(card && card.id !== progress.gameId ? progress.gameId : null)
  }, [games, progress?.gameId, progress?.isActive])

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

  // Keep Stop current: re-scan the selected title for a running game process
  // while the detail panel is open (covers external Steam/Epic launches).
  useEffect(() => {
    const selectedCardForRefresh = games.find((game) => game.id === selectedId)
    const exactId = selectedVariantId && selectedCardForRefresh?.variants?.some((variant) => variant.id === selectedVariantId)
      ? selectedVariantId
      : selectedId
    if (!exactId) return
    let active = true
    const refreshRunState = () => {
      void host.getGame(exactId).then((result) => {
        if (!active || !result.ok || !result.game) return
        const next = result.game
        setGames((items) => {
          let updated = setExactRunState(items, next.id, !!next.isRunning, !!next.canStop)
          for (const variant of next.variants ?? []) {
            updated = setExactRunState(updated, variant.id, !!variant.isRunning, !!variant.canStop)
          }
          return updated
        })
      }).catch(() => {})
    }
    refreshRunState()
    const timer = window.setInterval(refreshRunState, 2000)
    return () => {
      active = false
      window.clearInterval(timer)
    }
  }, [selectedId, selectedVariantId])

  useEffect(() => {
    if (view !== 'library') setLibraryPane('shelf')
    if (view !== 'library') {
      // Search is a library affordance. Leaving the library must also clear its
      // query/results so returning never looks like a stale filtered page.
      searchGen.current += 1
      setQuery('')
      setCatalogHits([])
      setCatalogSearching(false)
    }
  }, [view])

  // Auto-clear terminal launch/install messages after a short delay. Anything
  // that tells the user to go finish a step elsewhere stays until they act.
  useEffect(() => {
    if (!statusMsg || progress?.isActive || statusSticky) return
    const t = window.setTimeout(() => setActionStatus(null, null), 4500)
    return () => window.clearTimeout(t)
  }, [statusMsg, progress?.isActive, statusSticky])

  // Merge store search partials immediately.
  useEffect(() => {
    return onHostEvent('stores.search.partial', (data) => {
      const d = data as { query?: string; results?: CatalogHit[]; gen?: number }
      const q = query.trim()
      if (!d?.results || !q || q.length < 2) return
      if (d.query && d.query.trim().toLowerCase() !== q.toLowerCase()) return
      const present = libraryPresenceKeys(games)
      setCatalogHits((prev) => {
        const map = new Map<string, CatalogHit>()
        for (const h of prev) {
          if (!catalogHitIsPresent(h, present)) map.set(h.id.toLowerCase(), h)
        }
        for (const h of d.results!) {
          if (catalogHitIsPresent(h, present)) continue
          map.set(h.id.toLowerCase(), h)
        }
        return Array.from(map.values())
      })
    })
  }, [query, games])

  const runAppUpdate = useCallback(async () => {
    if (updateBusy) return
    setUpdateBusy(true)
    setUpdatePercent(0)
    try {
      const r = await host.installUpdate()
      if (r.shouldExit || r.installed) {
        setUpdateBanner(r.message || 'Restarting…')
      } else if (r.alreadyLatest) {
        setUpdateBanner(null)
        setUpdateLatest(null)
      } else {
        setUpdateBanner(r.message || 'Update failed')
      }
    } catch (e) {
      setUpdateBanner(e instanceof Error ? e.message : 'Update failed')
    } finally {
      setUpdateBusy(false)
    }
  }, [updateBusy])

  const libraryGames = useMemo(() => {
    const installingId = progress?.isActive ? progress.gameId : null
    const rows = games.filter(
      (g) => !g.isAddPortable && (g.installed || isInstallingGame(g, installingId)),
    )
    return sortGames(rows, settings?.sortMode ?? 'name', settings?.recent ?? [])
  }, [games, settings?.sortMode, settings?.recent, progress?.isActive, progress?.gameId])

  const now = useMemo(() => {
    const picked = pickNow(games, progress, settings?.recent ?? [])
    return retainNow(games, picked, holdNowId.current)
  }, [games, progress, settings?.recent])
  const visibleNow = now?.kind === 'recent' ? null : now
  const nowId = visibleNow?.game.id ?? null
  nowIdRef.current = nowId

  const pinnedGames = useMemo(
    () => libraryGames.filter((game) => game.isFavorite && game.id !== nowId),
    [libraryGames, nowId],
  )
  const pinnedShelfKey = pinnedGames.map((game) => game.id).join('\u001f')
  const scrollPinned = useCallback((direction: -1 | 1) => {
    const track = pinnedTrackRef.current
    if (!track) return
    const distance = Math.max(320, Math.round(track.clientWidth * 0.72))
    track.scrollBy({
      left: direction * distance,
      behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth',
    })
  }, [])

  // A retained room keeps its DOM and scroll position. Reset before paint when
  // returning to the shelf or when its membership changes, otherwise the new
  // first favorite can inherit an old offset and appear clipped off-screen.
  useLayoutEffect(() => {
    if (view !== 'library' || libraryPane !== 'shelf') return
    const track = pinnedTrackRef.current
    if (!track) return
    track.scrollTo({ left: 0, behavior: 'auto' })
  }, [libraryPane, pinnedShelfKey, view])

  /** Pinned and Now already show that title, so All does not repeat it. */
  const unpinnedGames = useMemo(
    () => libraryGames.filter((game) => !game.isFavorite && game.id !== nowId),
    [libraryGames, nowId],
  )

  useEffect(() => {
    if (now?.kind === 'download' || now?.kind === 'playing') {
      holdNowId.current = now.game.id
    }
  }, [now])

  const libraryMatches = useMemo(() => {
    const q = query.trim()
    if (q.length < 2) return [] as Game[]
    return games
      .filter(isSearchableLibraryGame)
      .map((game) => ({
        game,
        // Search is a title search. Store names, tools and backend labels must
        // never turn into a page full of unrelated game cards.
        score: smartSearchScore(game.title, q),
      }))
      .filter(({ score }) => score >= 0)
      .sort(
        (a, b) =>
          b.score - a.score ||
          Number(!!b.game.owned) - Number(!!a.game.owned) ||
          a.game.title.localeCompare(b.game.title, undefined, { sensitivity: 'base' }),
      )
      .map(({ game }) => game)
  }, [games, query])

  const catalogGames = useMemo(() => {
    const present = libraryPresenceKeys(games)
    return catalogHits
      .filter((hit) => !catalogHitIsPresent(hit, present))
      .map((hit) => hitToGame(hit, games))
  }, [catalogHits, games])

  const gridGames = useMemo(
    () => query.trim().length >= 2 ? [...libraryMatches, ...catalogGames] : unpinnedGames,
    [catalogGames, libraryMatches, query, unpinnedGames],
  )

  // Catalog search — a pending query is never an empty result. Keep the loading
  // state through debounce and provider work so the UI cannot flash a false
  // "No matches" before Epic/Steam returns. Do not re-run on games/run-state
  // polls — that cleared hits and spun the spinner every 2s while detail was open.
  useEffect(() => {
    const q = query.trim()
    if (q.length < 2) {
      setCatalogHits([])
      setCatalogSearching(false)
      searchGen.current += 1
      return
    }
    const gen = ++searchGen.current
    // Results belong to the exact query that produced them. Clear the prior
    // generation immediately so a fast replacement (for example Valorant →
    // Mortal Shell) never shows unrelated catalog cards while the new backend
    // search is debouncing or in flight.
    setCatalogHits([])
    setCatalogSearching(true)
    const t = window.setTimeout(() => {
      void host
        .storeSearch(q)
        .then((r) => {
          if (searchGen.current !== gen) return
          // A cancelled generation has no trustworthy results for this query.
          if (r.cancelled) return
          const present = libraryPresenceKeys(gamesRef.current)
          const hits = (r.results ?? []).filter((h) => !catalogHitIsPresent(h, present))
          setCatalogHits(hits)
        })
        .catch(() => {
          if (searchGen.current === gen) setCatalogHits([])
        })
        .finally(() => {
          if (searchGen.current === gen) setCatalogSearching(false)
        })
    }, 140)
    return () => {
      window.clearTimeout(t)
    }
  }, [query])

  const selectedCard = useMemo(() => {
    if (!selectedId) return null
    const fromLibrary =
      cardForExactId(games, selectedId) ?? findLibraryGame(games, selectedId)
    if (fromLibrary) return fromLibrary
    const catalog = catalogGames.find((g) => g.id === selectedId)
    if (!catalog) return null
    return findLibraryGameByTitle(games, catalog.title) ?? catalog
  }, [games, catalogGames, selectedId])

  // Cards stay canonical in the grid; the detail rail materializes exactly one
  // real source so every bridge action carries that source's own id/target.
  const selected = useMemo(
    () => selectedCard ? materializeVariant(selectedCard, selectedVariantId) : null,
    [selectedCard, selectedVariantId],
  )

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const target = e.target instanceof HTMLElement ? e.target : null
      const tag = target?.tagName
      const typing = tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT'
      const interactiveTarget = target?.closest(
        'button:not([data-game-id]), a, input, textarea, select, [role="button"]:not([data-game-id]), [role="link"]',
      )
      if (e.key === 'Escape') {
        if (view === 'settings') {
          setView('library')
          return
        }
        if (libraryPane === 'game') {
          if (actionLocked) return
          setOverlayMotion('instant')
          setLibraryPane('shelf')
          selectCard(null)
          return
        }
        if (query) {
          setQuery('')
          return
        }
        return
      }
      if (actionLocked) return
      if (interactiveTarget) return
      if (!typing && e.key === '/') {
        e.preventDefault()
        searchInputRef.current?.focus()
        return
      }
      if (!typing && e.key === 'F5') {
        e.preventDefault()
        void loadLibrary(true)
        return
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [view, libraryPane, query, loadLibrary, actionLocked])

  async function runPrimary(skipDeps = false, target?: Game) {
    const game = target ?? selected
    if (!game || busy) return
    const nextAction = resolvePrimaryAction(game)
    if (nextAction === 'none') {
      setActionStatus(
        game.installed
          ? 'No action available for this title.'
          : 'Not installable from Exo yet — sign in to a backend or pick a supported title.',
        game.id,
      )
      return
    }
    setBusy(true)
    setActionStatus(null, game.id)
    setDepPrompt(null)
    try {
      if (nextAction === 'play') {
        setActionStatus('Preparing launch…', game.id)
        const res = await host.launch(game.id, { skipDeps })
        if (res.needsDependencies && res.missingDependencies?.length) {
          setDepPrompt({ action: 'play', deps: res.missingDependencies })
          setActionStatus(res.message || 'Install required', game.id)
          setBusy(false)
          return
        }
        setActionStatus(
          res.message || (res.ok ? 'Running' : 'Launch failed'),
          game.id,
          !res.ok,
        )
        if (!res.ok) setBusy(false)
        else setTimeout(() => setBusy(false), 1500)
      } else if (nextAction === 'install') {
        setActionStatus('Starting install…', game.id)
        setProgress({
          gameId: game.id,
          phase: 'preparing',
          percent: null,
          status: 'Starting install…',
          canCancel: true,
          isActive: true,
        })
        const res = await host.install(game.id, undefined, game.title, { skipDeps })
        if (res.queuedGameIds) setQueuedIds(res.queuedGameIds)
        if (res.needsDependencies && res.missingDependencies?.length) {
          setDepPrompt({ action: 'install', deps: res.missingDependencies })
          setActionStatus(res.message || 'Install required', game.id)
          setBusy(false)
          setProgress(null)
          return
        }
        if (res.queued) {
          setActionStatus(res.message || 'Queued.', game.id)
          setBusy(false)
          setProgress((p) => (p?.gameId === game.id ? null : p))
          return
        }
        setActionStatus(res.message || (res.ok ? 'Install complete' : 'Install failed'), game.id)
        if (res.progress) setProgress(res.progress)
        if (!res.progress?.isActive) {
          setBusy(false)
          setProgress((p) =>
            p ? { ...p, isActive: false, canCancel: false, phase: res.ok ? 'completed' : 'failed' } : p,
          )
        }
      } else if (nextAction === 'update') {
        setActionStatus('Starting update…', game.id)
        setProgress({
          gameId: game.id,
          phase: 'preparing',
          percent: null,
          status: 'Starting update…',
          canCancel: true,
          isActive: true,
        })
        const res = await host.update(game.id, { skipDeps })
        if (res.needsDependencies && res.missingDependencies?.length) {
          setDepPrompt({ action: 'update', deps: res.missingDependencies })
          setActionStatus(res.message || 'Install required', game.id)
          setBusy(false)
          setProgress(null)
          return
        }
        setActionStatus(res.message || (res.ok ? 'Update complete' : 'Update failed'), game.id)
        if (res.progress) setProgress(res.progress)
        if (!res.progress?.isActive) {
          setBusy(false)
          setProgress((p) =>
            p ? { ...p, isActive: false, canCancel: false, phase: res.ok ? 'completed' : 'failed' } : p,
          )
        }
      } else {
        setBusy(false)
      }
    } catch (e) {
      setActionStatus(e instanceof Error ? e.message : 'Action failed', game.id)
      setBusy(false)
      setProgress((p) => (p?.isActive ? { ...p, isActive: false, canCancel: false, phase: 'failed' } : p))
    }
  }

  async function onPrimary() {
    if (selected?.canStop) {
      await onStopGame(selected)
      return
    }
    await runPrimary(false)
  }

  function activateCard(game: Game) {
    const exact = materializeVariant(game, game.selectedVariantId ?? null)
    if (exact.canStop) void onStopGame(exact)
    else void runPrimary(false, exact)
  }

  async function onStopGame(target?: Game) {
    const game = target ?? selected
    if (!game || !game.canStop || busy || progress?.isActive) return
    setBusy(true)
    setActionStatus(`Closing ${game.title}…`, game.id)
    try {
      const result = await host.stop(game.id)
      setActionStatus(result.message ?? (result.ok ? 'Game closed.' : 'Could not close the game.'), game.id, !result.ok)
      if (result.ok) {
        setGames((items) => setExactRunState(items, game.id, false, false))
        void host.getGame(game.id).then((refreshed) => {
          if (refreshed.ok && refreshed.game) {
            setGames((items) => mergeExactGame(items, refreshed.game!))
          }
        }).catch(() => {})
      }
    } catch (error) {
      setActionStatus(error instanceof Error ? error.message : 'Could not close the game.', game.id, true)
    } finally {
      setBusy(false)
    }
  }

  async function onOfferMissingDeps() {
    if (!depPrompt) return
    for (const d of depPrompt.deps) {
      if (d.canOfferInstall !== false) await host.offerDepInstall(d.id)
    }
    // Official installer opened — keep prompt so user can Continue after install.
    setDepPrompt((prev) => (prev ? { ...prev, awaitingContinue: true } : prev))
    setActionStatus('Installer opened. Continue when ready.', selected?.id ?? selectedIdRef.current)
  }

  async function onContinueAfterDeps() {
    if (!depPrompt) return
    setDepPrompt(null)
    await runPrimary(true)
  }

  async function onCancel() {
    try {
      // cancelInstall — bridge parity
      const res = await host.cancelInstall()
      setBusy(false)
      setActionStatus(res.message ?? 'Cancel requested', progress?.gameId ?? selectedIdRef.current)
      setProgress((p) =>
        p?.isActive ? { ...p, isActive: false, canCancel: false, phase: 'cancelled' } : p,
      )
    } catch (e) {
      setBusy(false)
      setActionStatus(e instanceof Error ? e.message : 'Cancel failed', progress?.gameId ?? selectedIdRef.current)
    }
  }

  async function onToggleFavorite(id: string) {
    try {
      const res = await host.toggleFavorite(id)
      setGames((prev) =>
        prev.map((g) =>
          g.id === id || g.variants?.some((variant) => variant.id === id)
            ? { ...g, isFavorite: !!res.isFavorite, coverUrl: g.coverUrl } // keep cover across pin remount
            : g,
        ),
      )
    } catch (e) {
      setActionStatus(e instanceof Error ? e.message : 'Favorite failed', id)
    }
  }

  const searching = query.trim().length >= 2
  const markBusy =
    busy ||
    !!progress?.isActive ||
    updateBusy ||
    booting
  const emptyLibrary = !booting && !loading && libraryGames.length === 0 && !searching
  const displayedGame = selected

  async function finishOnboarding(refreshLibrary = false) {
    const next = await host.setSettings({ onboardingComplete: true })
    if (!next?.onboardingComplete) {
      throw new Error('Could not save settings — try again.')
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

  async function addFolderFromLibrary() {
    const result = await addPortableFolder()
    if (result.cancelled) return
    if (!result.ok) {
      setActionStatus(result.message ?? 'Could not add portable game', null)
      return
    }
    void loadLibrary(true)
  }

  // Wait for settings so we don't flash library before first-run connect.
  if (!settings || (!settings.onboardingComplete && !storeMatrixReady)) {
    return (
      <div className="exo-app">
        <AppAmbient />
        {settingsError ? (
          <div className="flex flex-1 items-center justify-center px-6">
            <div className="max-w-md rounded-2xl border border-line-soft bg-elevated p-6 text-center" role="alert">
              <h1 className="text-base font-semibold text-fg">Settings could not be loaded</h1>
              <p className="mt-2 text-[12px] leading-relaxed text-muted">{settingsError}</p>
              <button type="button" className="exo-cta mt-5 h-9 px-5 text-[12px]" onClick={() => void loadSettings()}>
                Try again
              </button>
            </div>
          </div>
        ) : (
          <BootSplash />
        )}
      </div>
    )
  }

  // First run connects only services already supported by the host. The local
  // profile works by itself; configured builds may optionally connect Exo ID.
  if (!settings.onboardingComplete) {
    return (
      <OnboardingPanel
        stores={stores}
        message={authMsg}
        onSettings={setSettings}
        onStores={setStores}
        onComplete={finishOnboarding}
        onAddFolder={addFolderDuringOnboarding}
      />
    )
  }

  const settingsPanel = (
          <SettingsPanel
            settings={settings}
            stores={stores}
            message={authMsg}
            updateBusy={updateBusy}
            updatePercent={updatePercent}
            updateAvailable={!!updateBanner && !updateBusy}
            onCheckUpdate={async () => {
              setAuthMsg(null)
              try {
                const r = await host.checkUpdate()
                if (r.updateAvailable) {
                  setUpdateBanner(r.message || `Update v${r.latest} available`)
                  setUpdateLatest(r.latest ?? null)
                  setAuthMsg(r.message || 'Update available.')
                } else {
                  setUpdateBanner(null)
                  setUpdateLatest(null)
                  setAuthMsg(r.message || 'Already up to date.')
                }
              } catch (e) {
                setAuthMsg(e instanceof Error ? e.message : 'Update check failed')
              }
            }}
            onInstallUpdate={() => void runAppUpdate()}
            onSettings={(next) => {
              setSettings(next)
            }}
            onStores={async (next) => {
              setStores(next)
              await loadLibrary(true)
            }}
            onClose={() => setView('library')}
          />
  )

  return (
    <>
      <div className="h-full">
      <div className="exo-app" data-controller-scope="launcher">
      <AppAmbient />
      {booting && <BootSplash />}
      <header className={`exo-titlebar${markBusy ? ' is-busy' : ''}`}>
        <button
          type="button"
          data-controller-target=""
          data-controller-safe=""
          className="exo-brand exo-no-drag shrink-0"
          disabled={actionLocked}
          onClick={() => {
            setQuery('')
            selectCard(null)
            setLibraryPane('shelf')
            setView('library')
          }}
          aria-label="Home library"
        >
          <ExoMark size={28} className="exo-brand-logo" alive={markBusy} />
        </button>

        <nav className="exo-nav exo-no-drag" aria-label="Launcher">
          {NAV_TABS.map((tab) => (
            <button
              key={tab.id}
              type="button"
              data-controller-target=""
              data-controller-safe=""
              className={`exo-nav-btn${view === tab.id ? ' is-on' : ''}`}
              data-label={tab.label}
              aria-current={view === tab.id ? 'page' : undefined}
              onClick={() => {
                selectCard(null)
                setView(tab.id)
              }}
            >
              {tab.label}
            </button>
          ))}
          <button
            type="button"
            data-controller-target=""
            data-controller-safe=""
            className={`exo-you exo-no-drag${view === 'profile' ? ' is-on' : ''}`}
            aria-label="Your profile"
            aria-pressed={view === 'profile'}
            onClick={() => {
              selectCard(null)
              setView('profile')
            }}
          >
            {selfAvatarImage ? (
              <img src={selfAvatarImage} alt="" onError={() => setSelfAvatarImage(null)} />
            ) : (
              <span className="text-[10px] font-semibold text-faint">{selfInitial}</span>
            )}
          </button>
        </nav>

        <label className={`exo-titlebar-search exo-no-drag${query ? ' has-query' : ''}`}>
          <span className="exo-search-capsule" aria-hidden="true" />
          <input
            ref={searchInputRef}
            value={query}
            onChange={(e) => {
              setView('library')
              setLibraryPane('shelf')
              setQuery(e.target.value)
            }}
            onKeyDown={(e) => {
              if (e.key !== 'Escape') return
              e.preventDefault()
              e.stopPropagation()
              if (query) setQuery('')
              e.currentTarget.blur()
            }}
            onFocus={() => {
              setView('library')
              setLibraryPane('shelf')
            }}
            disabled={actionLocked}
            placeholder="Search"
            className="exo-search"
            autoComplete="off"
            spellCheck={false}
            aria-label="Search library and stores"
          />
        </label>

        <div className="exo-titlebar-actions exo-no-drag">
          <button
            type="button"
            data-controller-target=""
            data-controller-safe=""
            className={`exo-settings-btn exo-no-drag${view === 'settings' ? ' is-on' : ''}`}
            aria-label="Open settings"
            aria-pressed={view === 'settings'}
            onClick={() => setView(view === 'settings' ? 'library' : 'settings')}
          >
            <Settings size={16} />
          </button>
          <WindowChrome />
        </div>
      </header>

      <div className="exo-toast-stack">
        {depPrompt && (
          <div
            className="exo-toast"
            role="status"
            aria-label="Missing dependency"
          >
          <p className="min-w-0 flex-1 font-medium text-fg">
            {depPrompt.awaitingContinue
              ? `Installed ${depPrompt.deps.map((d) => d.name).join(', ')}? Continue to retry.`
              : `Need ${depPrompt.deps.map((d) => d.name).join(', ')}`}
          </p>
          <div className="flex shrink-0 items-center gap-2">
            <button
              type="button"
              className="text-faint hover:text-fg"
              onClick={() => {
                setDepPrompt(null)
                void runPrimary(true)
              }}
            >
              Skip
            </button>
            {depPrompt.awaitingContinue ? (
              <button
                type="button"
                className="exo-cta h-8 px-4 text-[12px]"
                onClick={() => void onContinueAfterDeps()}
              >
                Continue
              </button>
            ) : (
              <button
                type="button"
                className="exo-cta h-8 px-4 text-[12px]"
                onClick={() => void onOfferMissingDeps()}
              >
                Install
              </button>
            )}
          </div>
          </div>
        )}

        {updateBanner && (
          <BannerIn
            role="status"
            className="exo-toast"
          >
          <div className="min-w-0 flex-1">
            <p className="font-medium text-fg">{updateBanner}</p>
          </div>
          <div className="flex shrink-0 items-center gap-2">
            {!updateBusy && (
              <button
                type="button"
                className="text-faint hover:text-fg"
                onClick={() => {
                  if (updateLatest) {
                    sessionStorage.setItem('exo.launcher.dismissed-update', updateLatest)
                  }
                  setUpdateBanner(null)
                }}
              >
                Later
              </button>
            )}
            <button
              type="button"
              className={`exo-cta exo-update-action h-8 px-4 text-[12px]${updateBusy ? ' is-active' : ''}`}
              disabled={updateBusy}
              onClick={() => void runAppUpdate()}
            >
              {updateBusy && (
                <span
                  className="exo-action-progress"
                  style={{ '--progress': Math.max(0, Math.min(100, updatePercent)) / 100 } as CSSProperties}
                  aria-hidden="true"
                />
              )}
              <span className="exo-action-state">
                <span className="exo-action-content exo-action-idle" aria-hidden={updateBusy}>
                  <strong>Update now</strong>
                </span>
                <span className="exo-action-content exo-action-active" aria-hidden={!updateBusy}>
                  <Loader2 size={16} className="animate-spin motion-reduce:animate-none" />
                  <strong>{`Installing… ${Math.round(updatePercent)}%`}</strong>
                </span>
              </span>
            </button>
            {updateBusy && (
              <span
                className="sr-only"
                role="progressbar"
                aria-label="App update progress"
                aria-valuemin={0}
                aria-valuemax={100}
                aria-valuenow={Math.round(Math.max(0, Math.min(100, updatePercent)))}
                aria-valuetext={`Installing update, ${Math.round(updatePercent)} percent`}
              />
            )}
            {updateBusy && (
              <span className="sr-only" role="status" aria-live="polite" aria-atomic="true">
                Installing update · {Math.round(updatePercent)}%
              </span>
            )}
          </div>
          </BannerIn>
        )}

        {statusMsg && statusGameId === null && (
          <BannerIn
            role="alert"
            className="exo-toast is-bad"
          >
            {statusMsg}
          </BannerIn>
        )}
      </div>

      <div className="exo-set-room" hidden={view !== 'settings'}>{settingsPanel}</div>
      {/* Keep page shells mounted once onboarding is complete. Their effects
          warm cache/profile data while hidden, but active presence polling is
          still owned by the visible FriendsRoom prop. */}
      <div className="relative z-10 flex min-h-0 flex-1 flex-col" hidden={view !== 'friends'}>
        <FriendsRoom active={view === 'friends'} />
      </div>
      <div className="exo-pane relative z-10" hidden={view !== 'profile'}>
        <ProfileRoom games={games} active={view === 'profile'} />
      </div>
      <div className="relative z-10 flex min-h-0 flex-1" hidden={view !== 'library'}>
        <main
          ref={libraryMainRef}
          className={`exo-library-pane min-w-0 flex-1${libraryPane === 'game' ? ' is-overlay-open' : ''}`}
          inert={libraryPane === 'game' ? true : undefined}
        >
          {libraryError && emptyLibrary ? (
            <div className="exo-empty" role="alert">
              <h2>Library unavailable</h2>
              <p>{libraryError}</p>
              <button type="button" className="exo-cta h-10 px-5 text-[12px]" onClick={() => void loadLibrary(true)}>
                Retry
              </button>
            </div>
          ) : emptyLibrary ? (
            <div className="exo-empty">
              <h2>Nothing here yet</h2>
              <p>Installed games from store apps on this PC, or a folder you add.</p>
              <button type="button" className="exo-cta h-10 px-5 text-[12px]" onClick={() => void addFolderFromLibrary()}>
                Add a folder
              </button>
            </div>
          ) : (
            <div className="flex min-h-0 flex-1 flex-col">
              {visibleNow && !searching && (
                <NowStage
                  game={visibleNow.game}
                  kind={visibleNow.kind}
                  progress={visibleNow.kind === 'download' ? progress : null}
                  disabled={isCardActionLocked(visibleNow.game)}
                  onOpen={() => openGamePage(visibleNow.game.id)}
                  onPrimary={() => void activateCard(visibleNow.game)}
                  onStop={() => void onStopGame(visibleNow.game)}
                />
              )}
              {pinnedGames.length > 0 && !searching && (
                <section className="exo-pin-row" aria-label="Pinned">
                  <div className="exo-pin-head">
                    <h2 className="exo-shelf-title">Pinned</h2>
                    {pinnedGames.length > 1 ? (
                      <div className="exo-pin-nav" aria-label="Pinned games navigation">
                        <button
                          type="button"
                          className="exo-pin-nav-btn"
                          aria-label="Show earlier pinned games"
                          onClick={() => scrollPinned(-1)}
                        >
                          <ChevronLeft size={16} />
                        </button>
                        <button
                          type="button"
                          className="exo-pin-nav-btn"
                          aria-label="Show later pinned games"
                          onClick={() => scrollPinned(1)}
                        >
                          <ChevronRight size={16} />
                        </button>
                      </div>
                    ) : null}
                  </div>
                  <div id="exo-pinned-games" ref={pinnedTrackRef} className="exo-pin-track">
                    {pinnedGames.map((game, index) => (
                      <GameCard
                        key={game.id}
                        game={game}
                        preload={index < 12}
                        selected={game.id === selectedId}
                        onSelect={() => openGamePage(game.id)}
                        onActivate={() => activateCard(game)}
                        transfer={transferForGame(progress, game)}
                        queued={queuedIds.includes(game.id)}
                        disabled={isCardActionLocked(game)}
                      />
                    ))}
                  </div>
                </section>
              )}
              <BrowseShelf
                games={gridGames}
                selectedId={selectedId}
                activeGameId={activeGameId}
                onActiveGameChange={setActiveGameId}
                loading={catalogSearching}
                heading={searching ? 'Search' : 'All'}
                emptyMessage={searching ? `No matches for “${query.trim()}”.` : 'Nothing in All.'}
                isDisabled={isCardActionLocked}
                queuedIds={queuedIds}
                scrollRootRef={libraryMainRef}
                transferFor={(game) => transferForGame(progress, game)}
                onSelect={(game) => {
                  setActiveGameId(game.id)
                  openGamePage(game.id)
                }}
                onActivate={(game) => activateCard(game)}
              />
            </div>
          )}
        </main>
        <GameOverlay
          open={libraryPane === 'game' && !!displayedGame}
          instant={overlayMotion === 'instant'}
          label={displayedGame?.title ?? 'Game'}
          scrim={
            <button
              type="button"
              className="exo-game-overlay-scrim"
              aria-label="Close"
              tabIndex={-1}
              disabled={actionLocked}
              onClick={() => {
                setLibraryPane('shelf')
                selectCard(null)
              }}
            />
          }
        >
          {displayedGame ? (
            <GamePage
              selected={displayedGame}
              busy={busy}
              statusMsg={statusBelongsToSelection(statusGameId, displayedGame) ? statusMsg : null}
              progress={progress}
              closeDisabled={actionLocked}
              onPrimary={() => void onPrimary()}
              onStop={() => void onStopGame()}
              onCancel={() => void onCancel()}
              onClose={() => {
                setLibraryPane('shelf')
                selectCard(null)
              }}
              onToggleFavorite={(id) => void onToggleFavorite(id)}
              onSelectSource={(id) => {
                setSelectedVariantId(id)
                setActionStatus(null, id)
              }}
              onStatus={(message, sticky) => setActionStatus(message, displayedGame.id, !!sticky)}
              onUninstalled={() => {
                setLibraryPane('shelf')
                selectCard(null)
                void loadLibrary(true)
              }}
            />
          ) : null}
        </GameOverlay>
        </div>
      </div>
      </div>
    </>
  )
}
