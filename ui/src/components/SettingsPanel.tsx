import { formatTransferPercent, transferPercent } from '../lib/utils'
import { shouldReduceMotion } from '../lib/motionPreference'
import { useGlide } from '../lib/glide'
import { memo, useCallback, useEffect, useRef, useState } from 'react'
import {
  host,
  onHostEvent,
  type DependencyItem,
  type LauncherSettings,
  type StoreStatus,
} from '../lib/host'
import { AboutSection } from './settings/AboutSection'
import { AppsSection, preloadAppsSection } from './settings/AppsSection'
import { CleanupSection } from './settings/CleanupSection'
import { CaptureSection } from './settings/CaptureSection'
import { GeneralSection } from './settings/GeneralSection'
import { KeysSection } from './settings/KeysSection'
import { LucidSection } from './settings/LucidSection'
import { StoresSection, type StoreAction, type StoreNote } from './settings/StoresSection'

let preparedRuntimes: DependencyItem[] | null = null
let runtimeRequest: Promise<DependencyItem[]> | null = null
function readRuntimes(): Promise<DependencyItem[]> {
  if (runtimeRequest) return runtimeRequest
  const request = host.listDeps().then(result => result.items ?? [])
    .finally(() => { if (runtimeRequest === request) runtimeRequest = null })
  runtimeRequest = request
  return request
}
export async function preloadSettingsRoom() {
  await Promise.allSettled([
    readRuntimes().then(items => { preparedRuntimes = items }),
    preloadAppsSection(),
  ])
}

