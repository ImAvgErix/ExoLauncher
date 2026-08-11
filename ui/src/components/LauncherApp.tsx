/**
 * Exo Launcher shell — installed library, pinned row, search discovers installs.
 * CTA strings (Play | Install | Update) and cancelInstall live via DetailPanel + host.
 */
import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties } from 'react'
import { ChevronLeft, ChevronRight, Loader2, Minus, Search, Settings, X } from 'lucide-react'
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
import { cn, smartSearchScore } from '../lib/utils'
import { DetailRail, GridItem } from '../motion'
import { DetailPanel } from './DetailPanel'
import { GameCard } from './GameCard'
import { OnboardingPanel } from './OnboardingPanel'
import { SettingsPanel, SettingsShell } from './SettingsPanel'

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

function hitToGame(hit: CatalogHit): Game {
  return {
    id: hit.id,
    title: hit.title,
    store: hit.store,
    installed: !!hit.installed,
    owned: hit.owned,
    canInstall: !!hit.canInstall,
    primaryAction: hit.installed ? 'play' : hit.canInstall ? 'install' : 'none',
    coverUrl: hit.coverUrl,
    coverSource: hit.coverSource,
    status: hit.installed ? 'Ready' : hit.owned ? 'Owned' : 'Catalog',
    deps: [],
    launchNote: '',
    launchTarget: hit.launchTarget,
  }
}

function cardForExactId(games: Game[], id: string | null): Game | null {
  if (!id) return null
  return games.find((game) =>
    game.id === id || game.variants?.some((variant) => variant.id === id),
  ) ?? null
}

function materializeVariant(card: Game, variantId: string | null): Game {
  const variant = card.variants?.find((item) => item.id === variantId)
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

function variantFromGame(game: Game): GameVariant {
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
    playtimeMinutes: game.playtimeMinutes,
    lastPlayedUtc: game.lastPlayedUtc,
    status: game.status,
    isRunning: game.isRunning,
    canStop: game.canStop,
  }
}

/** Keep a grouped card intact when `game.get` refreshes one of its exact sources. */
function mergeExactGame(items: Game[], refreshed: Game): Game[] {
  return items.map((item) => {
    if (item.id === refreshed.id) return refreshed
    if (!item.variants?.some((variant) => variant.id === refreshed.id)) return item
    return {
      ...item,
      variants: item.variants.map((variant) =>
        variant.id === refreshed.id ? variantFromGame(refreshed) : variant,
      ),
    }
  })
}

/** Update one exact source's transient run state without collapsing its card. */
function setExactRunState(items: Game[], id: string, isRunning: boolean, canStop: boolean): Game[] {
  return items.map((item) => {
    const variants = item.variants?.map((variant) =>
      variant.id === id ? { ...variant, isRunning, canStop } : variant,
    )
    if (item.id === id) return { ...item, isRunning, canStop, variants }
    if (variants?.some((variant) => variant.id === id)) return { ...item, variants }
    return item
  })
}

