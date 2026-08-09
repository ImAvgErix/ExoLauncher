/**
 * Exo Launcher shell — Exo OS visual parity (AMOLED ambient, CTA sweep, quiet chrome).
 */
import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  ArrowLeft,
  Check,
  Download,
  FolderOpen,
  Loader2,
  Minus,
  Play,
  RefreshCw,
  Search,
  Settings,
  Star,
  Trash2,
  X,
} from 'lucide-react'
import {
  host,
  onHostEvent,
  resolvePrimaryAction,
  type DependencyItem,
  type Game,
  type InstallProgress,
  type LauncherSettings,
  type SortMode,
  type StoreId,
  type StoreStatus,
} from '../lib/host'
import {
  cn,
  formatPlaytime,
  formatSize,
  formatSpeed,
  monogram,
  sortGames,
  storeDotColor,
  storeLabel,
} from '../lib/utils'
import { DetailRail, FadeIn, GridItem } from '../motion'

type View = 'library' | 'settings' | 'deps'
type StoreFilter = StoreId | 'all' | string

const FILTERS: { id: StoreFilter; label: string }[] = [
  { id: 'all', label: 'All' },
  { id: 'steam', label: 'Steam' },
  { id: 'epic', label: 'Epic' },
  { id: 'riot', label: 'Riot' },
  { id: 'gog', label: 'GOG' },
  { id: 'local', label: 'Local' },
  { id: 'amazon', label: 'Amazon' },
]

const SORTS: { id: SortMode; label: string }[] = [
  { id: 'name', label: 'Name' },
  { id: 'recent', label: 'Recent' },
  { id: 'favorites', label: 'Pinned' },
  { id: 'size', label: 'Size' },
  { id: 'store', label: 'Store' },
]

