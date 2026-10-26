import { useEffect, useRef, useState, type ReactNode } from 'react'
import {
  host,
  type DependencyItem,
  type GpuProfile,
  type LauncherSettings,
  type LucidState,
  type ReplacedApp,
  type StoreStatus,
} from '../lib/host'
import { useCapture } from '../lib/capture'
import { Button } from './ui/Button'
import { Progress } from './ui/Progress'
import { Spinner } from './ui/Spinner'
import { CaptureStep } from './onboarding/CaptureStep'
import { CleanupStep } from './onboarding/CleanupStep'
import { report } from '../lib/report'
import { settingsStoreRows } from '../lib/stores'
import { AppsStep } from './onboarding/AppsStep'
import { STEPS, type Feedback, type MissingRuntime, type StepId, type StoreAction, type StoreCheckState } from './onboarding/model'
import { StoresStep } from './onboarding/StoresStep'
import { DiscordStep } from './onboarding/DiscordStep'
import { PcStep } from './onboarding/PcStep'
import { WindowChrome } from './WindowChrome'

let onboardingStoreCheck: Promise<StoreStatus[]> | null = null

function checkOnboardingStoresOnce(): Promise<StoreStatus[]> {
  onboardingStoreCheck ??= host.storesCheck().then((result) => {
    if (result.state === 'failed') throw new Error('Local store check failed.')
    return host.storesMatrix()
  }).catch((error: unknown) => {
    onboardingStoreCheck = null
    throw error
  })
  return onboardingStoreCheck
}

/** Runtimes the host reports as absent and is able to offer for install. */
function missingInstallableRuntimes(result: { items?: DependencyItem[] | null }): MissingRuntime[] {
  return (result.items ?? [])
    .filter((item) => item.status.toLowerCase() !== 'present' && item.canOfferInstall)
    .map((item) => ({ id: item.id, name: item.name }))
}

const LEDE: Record<StepId, string> = {
  welcome: 'Exo looks at this PC and sets up the rest. Everything here can be changed later.',
  capture: 'Keep the last moments of a game, record, or snip the screen. Change the keys if they clash with a game.',
  cleanup: 'Exo does the job of these. Remove the ones you don’t need, with every file and setting they leave behind.',
  stores: "Exo found these on this PC. Sign in where offered so games you own but haven't installed, and games friends give you, show up too.",
  apps: 'The service Exo plays your music from. The song stays in the top bar in every room.',
  discord: 'Exo has its own Discord: your servers, DMs and calls, without the Discord app. Sign in now or later in the Discord room.',
  pc: 'Exo sets up the rest by itself. One thing changes your graphics driver, so it asks first.',
}

/** Steps with nothing to offer on this PC are left out. */
function visibleSteps(offers: Record<'pc' | 'capture' | 'cleanup', boolean>) {
  return STEPS.filter(item => !(item.id in offers) || offers[item.id as keyof typeof offers])
}

function ledeFor(id: StepId, pcNeeded: boolean, cleared: number): string {
  if (id === 'pc' && !pcNeeded) return 'Exo sets up the rest by itself.'
  if (id === 'cleanup' && cleared > 0) return `${LEDE.cleanup} Exo already cleared ${cleared} leftovers of apps that were removed.`
  return LEDE[id]
}

export interface OnboardingPanelProps {
  stores: StoreStatus[]
  onSettings: (next: LauncherSettings) => void
  onStores: (next: StoreStatus[]) => void
  onComplete: (refreshLibrary: boolean) => Promise<void>
  /** Return false when the picker was canceled. Void and true mean the library changed. */
  onAddFolder?: () => boolean | void | Promise<boolean | void>
}