export function LauncherApp() {
  const [games, setGames] = useState<Game[]>([])
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [selectedVariantId, setSelectedVariantId] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
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
  const [pinnedNav, setPinnedNav] = useState({ back: false, forward: false })
  const searchGen = useRef(0)
  const searchInputRef = useRef<HTMLInputElement>(null)
  const pinnedRailRef = useRef<HTMLDivElement>(null)
  const selectedIdRef = useRef<string | null>(null)
  selectedIdRef.current = selectedId
  const actionLocked = busy || !!progress?.isActive
  const lockedGameId = progress?.isActive ? progress.gameId : statusGameId ?? selectedId

  const selectCard = useCallback((id: string | null) => {
    setSelectedId(id)
    setSelectedVariantId(null)
  }, [])

  const syncPinnedNav = useCallback(() => {
    const rail = pinnedRailRef.current
    if (!rail) {
      setPinnedNav({ back: false, forward: false })
      return
    }
    // Keep the resting carousel on whole-card boundaries. The scrollport can
    // be wider than an exact number of fixed-size cards, so cover only that
    // remainder instead of leaving a clipped preview of the next card.
    const railBounds = rail.getBoundingClientRect()
    const wholeCards = Array.from(rail.children)
      .map((child) => (child as HTMLElement).getBoundingClientRect())
      .filter((bounds) => bounds.left >= railBounds.left - 0.5 && bounds.right <= railBounds.right + 0.5)
    if (wholeCards.length > 0 && rail.parentElement) {
      const firstWholeCard = wholeCards[0]
      const lastWholeCard = wholeCards[wholeCards.length - 1]
      rail.parentElement.style.setProperty(
        '--pinned-left-edge-width',
        `${Math.max(0, firstWholeCard.left - railBounds.left)}px`,
      )
      rail.parentElement.style.setProperty(
        '--pinned-right-edge-width',
        `${Math.max(0, railBounds.right - lastWholeCard.right)}px`,
      )
    }
    const max = Math.max(0, rail.scrollWidth - rail.clientWidth)
    const next = {
      back: rail.scrollLeft > 2,
      forward: rail.scrollLeft < max - 2,
    }
    setPinnedNav((current) =>
      current.back === next.back && current.forward === next.forward ? current : next,
    )
  }, [])

  function movePinnedRail(delta: number) {
    pinnedRailRef.current?.scrollBy({ left: delta, behavior: 'smooth' })
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
    setLoading(true)
    setActionStatus(null, null)
    try {
      const res = await host.getLibrary(force)
      const favoriteIds = res.favorites
        ? new Set(res.favorites.map((id) => id.toLowerCase()))
        : null
      setGames((prev) => {
        const prevById = new Map(prev.map((g) => [g.id, g]))
        return res.games.map((g) => {
          const old = prevById.get(g.id)
          const coverUrl = g.coverUrl || old?.coverUrl || null
          // Native grouping can aggregate a persisted alternate-store pin into
          // the canonical card. Keep that authoritative aggregate instead of
          // looking only for the selected card id in the raw settings list.
          const isFavorite = !!g.isFavorite || !!favoriteIds?.has(g.id.toLowerCase())
          return { ...g, coverUrl, isFavorite }
        })
      })
      if (res.stores?.length) setStores(res.stores)
      if (res.progress?.isActive) setProgress(res.progress)
    } catch (e) {
      setActionStatus(e instanceof Error ? e.message : 'Library load failed', null)
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
      // Never wipe a good cover with null during cache warm / pin churn.
      // Never regress isFavorite — prefer existing UI pin over host false.
      setGames((prev) => {
        const prevById = new Map(prev.map((g) => [g.id, g]))
        return d.games!.map((g) => {
          const old = prevById.get(g.id)
          const coverUrl = g.coverUrl || old?.coverUrl || null
          const isFavorite = !!g.isFavorite
          return { ...old, ...g, coverUrl, isFavorite }
        })
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

  // Check only the selected title for an externally-started, safely stoppable
  // game process. The library grid intentionally does not scan every install.
  useEffect(() => {
    const selectedCardForRefresh = games.find((game) => game.id === selectedId)
    const exactId = selectedVariantId && selectedCardForRefresh?.variants?.some((variant) => variant.id === selectedVariantId)
      ? selectedVariantId
      : selectedId
    if (!exactId) return
    let active = true
    void host.getGame(exactId).then((result) => {
      if (!active || !result.ok || !result.game) return
      setGames((items) => mergeExactGame(items, result.game!))
    }).catch(() => {})
    return () => { active = false }
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
      const installedIds = new Set(
        games.filter((g) => g.installed).map((g) => g.id.toLowerCase()),
      )
      setCatalogHits((prev) => {
        const map = new Map<string, CatalogHit>()
        for (const h of prev) map.set(h.id.toLowerCase(), h)
        for (const h of d.results!) {
          if (installedIds.has(h.id.toLowerCase()) && h.installed) continue
          if (h.installed) continue
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

  const installedGames = useMemo(
    () => games.filter((g) => g.installed && !g.isAddPortable),
    [games],
  )

  const pinnedGames = useMemo(
    () => installedGames.filter((g) => g.isFavorite),
    [installedGames],
  )
  const pinnedRailMounted = view === 'library' && query.trim().length < 2

  useEffect(() => {
    const rail = pinnedRailRef.current
    if (!rail) {
      syncPinnedNav()
      return
    }
    syncPinnedNav()
    rail.addEventListener('scroll', syncPinnedNav, { passive: true })
    const observer = new ResizeObserver(syncPinnedNav)
    observer.observe(rail)
    return () => {
      rail.removeEventListener('scroll', syncPinnedNav)
      observer.disconnect()
    }
  // Settings and active search both remove the rail from the DOM. Rebind its
  // observer and recompute the edge masks whenever the rail mounts again.
  }, [pinnedGames.length, pinnedRailMounted, syncPinnedNav])

  const libraryGrid = useMemo(() => {
    const pinnedIds = new Set(pinnedGames.map((g) => g.id))
    // Pinned row + remaining grid without duplicates
    return installedGames.filter((g) => !pinnedIds.has(g.id))
  }, [installedGames, pinnedGames])

  const libraryMatches = useMemo(() => {
    const q = query.trim()
    if (q.length < 2) return [] as Game[]
    return installedGames
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
  }, [installedGames, query])

  const catalogGames = useMemo(() => catalogHits.map(hitToGame), [catalogHits])

  // Catalog search — a pending query is never an empty result. Keep the loading
  // state through debounce and provider work so the UI cannot flash a false
  // "No matches" before Epic/Steam returns.
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
          const installedIds = new Set(
            games.filter((g) => g.installed).map((g) => g.id.toLowerCase()),
          )
          const hits = (r.results ?? []).filter((h) => {
            // Drop already-installed; keep owned-not-installed.
            if (installedIds.has(h.id.toLowerCase())) return false
            if (h.installed) return false
            return true
          })
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
  }, [query, games])

  const selectedCard = useMemo(() => {
    if (!selectedId) return null
    return (
      games.find((g) => g.id === selectedId) ??
      catalogGames.find((g) => g.id === selectedId) ??
      null
    )
  }, [games, catalogGames, selectedId])

  // Cards stay canonical in the grid; the detail rail materializes exactly one
  // real source so every bridge action carries that source's own id/target.
  const selected = useMemo(
    () => selectedCard ? materializeVariant(selectedCard, selectedVariantId) : null,
    [selectedCard, selectedVariantId],
  )

  const action = selected ? resolvePrimaryAction(selected) : 'none'

  const navGames = useMemo(() => {
    if (query.trim().length >= 2) {
      return [...libraryMatches, ...catalogGames]
    }
    return [...pinnedGames, ...libraryGrid]
  }, [query, libraryMatches, catalogGames, pinnedGames, libraryGrid])

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
        const cols = window.innerWidth >= 1024 ? 4 : window.innerWidth >= 640 ? 3 : 2
        let next = focusIndex
        if (e.key === 'ArrowRight') next += 1
        if (e.key === 'ArrowLeft') next -= 1
        if (e.key === 'ArrowDown') next += cols
        if (e.key === 'ArrowUp') next -= cols
        next = Math.max(0, Math.min(navGames.length - 1, next))
        setFocusIndex(next)
        const g = navGames[next]
        if (g) {
          selectCard(g.id)
          setActionStatus(null, null)
          window.requestAnimationFrame(() => {
            const escapedId = CSS.escape(g.id)
            document.querySelector<HTMLButtonElement>(`button[data-game-id="${escapedId}"]`)?.focus()
          })
        }
      }
      if (e.key === 'Enter' && selected && !busy) {
        e.preventDefault()
        void onPrimary()
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [view, focusIndex, navGames, selected, busy, loadLibrary, actionLocked])

  async function runPrimary(skipDeps = false) {
    if (!selected || busy) return
    if (action === 'none') {
      setActionStatus(
        selected.installed
          ? 'No action available for this title.'
          : 'Not installable from Exo yet — sign in to a backend or pick a supported title.',
        selected.id,
      )
      return
    }
    setBusy(true)
    setActionStatus(null, selected.id)
    setDepPrompt(null)
    try {
      if (action === 'play') {
        setActionStatus('Preparing launch…', selected.id)
        const res = await host.launch(selected.id, { skipDeps })
        if (res.needsDependencies && res.missingDependencies?.length) {
          setDepPrompt({ action: 'play', deps: res.missingDependencies })
          setActionStatus(res.message || 'Install required', selected.id)
          setBusy(false)
          return
        }
        setActionStatus(
          res.message || (res.ok ? 'Running' : 'Launch failed'),
          selected.id,
          !res.ok,
        )
        if (!res.ok) setBusy(false)
        else setTimeout(() => setBusy(false), 4000)
      } else if (action === 'install') {
        setActionStatus('Starting install…', selected.id)
        setProgress({
          gameId: selected.id,
          phase: 'preparing',
          percent: 0,
          status: 'Starting install…',
          canCancel: true,
          isActive: true,
        })
        const res = await host.install(selected.id, undefined, selected.title, { skipDeps })
        if (res.needsDependencies && res.missingDependencies?.length) {
          setDepPrompt({ action: 'install', deps: res.missingDependencies })
          setActionStatus(res.message || 'Install required', selected.id)
          setBusy(false)
          setProgress(null)
          return
        }
        setActionStatus(res.message || (res.ok ? 'Install complete' : 'Install failed'), selected.id)
        if (res.progress) setProgress(res.progress)
        if (!res.progress?.isActive) {
          setBusy(false)
          setProgress((p) =>
            p ? { ...p, isActive: false, canCancel: false, phase: res.ok ? 'completed' : 'failed' } : p,
          )
        }
      } else if (action === 'update') {
        setActionStatus('Starting update…', selected.id)
        setProgress({
          gameId: selected.id,
          phase: 'preparing',
          percent: 0,
          status: 'Starting update…',
          canCancel: true,
          isActive: true,
        })
        const res = await host.update(selected.id, { skipDeps })
        if (res.needsDependencies && res.missingDependencies?.length) {
          setDepPrompt({ action: 'update', deps: res.missingDependencies })
          setActionStatus(res.message || 'Install required', selected.id)
          setBusy(false)
          setProgress(null)
          return
        }
        setActionStatus(res.message || (res.ok ? 'Update complete' : 'Update failed'), selected.id)
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
      setActionStatus(e instanceof Error ? e.message : 'Action failed', selected.id)
      setBusy(false)
      setProgress((p) => (p?.isActive ? { ...p, isActive: false, canCancel: false, phase: 'failed' } : p))
    }
  }

  async function onPrimary() {
    if (selected?.canStop) {
      await onStopGame()
      return
    }
    await runPrimary(false)
  }

  async function onStopGame() {
    if (!selected || !selected.canStop || busy || progress?.isActive) return
    setBusy(true)
    setActionStatus(`Closing ${selected.title}…`, selected.id)
    try {
      const result = await host.stop(selected.id)
      setActionStatus(result.message ?? (result.ok ? 'Game closed.' : 'Could not close the game.'), selected.id, !result.ok)
      if (result.ok) {
        setGames((items) => setExactRunState(items, selected.id, false, false))
        // Do not keep the Stop button in a Closing state while a broad external
        // process reconciliation runs. It is safe to reconcile in background
        // because the native result already revalidated process identity/exit.
        void host.getGame(selected.id).then((refreshed) => {
          if (refreshed.ok && refreshed.game) {
            setGames((items) => mergeExactGame(items, refreshed.game!))
          }
        }).catch(() => {})
      }
    } catch (error) {
      setActionStatus(error instanceof Error ? error.message : 'Could not close the game.', selected.id, true)
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
  const emptyLibrary = !loading && installedGames.length === 0 && !searching

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

  // Wait for settings so we don't flash library before first-run connect.
  if (!settings) {
    return (
      <div className="exo-app">
        <div className="flex flex-1 items-center justify-center px-8">
          {settingsError ? (
            <div className="max-w-md rounded-2xl border border-line-soft bg-elevated p-6 text-center" role="alert">
              <h1 className="text-base font-semibold text-fg">Settings could not be loaded</h1>
              <p className="mt-2 text-[12px] leading-relaxed text-muted">{settingsError}</p>
              <button type="button" className="exo-cta mt-5 h-9 px-5 text-[12px]" onClick={() => void loadSettings()}>
                Try again
              </button>
            </div>
          ) : (
            <div className="text-sm text-muted" role="status">Starting…</div>
          )}
        </div>
      </div>
    )
  }

  // First-run is an installed-client inventory, never a store sign-in flow.
  if (!settings.onboardingComplete) {
    return (
      <OnboardingPanel
        stores={stores}
        message={authMsg}
        onContinue={() => void finishOnboarding()}
        onSkip={() => void finishOnboarding()}
      />
    )
  }

  if (view === 'settings') {
    return (
      <>
        <SettingsShell
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
    <>
      <div className="exo-app">
      <header className="exo-titlebar">
        <button
          type="button"
          className="exo-brand exo-no-drag shrink-0"
          title="Exo Launcher"
          disabled={actionLocked}
          onClick={() => {
            setQuery('')
            selectCard(null)
            setView('library')
          }}
          aria-label="Home library"
        >
          <img src="./logo.png" alt="" className="exo-brand-logo" width={28} height={28} draggable={false} />
        </button>

        <div className="relative mx-2 min-w-0 flex-1 exo-no-drag">
          <Search className="pointer-events-none absolute left-3 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-faint" />
          <input
            ref={searchInputRef}
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            disabled={actionLocked}
            placeholder="Search to install…"
            className="exo-search max-w-none w-full"
            aria-label="Search to install"
          />
          {catalogSearching && (
            <Loader2 className="pointer-events-none absolute right-3 top-1/2 h-3.5 w-3.5 -translate-y-1/2 animate-spin text-faint" />
          )}
        </div>

        <div className="exo-titlebar-actions">
          <button
            type="button"
            className="exo-winbtn"
            title="Settings"
            disabled={actionLocked}
            onClick={() => setView('settings')}
          >
            <Settings size={15} strokeWidth={1.75} />
          </button>
          <div className="exo-titlebar-divider" />
          <button type="button" className="exo-winbtn" title="Minimize" onClick={() => void host.minimize()}>
            <Minus size={15} strokeWidth={1.75} />
          </button>
          <button type="button" className="exo-winbtn is-close" title="Close" onClick={() => void host.close()}>
            <X size={15} strokeWidth={1.75} />
          </button>
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
        <div
          className="relative z-10 flex items-center justify-between gap-3 border-b border-line-soft bg-elevated px-5 py-2.5 text-[12px]"
          role="status"
          aria-label="App update"
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
                  <Loader2 className="h-3.5 w-3.5 animate-spin" />
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
        </div>
      )}

      {statusMsg && statusGameId === null && (
        <div
          className="relative z-10 border-b border-line-soft px-5 py-2.5 text-[12px] text-bad"
          role="alert"
        >
          {statusMsg}
        </div>
      )}

      <div className="relative z-10 flex min-h-0 flex-1">
        <main className={cn('min-w-0 flex-1 overflow-y-auto', selected ? 'hidden md:block' : 'block')}>
          <div className="px-5 pb-10 pt-4 sm:px-6">
            <div className="mb-6 flex items-end justify-between gap-4">
              <div>
                <p className="text-[11px] font-medium uppercase tracking-[0.18em] text-faint">Library</p>
                <h1 className="mt-1 text-[24px] font-semibold tracking-[-0.03em] text-fg">
                  Your games
                </h1>
              </div>
              <span className="pb-1 text-[12px] tabular-nums text-faint">
                {installedGames.length} installed
              </span>
            </div>
            {loading && installedGames.length === 0 ? (
              <div className="exo-enter flex flex-col items-center justify-center py-24">
                <img
                  src="./logo.png"
                  alt=""
                  width={56}
                  height={56}
                  className="mb-5 size-14 rounded-2xl shadow-[0_20px_50px_rgba(0,0,0,0.55)]"
                  draggable={false}
                />
                <p className="text-sm text-fg-muted">Scanning libraries…</p>
              </div>
            ) : emptyLibrary ? (
              <div className="exo-enter flex flex-col items-center justify-center py-24 text-center">
                <div
                  className="mb-5 grid size-14 place-items-center text-lg font-bold text-muted"
                  style={{
                    borderRadius: 16,
                    border: '1px solid #2a2a2a',
                    background: '#000',
                  }}
                >
                  ·
                </div>
                <p className="text-[15px] font-medium tracking-tight text-fg">Nothing installed</p>
                <p className="mt-2 text-[13px] text-faint">Search the stores to install a game into Exo.</p>
                <button
                  type="button"
                  className="exo-cta mt-5 h-10 px-5 text-[12px]"
                  onClick={() => searchInputRef.current?.focus()}
                >
                  Search to install
                </button>
              </div>
            ) : searching ? (
              <>
                {libraryMatches.length > 0 && (
                  <section className="mb-10">
                    <h3 className="mb-3 text-[12px] font-medium uppercase tracking-wider text-faint">
                      Library
                    </h3>
                    <div
                      className="grid gap-x-4 gap-y-5"
                      style={{ gridTemplateColumns: 'repeat(auto-fill, minmax(172px, 1fr))' }}
                    >
                      {libraryMatches.map((game, i) => (
                        <GridItem key={game.id} index={i}>
                          <GameCard
                            game={game}
                            selected={selectedId === game.id}
                            disabled={actionLocked && lockedGameId !== game.id}
                            onSelect={() => {
                              selectCard(game.id)
                              setFocusIndex(i)
                              setActionStatus(null, null)
                            }}
                            onToggleFavorite={() => void onToggleFavorite(game.id)}
                          />
                        </GridItem>
                      ))}
                    </div>
                  </section>
                )}

                <section>
                  <div className="mb-3 flex items-center gap-2">
                    <h3 className="text-[12px] font-medium uppercase tracking-wider text-faint">
                      Install
                    </h3>
                    {catalogSearching && (
                      <Loader2 className="h-3.5 w-3.5 animate-spin text-faint" />
                    )}
                  </div>
                  {catalogGames.length === 0 && !catalogSearching ? (
                    <p className="text-[13px] text-faint">
                      {libraryMatches.length === 0
                        ? `No matches for “${query.trim()}”.`
                        : 'No installable titles for this search.'}
                    </p>
                  ) : (
                    <div
                      className="grid gap-x-4 gap-y-5"
                      style={{ gridTemplateColumns: 'repeat(auto-fill, minmax(172px, 1fr))' }}
                    >
                      {catalogGames.map((game, i) => (
                        <GridItem key={game.id} index={i}>
                          <GameCard
                            game={game}
                            selected={selectedId === game.id}
                            disabled={actionLocked && lockedGameId !== game.id}
                            onSelect={() => {
                              selectCard(game.id)
                              setFocusIndex(libraryMatches.length + i)
                              setActionStatus(null, null)
                            }}
                          />
                        </GridItem>
                      ))}
                    </div>
                  )}
                </section>
              </>
            ) : (
              <>
                {pinnedGames.length > 0 && (
                  <section className="exo-pinned-section mb-7 min-w-0 max-w-full">
                    <div className="mb-3 flex items-center justify-between gap-3">
                      <h3 className="text-[12px] font-medium uppercase tracking-wider text-faint">
                        Pinned
                      </h3>
                      {(pinnedNav.back || pinnedNav.forward) && (
                        <div className="flex items-center gap-1" aria-label="Pinned game navigation">
                          <button
                            type="button"
                            className="exo-pinned-nav-button"
                            aria-label="Previous pinned games"
                            disabled={!pinnedNav.back}
                            onClick={() => movePinnedRail(-564)}
                          >
                            <ChevronLeft size={15} />
                          </button>
                          <button
                            type="button"
                            className="exo-pinned-nav-button"
                            aria-label="Next pinned games"
                            disabled={!pinnedNav.forward}
                            onClick={() => movePinnedRail(564)}
                          >
                            <ChevronRight size={15} />
                          </button>
                        </div>
                      )}
                    </div>
                    <div className="exo-pinned-viewport">
                      <div
                        ref={pinnedRailRef}
                        className="exo-pinned-row"
                        tabIndex={0}
                        role="region"
                        aria-label="Pinned games"
                        onWheel={(event) => {
                          if (Math.abs(event.deltaY) <= Math.abs(event.deltaX)) return
                          if (event.currentTarget.scrollWidth <= event.currentTarget.clientWidth) return
                          event.preventDefault()
                          movePinnedRail(event.deltaY)
                        }}
                        onKeyDown={(event) => {
                          if (event.key === 'ArrowRight') {
                            event.preventDefault()
                            movePinnedRail(220)
                          } else if (event.key === 'ArrowLeft') {
                            event.preventDefault()
                            movePinnedRail(-220)
                          } else if (event.key === 'Home') {
                            event.preventDefault()
                            pinnedRailRef.current?.scrollTo({ left: 0, behavior: 'smooth' })
                          } else if (event.key === 'End') {
                            event.preventDefault()
                            const rail = pinnedRailRef.current
                            rail?.scrollTo({ left: rail.scrollWidth, behavior: 'smooth' })
                          }
                        }}
                      >
                        {pinnedGames.map((game, i) => (
                          <GameCard
                            key={game.id}
                            game={game}
                            size="lg"
                            selected={selectedId === game.id}
                            disabled={actionLocked && lockedGameId !== game.id}
                            onSelect={() => {
                              selectCard(game.id)
                              setFocusIndex(i)
                              setActionStatus(null, null)
                            }}
                            onToggleFavorite={() => void onToggleFavorite(game.id)}
                          />
                        ))}
                      </div>
                      {pinnedNav.back && <span className="exo-pinned-edge is-left" aria-hidden="true" />}
                      {pinnedNav.forward && <span className="exo-pinned-edge is-right" aria-hidden="true" />}
                    </div>
                  </section>
                )}

                {libraryGrid.length > 0 && (
                  <section>
                    {pinnedGames.length > 0 && (
                      <h3 className="mb-3 text-[12px] font-medium uppercase tracking-wider text-faint">
                        Installed
                      </h3>
                    )}
                    <div
                      className="grid gap-x-4 gap-y-5"
                      style={{ gridTemplateColumns: 'repeat(auto-fill, minmax(172px, 1fr))' }}
                    >
                      {libraryGrid.map((game, i) => (
                        <GridItem key={game.id} index={i}>
                          <GameCard
                            game={game}
                            selected={selectedId === game.id}
                            disabled={actionLocked && lockedGameId !== game.id}
                            onSelect={() => {
                              selectCard(game.id)
                              setFocusIndex(pinnedGames.length + i)
                              setActionStatus(null, null)
                            }}
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

        {selected && (
          <div className="flex shrink-0 items-stretch py-3 pr-3 pl-1">
            <DetailRail open>
              <DetailPanel
                selected={selected}
                busy={busy}
                statusMsg={statusGameId === selected.id ? statusMsg : null}
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
                onStatus={(message) => setActionStatus(message, selected.id)}
                onUninstalled={() => {
                  selectCard(null)
                  void loadLibrary(true)
                }}
                closeDisabled={actionLocked}
              />
            </DetailRail>
          </div>
        )}
        </div>
      </div>
    </>
  )
}