export function LauncherApp() {
  const [games, setGames] = useState<Game[]>([])
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState(false)
  const [statusMsg, setStatusMsg] = useState<string | null>(null)
  const [progress, setProgress] = useState<InstallProgress | null>(null)
  const [view, setView] = useState<View>('library')
  const [settings, setSettings] = useState<LauncherSettings | null>(null)
  const [deps, setDeps] = useState<DependencyItem[]>([])
  const [stores, setStores] = useState<StoreStatus[]>([])
  const [query, setQuery] = useState('')
  const [store, setStore] = useState<StoreFilter>('all')
  const [sortMode, setSortMode] = useState<SortMode>('name')
  const [recent, setRecent] = useState<string[]>([])
  const [updateBanner, setUpdateBanner] = useState<string | null>(null)
  const [updateUrl, setUpdateUrl] = useState<string | null>(null)
  const [focusIndex, setFocusIndex] = useState(0)

  const loadLibrary = useCallback(async (force = false) => {
    setLoading(true)
    setStatusMsg(null)
    try {
      const res = await host.getLibrary(force)
      setGames(res.games)
      if (res.stores?.length) setStores(res.stores)
      if (res.progress?.isActive) setProgress(res.progress)
      if (res.recent) setRecent(res.recent)
      if (res.sortMode && SORTS.some((s) => s.id === res.sortMode)) {
        setSortMode(res.sortMode as SortMode)
      }
    } catch (e) {
      setStatusMsg(e instanceof Error ? e.message : 'Library load failed')
    } finally {
      setLoading(false)
    }
  }, [])

  const loadSettings = useCallback(async () => {
    try {
      const s = await host.getSettings()
      setSettings(s)
      if (s.sortMode && SORTS.some((x) => x.id === s.sortMode)) setSortMode(s.sortMode as SortMode)
      if (s.recent) setRecent(s.recent)
    } catch {
      /* ignore */
    }
  }, [])

  const loadDeps = useCallback(async () => {
    try {
      const res = await host.listDeps()
      setDeps(res.items)
    } catch {
      /* ignore */
    }
  }, [])

  useEffect(() => {
    void loadLibrary()
    void loadSettings()
    void host.storesMatrix().then(setStores).catch(() => {})
    void host
      .checkUpdate()
      .then((r) => {
        if (r.updateAvailable && r.message) {
          setUpdateBanner(r.message)
          setUpdateUrl(r.url ?? null)
        }
      })
      .catch(() => {})

    const offLaunch = onHostEvent('launch.status', (data) => {
      const d = data as { message?: string; phase?: string; ok?: boolean }
      if (d?.message) setStatusMsg(d.message)
      if (d?.phase === 'running' || d?.phase === 'failed' || d?.phase === 'handoff') setBusy(false)
    })
    const offProgress = onHostEvent('install.progress', (data) => {
      const p = data as InstallProgress
      setProgress(p)
      if (p?.status) setStatusMsg(p.status)
      if (!p?.isActive && (p?.phase === 'completed' || p?.phase === 'failed' || p?.phase === 'cancelled')) {
        setBusy(false)
        if (p.phase === 'completed') void loadLibrary(true)
      }
    })
    const offCovers = onHostEvent('library.updated', (data) => {
      const d = data as { games?: Game[] }
      if (d?.games?.length) setGames(d.games)
    })
    return () => {
      offLaunch()
      offProgress()
      offCovers()
    }
  }, [loadLibrary, loadSettings])

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase()
    const base = games.filter((g) => {
      if (store !== 'all' && g.store.toLowerCase() !== String(store).toLowerCase()) return false
      if (!q) return true
      return (
        g.title.toLowerCase().includes(q) ||
        g.store.toLowerCase().includes(q) ||
        storeLabel(g.store).toLowerCase().includes(q)
      )
    })
    return sortGames(base, sortMode, recent)
  }, [games, query, store, sortMode, recent])

  const selected = useMemo(
    () => (selectedId ? (games.find((g) => g.id === selectedId) ?? null) : null),
    [games, selectedId],
  )

  const action = selected ? resolvePrimaryAction(selected) : 'none'

  // Keyboard: / focus search, Esc close detail, Enter launch, arrows move selection
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const tag = (e.target as HTMLElement)?.tagName
      const typing = tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT'
      if (e.key === 'Escape') {
        if (view !== 'library') setView('library')
        else setSelectedId(null)
        return
      }
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
        next = Math.max(0, Math.min(filtered.length - 1, next))
        setFocusIndex(next)
        const g = filtered[next]
        if (g) setSelectedId(g.id)
      }
      if (e.key === 'Enter' && selected && !busy) {
        e.preventDefault()
        void onPrimary()
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [view, focusIndex, filtered, selected, busy, loadLibrary])

  async function onPrimary() {
    if (!selected || busy) return
    if (action === 'none') {
      setStatusMsg(
        selected.installed
          ? 'No action available for this title.'
          : 'Not installable from Exo yet — install the store backend or pick a supported title.',
      )
      return
    }
    setBusy(true)
    setStatusMsg(null)
    try {
      if (action === 'play') {
        setStatusMsg('Preparing launch…')
        const res = await host.launch(selected.id)
        const msg = res.message || (res.ok ? 'Running' : 'Launch failed')
        setStatusMsg(msg)
        if (!res.ok) setBusy(false)
        else {
          setRecent((r) => [selected.id, ...r.filter((x) => x !== selected.id)].slice(0, 40))
          // Clear busy even if host event is missed
          setTimeout(() => setBusy(false), 4000)
        }
      } else if (action === 'install') {
        let installPath: string | undefined
        if (selected.store === 'local' || selected.isAddPortable) {
          const pick = await host.pickFolder('Choose folder containing the game executable')
          if (!pick.ok || pick.cancelled || !pick.path) {
            setStatusMsg(pick.message ?? 'Folder selection cancelled.')
            setBusy(false)
            return
          }
          installPath = pick.path
        }
        setStatusMsg('Starting install…')
        setProgress({
          gameId: selected.id,
          phase: 'preparing',
          percent: 0,
          status: 'Starting install…',
          canCancel: true,
          isActive: true,
        })
        const res = await host.install(selected.id, installPath)
        setStatusMsg(res.message || (res.ok ? 'Install complete' : 'Install failed'))
        if (res.progress) setProgress(res.progress)
        if (!res.progress?.isActive) {
          setBusy(false)
          setProgress((p) =>
            p ? { ...p, isActive: false, canCancel: false, phase: res.ok ? 'completed' : 'failed' } : p,
          )
        }
        if (res.ok) void loadLibrary(true)
      } else if (action === 'update') {
        setStatusMsg('Starting update…')
        setProgress({
          gameId: selected.id,
          phase: 'preparing',
          percent: 0,
          status: 'Starting update…',
          canCancel: true,
          isActive: true,
        })
        const res = await host.update(selected.id)
        setStatusMsg(res.message || (res.ok ? 'Update complete' : 'Update failed'))
        if (res.progress) setProgress(res.progress)
        if (!res.progress?.isActive) {
          setBusy(false)
          setProgress((p) =>
            p ? { ...p, isActive: false, canCancel: false, phase: res.ok ? 'completed' : 'failed' } : p,
          )
        }
        if (res.ok) void loadLibrary(true)
      } else {
        setBusy(false)
      }
    } catch (e) {
      setStatusMsg(e instanceof Error ? e.message : 'Action failed')
      setBusy(false)
      setProgress((p) => (p?.isActive ? { ...p, isActive: false, canCancel: false, phase: 'failed' } : p))
    }
  }

  async function onCancel() {
    try {
      const res = await host.cancelInstall()
      setStatusMsg(res.message ?? 'Cancel requested')
    } catch (e) {
      setStatusMsg(e instanceof Error ? e.message : 'Cancel failed')
    }
  }

  async function patchSettings(patch: Partial<LauncherSettings>) {
    try {
      const next = await host.setSettings(patch)
      setSettings(next)
      if (patch.sortMode && SORTS.some((s) => s.id === patch.sortMode)) {
        setSortMode(patch.sortMode as SortMode)
      }
    } catch (e) {
      setStatusMsg(e instanceof Error ? e.message : 'Settings save failed')
    }
  }

  async function onToggleFavorite(id: string) {
    try {
      const res = await host.toggleFavorite(id)
      setGames((prev) =>
        prev.map((g) => (g.id === id ? { ...g, isFavorite: !!res.isFavorite } : g)),
      )
    } catch (e) {
      setStatusMsg(e instanceof Error ? e.message : 'Favorite failed')
    }
  }

  const ctaLabel =
    action === 'play' ? 'Play' : action === 'install' ? 'Install' : action === 'update' ? 'Update' : selected && !selected.installed ? 'Not installed' : 'Unavailable'

  if (view === 'settings') {
    return (
      <ShellChrome
        onRefresh={() => void loadLibrary(true)}
        loading={loading}
        onDeps={() => {
          setView('deps')
          void loadDeps()
        }}
        onSettings={() => setView('library')}
      >
        <SettingsPanel
          settings={settings}
          stores={stores}
          onPatch={patchSettings}
          onBack={() => setView('library')}
          onAuth={(storeId) =>
            void host.storesAuth(storeId).then((r) => setStatusMsg(r.message ?? (r.ok ? 'Signed in' : 'Auth failed')))
          }
          onPickInstallRoot={async () => {
            const pick = await host.pickFolder('Default install root')
            if (pick.ok && pick.path) await patchSettings({ defaultInstallRoot: pick.path })
          }}
        />
      </ShellChrome>
    )
  }

  if (view === 'deps') {
    return (
      <ShellChrome
        onRefresh={() => void loadLibrary(true)}
        loading={loading}
        onDeps={() => setView('library')}
        onSettings={() => setView('settings')}
      >
        <DepsPanel
          items={deps}
          onOffer={(id) => void host.offerDepInstall(id)}
          onBack={() => setView('library')}
          onRefresh={() => void loadDeps()}
        />
      </ShellChrome>
    )
  }

  return (
    <div className="exo-app">
      <div className="exo-ambient" />
      <header className="exo-titlebar">
        <div className="exo-brand">
          <img src="./logo.png" alt="" className="exo-brand-logo" width={28} height={28} draggable={false} />
          <div className="exo-brand-text">
            <span className="exo-brand-name">Exo Launcher</span>
            <span className="exo-brand-role">Library</span>
          </div>
        </div>

        <div className="relative mx-auto hidden w-full max-w-[280px] flex-1 sm:block exo-no-drag">
          <Search className="pointer-events-none absolute left-3.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-faint" />
          <input
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="Search  ·  /"
            className="exo-search"
            aria-label="Search library"
          />
        </div>

        <div className="exo-titlebar-actions">
          <button type="button" className="exo-winbtn" title="Refresh (F5)" onClick={() => void loadLibrary(true)}>
            <RefreshCw size={15} strokeWidth={1.75} className={loading ? 'animate-spin' : ''} />
          </button>
          <button
            type="button"
            className="exo-winbtn is-wide"
            title="Dependencies"
            onClick={() => {
              setView('deps')
              void loadDeps()
            }}
          >
            Deps
          </button>
          <button type="button" className="exo-winbtn" title="Settings" onClick={() => setView('settings')}>
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

      {updateBanner && (
        <div className="relative z-10 flex items-center justify-between gap-3 border-b border-line-soft bg-surface px-5 py-2 text-[12px] text-muted">
          <span>{updateBanner}</span>
          <div className="flex items-center gap-2">
            {updateUrl && (
              <button type="button" className="exo-ghost-btn" onClick={() => void host.openUrl(updateUrl)}>
                Get update
              </button>
            )}
            <button type="button" className="text-faint hover:text-fg" onClick={() => setUpdateBanner(null)}>
              Dismiss
            </button>
          </div>
        </div>
      )}

      <div className="relative z-10 flex min-h-0 flex-1">
        <main className={cn('min-w-0 flex-1 overflow-y-auto', selected ? 'hidden md:block' : 'block')}>
          <div className="px-6 pb-10 pt-6 sm:px-8">
            <div className="relative mb-5 sm:hidden">
              <Search className="pointer-events-none absolute left-3.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-faint" />
              <input
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                placeholder="Search"
                className="exo-search max-w-none"
              />
            </div>

            <div className="mb-5 flex flex-wrap items-center gap-2">
              <div className="flex flex-wrap gap-1.5">
                {FILTERS.map((f) => (
                  <button
                    key={f.id}
                    type="button"
                    onClick={() => setStore(f.id)}
                    className={cn('exo-chip', store === f.id ? 'is-on' : 'is-off')}
                  >
                    {f.label}
                  </button>
                ))}
              </div>
              <div className="ml-auto flex items-center gap-1.5">
                {SORTS.map((s) => (
                  <button
                    key={s.id}
                    type="button"
                    onClick={() => {
                      setSortMode(s.id)
                      void patchSettings({ sortMode: s.id })
                    }}
                    className={cn(
                      'rounded-full px-2.5 py-1 text-[11px]',
                      sortMode === s.id ? 'bg-elevated text-fg' : 'text-faint hover:text-muted',
                    )}
                  >
                    {s.label}
                  </button>
                ))}
              </div>
            </div>

            {loading && games.length === 0 ? (
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
            ) : filtered.length === 0 ? (
              <div className="exo-enter flex flex-col items-center justify-center py-24 text-center">
                <div
                  className="mb-5 grid size-14 place-items-center text-lg font-bold text-muted"
                  style={{
                    borderRadius: 16,
                    border: '1px solid #2a2a2a',
                    background: 'linear-gradient(160deg,#1a1a1e 0%,#0a0a0a 100%)',
                  }}
                >
                  ·
                </div>
                <p className="text-[15px] font-medium tracking-tight text-fg">Nothing here</p>
                <p className="mt-2 max-w-xs text-[13px] leading-relaxed text-faint">
                  Connect Steam, sign in to Legendary or gogdl in Settings, or add a portable game.
                </p>
              </div>
            ) : (
              <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-4">
                {filtered.map((game, i) => (
                  <GridItem key={game.id} index={i}>
                    <GameCard
                      game={game}
                      selected={selectedId === game.id}
                      onSelect={() => {
                        setSelectedId(game.id)
                        setFocusIndex(i)
                        setStatusMsg(null)
                      }}
                      onToggleFavorite={() => void onToggleFavorite(game.id)}
                    />
                  </GridItem>
                ))}
              </div>
            )}
          </div>
        </main>

        <DetailRail open={!!selected}>
          {selected && (
            <aside className="flex h-full w-full flex-col" aria-label={`${selected.title} details`}>
              <div className="flex items-center gap-2 border-b border-line-soft px-4 py-3 md:hidden">
                <button
                  type="button"
                  onClick={() => setSelectedId(null)}
                  className="inline-flex items-center gap-1.5 rounded-full px-2 py-1.5 text-sm text-muted hover:bg-hover hover:text-fg"
                >
                  <ArrowLeft className="h-4 w-4" />
                  Library
                </button>
              </div>

              <div className="relative h-52 shrink-0 sm:h-60" style={{ background: coverBg(selected) }}>
                <CoverArt game={selected} className="absolute inset-0 h-full w-full" large />
                <div className="absolute inset-x-0 bottom-0 bg-gradient-to-t from-black via-black/80 to-transparent px-6 pb-5 pt-20">
                  <div className="flex items-start justify-between gap-3">
                    <div>
                      <h2 className="text-[22px] font-semibold tracking-tight text-fg">{selected.title}</h2>
                      <p className="mt-1 text-[13px] text-muted">
                        {storeLabel(selected.store)}
                        {selected.status ? ` · ${selected.status}` : ''}
                      </p>
                    </div>
                    {!selected.isAddPortable && (
                      <button
                        type="button"
                        className="exo-titlebar-button"
                        title={selected.isFavorite ? 'Unpin' : 'Pin'}
                        onClick={() => void onToggleFavorite(selected.id)}
                      >
                        <Star
                          size={16}
                          className={selected.isFavorite ? 'fill-current text-fg' : 'text-muted'}
                        />
                      </button>
                    )}
                  </div>
                </div>
              </div>

              <div className="flex flex-1 flex-col gap-4 overflow-y-auto px-6 py-6">
                <button
                  type="button"
                  disabled={busy || action === 'none'}
                  onClick={() => void onPrimary()}
                  className="exo-cta w-full"
                  aria-label={ctaLabel}
                >
                  {busy ? (
                    <Loader2 className="h-4 w-4 animate-spin" />
                  ) : action === 'install' || action === 'update' ? (
                    <Download className="h-4 w-4" />
                  ) : (
                    <Play className="h-4 w-4 fill-current" />
                  )}
                  {ctaLabel}
                </button>

                {(busy || statusMsg || (progress?.isActive && progress.gameId === selected.id)) && (
                  <FadeIn>
                    <div className="exo-status" aria-live="polite">
                      <div className="flex items-center gap-2 text-xs">
                        {(busy || progress?.isActive) && (
                          <Loader2 className="h-3.5 w-3.5 shrink-0 animate-spin text-fg" />
                        )}
                        {!busy && !progress?.isActive && statusMsg === 'Running' && (
                          <Check className="h-3.5 w-3.5 shrink-0 text-good" />
                        )}
                        <span className="text-fg">
                          {progress?.isActive ? progress.status || progress.phase : statusMsg || 'Working…'}
                        </span>
                        {progress?.isActive && progress.bytesPerSecond != null && (
                          <span className="ml-auto tabular-nums text-faint">
                            {formatSpeed(progress.bytesPerSecond)}
                          </span>
                        )}
                      </div>
                      {progress?.isActive && progress.percent != null && (
                        <div className="mt-2 h-1 overflow-hidden rounded-full bg-black/50">
                          <div
                            className="h-full rounded-full bg-fg transition-[width] duration-300"
                            style={{ width: `${Math.max(0, Math.min(100, progress.percent))}%` }}
                          />
                        </div>
                      )}
                      {progress?.canCancel && progress.isActive && (
                        <button
                          type="button"
                          className="mt-2 text-[11px] text-faint hover:text-fg"
                          onClick={() => void onCancel()}
                        >
                          Cancel
                        </button>
                      )}
                    </div>
                  </FadeIn>
                )}

                {!selected.isAddPortable && (
                  <div className="flex flex-wrap gap-2">
                    <button
                      type="button"
                      className="exo-ghost-btn"
                      onClick={() =>
                        void host.openFolder(selected.id).then((r) => {
                          if (!r.ok) setStatusMsg(r.message ?? 'Folder not found')
                        })
                      }
                    >
                      <FolderOpen className="h-3.5 w-3.5" />
                      Folder
                    </button>
                    <button
                      type="button"
                      className="exo-ghost-btn"
                      onClick={() =>
                        void host.uninstall(selected.id).then((r) => {
                          setStatusMsg(r.message ?? (r.ok ? 'Uninstalled' : 'Uninstall failed'))
                          if (r.ok) {
                            setSelectedId(null)
                            void loadLibrary(true)
                          }
                        })
                      }
                    >
                      <Trash2 className="h-3.5 w-3.5" />
                      Uninstall
                    </button>
                  </div>
                )}

                <div className="space-y-3 text-sm">
                  <Row label="Playtime" value={formatPlaytime(selected.playtimeMinutes)} />
                  <Row label="Size" value={formatSize(selected.sizeBytes)} />
                  <Row
                    label="Status"
                    value={selected.status || (selected.installed ? 'Ready' : 'Install required')}
                  />
                </div>

                {selected.deps?.length > 0 && (
                  <div className="flex flex-wrap gap-1.5">
                    {selected.deps.map((d) => (
                      <span key={d} className="exo-badge">
                        {d}
                      </span>
                    ))}
                  </div>
                )}

                <p className="text-[13px] leading-relaxed text-fg-subtle">
                  {selected.launchNote || 'No launch note.'}
                </p>

                <button
                  type="button"
                  onClick={() => setSelectedId(null)}
                  className="mt-auto hidden text-left text-xs text-fg-subtle hover:text-fg md:block"
                >
                  Close
                </button>
              </div>
            </aside>
          )}
        </DetailRail>
      </div>
    </div>
  )
}