export function OnboardingPanel({
  stores,
  onSettings,
  onStores,
  onComplete,
  onAddFolder,
}: OnboardingPanelProps) {
  const [step, setStep] = useState<StepId>('welcome')
  /** 1 going forward, -1 back: which side the next step slides in from. */
  const [direction, setDirection] = useState(1)
  const [feedback, setFeedback] = useState<Feedback | null>(null)
  /** A failed sign in or open, under its store's row. */
  const [storeErrors, setStoreErrors] = useState<Record<string, string | undefined>>({})
  const [refreshLibrary, setRefreshLibrary] = useState(false)
  const [storeCheckState, setStoreCheckState] = useState<StoreCheckState>('checking')
  const [storeBusy, setStoreBusy] = useState<Record<string, StoreAction | undefined>>({})
  const [folderBusy, setFolderBusy] = useState(false)
  const [completing, setCompleting] = useState(false)
  const [missingRuntimes, setMissingRuntimes] = useState<MissingRuntime[]>([])
  const [runtimeBusy, setRuntimeBusy] = useState(false)
  const runtimeBusyRef = useRef(false)
  const completingRef = useRef(false)
  const headingRef = useRef<HTMLHeadingElement>(null)
  const focusPending = useRef(false)

  // This PC shows when it has something to offer: Exo's profile for an NVIDIA, Radeon or Intel card (not applied yet), or Lucid.
  const [gpu, setGpu] = useState<GpuProfile | null>(null)
  const [tunePc, setTunePc] = useState(true)
  const [lucid, setLucid] = useState<LucidState | null>(null)
  const pcNeeded = !!gpu?.vendor && ['nvidia', 'amd', 'intel'].includes(gpu.vendor) && (gpu.status === 'ready' || gpu.status === 'changed')
  const capture = useCapture()
  // The apps Exo replaces; opening setup first clears what removed ones left in this user's folders.
  const [replaced, setReplaced] = useState<{ cleared: number; apps: ReplacedApp[] } | null>(null)
  const cleanupOffered = !!replaced && replaced.apps.some(app => app.installed || app.leftovers > 0)
  // Once the step has been shown it stays: removing the last app would otherwise drop the step under the user and start over at Welcome.
  const [cleanupSeen, setCleanupSeen] = useState(false)
  if (cleanupOffered && !cleanupSeen) setCleanupSeen(true)
  const cleanupNeeded = cleanupOffered || cleanupSeen
  const steps = visibleSteps({ pc: pcNeeded || !!lucid, capture: capture.state?.present !== false, cleanup: cleanupNeeded })
  const index = Math.max(0, steps.findIndex(item => item.id === step))
  const current = steps[index]
  const last = index === steps.length - 1
  const storeRows = settingsStoreRows(stores)

  useEffect(() => {
    host.replacedScan(true).then(({ cleared, apps }) => setReplaced({ cleared, apps }), (error: unknown) => report(error, 'OnboardingPanel.replacedScan'))
  }, [])

  useEffect(() => {
    host.gpuProfile().then(setGpu, (error: unknown) => report(error, 'OnboardingPanel.gpuProfile'))
    host.lucid().then(setLucid, (error: unknown) => report(error, 'OnboardingPanel.lucid'))
  }, [])

  useEffect(() => {
    void host.listDeps().then((result) => {
      setMissingRuntimes(missingInstallableRuntimes(result))
    }).catch((e: unknown) => report(e, 'OnboardingPanel.listDeps'))
  }, [])

  useEffect(() => {
    let active = true
    void checkOnboardingStoresOnce().then(
      (next) => {
        if (!active) return
        onStores(next)
        setStoreCheckState('ready')
      },
      () => {
        if (active) setStoreCheckState('error')
      },
    )
    return () => {
      active = false
    }
  }, [onStores])

  useEffect(() => {
    let active = true
    void host.getSettings().then(
      (next) => { if (active) onSettings(next) },
      (error: unknown) => report(error, 'OnboardingPanel.getSettings'),
    )
    return () => {
      active = false
    }
  }, [onSettings])

  useEffect(() => {
    if (!focusPending.current) return
    focusPending.current = false
    const frame = window.requestAnimationFrame(() => headingRef.current?.focus())
    return () => window.cancelAnimationFrame(frame)
  }, [step])

  // A confirmation passes; an error stays until the next action.
  useEffect(() => {
    if (!feedback || feedback.error) return
    const timer = window.setTimeout(() => setFeedback(null), 3600)
    return () => window.clearTimeout(timer)
  }, [feedback])

  function go(next: StepId) {
    if (next === step) return
    setFeedback(null)
    focusPending.current = true
    setDirection(steps.findIndex(item => item.id === next) > index ? 1 : -1)
    setStep(next)
  }

  async function runStoreAction(store: StoreStatus, action: StoreAction) {
    if (storeBusy[store.store]) return
    setStoreBusy((busy) => ({ ...busy, [store.store]: action }))
    setFeedback(null)
    const fail = (message: string) => setStoreErrors(errors => ({ ...errors, [store.store]: message }))
    setStoreErrors(errors => ({ ...errors, [store.store]: undefined }))
    try {
      const result = action === 'connect'
        ? await host.storesAuth(store.store)
        : await host.showStore(store.store)
      if (!result.ok) {
        fail(result.message ?? (action === 'connect'
          ? `Could not sign in to ${store.displayName}.`
          : `Could not open ${store.displayName}.`))
        return
      }
      setRefreshLibrary(true)
      try {
        onStores(await host.storesMatrix())
      } catch (e) {
        report(e, 'OnboardingPanel.storesMatrix')
      }
      if (result.message) setFeedback({ message: result.message })
    } catch (cause) {
      fail(cause instanceof Error ? cause.message : `Could not reach ${store.displayName}.`)
    } finally {
      setStoreBusy((busy) => {
        const next = { ...busy }
        delete next[store.store]
        return next
      })
    }
  }

  async function addFolder() {
    if (!onAddFolder || folderBusy) return
    setFolderBusy(true)
    setFeedback(null)
    try {
      const result = await onAddFolder()
      if (result !== false) {
        setRefreshLibrary(true)
        setFeedback({ message: 'Game folder added.' })
      }
    } catch (cause) {
      setFeedback({ message: cause instanceof Error ? cause.message : 'The folder could not be added.', error: true })
    } finally {
      setFolderBusy(false)
    }
  }

  async function installMissingRuntimes() {
    if (runtimeBusyRef.current) return
    runtimeBusyRef.current = true
    setRuntimeBusy(true)
    setFeedback(null)
    try {
      // All of them in one install: Windows asks once.
      const result = await host.runtimesInstall('missing', missingRuntimes.map((item) => item.id))
      const failures = result.results.filter(item => item.outcome === 'failed').map(item => `${item.name}: ${item.message}`)
      if (!result.ok && failures.length === 0 && result.message && !result.canceled) failures.push(result.message)
      const restart = result.results.some(item => item.outcome === 'restart')
      let stillMissing = missingRuntimes.length
      try {
        const next = missingInstallableRuntimes(await host.listDeps())
        stillMissing = next.length
        setMissingRuntimes(next)
      } catch (error) {
        report(error, 'OnboardingPanel.listDeps')
      }
      setFeedback(failures.length
        ? { message: failures.join(' '), error: true }
        : stillMissing > 0
          ? { message: 'Windows doesn’t list them all yet. Restart Windows, then check again.' }
          : { message: restart ? 'Runtimes installed. Restart Windows to finish.' : 'Runtimes installed.' })
    } finally {
      runtimeBusyRef.current = false
      setRuntimeBusy(false)
    }
  }

  async function finish() {
    if (completingRef.current) return
    completingRef.current = true
    setCompleting(true)
    setFeedback(null)
    try {
      if (pcNeeded && tunePc && gpu) {
        const applied = await host.gpuProfile('apply', gpu.vendor === 'nvidia' ? gpu.gsync : undefined).catch((error: unknown) => { report(error, 'OnboardingPanel.applyProfile'); return null })
        if (applied && !applied.ok) report(new Error(applied.message ?? 'GPU profile not applied'), 'OnboardingPanel.applyProfile')
      }
      await onComplete(refreshLibrary)
    } catch (cause) {
      completingRef.current = false
      setCompleting(false)
      setFeedback({ message: cause instanceof Error ? cause.message : 'Setup could not be completed.', error: true })
    }
  }

  // One renderer per step, looked up rather than chained.
  const body: Record<StepId, () => ReactNode> = {
    welcome: () => null,
    stores: () => (
      <StoresStep
        storeCheckState={storeCheckState}
        storeRows={storeRows}
        storeBusy={storeBusy}
        storeErrors={storeErrors}
        onStoreAction={(store, action) => void runStoreAction(store, action)}
        missingRuntimes={missingRuntimes}
        runtimeBusy={runtimeBusy}
        onInstallRuntimes={() => void installMissingRuntimes()}
        canAddFolder={!!onAddFolder}
        folderBusy={folderBusy}
        onAddFolder={() => void addFolder()}
      />
    ),
    apps: () => <AppsStep onFeedback={setFeedback} />,
    discord: () => <DiscordStep />,
    capture: () => <CaptureStep />,
    pc: () => <PcStep profile={pcNeeded ? gpu : null} tune={tunePc} onTune={setTunePc} lucid={lucid} onLucid={setLucid} />,
    cleanup: () => replaced ? <CleanupStep apps={replaced.apps} onApps={apps => setReplaced(value => ({ cleared: value?.cleared ?? 0, apps }))} /> : null,
  }
  const lede = ledeFor(current.id, pcNeeded, replaced?.cleared ?? 0)
  return (
    <div className="exo-app exo-ob">
      <header className="exo-titlebar exo-ob-titlebar">
        <div className="exo-titlebar-actions">
          <WindowChrome />
        </div>
      </header>

      <main className="exo-ob-stage">
        <section className={`exo-ob-step${current.id === 'welcome' ? ' is-welcome' : ''}`} key={current.id} data-direction={direction < 0 ? 'back' : 'forward'} aria-labelledby="exo-ob-title">
          {current.id === 'welcome' ? <img className="exo-ob-mark" src="logo.svg" alt="" draggable={false} /> : (
            // The steps done out of all of them (Welcome is not one).
            <Progress value={(index - 1) / (steps.length - 1)} label={`Step ${index} of ${steps.length - 1}`} className="exo-ob-progress" />
          )}
          <h1 id="exo-ob-title" ref={headingRef} tabIndex={-1} className="exo-ob-title">{current.title}</h1>
          <p className="exo-ob-lede">{lede}</p>

          {current.id === 'welcome' ? null : (
            <div className="exo-ob-body">
              {body[current.id]()}
            </div>
          )}
        </section>

        <Footer
          feedback={feedback}
          index={index}
          last={last}
          completing={completing}
          onBack={() => go(steps[index - 1].id)}
          onNext={() => (last ? void finish() : go(steps[index + 1].id))}
        />
      </main>
    </div>
  )
}

/** The note on the left; Back and the one white button on the right. */
function Footer({ feedback, index, last, completing, onBack, onNext }: {
  feedback: Feedback | null
  index: number
  last: boolean
  completing: boolean
  onBack: () => void
  onNext: () => void
}) {
  const label = last ? 'Start using Exo' : index === 0 ? 'Get started' : 'Continue'
  return (
    <footer className={`exo-ob-foot${index === 0 ? ' is-welcome' : ''}`}>
      <p className={`exo-ob-note${feedback?.error ? ' is-error' : ''}`} role={feedback?.error ? 'alert' : 'status'} aria-live="polite">
        {feedback?.message ?? ' '}
      </p>
      <div className="exo-ob-actions">
        {index > 0 ? <Button variant="ghost" isDisabled={completing} onPress={onBack}>Back</Button> : null}
        <Button variant="primary" className="exo-ob-cta" isDisabled={completing} onPress={onNext}>
          {completing ? <Spinner label="Opening Exo" /> : null}
          {label}
        </Button>
      </div>
    </footer>
  )
}
