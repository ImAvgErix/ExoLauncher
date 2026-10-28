import { useGlide } from '../lib/glide'
import { useScrollSpy } from '../lib/scrollSpy'
import { memo, useCallback, useEffect, useRef, useState } from 'react'
import {
  host,
  type LauncherSettings,
  type StoreStatus,
} from '../lib/host'
import { SETTINGS_SECTIONS, type SettingsSectionId } from '../lib/settingsSections'
import { AboutSection } from './settings/AboutSection'
import { AppsSection, preloadAppsSection } from './settings/AppsSection'
import { GeneralSection } from './settings/GeneralSection'
import { KeysSection } from './settings/KeysSection'
import { Section } from './settings/parts'
import { StoresSection, type StoreAction, type StoreNote } from './settings/StoresSection'

export async function preloadSettingsRoom() {
  await preloadAppsSection()
}

// The section list, as Discord's settings list its pages.
const SECTION_IDS = SETTINGS_SECTIONS.map(each => each.section)

export const SettingsPanel = memo(function SettingsPanel({
  settings,
  stores,
  updateBusy,
  updateError,
  updatePercent,
  updateStatus = 'Preparing…',
  updateAvailable,
  onCheckUpdate,
  onInstallUpdate,
  onSettings,
  onStores,
  active,
  section = null,
}: {
  settings: LauncherSettings | null
  stores: StoreStatus[]
  updateBusy: boolean
  /** Why the last update install failed. */
  updateError: string | null
  updatePercent: number | null
  updateStatus?: string
  updateAvailable: boolean
  onCheckUpdate: () => Promise<{
    ok: boolean
    message?: string
    updateAvailable?: boolean
    latest?: string | null
  }>
  onInstallUpdate: () => Promise<void> | void
  active: boolean
  /** A section to open at (the Library's Sign in to a store). Each new object jumps once. */
  section?: { id: SettingsSectionId } | null
  onSettings: (next: LauncherSettings) => void
  onStores?: (next: StoreStatus[]) => void | Promise<void>
}) {
  const saveQueue = useRef<Promise<unknown>>(Promise.resolve())
  const settingsRef = useRef(settings)
  useEffect(() => {
    settingsRef.current = settings
  }, [settings])
  const scrollRef = useRef<HTMLDivElement>(null)
  const navGlide = useGlide<HTMLUListElement>()
  const [current, jumpTo] = useScrollSpy(scrollRef, SECTION_IDS, 'set-', active)
  const [storeNotes, setStoreNotes] = useState<Record<string, StoreNote | undefined>>({})
  /** A store row's result: a plain line, or (error) a failure under the row; an empty text clears it. */
  const storeStatus = useCallback((store: string, text: string, error = false) => {
    setStoreNotes(previous => ({ ...previous, [store]: text ? { text, error } : undefined }))
  }, [])
  const [storeBusy, setStoreBusy] = useState<Record<string, StoreAction | undefined>>({})
  const storeBusyRef = useRef<Record<string, StoreAction | undefined>>({})
  const [storeCheckBusy, setStoreCheckBusy] = useState(false)
  const [storeCheckCopy, setStoreCheckCopy] = useState<string | null>(null)
  /** Scrolls to a section and marks it (lib/scrollSpy); keyboard users land in it, not back at the top of the list. */
  const jump = useCallback((id: SettingsSectionId, smooth = true) => {
    jumpTo(id, smooth)
    scrollRef.current?.querySelector<HTMLElement>(`#set-${id}-title`)?.focus({ preventScroll: true })
  }, [jumpTo])

  // Opened for one section (the Library's Sign in to a store): land on it once, already there.
  const openedFor = useRef<typeof section>(null)
  useEffect(() => {
    if (!active || !section || openedFor.current === section) return
    openedFor.current = section
    jump(section.id, false)
  }, [active, section, jump])

  /** Saves in order; null when it took, else the host's reason (a folder it refused says why). */
  const saveWithReason = useCallback(async (patch: Partial<LauncherSettings>): Promise<string | null> => {
    if (!settingsRef.current) return 'Settings are still loading.'
    const queued = saveQueue.current.then(async () => {
      try {
        const next = await host.setSettings(patch)
        settingsRef.current = next
        onSettings(next)
        return null
      } catch (error) {
        if (settingsRef.current) onSettings(settingsRef.current)
        return error instanceof Error && error.message ? error.message : 'Could not save. Try again.'
      }
    })
    saveQueue.current = queued
    return queued
  }, [onSettings])

  const refreshLibrary = useCallback(() => {
    void host.storesMatrix().then(next => onStores?.(next)).catch(() => undefined)
  }, [onStores])

  const startStoreAction = useCallback((store: string, action: StoreAction): boolean => {
    if (storeBusyRef.current[store]) return false
    const next = { ...storeBusyRef.current, [store]: action }
    storeBusyRef.current = next
    setStoreBusy(next)
    return true
  }, [])

  const finishStoreAction = useCallback((store: string, action: StoreAction) => {
    if (storeBusyRef.current[store] !== action) return
    const next = { ...storeBusyRef.current }
    delete next[store]
    storeBusyRef.current = next
    setStoreBusy(next)
  }, [])

  const connectStore = useCallback(async (store: StoreStatus) => {
    if (!startStoreAction(store.store, 'connect')) return
    storeStatus(store.store, '')
    try {
      const result = await host.storesAuth(store.store)
      if (result.message || !result.ok)
        storeStatus(store.store, result.message ?? `Could not sign in to ${store.displayName}.`, !result.ok)
      if (result.ok) {
        try {
          await onStores?.(await host.storesMatrix())
        } catch {
          storeStatus(store.store, 'Signed in, but the store list could not be read again.', true)
        }
      }
    } catch (error) {
      storeStatus(store.store, error instanceof Error ? error.message : `Could not sign in to ${store.displayName}.`, true)
    } finally {
      finishStoreAction(store.store, 'connect')
    }
  }, [startStoreAction, finishStoreAction, onStores, storeStatus])

  const checkStores = useCallback(async () => {
    if (storeCheckBusy) return
    setStoreCheckBusy(true)
    setStoreCheckCopy(null)
    try {
      const check = await host.storesCheck()
      if (check.state === 'failed') {
        setStoreCheckCopy('The check failed. Showing the last results.')
        return
      }
      await onStores?.(await host.storesMatrix())
    } catch {
      setStoreCheckCopy('The check failed. Showing the last results.')
    } finally {
      setStoreCheckBusy(false)
    }
  }, [storeCheckBusy, onStores])

  const openStore = useCallback(async (store: StoreStatus) => {
    if (!startStoreAction(store.store, 'open')) return
    storeStatus(store.store, '')
    try {
      const result: { ok: boolean; message?: string } = store.store === 'microsoft-store'
        ? await host.openUrl('ms-windows-store://home')
        : await host.showStore(store.store)
      if (!result.ok) storeStatus(store.store, result.message ?? `Could not open ${store.displayName}.`, true)
    } catch (error) {
      storeStatus(store.store, error instanceof Error ? error.message : `Could not open ${store.displayName}.`, true)
    } finally {
      finishStoreAction(store.store, 'open')
    }
  }, [startStoreAction, finishStoreAction, storeStatus])

  return (
    <main className="exo-settings" aria-label="Settings">
      <span className="exo-glass is-panel exo-sheet-glass" aria-hidden="true" />
      <nav className="exo-settings-nav" aria-label="Settings sections">
        <ul ref={navGlide} className="exo-glide">
          {SETTINGS_SECTIONS.map(({ section, label }) => (
            <li key={section}>
              <button
                type="button"
                aria-current={current === section ? 'true' : undefined}
                className={current === section ? 'is-current' : undefined}
                onClick={() => jump(section)}
              >
                {label}
              </button>
            </li>
          ))}
        </ul>
      </nav>
      <div className="exo-settings-scroll" ref={scrollRef} data-room-scroll>
        <div className="exo-settings-layout">
          <div className="exo-settings-column">
            <GeneralSection settings={settings} onSave={saveWithReason} onLibraryChanged={refreshLibrary} />
            <Section id="accounts" title="Accounts and keys">
              <StoresSection
                stores={stores}
                storeBusy={storeBusy}
                storeCheckBusy={storeCheckBusy}
                storeCheckCopy={storeCheckCopy}
                storeNotes={storeNotes}
                onCheckStores={checkStores}
                onConnect={connectStore}
                onOpen={openStore}
                active={active}
              />
              <AppsSection />
              <KeysSection settings={settings} onSettings={onSettings} />
            </Section>
            <AboutSection
              version={settings?.appVersion ?? null}
              updateBusy={updateBusy}
              updateError={updateError}
              updatePercent={updatePercent}
              updateStatus={updateStatus}
              updateAvailable={updateAvailable}
              onCheckUpdate={onCheckUpdate}
              onInstallUpdate={onInstallUpdate}
            />
          </div>
        </div>
      </div>
    </main>
  )
})