function GameCard({
  game,
  selected,
  onSelect,
  onToggleFavorite,
}: {
  game: Game
  selected: boolean
  onSelect: () => void
  onToggleFavorite: () => void
}) {
  const isAdd = game.isAddPortable || game.id === 'local:add'
  return (
    <div className="group relative w-full text-left">
      <button type="button" onClick={onSelect} className="w-full text-left" aria-label={game.title}>
        <div
          className={cn(
            'exo-cover group aspect-[3/4]',
            selected && 'is-selected',
            isAdd && 'exo-add-tile',
          )}
          style={!isAdd ? { background: coverBg(game) } : undefined}
        >
          {isAdd ? (
            <div className="absolute inset-0 flex flex-col items-center justify-center gap-2 text-muted">
              <div
                className="grid size-12 place-items-center text-xl font-light text-fg"
                style={{
                  borderRadius: 14,
                  border: '1px solid #2a2a2a',
                  background: 'linear-gradient(160deg,#222 0%,#0c0c0c 100%)',
                }}
              >
                +
              </div>
              <span className="text-[11px] text-faint">Portable</span>
            </div>
          ) : (
            <>
              <CoverArt game={game} className="absolute inset-0 h-full w-full" />
              {!game.installed && <div className="absolute inset-0 bg-black/45" />}
              <div className="pointer-events-none absolute inset-x-0 bottom-0 h-20 bg-gradient-to-t from-black/85 via-black/25 to-transparent" />
              <div className="absolute left-2 top-2 flex gap-1">
                {game.isFavorite && (
                  <span className="rounded-full bg-black/55 px-1.5 py-0.5 text-[10px] text-fg">Pinned</span>
                )}
                {game.updateAvailable && <span className="exo-badge is-warn">Update</span>}
                {!game.installed && !game.isAddPortable && (
                  <span className="exo-badge">Install</span>
                )}
              </div>
            </>
          )}
        </div>
        <div className="mt-2.5 px-0.5">
          <div className="truncate text-[13px] font-medium tracking-tight text-fg">{game.title}</div>
          <div className="mt-0.5 flex items-center gap-1.5 text-[11px] text-faint">
            <span className="h-1 w-1 rounded-full" style={{ background: storeDotColor(game.store) }} />
            <span>{storeLabel(game.store)}</span>
          </div>
        </div>
      </button>
      {!isAdd && (
        <button
          type="button"
          className="absolute right-2 top-2 rounded-full bg-black/50 p-1.5 text-muted opacity-0 transition-opacity group-hover:opacity-100"
          title={game.isFavorite ? 'Unpin' : 'Pin'}
          onClick={(e) => {
            e.stopPropagation()
            onToggleFavorite()
          }}
        >
          <Star size={12} className={game.isFavorite ? 'fill-current text-fg' : ''} />
        </button>
      )}
    </div>
  )
}

