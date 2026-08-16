/**
 * Exo Launcher shell — installed library, pinned row, search discovers installs.
 * CTA strings (Play | Download | Install | Update) and cancelInstall live via DetailPanel + host.
 */
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type CSSProperties } from 'react'
import { Loader2, Search, Settings } from '../brand/icons'
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
  type StoreStatus,
} from '../lib/host'
import { smartSearchScore, sortGames, titleIdentity } from '../lib/utils'
import { pickNow } from '../lib/now'
import { addPortableFolder } from '../lib/portable'
import { GridItem, BannerIn, GameOverlay } from '../motion'
import { steamAppId } from './CoverArt'
import { DetailPanel } from './DetailPanel'
import { GameCard } from './GameCard'
import { NowStage } from './NowStage'
import { OnboardingPanel } from './OnboardingPanel'
import { SettingsPanel, SettingsShell } from './SettingsPanel'
import { WindowChrome } from './WindowChrome'

type View = 'library' | 'settings'

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

function hitToGame(hit: CatalogHit, library: Game[] = []): Game {
  const existing = findLibraryGame(library, hit.id)
  const owned = !!(hit.owned || hit.canInstall || existing?.owned || existing?.canInstall || existing?.installed)
  const installed = !!(hit.installed || existing?.installed)
  const canInstall = !installed && owned
  return {
    id: hit.id,
    title: hit.title,
    store: hit.store,
    installed,
    owned,
    canInstall,
    primaryAction: installed ? 'play' : canInstall ? 'install' : 'none',
    coverUrl: hit.coverUrl ?? existing?.coverUrl,
    coverSource: hit.coverSource ?? existing?.coverSource,
    status: installed ? 'Ready' : owned ? 'Owned' : 'Catalog',
    deps: [],
    launchNote: '',
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
    <div className="exo-boot" role="status" aria-label="Starting">
      <ExoMark size={56} alive className="exo-mascot" title="Exo" />
      <span className="exo-boot-bar" aria-hidden>
        <i />
      </span>
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
  const [games, setGames] = useState<Game[]>([])
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [selectedVariantId, setSelectedVariantId] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [booting, setBooting] = useState(true)
  const coldStart = useRef(true)
  const bootAt = useRef(Date.now())
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
  const [focusIndex, setFocusIndex] = useState(0)
  const [catalogHits, setCatalogHits] = useState<CatalogHit[]>([])
  const [catalogSearching, setCatalogSearching] = useState(false)
  const [authMsg, setAuthMsg] = useState<string | null>(null)
  const searchGen = useRef(0)
  const searchInputRef = useRef<HTMLInputElement>(null)
  const selectedIdRef = useRef<string | null>(null)
  const gamesRef = useRef<Game[]>([])
  const libraryMainRef = useRef<HTMLElement>(null)
  const libraryScrollRef = useRef(0)
  const [heldGame, setHeldGame] = useState<Game | null>(null)
  selectedIdRef.current = selectedId
  gamesRef.current = games
  const actionLocked = busy || !!progress?.isActive
  const markBusy = busy || !!progress?.isActive || updateBusy || catalogSearching || booting
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

  function openGamePage(id: string, index?: number) {
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
    setQuery('')
    if (typeof index === 'number') setFocusIndex(index)
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
    try {
      const res = await host.getLibrary(force)
      const favoriteIds = res.favorites
        ? new Set(res.favorites.map((id) => id.toLowerCase()))
        : null
      setGames((prev) => {
        const prevById = new Map(prev.map((g) => [g.id, g]))
        const incoming = res.games.map((g) => {
          const old = prevById.get(g.id)
          const coverUrl = g.coverUrl || old?.coverUrl || null
          // Host OverlayUserPrefs is authoritative; also match variant ids so a
          // pin on steam:X still marks the grouped card after a scan.
          const isFavorite = gameMatchesFavoriteIds(g, favoriteIds)
          return { ...g, coverUrl, isFavorite }
        })
        return mergeHostGames(prev, incoming, selectedIdRef.current)
      })
      if (res.stores?.length) setStores(res.stores)
      if (res.progress?.isActive) setProgress(res.progress)
    } catch (e) {
      setActionStatus(e instanceof Error ? e.message : 'Library load failed', null)
    } finally {
      setLoading(false)
      if (coldStart.current) {
        coldStart.current = false
        const wait = Math.max(0, 1400 - (Date.now() - bootAt.current))
        window.setTimeout(() => setBooting(false), wait)
      }
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

  useEffect(() => {
    void loadSettings()
    void host.storesMatrix().then(setStores).catch(() => {})
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
      if (!d?.games?.length) return
      // Never wipe a good cover with null during cache warm.
      // Trust host isFavorite so unpin cannot be re-applied by a stale local pin.
      setGames((prev) => {
        const prevById = new Map(prev.map((g) => [g.id, g]))
        const incoming = d.games!.map((g) => {
          const old = prevById.get(g.id)
          const coverUrl = g.coverUrl || old?.coverUrl || null
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
      offLaunch()
      offProgress()
      offCovers()
      offUpdate()
    }
  }, [loadLibrary, loadSettings])

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
    if (settings?.onboardingComplete) void loadLibrary()
    else if (settings) setLoading(false)
  }, [loadLibrary, settings?.onboardingComplete])

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

  const now = useMemo(
    () => pickNow(games, progress, settings?.recent ?? []),
    [games, progress, settings?.recent],
  )
  const nowId = now?.game.id ?? null

  const pinnedGames = useMemo(
    () => libraryGames.filter((g) => g.isFavorite && g.id !== nowId),
    [libraryGames, nowId],
  )

  const pinnedIds = useMemo(
    () => new Set(pinnedGames.map((g) => g.id)),
    [pinnedGames],
  )

  const libraryGrid = useMemo(
    () => libraryGames.filter((g) => !pinnedIds.has(g.id) && g.id !== nowId),
    [libraryGames, pinnedIds, nowId],
  )

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

  useLayoutEffect(() => {
    if (selected) setHeldGame(selected)
  }, [selected])

  const navGames = useMemo(() => {
    if (query.trim().length >= 2) {
      return [...libraryMatches, ...catalogGames]
    }
    return libraryGames
  }, [query, libraryMatches, catalogGames, libraryGames])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const tag = (e.target as HTMLElement)?.tagName
      const typing = tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT'
      if (e.key === 'Escape') {
        if (!actionLocked) {
          if (view !== 'library') setView('library')
          else selectCard(null)
        }
        return
      }
      if (actionLocked) return
      if (!typing && e.key === '/') {
        e.preventDefault()
        const el = document.querySelector<HTMLInputElement>('input.exo-search')
        el?.focus()
        return
      }
      if (!typing && e.key === 'F5') {
        e.preventDefault()
        void loadLibrary(true)
        return
      }
      if (view !== 'library' || typing) return
      if (e.key === 'ArrowRight' || e.key === 'ArrowLeft' || e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        e.preventDefault()
        const cols = Math.max(2, Math.min(8, Math.floor((window.innerWidth - 96) / 158)))
        let next = focusIndex
        if (e.key === 'ArrowRight') next += 1
        if (e.key === 'ArrowLeft') next -= 1
        if (e.key === 'ArrowDown') next += cols
        if (e.key === 'ArrowUp') next -= cols
        next = Math.max(0, Math.min(navGames.length - 1, next))
        setFocusIndex(next)
        const g = navGames[next]
        if (g) {
          setFocusIndex(next)
          window.requestAnimationFrame(() => {
            const escapedId = CSS.escape(g.id)
            document.querySelector<HTMLButtonElement>(`button[data-game-id="${escapedId}"]`)?.focus({ preventScroll: true })
          })
        }
      }
      if (e.key === 'Enter' && !busy) {
        const focused = navGames[focusIndex]
        if (focused) {
          e.preventDefault()
          openGamePage(focused.id, focusIndex)
        }
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [view, focusIndex, navGames, selected, busy, loadLibrary, actionLocked])

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
        if (res.needsDependencies && res.missingDependencies?.length) {
          setDepPrompt({ action: 'install', deps: res.missingDependencies })
          setActionStatus(res.message || 'Install required', game.id)
          setBusy(false)
          setProgress(null)
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
  const emptyLibrary = !booting && !loading && libraryGames.length === 0 && !searching
  const showGamePage = !!selected && !searching
  const displayedGame = selected ?? heldGame
  const overlayLock = !!displayedGame && !searching
  const onOverlayExit = useCallback(() => {
    if (!selectedIdRef.current) setHeldGame(null)
  }, [])

  useLayoutEffect(() => {
    const main = libraryMainRef.current
    if (!main) return
    if (overlayLock) {
      main.scrollTop = libraryScrollRef.current
      return
    }
    libraryScrollRef.current = main.scrollTop
  }, [overlayLock])

  useEffect(() => {
    const main = libraryMainRef.current
    if (!main) return
    const remember = () => {
      if (!selectedIdRef.current) libraryScrollRef.current = main.scrollTop
    }
    main.addEventListener('pointerdown', remember, true)
    main.addEventListener('scroll', remember, { passive: true })
    return () => {
      main.removeEventListener('pointerdown', remember, true)
      main.removeEventListener('scroll', remember)
    }
  }, [])

  async function finishOnboarding() {
    try {
      const next = await host.setSettings({ onboardingComplete: true })
      if (!next?.onboardingComplete) {
        setAuthMsg('Could not save settings — try again.')
        return
      }
      setSettings(next)
    } catch (e) {
      setAuthMsg(e instanceof Error ? e.message : 'Could not save onboarding')
    }
  }

  async function addFolderDuringOnboarding() {
    const result = await addPortableFolder()
    if (result.cancelled) return
    if (!result.ok) {
      setAuthMsg(result.message)
      return
    }
    await finishOnboarding()
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
  if (!settings) {
    return (
      <div className="exo-app">
        {settingsError ? (
          <div className="flex flex-1 items-center justify-center px-8">
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

  // First-run is one move. It does not inventory missing stores.
  if (!settings.onboardingComplete) {
    return (
      <OnboardingPanel
        message={authMsg}
        onOpenLibrary={() => void finishOnboarding()}
        onAddFolder={() => void addFolderDuringOnboarding()}
      />
    )
  }

  if (view === 'settings') {
    return (
      <>
        <SettingsShell
          alive={updateBusy}
          onBack={() => setView('library')}
        >
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
          />
        </SettingsShell>
      </>
    )
  }

  return (
      <div className="exo-app">
      <header className={`exo-titlebar exo-titlebar-home${markBusy ? ' is-busy' : ''}`}>
        <button
          type="button"
          className="exo-brand exo-no-drag shrink-0"
          title="Home"
          disabled={actionLocked}
          onClick={() => {
            setQuery('')
            selectCard(null)
            setView('library')
          }}
          aria-label="Home library"
        >
          <ExoMark size={28} className="exo-brand-logo" alive={markBusy} />
        </button>

        <div className={`exo-titlebar-search exo-no-drag${query.trim() ? ' is-open' : ''}`}>
          <Search className="exo-search-glyph" />
          <input
            ref={searchInputRef}
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            disabled={actionLocked}
            placeholder="Search"
            className="exo-search"
            aria-label="Search library and stores"
          />
        </div>

        <div className="exo-titlebar-actions">
          <button
            type="button"
            className="exo-winbtn"
            title="Settings"
            aria-label="Settings"
            disabled={actionLocked}
            onClick={() => setView('settings')}
          >
            <Settings size={16} />
          </button>
          <div className="exo-titlebar-divider" />
          <WindowChrome />
        </div>
      </header>

      {depPrompt && (
        <div
          className="relative z-10 flex items-center justify-between gap-3 border-b border-line-soft bg-elevated px-5 py-2.5 text-[12px]"
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
          className="relative z-10 flex items-center justify-between gap-3 border-b border-line-soft bg-elevated px-5 py-2.5 text-[12px]"
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
                  <Loader2 size={16} className="animate-spin" />
                  <strong>{`Installing… ${Math.round(updatePercent)}%`}</strong>
                </span>
              </span>
              {updateBusy && (
                <span
                  className="sr-only"
                  role="progressbar"
                  aria-label="App update progress"
                  aria-valuemin={0}
                  aria-valuemax={100}
                  aria-valuenow={Math.round(Math.max(0, Math.min(100, updatePercent)))}
                />
              )}
            </button>
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
          className="relative z-10 border-b border-line-soft px-5 py-2.5 text-[12px] text-bad"
        >
          {statusMsg}
        </BannerIn>
      )}

      <div className="relative z-10 flex min-h-0 flex-1">
        {booting && <BootSplash />}
        <main
          ref={libraryMainRef}
          className={`exo-library-pane min-w-0 flex-1${overlayLock ? ' is-overlay-open' : ''}`}
          inert={overlayLock ? true : undefined}
        >
          <div className="exo-home">
            {emptyLibrary ? (
              <div className="exo-enter flex flex-col items-center justify-center py-24 text-center">
                <p className="text-[15px] font-medium tracking-tight text-fg">Nothing here yet</p>
                <button
                  type="button"
                  className="exo-cta mt-5 h-10 px-5 text-[12px]"
                  onClick={() => void addFolderFromLibrary()}
                >
                  Add a folder
                </button>
              </div>
            ) : searching ? (
              <>
                {libraryMatches.length > 0 && (
                  <section>
                    <div className="exo-home-head">
                      <h3 className="exo-section-label">Library</h3>
                    </div>
                    <div className="exo-game-grid">
                      {libraryMatches.map((game, i) => (
                        <GridItem key={game.id} index={i}>
                          <GameCard
                            game={game}
                            selected={selectedId === game.id}
                            disabled={isCardActionLocked(game)}
                            transfer={transferForGame(progress, game)}
                            onSelect={() => openGamePage(game.id, i)}
                            onActivate={() => activateCard(game)}
                            onToggleFavorite={() => void onToggleFavorite(game.id)}
                          />
                        </GridItem>
                      ))}
                    </div>
                </section>
              )}

              {(catalogGames.length > 0 || catalogSearching) && (
                <section>
                  <div className="exo-home-head">
                    <h3 className="exo-section-label">Install</h3>
                    {catalogSearching && (
                      <Loader2 size={16} className="animate-spin text-faint" />
                    )}
                  </div>
                  {catalogGames.length > 0 && (
                    <div className="exo-game-grid">
                      {catalogGames.map((game, i) => (
                        <GridItem key={game.id} index={i}>
                          <GameCard
                            game={game}
                            selected={selectedId === game.id}
                            disabled={isCardActionLocked(game)}
                            transfer={transferForGame(progress, game)}
                            onSelect={() => openGamePage(game.id, libraryMatches.length + i)}
                            onActivate={() => activateCard(game)}
                          />
                        </GridItem>
                      ))}
                    </div>
                  )}
                </section>
              )}
              {libraryMatches.length === 0 && catalogGames.length === 0 && !catalogSearching && (
                <p className="px-5 pt-6 text-[13px] text-faint">
                  {`No matches for “${query.trim()}”.`}
                </p>
              )}
              </>
            ) : (
              <>
                {now && (
                  <NowStage
                    game={now.game}
                    kind={now.kind}
                    progress={now.kind === 'download' ? progress : null}
                    disabled={isCardActionLocked(now.game)}
                    onOpen={() => openGamePage(now.game.id, 0)}
                    onPrimary={() => void activateCard(now.game)}
                    onStop={() => void onStopGame(now.game)}
                  />
                )}

                {pinnedGames.length > 0 && (
                  <section className="exo-pinned-section">
                    <div className="exo-home-head">
                      <h3 className="exo-section-label">Pinned</h3>
                    </div>
                    <div className="exo-pin-track" role="region" aria-label="Pinned games">
                      {pinnedGames.map((game, i) => (
                        <GameCard
                          key={game.id}
                          game={game}
                          selected={selectedId === game.id}
                          disabled={isCardActionLocked(game)}
                          transfer={transferForGame(progress, game)}
                          onSelect={() => openGamePage(game.id, i)}
                          onActivate={() => activateCard(game)}
                          onToggleFavorite={() => void onToggleFavorite(game.id)}
                        />
                      ))}
                    </div>
                  </section>
                )}

                {libraryGrid.length > 0 && (
                  <section>
                    <div className="exo-home-head">
                      <h3 className="exo-section-label">Library</h3>
                    </div>
                    <div className="exo-game-grid">
                      {libraryGrid.map((game, i) => (
                        <GridItem key={game.id} index={i}>
                          <GameCard
                            game={game}
                            selected={selectedId === game.id}
                            disabled={isCardActionLocked(game)}
                            transfer={transferForGame(progress, game)}
                            onSelect={() => openGamePage(game.id, i)}
                            onActivate={() => activateCard(game)}
                            onToggleFavorite={() => void onToggleFavorite(game.id)}
                          />
                        </GridItem>
                      ))}
                    </div>
                  </section>
                )}

              </>
            )}
          </div>
        </main>
        <GameOverlay
          open={showGamePage}
          label={displayedGame ? `${displayedGame.title} details` : 'Game'}
          onExitComplete={onOverlayExit}
          scrim={
            <div
              className="exo-game-overlay-scrim"
              aria-hidden="true"
              onClick={() => {
                if (!actionLocked) selectCard(null)
              }}
            />
          }
        >
          {displayedGame ? (
            <DetailPanel
              selected={displayedGame}
              busy={busy}
              statusMsg={statusBelongsToSelection(statusGameId, displayedGame) ? statusMsg : null}
              progress={progress}
              onPrimary={() => void onPrimary()}
              onStop={() => void onStopGame()}
              onCancel={() => void onCancel()}
              onClose={() => selectCard(null)}
              onToggleFavorite={(id) => void onToggleFavorite(id)}
              onSelectSource={(id) => {
                setSelectedVariantId(id)
                setActionStatus(null, id)
              }}
              onStatus={(message, sticky) => setActionStatus(message, displayedGame.id, !!sticky)}
              onUninstalled={() => {
                selectCard(null)
                void loadLibrary(true)
              }}
              closeDisabled={actionLocked}
            />
          ) : null}
        </GameOverlay>
        </div>
      </div>
  )
}
