import { useState, type CSSProperties } from 'react'
import { ExternalLink, FileText, HeartHandshake, Loader2, Minus, X } from 'lucide-react'
import { host, type LauncherSettings, type StoreStatus } from '../lib/host'
import { TrophyNotificationSettings } from './TrophyNotificationSettings'

const BUY_ME_A_COFFEE = 'https://www.buymeacoffee.com/UhhErix'
const RELEASES = 'https://github.com/ImAvgErix/ExoLauncher/releases/latest'
const ISSUES = 'https://github.com/ImAvgErix/ExoLauncher/issues'
const PRIVACY = 'https://github.com/ImAvgErix/ExoLauncher/blob/main/PRIVACY.md'

export function SettingsShell({ children, onBack }: { children: React.ReactNode; onBack: () => void }) {
  return (
    <div className="exo-app">
      <header className="exo-titlebar">
        <button type="button" className="exo-brand exo-no-drag shrink-0" title="Exo Launcher" onClick={onBack} aria-label="Back to library">
          <img src="./logo.png" alt="" className="exo-brand-logo" width={28} height={28} draggable={false} />
        </button>
        <span className="text-[13px] font-semibold tracking-tight">Settings</span>
        <div className="exo-titlebar-actions">
          <button type="button" className="exo-winbtn" title="Minimize" onClick={() => void host.minimize()}><Minus size={15} strokeWidth={1.75} /></button>
          <button type="button" className="exo-winbtn is-close" title="Close" onClick={() => void host.close()}><X size={15} strokeWidth={1.75} /></button>
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
  const storeRows = (stores.length ? stores : [
    { store: 'steam', displayName: 'Steam', agentPresent: false },
    { store: 'epic', displayName: 'Epic', agentPresent: false },
    { store: 'gog', displayName: 'GOG', agentPresent: false },
    { store: 'riot', displayName: 'Riot', agentPresent: false },
  ]).filter((store) => store.store !== 'local')

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
    <div className="mx-auto flex h-full w-full max-w-[1340px] min-h-[720px] flex-col px-7 py-5">
      <div className="mb-4 flex shrink-0 items-end justify-between gap-5">
        <div>
          <p className="text-[10px] font-medium uppercase tracking-[0.14em] text-faint">Preferences</p>
          <h2 className="mt-1 text-[17px] font-medium tracking-tight text-fg">Launcher settings</h2>
        </div>
        <span className="pb-0.5 text-[11px] text-faint">v{settings?.appVersion ?? '—'}</span>
      </div>

      {panelMessage && (
        <div className="mb-3 shrink-0 border-l-2 border-line bg-elevated px-3 py-2 text-[12px] text-fg" role="status" aria-live="polite">
          {panelMessage}
        </div>
      )}

      <div className="grid min-h-0 flex-1 items-start gap-x-7 gap-y-5 xl:grid-cols-[0.86fr_1.02fr_1.2fr]">
        <div className="divide-y divide-line-soft">
          <section className="pb-4" aria-labelledby="updates-heading">
            <div className="flex items-center justify-between gap-3">
              <div>
                <h3 id="updates-heading" className="text-[14px] font-medium text-fg">App updates</h3>
                <p className="mt-0.5 text-[11px] text-faint">Check and install in Exo.</p>
              </div>
              <div className="flex shrink-0 gap-2">
                <button type="button" className="exo-ghost-btn min-h-9" disabled={updateBusy} onClick={onCheckUpdate}>Check</button>
                {(updateAvailable || updateBusy) && (
                  <button type="button" className={`exo-cta exo-update-action h-9 px-3.5 text-[12px]${updateBusy ? ' is-active' : ''}`} disabled={updateBusy} onClick={onInstallUpdate}>
                    {updateBusy && <span className="exo-action-progress" style={{ '--progress': Math.max(0, Math.min(100, updatePercent)) / 100 } as CSSProperties} aria-hidden="true" />}
                    <span className="exo-action-state">
                      <span className="exo-action-content exo-action-idle" aria-hidden={updateBusy}><strong>Update</strong></span>
                      <span className="exo-action-content exo-action-active" aria-hidden={!updateBusy}><Loader2 className="h-3.5 w-3.5 animate-spin" /><strong>{`${Math.round(updatePercent)}%`}</strong></span>
                    </span>
                    {updateBusy && <span className="sr-only" role="progressbar" aria-label="App update progress" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(Math.max(0, Math.min(100, updatePercent)))} />}
                  </button>
                )}
              </div>
            </div>
          </section>

          <section className="py-4" aria-labelledby="portable-heading">
            <div className="flex items-center justify-between gap-3">
              <div>
                <h3 id="portable-heading" className="text-[14px] font-medium text-fg">Portable game</h3>
                <p className="mt-0.5 text-[11px] text-faint">Add a local game folder.</p>
              </div>
              <button type="button" className="exo-ghost-btn min-h-9 shrink-0" onClick={() => void (async () => {
                const pick = await host.pickFolder('Choose game folder')
                if (!pick.ok || !pick.path) return
                const result = await host.install('local:add', pick.path, undefined)
                setLocalMsg(result.message ?? (result.ok ? 'Portable game added.' : 'Could not add portable game'))
              })()}>
                Add folder…
              </button>
            </div>
          </section>

          <section className="pt-4" aria-labelledby="help-heading">
            <h3 id="help-heading" className="text-[14px] font-medium text-fg">Help &amp; support</h3>
            <div className="mt-2 grid grid-cols-2 gap-2">
              <button type="button" className="exo-settings-link" onClick={() => void host.openUrl(ISSUES)}><FileText size={14} />Report issue</button>
              <button type="button" className="exo-settings-link" onClick={() => void host.openUrl(BUY_ME_A_COFFEE)}><HeartHandshake size={14} />Buy me a coffee</button>
              <button type="button" className="exo-settings-link" onClick={() => void host.openUrl(PRIVACY)}><FileText size={14} />Privacy</button>
              <button type="button" className="exo-settings-link" onClick={() => void host.openUrl(RELEASES)}><ExternalLink size={14} />Releases</button>
            </div>
          </section>
        </div>

        <section className="min-w-0" aria-labelledby="backends-heading">
          <div className="flex items-baseline justify-between gap-3">
            <h3 id="backends-heading" className="text-[14px] font-medium text-fg">Store apps</h3>
            <span className="text-[10px] text-faint">Use your installed client</span>
          </div>
          <ul className="mt-2 grid grid-cols-2 gap-x-4 divide-y-0">
            {storeRows.map((store) => {
              const clientInstalled = store.clientPresent ?? store.agentPresent
              // The matrix is the source of truth for which rows can surface
              // an official client. New passive clients inherit this without
              // another UI allowlist, while an absent backend stays inert.
              const canOpen = clientInstalled && !!store.agentPresent
              const isOpening = openingStore === store.store
              return (
                <li key={store.store} className="flex min-w-0 items-center justify-between gap-2 border-t border-line-soft py-2.5">
                  <div className="min-w-0">
                    <div className="truncate text-[13px] text-fg">{store.displayName}</div>
                    <div className={`mt-0.5 text-[10px] ${clientInstalled ? 'text-good' : 'text-faint'}`}>{clientInstalled ? 'Installed' : 'Not installed'}</div>
                  </div>
                  {canOpen && <button type="button" className="exo-ghost-btn min-h-8 shrink-0 px-2.5 text-[11px]" disabled={isOpening} onClick={() => void openStore(store)}>{isOpening ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : 'Open'}</button>}
                </li>
              )
            })}
          </ul>
        </section>

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
