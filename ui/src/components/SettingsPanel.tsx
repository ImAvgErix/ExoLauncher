import { useState, type CSSProperties } from 'react'
import { Loader2, Minus, X } from 'lucide-react'
import {
  host,
  type LauncherSettings,
  type StoreStatus,
} from '../lib/host'
import { TrophyNotificationSettings } from './TrophyNotificationSettings'

export function SettingsShell({
  children,
  onBack,
}: {
  children: React.ReactNode
  onBack: () => void
}) {
  return (
    <div className="exo-app">
      <header className="exo-titlebar">
        <button
          type="button"
          className="exo-brand exo-no-drag shrink-0"
          title="Exo Launcher"
          onClick={onBack}
          aria-label="Back to library"
        >
          <img src="./logo.png" alt="" className="exo-brand-logo" width={28} height={28} draggable={false} />
        </button>
        <span className="text-[13px] font-semibold tracking-tight">Settings</span>
        <div className="exo-titlebar-actions">
          <button type="button" className="exo-winbtn" title="Minimize" onClick={() => void host.minimize()}>
            <Minus size={15} strokeWidth={1.75} />
          </button>
          <button type="button" className="exo-winbtn is-close" title="Close" onClick={() => void host.close()}>
            <X size={15} strokeWidth={1.75} />
          </button>
        </div>
      </header>
      <div className="relative z-10 min-h-0 flex-1 overflow-y-auto">{children}</div>
    </div>
  )
}

