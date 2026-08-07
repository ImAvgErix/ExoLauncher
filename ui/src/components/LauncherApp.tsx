/**
 * Exo Launcher AMOLED shell — library + quiet detail + settings.
 * Same design language as Exo / ExoOS: true black, hairlines, white pill CTA.
 */
import { useCallback, useEffect, useMemo, useState } from 'react'
import { Minus, RefreshCw, Settings, X } from 'lucide-react'
import {
  host,
  onHostEvent,
  type DependencyItem,
  type Game,
  type LauncherSettings,
} from '../lib/host'
import { cn, formatPlaytime, formatSize, storeDotColor, storeLabel } from '../lib/utils'

type View = 'library' | 'settings' | 'deps'

export function LauncherApp() {
  const [games, setGames] = useState<Game[]>([])
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [launching, setLaunching] = useState(false)
  const [statusMsg, setStatusMsg] = useState<string | null>(null)
  const [view, setView] = useState<View>('library')
  const [settings, setSettings] = useState<LauncherSettings | null>(null)
  const [deps, setDeps] = useState<DependencyItem[]>([])
  const [filter, setFilter] = useState('')

  const loadLibrary = useCallback(async (force = false) => {
    setLoading(true)
    setStatusMsg(null)
    try {
      const res = await host.getLibrary(force)
      setGames(res.games)
      setSelectedId((prev) => {
        if (prev && res.games.some((g) => g.id === prev)) return prev
        return res.games[0]?.id ?? null
      })
    } catch (e) {
      setStatusMsg(e instanceof Error ? e.message : 'Library load failed')
    } finally {
      setLoading(false)
    }
  }, [])

  const loadSettings = useCallback(async () => {
    try {
      setSettings(await host.getSettings())
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
    const off = onHostEvent('launch.status', (data) => {
      const d = data as { message?: string; ok?: boolean }
      if (d?.message) setStatusMsg(d.message)
    })
    return off
  }, [loadLibrary, loadSettings])

  const selected = useMemo(
    () => games.find((g) => g.id === selectedId) ?? null,
    [games, selectedId],
  )

  const filtered = useMemo(() => {
    const q = filter.trim().toLowerCase()
    if (!q) return games
    return games.filter(
      (g) =>
        g.title.toLowerCase().includes(q) ||
        g.store.toLowerCase().includes(q) ||
        storeLabel(g.store).toLowerCase().includes(q),
    )
  }, [games, filter])

  async function onPlay() {
    if (!selected || launching) return
    setLaunching(true)
    setStatusMsg(null)
    try {
      const res = await host.launch(selected.id)
      setStatusMsg(res.message)
    } catch (e) {
      setStatusMsg(e instanceof Error ? e.message : 'Launch failed')
    } finally {
      setLaunching(false)
    }
  }

  async function patchSettings(patch: Partial<LauncherSettings>) {
    try {
      const next = await host.setSettings(patch)
      setSettings(next)
    } catch (e) {
      setStatusMsg(e instanceof Error ? e.message : 'Settings save failed')
    }
  }

  return (
    <div className="flex h-full w-full flex-col bg-bg text-fg">
      {/* Title rail — drag + caption */}
      <header className="exo-titlebar flex h-14 shrink-0 items-center justify-between border-b border-[var(--exo-hairline)] px-5">
        <div className="flex items-center gap-3">
          <span className="text-[15px] font-medium tracking-tight">Exo Launcher</span>
          <span className="rounded-full border border-[var(--exo-hairline)] px-2 py-0.5 text-[10px] uppercase tracking-[0.12em] text-[var(--exo-muted)]">
            Phase 1
          </span>
        </div>
        <div className="flex items-center gap-1">
          <button
            type="button"
            className="exo-titlebar-button"
            title="Refresh library"
            onClick={() => void loadLibrary(true)}
          >
            <RefreshCw size={15} strokeWidth={1.75} className={loading ? 'animate-spin' : ''} />
          </button>
          <button
            type="button"
            className={cn('exo-titlebar-button', view === 'deps' && 'text-fg')}
            title="Dependencies"
            onClick={() => {
              setView(view === 'deps' ? 'library' : 'deps')
              void loadDeps()
            }}
          >
            <span className="text-[11px] font-medium tracking-wide">Deps</span>
          </button>
          <button
            type="button"
            className={cn('exo-titlebar-button', view === 'settings' && 'text-fg')}
            title="Settings"
            onClick={() => setView(view === 'settings' ? 'library' : 'settings')}
          >
            <Settings size={15} strokeWidth={1.75} />
          </button>
          <div className="mx-1 h-4 w-px bg-[var(--exo-hairline)]" />
          <button
            type="button"
            className="exo-titlebar-button"
            title="Minimize"
            onClick={() => void host.minimize()}
          >
            <Minus size={15} strokeWidth={1.75} />
          </button>
          <button
            type="button"
            className="exo-titlebar-button is-close"
            title="Close"
            onClick={() => void host.close()}
          >
            <X size={15} strokeWidth={1.75} />
          </button>
        </div>
      </header>

      <main className="flex min-h-0 flex-1">
        {view === 'settings' ? (
          <SettingsPanel
            settings={settings}
            onPatch={patchSettings}
            onBack={() => setView('library')}
          />
        ) : view === 'deps' ? (
          <DepsPanel
            items={deps}
            onOffer={(id) => void host.offerDepInstall(id)}
            onBack={() => setView('library')}
            onRefresh={() => void loadDeps()}
          />
        ) : (
          <>
            {/* Library column */}
            <section className="flex w-[420px] shrink-0 flex-col border-r border-[var(--exo-hairline)]">
              <div className="px-5 pb-3 pt-5">
                <p className="mb-3 text-[11px] font-medium uppercase tracking-[0.14em] text-[var(--exo-muted)]">
                  Library
                </p>
                <input
                  type="search"
                  value={filter}
                  onChange={(e) => setFilter(e.target.value)}
                  placeholder="Filter titles"
                  className="w-full rounded-full border border-[var(--exo-hairline)] bg-surface px-4 py-2 text-[13px] text-fg outline-none placeholder:text-[var(--exo-muted)] focus:border-[rgba(255,255,255,0.2)]"
                />
              </div>
              <div className="min-h-0 flex-1 overflow-y-auto px-3 pb-4">
                {loading && games.length === 0 ? (
                  <p className="px-2 py-8 text-center text-[13px] text-[var(--exo-muted)]">
                    Scanning…
                  </p>
                ) : filtered.length === 0 ? (
                  <p className="px-2 py-8 text-center text-[13px] text-[var(--exo-muted)]">
                    No titles match.
                  </p>
                ) : (
                  <ul className="flex flex-col gap-1.5">
                    {filtered.map((g) => (
                      <li key={g.id}>
                        <button
                          type="button"
                          className={cn(
                            'game-card flex w-full items-center gap-3 rounded-xl border border-transparent px-3 py-2.5 text-left transition-colors',
                            selectedId === g.id && 'is-selected',
                          )}
                          onClick={() => setSelectedId(g.id)}
                        >
                          <CoverMark title={g.title} store={g.store} />
                          <div className="min-w-0 flex-1">
                            <div className="truncate text-[13.5px] font-medium tracking-tight">
                              {g.title}
                            </div>
                            <div className="mt-0.5 flex items-center gap-1.5 text-[11px] text-[var(--exo-muted)]">
                              <span
                                className="inline-block h-1.5 w-1.5 rounded-full"
                                style={{ background: storeDotColor(g.store) }}
                              />
                              <span>{storeLabel(g.store)}</span>
                              {!g.installed && g.status !== 'Demo' && (
                                <span className="text-[var(--exo-faint)]">· not installed</span>
                              )}
                              {g.status === 'Demo' && (
                                <span className="text-[var(--exo-faint)]">· demo</span>
                              )}
                            </div>
                          </div>
                        </button>
                      </li>
                    ))}
                  </ul>
                )}
              </div>
            </section>

            {/* Detail pane — lots of air, one primary action */}
            <section className="flex min-w-0 flex-1 flex-col">
              {selected ? (
                <div className="flex flex-1 flex-col px-12 py-10">
                  <div className="flex items-start gap-8">
                    <CoverMark title={selected.title} store={selected.store} large />
                    <div className="min-w-0 flex-1 pt-1">
                      <div className="mb-2 flex items-center gap-2">
                        <span
                          className="inline-block h-1.5 w-1.5 rounded-full"
                          style={{ background: storeDotColor(selected.store) }}
                        />
                        <span className="text-[11px] uppercase tracking-[0.14em] text-[var(--exo-muted)]">
                          {storeLabel(selected.store)}
                        </span>
                        <StatusPill status={selected.status} installed={selected.installed} />
                      </div>
                      <h1 className="text-[32px] font-medium leading-tight tracking-tight">
                        {selected.title}
                      </h1>
                    </div>
                  </div>

                  <div className="mt-10 grid max-w-lg grid-cols-3 gap-4">
                    <Fact label="Playtime" value={formatPlaytime(selected.playtimeMinutes)} />
                    <Fact label="Size" value={formatSize(selected.sizeBytes)} />
                    <Fact label="Status" value={selected.status || (selected.installed ? 'Ready' : '—')} />
                  </div>

                  <p className="mt-8 max-w-xl text-[13px] leading-relaxed text-[var(--exo-secondary)]">
                    {selected.launchNote || 'No launch note.'}
                  </p>

                  {selected.deps.length > 0 && (
                    <p className="mt-3 max-w-xl text-[12px] text-[var(--exo-muted)]">
                      Depends on: {selected.deps.join(' · ')}
                    </p>
                  )}

                  <div className="mt-auto flex items-center gap-4 pt-12">
                    <button
                      type="button"
                      className="pill-primary rounded-full bg-white px-8 py-2.5 text-[13px] font-medium text-black transition-transform disabled:opacity-40"
                      disabled={launching}
                      onClick={() => void onPlay()}
                    >
                      {launching ? 'Starting…' : 'Play'}
                    </button>
                    {statusMsg && (
                      <span className="max-w-md text-[12px] text-[var(--exo-muted)]">{statusMsg}</span>
                    )}
                  </div>
                </div>
              ) : (
                <div className="flex flex-1 items-center justify-center">
                  <p className="text-[13px] text-[var(--exo-muted)]">Select a title.</p>
                </div>
              )}
            </section>
          </>
        )}
      </main>
    </div>
  )
}

function CoverMark({
  title,
  store,
  large = false,
}: {
  title: string
  store: string
  large?: boolean
}) {
  const initial = title.trim().charAt(0).toUpperCase() || '?'
  const size = large ? 'h-[120px] w-[90px] text-[28px]' : 'h-12 w-9 text-[14px]'
  return (
    <div
      className={cn(
        'flex shrink-0 items-center justify-center rounded-lg border border-[var(--exo-hairline)] bg-raised font-medium tracking-tight text-fg',
        size,
      )}
      style={{
        boxShadow: `inset 0 -2px 0 0 ${storeDotColor(store)}`,
      }}
      aria-hidden
    >
      {initial}
    </div>
  )
}

function Fact({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-xl border border-[var(--exo-hairline)] bg-surface px-4 py-3">
      <div className="text-[10px] uppercase tracking-[0.14em] text-[var(--exo-muted)]">{label}</div>
      <div className="mt-1.5 text-[14px] font-medium tracking-tight">{value}</div>
    </div>
  )
}

function StatusPill({ status, installed }: { status: string; installed: boolean }) {
  const label = status || (installed ? 'Ready' : 'Missing')
  return (
    <span className="rounded-full border border-[var(--exo-hairline)] px-2 py-0.5 text-[10px] uppercase tracking-[0.1em] text-[var(--exo-muted)]">
      {label}
    </span>
  )
}

function SettingsPanel({
  settings,
  onPatch,
  onBack,
}: {
  settings: LauncherSettings | null
  onPatch: (p: Partial<LauncherSettings>) => void
  onBack: () => void
}) {
  return (
    <div className="flex flex-1 flex-col px-12 py-10">
      <button
        type="button"
        className="mb-8 self-start text-[12px] text-[var(--exo-muted)] hover:text-fg"
        onClick={onBack}
      >
        Back to library
      </button>
      <h2 className="text-[24px] font-medium tracking-tight">Settings</h2>
      <p className="mt-2 max-w-lg text-[13px] text-[var(--exo-secondary)]">
        Quiet defaults. Consent before any download or elevated action. Anti-cheat safe mode is
        always on.
      </p>

      <div className="mt-10 max-w-xl space-y-1">
        <ToggleRow
          title="Close store clients after launch"
          detail="Soft-close store UI chrome when possible. Never kills anti-cheat services."
          value={settings?.closeStoreClientsAfterLaunch ?? true}
          onChange={(v) => onPatch({ closeStoreClientsAfterLaunch: v })}
        />
        <ToggleRow
          title="Auto-install missing redistributables"
          detail="Off by default. When on, still opens the official installer — never silent force."
          value={settings?.autoInstallRedistributables ?? false}
          onChange={(v) => onPatch({ autoInstallRedistributables: v })}
        />
        <ToggleRow
          title="Minimize Exo while playing"
          detail="Hides this window when a launch is requested."
          value={settings?.minimizeWhilePlaying ?? true}
          onChange={(v) => onPatch({ minimizeWhilePlaying: v })}
        />
        <ToggleRow
          title="Anti-cheat safe mode"
          detail="Always on. No game binary edits, no kernel hacks, no bypass."
          value
          locked
          onChange={() => undefined}
        />
      </div>

      <p className="mt-auto pt-10 text-[11px] text-[var(--exo-muted)]">
        Exo Launcher {settings?.appVersion ?? '—'} · MIT · ImAvgErix
      </p>
    </div>
  )
}

function ToggleRow({
  title,
  detail,
  value,
  onChange,
  locked = false,
}: {
  title: string
  detail: string
  value: boolean
  onChange: (v: boolean) => void
  locked?: boolean
}) {
  return (
    <div className="toggle-row flex items-center justify-between gap-6 rounded-xl border border-transparent px-4 py-3.5">
      <div className="min-w-0">
        <div className="text-[13.5px] font-medium tracking-tight">{title}</div>
        <div className="mt-0.5 text-[12px] leading-relaxed text-[var(--exo-muted)]">{detail}</div>
      </div>
      <button
        type="button"
        role="switch"
        aria-checked={value}
        disabled={locked}
        onClick={() => !locked && onChange(!value)}
        className={cn(
          'relative h-6 w-11 shrink-0 rounded-full border transition-colors',
          value ? 'border-white/30 bg-white' : 'border-[var(--exo-hairline)] bg-raised',
          locked && 'opacity-60',
        )}
      >
        <span
          className={cn(
            'absolute top-0.5 h-4.5 w-4.5 rounded-full transition-all',
            value ? 'left-5.5 bg-black' : 'left-0.5 bg-[var(--exo-muted)]',
          )}
          style={{
            width: 18,
            height: 18,
            top: 2,
            left: value ? 22 : 3,
          }}
        />
      </button>
    </div>
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
    <div className="flex flex-1 flex-col px-12 py-10">
      <button
        type="button"
        className="mb-8 self-start text-[12px] text-[var(--exo-muted)] hover:text-fg"
        onClick={onBack}
      >
        Back to library
      </button>
      <div className="flex items-end justify-between gap-4">
        <div>
          <h2 className="text-[24px] font-medium tracking-tight">Dependencies</h2>
          <p className="mt-2 max-w-lg text-[13px] text-[var(--exo-secondary)]">
            Detect common runtimes. Installers open the official vendor page — nothing is forced.
          </p>
        </div>
        <button
          type="button"
          className="rounded-full border border-[var(--exo-hairline)] px-4 py-1.5 text-[12px] text-[var(--exo-secondary)] hover:text-fg"
          onClick={onRefresh}
        >
          Rescan
        </button>
      </div>

      <ul className="mt-10 max-w-2xl space-y-2">
        {items.length === 0 ? (
          <li className="text-[13px] text-[var(--exo-muted)]">No results yet. Press Rescan.</li>
        ) : (
          items.map((d) => (
            <li
              key={d.id}
              className="flex items-center justify-between gap-4 rounded-xl border border-[var(--exo-hairline)] bg-surface px-5 py-4"
            >
              <div className="min-w-0">
                <div className="flex items-center gap-2">
                  <span className="text-[13.5px] font-medium">{d.name}</span>
                  <span className="rounded-full border border-[var(--exo-hairline)] px-2 py-0.5 text-[10px] uppercase tracking-[0.1em] text-[var(--exo-muted)]">
                    {d.status}
                  </span>
                </div>
                <p className="mt-1 text-[12px] text-[var(--exo-muted)]">{d.detail}</p>
              </div>
              {d.canOfferInstall && d.status !== 'Present' && (
                <button
                  type="button"
                  className="shrink-0 rounded-full bg-white px-4 py-1.5 text-[12px] font-medium text-black"
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