/**
 * Cover art: monogram is always underneath.
 * Image only becomes visible after onLoad — never a broken-image glyph.
 * Allow data:, blob:, and our covers virtual host only (CDN blocked).
 */
function isSafeCoverUrl(url: string | null | undefined): url is string {
  if (!url) return false
  if (url.startsWith('data:image/')) return true
  if (url.startsWith('blob:')) return true
  if (url.startsWith('https://covers.exo-launcher.local/')) return true
  return false
}

function CoverArt({ game, className, large }: { game: Game; className?: string; large?: boolean }) {
  const [loaded, setLoaded] = useState(false)
  const [failed, setFailed] = useState(false)
  const safeUrl = isSafeCoverUrl(game.coverUrl) ? game.coverUrl : null

  useEffect(() => {
    setLoaded(false)
    setFailed(false)
  }, [safeUrl, game.id])

  const showImg = !!safeUrl && !failed
  return (
    <div className={cn('relative overflow-hidden', className)}>
      <div
        className={cn('exo-cover-mono', loaded && showImg && 'is-under')}
        style={{ fontSize: large ? 42 : 28 }}
        aria-hidden
      >
        {monogram(game.title)}
      </div>
      {showImg && (
        <img
          key={safeUrl}
          src={safeUrl}
          alt=""
          className={cn(
            'absolute inset-0 h-full w-full object-cover transition-opacity duration-300',
            loaded ? 'opacity-100' : 'opacity-0 pointer-events-none',
          )}
          draggable={false}
          loading="lazy"
          decoding="async"
          onLoad={() => setLoaded(true)}
          onError={() => {
            setFailed(true)
            setLoaded(false)
          }}
        />
      )}
    </div>
  )
}