export function SettingsPanel({
  settings,
  stores,
  authBusy,
  authMsg,
  updateBusy,
  updatePercent,
  updateAvailable,
  onAuth,
  onCheckUpdate,
  onInstallUpdate,
  onSettings,
}: {
  settings: LauncherSettings | null
  stores: StoreStatus[]
  authBusy: string | null
  authMsg: string | null
  updateBusy: boolean
  updatePercent: number
  updateAvailable: boolean
  onAuth: (store: string) => void
  onCheckUpdate: () => void
  onInstallUpdate: () => void
  onSettings: (next: LauncherSettings) => void
}) {
  const agentStores = (stores.length ? stores : []).filter((s) => s.store !== 'local')
  const [openingStore, setOpeningStore] = useState<string | null>(null)
  const [localMsg, setLocalMsg] = useState<string | null>(null)
  const [trophyBusy, setTrophyBusy] = useState(false)

  const panelMessage = localMsg ?? authMsg
  const fallbackStores: StoreStatus[] = [
    { store: 'steam', displayName: 'Steam', agentPresent: false },
    { store: 'epic', displayName: 'Epic', agentPresent: false },
    { store: 'gog', displayName: 'GOG', agentPresent: false },
    { store: 'riot', displayName: 'Riot', agentPresent: false },
  ]

  async function openStore(store: StoreStatus) {
    setOpeningStore(store.store)
    setLocalMsg(`Opening ${store.displayName}…`)
    try {
      const result = await host.showStore(store.store)
      setLocalMsg(result.message ?? (result.ok ? `Opening ${store.displayName}…` : `Could not open ${store.displayName}`))
    } catch (error) {
      setLocalMsg(error instanceof Error ? error.message : `Could not open ${store.displayName}`)
    } finally {
      setOpeningStore(null)
    }
  }

  async function saveTrophySettings(patch: Partial<LauncherSettings>) {
    if (!settings) return
    const optimistic = { ...settings, ...patch }
    onSettings(optimistic)
    setLocalMsg(null)
    try {
      onSettings(await host.setSettings(patch))
    } catch (error) {
      onSettings(settings)
      setLocalMsg(error instanceof Error ? error.message : 'Could not save trophy settings')
    }
  }

  return (
    <div className="mx-auto w-full max-w-[1280px] px-6 py-7">
      <div className="mb-6 max-w-2xl">
        <p className="text-[11px] font-medium uppercase tracking-[0.14em] text-faint">Preferences</p>
        <h2 className="mt-1 text-lg font-medium tracking-tight text-fg">Launcher settings</h2>
      </div>
      {panelMessage && (
        <div
          className="mb-5 border-l-2 border-line bg-elevated px-3 py-2.5 text-[13px] text-fg"
          role="status"
          aria-live="polite"
        >
          {panelMessage}
        </div>
      )}

      <div className="grid items-start gap-x-8 gap-y-0 lg:grid-cols-2">
        <div className="divide-y divide-line-soft">
          <section className="pb-6" aria-labelledby="updates-heading">
            <div className="flex items-center justify-between gap-4">
              <div>
                <h3 id="updates-heading" className="text-[15px] font-medium text-fg">App updates</h3>
                <p className="mt-1.5 text-[12px] text-faint">Version {settings?.appVersion ?? '—'}</p>
              </div>
              <div className="flex flex-wrap justify-end gap-2">
                <button type="button" className="exo-ghost-btn min-h-10" disabled={updateBusy} onClick={onCheckUpdate}>Check</button>
                {(updateAvailable || updateBusy) && (
                  <button
                    type="button"
                    className={`exo-cta exo-update-action h-10 px-4 text-[13px]${updateBusy ? ' is-active' : ''}`}
                    disabled={updateBusy}
                    onClick={onInstallUpdate}
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
                        <strong>{`Updating… ${Math.round(updatePercent)}%`}</strong>
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
                )}
                {updateBusy && (
                  <span className="sr-only" role="status" aria-live="polite" aria-atomic="true">
                    Updating app · {Math.round(updatePercent)}%
                  </span>
                )}
              </div>
            </div>
          </section>

          <section className="py-6" aria-labelledby="portable-heading">
            <div className="flex items-center justify-between gap-4">
              <h3 id="portable-heading" className="text-[15px] font-medium text-fg">Portable game</h3>
              <button
                type="button"
                className="exo-ghost-btn min-h-10 shrink-0"
                onClick={() => void (async () => {
                  const pick = await host.pickFolder('Choose game folder')
                  if (!pick.ok || !pick.path) return
                  const result = await host.install('local:add', pick.path, undefined)
                  setLocalMsg(result.message ?? (result.ok ? 'Portable game added.' : 'Could not add portable game'))
                })()}
              >
                Add folder…
              </button>
            </div>
          </section>
        </div>

        <div className="divide-y divide-line-soft border-t border-line-soft lg:border-t-0">
          <section className="pb-6 pt-6 lg:pt-0" aria-labelledby="backends-heading">
            <h3 id="backends-heading" className="text-[15px] font-medium text-fg">Store backends</h3>
            <ul className="mt-2 divide-y divide-line-soft">
              {(agentStores.length ? agentStores : fallbackStores).map((store) => {
                // A stale account token must never make an absent backend look
                // connected. There is no useful Settings action until its
                // local backend/client is actually available.
                // GOG can use Exo's bundled gogdl backend while Galaxy itself
                // is absent. Only a real vendor client may say Ready or Open.
                const clientInstalled = store.clientPresent ?? store.agentPresent
                const backendAvailable = !!store.agentPresent
                // A headless backend can be authenticated, but it must never
                // make an absent vendor desktop client look installed.
                const accountConnected = backendAvailable && !!store.signedIn
                const connected = clientInstalled && accountConnected
                const canOpen = clientInstalled && ['steam', 'epic', 'gog', 'riot'].includes(store.store)
                const canAuthenticate = backendAvailable && (store.store === 'epic' || store.store === 'gog')
                const isOpening = openingStore === store.store
                return (
                  <li key={store.store} className="flex items-center justify-between gap-3 py-2.5">
                    <div>
                      <div className="text-[14px] text-fg">{store.displayName}</div>
                      <div className={`mt-0.5 text-[11px] ${connected ? 'text-good' : 'text-faint'}`}>
                          {connected ? 'Connected' : clientInstalled ? 'Ready' : 'Not installed'}
                      </div>
                    </div>
                    <div className="flex shrink-0 gap-2">
                      {canOpen && (
                        <button type="button" className="exo-ghost-btn min-h-10" disabled={isOpening} onClick={() => void openStore(store)}>
                          {isOpening ? <><Loader2 className="h-3.5 w-3.5 animate-spin" /> Opening…</> : 'Open'}
                        </button>
                      )}
                      {canAuthenticate && (
                        <button type="button" className="exo-ghost-btn min-h-10" disabled={authBusy === store.store} onClick={() => onAuth(store.store)}>
                          {authBusy === store.store ? <><Loader2 className="h-3.5 w-3.5 animate-spin" /> Working…</> : accountConnected ? 'Reconnect' : 'Connect'}
                        </button>
                      )}
                    </div>
                  </li>
                )
              })}
            </ul>
          </section>

          <div className="py-6">
            <TrophyNotificationSettings
              settings={settings}
              previewBusy={trophyBusy}
              onSettings={onSettings}
              onSave={saveTrophySettings}
              onPreview={async () => {
                setTrophyBusy(true)
                try { await host.previewTrophy() }
                finally { setTrophyBusy(false) }
              }}
            />
          </div>
        </div>
      </div>
    </div>
  )
}
