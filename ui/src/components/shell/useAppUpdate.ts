import { useCallback, useEffect, useRef, useState } from 'react'
import { visibleInstallPercent } from '../../lib/utils'
import { host, onHostEvent } from '../../lib/host'
import { report } from '../../lib/report'
import { toRelease, type WhatsNewRelease } from '../../lib/whatsNew'

const DISMISSED_KEY = 'exo.launcher.dismissed-update'
/** The startup check may hold the boot mark this long, never more. */
const BOOT_HOLD_MS = 800

export type AppUpdate = {
  banner: string | null
  busy: boolean
  percent: number | null
  status: string
  /** True while an update is offered and not being installed. */
  available: boolean
  /** Why the last install failed, shown under the update row; null otherwise. */
  error: string | null
  /** The first check is still running inside its short startup hold. */
  checking: boolean
  latest: string | null
  /** The offered version's What's New (its release's whats-new.json), or null when it has none. */
  upcoming: WhatsNewRelease | null
  /** Hide the banner for this version until the next launch. */
  dismiss: () => void
  install: () => Promise<void>
  /** Manual check from Settings. Never rejects; failure is a message. */
  check: () => Promise<UpdateCheck>
}

type UpdateCheck = { ok: boolean; updateAvailable: boolean; latest?: string | null; message: string }

/**
 * App self-update: a capped check on the boot mark, then offers and installs
 * live in the update toast. Once the shell has rendered it never returns to
 * the splash, so hosted rooms keep their layout.
 */
export function holdsBoot(update: AppUpdate): boolean {
  return update.checking
}

export function useAppUpdate(): AppUpdate {
  const [banner, setBanner] = useState<string | null>(null)
  const [latest, setLatest] = useState<string | null>(null)
  const [upcoming, setUpcoming] = useState<WhatsNewRelease | null>(null)
  const [phase, setPhase] = useState<'checking' | 'idle' | 'available' | 'installing' | 'restarting' | 'failed'>('checking')
  const installing = useRef(false)
  const checkGeneration = useRef(Symbol())
  const busy = phase === 'installing' || phase === 'restarting'
  const [percent, setPercent] = useState<number | null>(null)
  const [status, setStatus] = useState("Preparing…")
  const [bootHold, setBootHold] = useState(true)

  useEffect(() => {
    const generation = Symbol()
    checkGeneration.current = generation
    const holdTimer = window.setTimeout(() => setBootHold(false), BOOT_HOLD_MS)
    void host.checkUpdate().then((r) => {
      if (generation !== checkGeneration.current || installing.current) return
      if (!r.ok || !r.updateAvailable) {
        setPhase(current => current === 'checking' ? 'idle' : current)
        return
      }
      const version = r.latest ?? ''
      if (version && sessionStorage.getItem(DISMISSED_KEY) === version) {
        setPhase('idle')
        return
      }
      setBanner(r.message || (version ? `Version ${version} is available.` : 'An update is available.'))
      setLatest(version || null)
      setUpcoming(r.upcoming ? toRelease(r.upcoming) : null)
      setPhase('available')
    }).catch((e: unknown) => {
      report(e, 'useAppUpdate.startupCheck', { level: 'warn' })
      if (generation === checkGeneration.current) setPhase(current => current === 'checking' ? 'idle' : current)
    }).finally(() => setBootHold(false))
    const off = onHostEvent('app.updateProgress', (data) => {
      if (!installing.current) return
      const d = data as { percent?: number | null; status?: string }
      setPercent(visibleInstallPercent(d?.percent))
      if (typeof d?.status === 'string') setStatus(d.status)
    })
    // A release that came out while Exo was open (the host's version watch): offered as the startup check offers one.
    const offFound = onHostEvent('app.updateFound', () => {
      if (installing.current) return
      void host.checkUpdate().then((r) => {
        if (installing.current || !r.ok || !r.updateAvailable) return
        const version = r.latest ?? ''
        if (version && sessionStorage.getItem(DISMISSED_KEY) === version) return
        setBanner(r.message || (version ? `Version ${version} is available.` : 'An update is available.'))
        setLatest(version || null)
        setUpcoming(r.upcoming ? toRelease(r.upcoming) : null)
        setPhase(current => current === 'installing' || current === 'restarting' ? current : 'available')
      }).catch((e: unknown) => report(e, 'useAppUpdate.found', { level: 'warn' }))
    })
    return () => {
      checkGeneration.current = Symbol()
      window.clearTimeout(holdTimer)
      off()
      offFound()
    }
  }, [])

  const dismiss = useCallback(() => {
    try { if (latest) sessionStorage.setItem(DISMISSED_KEY, latest) } catch { /* Dismiss still works when browser storage is unavailable. */ }
    setBanner(null)
    setPhase('idle')
  }, [latest])

  const install = useCallback(async () => {
    if (installing.current || busy || (!latest && phase !== 'available' && phase !== 'failed')) return
    installing.current = true
    checkGeneration.current = Symbol()
    setPhase('installing')
    setPercent(null)
    setStatus("Preparing…")
    try {
      const r = await host.installUpdate()
      if (r.ok && (r.shouldExit || r.installed)) {
        setPhase('restarting')
        setPercent(null)
        setStatus('Restarting…')
        setBanner(r.message || 'Restarting…')
      }
      else if (r.alreadyLatest) {
        setBanner(null)
        setLatest(null)
        setPhase('idle')
      } else {
        setPhase('failed')
        setBanner(r.message || 'The update could not be installed.')
      }
    } catch (e) {
      setBanner(e instanceof Error ? e.message : 'The update could not be installed.')
      setPhase('failed')
    } finally {
      installing.current = false
    }
  }, [busy, latest, phase])

  const check = useCallback(async (): Promise<UpdateCheck> => {
    if (installing.current || busy) return { ok: false, updateAvailable: false, message: 'An update is already being installed.' }
    const generation = Symbol()
    checkGeneration.current = generation
    try {
      const r = await host.checkUpdate(true)
      if (generation !== checkGeneration.current) return { ok: false, updateAvailable: false, message: 'Another update check started.' }
      if (!r.ok || (!r.updateAvailable && !r.alreadyLatest))
        return { ok: false, updateAvailable: false, message: r.message || 'Could not check for updates. Try again.' }
      if (r.updateAvailable) {
        setBanner(r.message || `Version ${r.latest} is available.`)
        setLatest(r.latest ?? null)
        setUpcoming(r.upcoming ? toRelease(r.upcoming) : null)
        setPhase('available')
        return { ok: true, updateAvailable: true, latest: r.latest, message: r.message || 'Update available.' }
      }
      setBanner(null)
      setLatest(null)
      setPhase('idle')
      return { ok: true, updateAvailable: false, message: r.message || "You're up to date." }
    } catch (e) {
      return { ok: false, updateAvailable: false, message: e instanceof Error ? e.message : 'Could not check for updates.' }
    }
  }, [busy])

  return {
    banner,
    busy,
    percent,
    status,
    available: phase === 'available' || phase === 'failed',
    error: phase === 'failed' ? banner : null,
    checking: bootHold && phase === 'checking',
    latest,
    upcoming,
    dismiss,
    install,
    check,
  }
}
