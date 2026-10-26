import { useCallback, useEffect, useState } from 'react'
import { host, type GpuProfile, type HdrState, type LauncherSettings, type PcHealth } from '../../lib/host'
import { useHdrState } from '../../lib/hdr'
import { report } from '../../lib/report'
import { trimStop } from '../../lib/utils'
import { Row, State } from '../settings/parts'

type Step = { key: 'power' | 'profile' | 'hdr'; what: string; run: () => Promise<string | null> }

/** What is left to set, from the three places Exo already reads. Each step is something the page below it offers on its own. */
function pendingSteps(
  profile: GpuProfile | null, health: PcHealth | null, screen: HdrState | null, settings: LauncherSettings | null,
  onSave: (patch: Partial<LauncherSettings>) => Promise<boolean>,
): Step[] {
  const steps: Step[] = []
  const power = health?.items.find(item => item.key === 'power')
  if (power && !power.applied) {
    steps.push({ key: 'power', what: `Exo’s power plan for ${power.cpu ?? 'this PC'}`, run: async () => {
      const next = await host.systemHealth('apply')
      return next.ok ? null : trimStop(next.message) || 'Not changed'
    } })
  }
  if (profile?.ok && profile.vendor && (profile.status === 'ready' || profile.status === 'changed')) {
    const maker = profile.vendor === 'nvidia' ? 'NVIDIA' : profile.vendor === 'amd' ? 'Radeon' : 'Intel'
    steps.push({ key: 'profile', what: `Exo’s ${maker} driver profile`, run: async () => {
      const next = await host.gpuProfile('apply', profile.gsync)
      return next.ok ? null : trimStop(next.message) || 'Not changed'
    } })
  }
  // Only a screen that shows HDR well: on a dim one HDR usually looks worse, so it stays the user's choice.
  if (screen?.supported && screen.verdict !== 'weak' && settings && !settings.hdrForGames) {
    steps.push({ key: 'hdr', what: 'HDR while an HDR game runs', run: async () => await onSave({ hdrForGames: true }) ? null : 'Could not save' })
  }
  return steps
}

/**
 * System > This PC, first row: everything Exo can set for this PC in one press, named before it runs (the power plan
 * tuned for the processor, the driver profile for the card, HDR for HDR games). Nothing here is hidden behind it: each
 * lives in its own section of this room with its own Undo, and the rest (display modes, BIOS, Windows settings) stay
 * where they ask first.
 */
export function BestSetup({ active, settings, onSave }: {
  active: boolean
  settings: LauncherSettings | null
  onSave: (patch: Partial<LauncherSettings>) => Promise<boolean>
}) {
  const [profile, setProfile] = useState<GpuProfile | null>(null)
  const [health, setHealth] = useState<PcHealth | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [done, setDone] = useState(false)
  const screen = useHdrState(active)

  const load = useCallback(async () => {
    try {
      const [p, h] = await Promise.all([host.gpuProfile(), host.systemHealth()])
      setProfile(p)
      setHealth(h)
    } catch (e) { report(e, 'BestSetup.state') }
  }, [])

  useEffect(() => {
    void load()
    if (!active) return
    const onFocus = () => { void load() }
    window.addEventListener('focus', onFocus)
    return () => window.removeEventListener('focus', onFocus)
  }, [load, active])

  const steps = pendingSteps(profile, health, screen, settings, onSave)

  const run = async () => {
    if (busy) return
    setBusy(true)
    setError(null)
    setDone(false)
    const failed: string[] = []
    for (const step of steps) {
      try {
        const problem = await step.run()
        if (problem) failed.push(`${step.what}: ${problem}`)
      } catch (e) {
        report(e, `BestSetup.${step.key}`)
        failed.push(`${step.what}: not changed`)
      }
    }
    await load()
    setError(failed.length ? failed.join('. ') : null)
    setDone(failed.length < steps.length)
    setBusy(false)
  }

  if (!profile || !health) return null
  if (steps.length === 0) {
    if (!done) return null
    return <Row label="Best settings for this PC" detail="Set. Each change is in its own section below, with Undo."><State on>Done</State></Row>
  }
  return (
    <Row label="Best settings for this PC" detail={`${steps.map(step => step.what).join(', ')}. Each stays in its own section below, with Undo.`} error={error}>
      {busy
        ? <State>Working…</State>
        : <button type="button" className="exo-set-btn is-primary" onClick={() => void run()}>Use best settings</button>}
    </Row>
  )
}