// The section list, as Discord's settings list its pages.
const SECTIONS = [
  { id: 'general', label: 'General' },
  { id: 'downloads', label: 'Downloads' },
  { id: 'apps', label: 'Music' },
  { id: 'lucid', label: 'Lucid' },
  { id: 'capture', label: 'Capture' },
  { id: 'stores', label: 'Game stores' },
  { id: 'cleanup', label: 'Clean up' },
  { id: 'keys', label: 'Optional keys' },
  { id: 'about', label: 'About' },
] as const
export type SettingsSectionId = (typeof SECTIONS)[number]['id']

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
  /** A section to open at (Library's Store settings). Each new object jumps once. */
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
  const [current, setCurrent] = useState<string>('general')
  /** The section the user picked in the list: marked until they scroll themselves. */
  const pickedRef = useRef<string | null>(null)
  const [storeNotes, setStoreNotes] = useState<Record<string, StoreNote | undefined>>({})
  /** A store row's result: a plain line, or (error) a failure under the row; an empty text clears it. */
  const storeStatus = useCallback((store: string, text: string, error = false) => {
    setStoreNotes(previous => ({ ...previous, [store]: text ? { text, error } : undefined }))
  }, [])
  const [storeBusy, setStoreBusy] = useState<Record<string, StoreAction | undefined>>({})
  const storeBusyRef = useRef<Record<string, StoreAction | undefined>>({})
  const [storeCheckBusy, setStoreCheckBusy] = useState(false)
  const [storeCheckCopy, setStoreCheckCopy] = useState<string | null>(null)
  const [deps, setDeps] = useState<DependencyItem[] | null>(() => preparedRuntimes)
  const depsRequest = useRef<Promise<DependencyItem[]> | null>(null)
  /** The runtime download's progress (a note) and what went wrong (an error), kept apart. */
  const [depsNote, setDepsNote] = useState<string | null>(null)
  const [depsError, setDepsError] = useState<string | null>(null)
  const [depBusy, setDepBusy] = useState<string | null>(null)
  const depBusyRef = useRef<string | null>(null)

  useEffect(() => onHostEvent('deps.progress', (data) => {
    const progress = data as { id?: string; bytes?: number; total?: number }
    if (progress.id !== depBusyRef.current || typeof progress.bytes !== 'number') return
    const percent = transferPercent({ bytesDownloaded: progress.bytes, bytesToDownload: progress.total })
    setDepsNote(percent != null
      ? percent === 100 ? 'Installing… Follow the installer prompts.' : `Downloading… ${formatTransferPercent(percent)}%`
      : `Downloading… ${(progress.bytes / 1048576).toFixed(1)} MB`)
  }), [])

  useEffect(() => {
    if (!active || deps !== null) return
    let live = true
    depsRequest.current ??= readRuntimes()
    void depsRequest.current
      .then((items) => { if (live) setDeps(items) })
      .catch((error: unknown) => {
        if (!live) return
        setDeps([])
        setDepsError(error instanceof Error ? error.message : 'Could not check runtimes.')
      })
    return () => { live = false }
  }, [active, deps])

  // Highlight the section being read: the last one whose heading has passed a
  // reading line. The line slides from near the top of the view to near its
  // bottom as the page scrolls, so short sections near the end (which can never
  // reach the top) still take their turn, in order, instead of being skipped.
  // A section picked in the list stays marked until the user scrolls by
  // themselves. At most once a frame.
  useEffect(() => {
    const root = scrollRef.current
    if (!root || !active) return
    let frame = 0
    const pick = () => {
      frame = 0
      if (pickedRef.current) return
      const box = root.getBoundingClientRect()
      const room = root.scrollHeight - root.clientHeight
      const progress = room > 0 ? Math.min(1, root.scrollTop / room) : 0
      const line = box.top + 48 + (box.height - 120) * progress
      let id: (typeof SECTIONS)[number]['id'] | undefined = SECTIONS[0]?.id
      for (const section of SECTIONS) {
        const element = root.querySelector(`#set-${section.id}`)
        if (element && element.getBoundingClientRect().top <= line) id = section.id
      }
      if (id) setCurrent(id)
    }
    const onScroll = () => { if (!frame) frame = window.requestAnimationFrame(pick) }
    const onUser = () => { pickedRef.current = null }
    pick()
    root.addEventListener('scroll', onScroll, { passive: true })
    for (const type of ['wheel', 'pointerdown', 'keydown', 'touchstart'] as const) root.addEventListener(type, onUser, { passive: true })
    return () => {
      root.removeEventListener('scroll', onScroll)
      for (const type of ['wheel', 'pointerdown', 'keydown', 'touchstart'] as const) root.removeEventListener(type, onUser)
      if (frame) window.cancelAnimationFrame(frame)
    }
  }, [active])

  // Opened for one section (Library's Store settings): land on it once, already there.
  const openedFor = useRef<typeof section>(null)
  useEffect(() => {
    if (!active || !section || openedFor.current === section) return
    openedFor.current = section
    pickedRef.current = section.id
    scrollRef.current?.querySelector(`#set-${section.id}`)?.scrollIntoView({ block: 'start' })
    scrollRef.current?.querySelector<HTMLElement>(`#set-${section.id}-title`)?.focus({ preventScroll: true })
    setCurrent(section.id)
  }, [active, section])

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
  const save = useCallback(async (patch: Partial<LauncherSettings>) => await saveWithReason(patch) === null, [saveWithReason])

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

  const getDependency = useCallback(async (item: DependencyItem) => {
    if (depBusyRef.current) {
      if (depBusyRef.current === item.id) {
        try { const result = await host.cancelDepInstall(item.id); setDepsNote(result.message ?? 'Canceling…') }
        catch (error) { setDepsError(error instanceof Error ? error.message : 'Could not cancel the download.') }
      }
      return
    }
    depBusyRef.current = item.id
    setDepBusy(item.id)
    setDepsNote(null)
    setDepsError(null)
    try {
      const result = await host.offerDepInstall(item.id)
      if (result.ok) {
        depsRequest.current = null
        preparedRuntimes = null
        setDeps(null)
        setDepsNote(result.restartRequired ? 'Restart Windows to finish installing.' : null)
      } else {
        setDepsNote(null)
        setDepsError(result.message ?? `Could not install ${item.name}.`)
      }
    } catch (error) {
      setDepsNote(null)
      setDepsError(error instanceof Error ? error.message : `Could not install ${item.name}.`)
    } finally {
      depBusyRef.current = null
      setDepBusy(null)
    }
  }, [])

  const recheckDependencies = useCallback(() => {
    depsRequest.current = null
    preparedRuntimes = null
    setDeps(null)
    setDepsNote(null)
    setDepsError(null)
  }, [])

  function jump(id: string) {
    pickedRef.current = id
    scrollRef.current?.querySelector(`#set-${id}`)?.scrollIntoView({ block: 'start', behavior: shouldReduceMotion() ? 'auto' : 'smooth' })
    // Keyboard users land in the section they picked, not back at the top of the list.
    scrollRef.current?.querySelector<HTMLElement>(`#set-${id}-title`)?.focus({ preventScroll: true })
    setCurrent(id)
  }

  return (
    <main className="exo-settings" aria-label="Settings">
      <span className="exo-glass is-panel exo-sheet-glass" aria-hidden="true" />
      <nav className="exo-settings-nav" aria-label="Settings sections">
        <h1>Settings</h1>
        <ul ref={navGlide} className="exo-glide">
          {SECTIONS.map(section => (
            <li key={section.id}>
              <button
                type="button"
                aria-current={current === section.id ? 'true' : undefined}
                className={current === section.id ? 'is-current' : undefined}
                onClick={() => jump(section.id)}
              >
                {section.label}
              </button>
            </li>
          ))}
        </ul>
      </nav>
      <div className="exo-settings-scroll" ref={scrollRef}>
        <div className="exo-settings-layout">
          <div className="exo-settings-column">
            <GeneralSection settings={settings} onSave={saveWithReason} onLibraryChanged={refreshLibrary} />
            <AppsSection />
            <LucidSection />
            <CaptureSection />
            <StoresSection
              stores={stores}
              storeBusy={storeBusy}
              storeCheckBusy={storeCheckBusy}
              storeCheckCopy={storeCheckCopy}
              storeNotes={storeNotes}
              onCheckStores={checkStores}
              onConnect={connectStore}
              onOpen={openStore}
              settings={settings}
              onSave={save}
              active={active}
            />
            <CleanupSection />
            <KeysSection settings={settings} onSettings={onSettings} />
            <AboutSection
              version={settings?.appVersion ?? null}
              deps={deps}
              depsNote={depsNote}
              depsError={depsError}
              depBusy={depBusy}
              updateBusy={updateBusy}
              updateError={updateError}
              updatePercent={updatePercent}
              updateStatus={updateStatus}
              updateAvailable={updateAvailable}
              onRecheck={recheckDependencies}
              onGetDependency={getDependency}
              onCheckUpdate={onCheckUpdate}
              onInstallUpdate={onInstallUpdate}
            />
          </div>
        </div>
      </div>
    </main>
  )
})
