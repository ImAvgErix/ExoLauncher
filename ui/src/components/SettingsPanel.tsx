import { useState, type CSSProperties } from 'react'
import { Coffee, ExternalLink, FileText, Loader2 } from '../brand/icons'
import { ExoMark } from '../brand/ExoMark'
import { host, type LauncherSettings, type StoreStatus } from '../lib/host'
import { TrophyNotificationSettings } from './TrophyNotificationSettings'
import { WindowChrome } from './WindowChrome'

const BUY_ME_A_COFFEE = 'https://www.buymeacoffee.com/UhhErix'
const RELEASES = 'https://github.com/ImAvgErix/ExoLauncher/releases/latest'
const ISSUES = 'https://github.com/ImAvgErix/ExoLauncher/issues'
const PRIVACY = 'https://github.com/ImAvgErix/ExoLauncher/blob/main/PRIVACY.md'

export function SettingsShell({ children, onBack, alive = false }: { children: React.ReactNode; onBack: () => void; alive?: boolean }) {
  return (
    <div className="exo-app">
      <header className={`exo-titlebar${alive ? ' is-busy' : ''}`}>
        <button type="button" className="exo-brand exo-no-drag shrink-0" title="Exo Launcher" onClick={onBack} aria-label="Back to library">
          <ExoMark size={28} className="exo-brand-logo" alive={alive} />
        </button>
        <span className="text-[13px] font-semibold tracking-tight">Settings</span>
        <div className="exo-titlebar-actions">
          <WindowChrome />
        </div>
      </header>
      <div className="relative z-10 min-h-0 flex-1 overflow-y-auto">{children}</div>
    </div>
  )
}