function ShellChrome({
  children,
  onRefresh,
  loading,
  onDeps,
  onSettings,
}: {
  children: React.ReactNode
  onRefresh: () => void
  loading: boolean
  onDeps: () => void
  onSettings: () => void
}) {
  return (
    <div className="exo-app">
      <div className="exo-ambient" />
      <header className="exo-titlebar">
        <div className="flex items-center gap-2.5">
          <div
            className="flex h-8 w-8 items-center justify-center text-[12px] font-bold"
            style={{
              borderRadius: 11,
              background: 'linear-gradient(160deg,#303034 0%,#121214 55%,#050505 100%)',
              border: '1px solid #2a2a2a',
            }}
          >
            Ex
          </div>
          <span className="text-[13px] font-semibold tracking-tight">Exo Launcher</span>
        </div>
        <div className="ml-auto flex items-center gap-1">
          <button type="button" className="exo-titlebar-button" onClick={onRefresh}>
            <RefreshCw size={15} className={loading ? 'animate-spin' : ''} />
          </button>
          <button type="button" className="exo-titlebar-button is-wide" onClick={onDeps}>
            <span className="text-[11px]">Deps</span>
          </button>
          <button type="button" className="exo-titlebar-button" onClick={onSettings}>
            <Settings size={15} />
          </button>
          <button type="button" className="exo-titlebar-button" onClick={() => void host.minimize()}>
            <Minus size={15} />
          </button>
          <button type="button" className="exo-titlebar-button is-close" onClick={() => void host.close()}>
            <X size={15} />
          </button>
        </div>
      </header>
      <div className="relative z-10 min-h-0 flex-1 overflow-y-auto">{children}</div>
    </div>
  )
}

function coverBg(game: Game) {
  const hue = hashHue(game.id + game.title)
  return `linear-gradient(160deg,
    hsl(${hue} 42% 28%) 0%,
    hsl(${(hue + 18) % 360} 38% 18%) 50%,
    #050505 100%)`
}

function hashHue(s: string) {
  let h = 0
  for (let i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) >>> 0
  return h % 360
}

function Row({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex items-center justify-between border-b border-border pb-3 last:border-0 last:pb-0">
      <span className="text-fg-subtle">{label}</span>
      <span className="tabular-nums text-fg">{value}</span>
    </div>
  )
}

function SettingsPanel({
  settings,
  stores,
  onPatch,
  onBack,
  onAuth,
  onPickInstallRoot,
}: {
  settings: LauncherSettings | null
  stores: StoreStatus[]
  onPatch: (p: Partial<LauncherSettings>) => void
  onBack: () => void
  onAuth: (store: string) => void
  onPickInstallRoot: () => void
}) {
  return (
    <div className="px-10 py-10">
      <button type="button" className="mb-8 text-xs text-fg-subtle hover:text-fg" onClick={onBack}>
        Back to library
      </button>
      <h2 className="text-2xl font-semibold tracking-tight">Settings</h2>
      <p className="mt-2 max-w-lg text-sm text-fg-muted">Quiet defaults. Anti-cheat safe mode is always on.</p>

      <div className="mt-10 max-w-xl space-y-3 text-sm">
        <Toggle
          title="Close store clients after launch"
          value={settings?.closeStoreClientsAfterLaunch ?? true}
          onChange={(v) => onPatch({ closeStoreClientsAfterLaunch: v })}
        />
        <Toggle
          title="Auto-install redistributables"
          hint="Still opens the official page — never silent-force"
          value={settings?.autoInstallRedistributables ?? false}
          onChange={(v) => onPatch({ autoInstallRedistributables: v })}
        />
        <Toggle
          title="Minimize while playing"
          value={settings?.minimizeWhilePlaying ?? true}
          onChange={(v) => onPatch({ minimizeWhilePlaying: v })}
        />
        <Toggle
          title="Copy portable games into Exo library"
          hint="Off = register folder in place"
          value={settings?.copyPortableIntoLibrary ?? false}
          onChange={(v) => onPatch({ copyPortableIntoLibrary: v })}
        />
        <Toggle
          title="Allow window resize"
          value={settings?.allowResize ?? true}
          onChange={(v) => onPatch({ allowResize: v })}
        />
        <Toggle
          title="Check for updates"
          value={settings?.checkForUpdates ?? true}
          onChange={(v) => onPatch({ checkForUpdates: v })}
        />
      </div>

      <div className="mt-10 max-w-xl">
        <h3 className="text-sm font-medium text-fg">Default install root</h3>
        <p className="mt-1 text-xs text-faint">{settings?.defaultInstallRoot || 'Not set — uses Exo AppData paths'}</p>
        <button type="button" className="exo-ghost-btn mt-3" onClick={onPickInstallRoot}>
          Choose folder
        </button>
      </div>

      <div className="mt-12 max-w-xl">
        <h3 className="text-sm font-medium text-fg">Exo family</h3>
        <p className="mt-1 text-xs text-faint">Same quiet shell. Presence without weight.</p>
        <div className="mt-3 flex flex-wrap gap-2">
          {(
            [
              ['Exo Hub', 'https://github.com/ImAvgErix/ExoHub/releases/latest'],
              ['Exo OS', 'https://github.com/ImAvgErix/ExoOS/releases/latest'],
              ['Exo Link', 'https://github.com/ImAvgErix/ExoLink/releases/latest'],
            ] as const
          ).map(([label, url]) => (
            <button
              key={label}
              type="button"
              className="exo-ghost-btn"
              onClick={() => void host.openUrl(url)}
            >
              {label}
            </button>
          ))}
        </div>
      </div>

      <div className="mt-12 max-w-xl">
        <h3 className="text-sm font-medium text-fg">Store agents</h3>
        <p className="mt-1 text-xs text-faint">Sign in for Legendary / gogdl when present.</p>
        <ul className="mt-4 space-y-2">
          {(stores.length
            ? stores
            : [
                { store: 'local', displayName: 'Local', agentPresent: true },
                { store: 'steam', displayName: 'Steam', agentPresent: false },
                { store: 'epic', displayName: 'Epic', agentPresent: false },
                { store: 'gog', displayName: 'GOG', agentPresent: false },
              ]
          ).map((s) => (
            <li
              key={s.store}
              className="flex items-center justify-between gap-3 rounded-xl border border-border bg-surface px-4 py-3"
            >
              <div>
                <div className="text-sm">{s.displayName}</div>
                <div className="mt-0.5 text-[11px] text-faint">
                  {s.agentPresent ? (
                    <span className="text-good">Agent present</span>
                  ) : (
                    <span>Not found</span>
                  )}
                </div>
              </div>
              {(s.store === 'epic' || s.store === 'gog') && (
                <button type="button" className="exo-ghost-btn" onClick={() => onAuth(s.store)}>
                  Sign in
                </button>
              )}
            </li>
          ))}
        </ul>
      </div>

      <p className="mt-16 text-[11px] text-fg-subtle">Exo Launcher {settings?.appVersion ?? '—'} · MIT</p>
    </div>
  )
}