export function SettingsPanel({
  settings,
  stores,
  message,
  updateBusy,
  updatePercent,
  updateAvailable,
  onCheckUpdate,
  onInstallUpdate,
  onSettings,
}: {
  settings: LauncherSettings | null
  stores: StoreStatus[]
  message: string | null
  updateBusy: boolean
  updatePercent: number
  updateAvailable: boolean
  onCheckUpdate: () => void
  onInstallUpdate: () => void
  onSettings: (next: LauncherSettings) => void
}) {
  const [openingStore, setOpeningStore] = useState<string | null>(null)
  const [localMsg, setLocalMsg] = useState<string | null>(null)
  const [trophyBusy, setTrophyBusy] = useState(false)
  const panelMessage = localMsg ?? message
  const storeRows = stores.filter((store) => {
    if (store.store === 'local') return false
    const clientInstalled = store.clientPresent ?? store.agentPresent
    return !!clientInstalled || !!store.signedIn
  })

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
      setLocalMsg(error instanceof Error ? error.message : 'Could not save achievement notification settings')
    }
  }

  return (
    <div className="mx-auto flex h-full w-full max-w-[1280px] min-h-0 flex-col px-4 py-4">
      {panelMessage && (
        <div className="mb-3 shrink-0 border-l-2 border-line bg-elevated px-3 py-2 text-[12px] text-fg" role="status" aria-live="polite">
          {panelMessage}
        </div>
      )}

      <div className="grid min-h-0 flex-1 items-start gap-4 xl:grid-cols-[1.15fr_0.85fr]">
        <div className="grid min-w-0 items-start gap-4 md:grid-cols-[0.9fr_1.1fr]">
          <div className="divide-y divide-line-soft">
            <section className="pb-3" aria-labelledby="updates-heading">
              <div className="flex items-center justify-between gap-3">
                <h3 id="updates-heading" className="text-[13px] font-medium text-fg">App updates</h3>
                <div className="flex shrink-0 gap-2">
                  <button type="button" className="exo-ghost-btn min-h-8 px-3 text-[11px]" disabled={updateBusy} onClick={onCheckUpdate}>Check</button>
                  {(updateAvailable || updateBusy) && (
                    <button type="button" className={`exo-cta exo-update-action h-8 px-3 text-[12px]${updateBusy ? ' is-active' : ''}`} disabled={updateBusy} onClick={onInstallUpdate}>
                      {updateBusy && <span className="exo-action-progress" style={{ '--progress': Math.max(0, Math.min(100, updatePercent)) / 100 } as CSSProperties} aria-hidden="true" />}
                      <span className="exo-action-state">
                        <span className="exo-action-content exo-action-idle" aria-hidden={updateBusy}><strong>Update</strong></span>
                        <span className="exo-action-content exo-action-active" aria-hidden={!updateBusy}><Loader2 size={16} className="animate-spin" /><strong>{`${Math.round(updatePercent)}%`}</strong></span>
                      </span>
                      {updateBusy && <span className="sr-only" role="progressbar" aria-label="App update progress" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(Math.max(0, Math.min(100, updatePercent)))} />}
                    </button>
                  )}
                </div>
              </div>
            </section>

            <section className="py-3" aria-labelledby="portable-heading">
              <div className="flex items-center justify-between gap-3">
                <h3 id="portable-heading" className="text-[13px] font-medium text-fg">Portable game</h3>
                <button type="button" className="exo-ghost-btn min-h-8 shrink-0 px-3 text-[11px]" onClick={() => void (async () => {
                  const pick = await host.pickFolder('Choose game folder')
                  if (!pick.ok || !pick.path) return
                  const result = await host.install('local:add', pick.path, undefined)
                  setLocalMsg(result.message ?? (result.ok ? 'Portable game added.' : 'Could not add portable game'))
                })()}>
                  Add folder…
                </button>
              </div>
            </section>

            <section className="pt-3" aria-labelledby="help-heading">
              <div className="flex items-baseline justify-between gap-3">
                <h3 id="help-heading" className="text-[13px] font-medium text-fg">Help &amp; support</h3>
                <span className="text-[11px] text-faint">v{settings?.appVersion ?? '—'}</span>
              </div>
              <div className="mt-2 grid grid-cols-2 gap-1.5">
                <button type="button" className="exo-settings-link" onClick={() => void host.openUrl(ISSUES)}><FileText size={16} />Report issue</button>
                <button type="button" className="exo-settings-link" onClick={() => void host.openUrl(BUY_ME_A_COFFEE)}><Coffee size={16} />Buy me a coffee</button>
                <button type="button" className="exo-settings-link" onClick={() => void host.openUrl(PRIVACY)}><FileText size={16} />Privacy</button>
                <button type="button" className="exo-settings-link" onClick={() => void host.openUrl(RELEASES)}><ExternalLink size={16} />Releases</button>
              </div>
            </section>
          </div>

          <section className="min-w-0" aria-labelledby="backends-heading">
            <h3 id="backends-heading" className="text-[13px] font-medium text-fg">Store apps</h3>
            {stores.length === 0 ? (
              <p className="mt-1.5 text-[12px] text-faint">Unknown — waiting for store scan.</p>
            ) : (
              <ul className="mt-1.5 grid grid-cols-1 divide-y divide-line-soft">
                {storeRows.map((store) => {
                  const clientInstalled = store.clientPresent ?? store.agentPresent
                  const canOpen = !!clientInstalled
                  const isOpening = openingStore === store.store
                  return (
                    <li key={store.store} className="flex min-w-0 items-center justify-between gap-2 py-2 first:border-t first:border-line-soft">
                      <div className="min-w-0">
                        <div className="truncate text-[13px] text-fg">{store.displayName}</div>
                        <div className={`mt-0.5 text-[10px] ${clientInstalled ? 'text-good' : 'text-faint'}`}>{clientInstalled ? 'Installed' : 'Not installed'}</div>
                      </div>
                      {canOpen && <button type="button" className="exo-ghost-btn min-h-8 shrink-0 px-2.5 text-[11px]" disabled={isOpening} onClick={() => void openStore(store)}>{isOpening ? <Loader2 size={16} className="animate-spin" /> : 'Open'}</button>}
                    </li>
                  )
                })}
              </ul>
            )}
          </section>
        </div>

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
  )
}