function Toggle({
  title,
  hint,
  value,
  onChange,
}: {
  title: string
  hint?: string
  value: boolean
  onChange: (v: boolean) => void
}) {
  return (
    <button
      type="button"
      className="flex w-full items-center justify-between rounded-xl border border-border bg-surface px-4 py-3 text-left transition-colors hover:border-line"
      onClick={() => onChange(!value)}
    >
      <div>
        <span>{title}</span>
        {hint && <p className="mt-0.5 text-[11px] text-faint">{hint}</p>}
      </div>
      <span
        className={cn(
          'relative h-6 w-11 shrink-0 rounded-full border transition-colors',
          value ? 'border-white/30 bg-primary' : 'border-border bg-surface-2',
        )}
      >
        <span
          className="absolute top-0.5 h-[18px] w-[18px] rounded-full transition-all"
          style={{ left: value ? 22 : 3, background: value ? '#0a0a0a' : '#63636b' }}
        />
      </span>
    </button>
  )
}

function DepsPanel({
  items,
  onOffer,
  onBack,
  onRefresh,
}: {
  items: DependencyItem[]
  onOffer: (id: string) => void
  onBack: () => void
  onRefresh: () => void
}) {
  return (
    <div className="px-10 py-10">
      <button type="button" className="mb-8 text-xs text-fg-subtle hover:text-fg" onClick={onBack}>
        Back to library
      </button>
      <div className="flex items-end justify-between gap-4">
        <div>
          <h2 className="text-2xl font-semibold tracking-tight">Dependencies</h2>
          <p className="mt-2 text-sm text-fg-muted">Official installers only — nothing forced.</p>
        </div>
        <button type="button" className="exo-ghost-btn" onClick={onRefresh}>
          Rescan
        </button>
      </div>
      <ul className="mt-10 max-w-2xl space-y-2">
        {items.length === 0 ? (
          <li className="text-sm text-fg-muted">No results yet. Press Rescan.</li>
        ) : (
          items.map((d) => (
            <li
              key={d.id}
              className="flex items-center justify-between gap-4 rounded-xl border border-border bg-surface px-5 py-4"
            >
              <div>
                <div className="text-sm font-medium">
                  {d.name}{' '}
                  <span
                    className={cn(
                      'text-[10px] uppercase',
                      d.status === 'Present' ? 'text-good' : 'text-fg-subtle',
                    )}
                  >
                    {d.status}
                  </span>
                </div>
                <p className="mt-1 text-xs text-fg-subtle">{d.detail}</p>
              </div>
              {d.canOfferInstall && d.status !== 'Present' && (
                <button
                  type="button"
                  className="exo-cta shrink-0 !h-9 !px-4 !text-xs"
                  onClick={() => onOffer(d.id)}
                >
                  Official installer
                </button>
              )}
            </li>
          ))
        )}
      </ul>
    </div>
  )
}
