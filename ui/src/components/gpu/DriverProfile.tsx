import { useCallback, useEffect, useState } from 'react'
import { host, type GpuProfile, type GpuSensors, type IntelLowLatency, type RadeonAntiLag } from '../../lib/host'
import { report } from '../../lib/report'
import { trimStop } from '../../lib/utils'
import { usePageClock } from '../../lib/pageActivity'
import { Choice } from '../settings/Choice'
import { Row, State } from '../settings/parts'
import { Spinner } from '../ui/Spinner'

const ANTI_LAG = [
  { value: 'games', label: 'While you play' },
  { value: 'always', label: 'Always' },
  { value: 'off', label: 'Off' },
] as const

const LOW_LATENCY = [
  { value: 'on', label: 'On' },
  { value: 'boost', label: 'On with boost' },
  { value: 'off', label: 'Off' },
] as const

/** The latency choice beside Use: a Radeon's Anti-Lag or an Intel GPU's Low latency mode. */
export type LatencyChoice = { antiLag?: RadeonAntiLag; lowLatency?: IntelLowLatency }

/**
 * PC › Graphics › Driver and profile on a Radeon or an Intel GPU: Exo's profile through the GPU's own driver, with
 * the latency choice beside its Use (a Radeon's Anti-Lag, while a game Exo launched runs by default: always on, it also
 * runs in Windows' own apps and costs CPU; an Intel GPU's Low latency mode, On by default), Undo while Exo's backup is
 * there, and on a Radeon the GPU's sensors under it.
 */
export function DriverProfile({ active, profile, head, busy, error, onAct }: {
  active: boolean
  profile: GpuProfile
  head: string
  /** What is being written, while it is. */
  busy: 'apply' | 'undo' | null
  error: string | null
  onAct: (action: 'apply' | 'undo', latency: LatencyChoice) => void
}) {
  const intel = profile.vendor === 'intel'
  const saved = intel ? profile.lowLatency ?? 'on' : profile.antiLag ?? 'games'
  const [choice, setChoice] = useState<string | null>(null)
  const latency = choice ?? saved
  const changed = latency !== saved
  const act = (action: 'apply' | 'undo') => {
    setChoice(null)
    onAct(action, intel ? { lowLatency: latency as IntelLowLatency } : { antiLag: latency as RadeonAntiLag })
  }
  const status = changed ? null : appliedState(profile)
  return (
    <>
      <Row label={profile.gpu || 'Graphics card'} detail={<>{status}{head}{profile.summary ? `. ${profile.summary}` : ''}</>}
        error={error ?? (profile.ok ? null : trimStop(profile.message) || 'The driver did not answer')}>
        <Choice label={intel ? 'Low latency' : 'Anti-Lag'} value={latency} choices={intel ? LOW_LATENCY : ANTI_LAG} disabled={!!busy} onChange={setChoice} />
        <ProfileButtons apply={applyWords(profile, changed, busy === 'apply')} undo={!!profile.canUndo} busy={busy} onAct={act} />
      </Row>
      {active && !intel && <SensorsRow />}
    </>
  )
}

/**
 * Exo's profile's Use (or Re-apply), named by `apply`, and Undo while Exo's backup is there. The one pressed keeps its
 * words and carries the bar while the driver is written.
 */
export function ProfileButtons({ apply, undo, busy, onAct }: {
  apply: string | null
  undo: boolean
  busy: 'apply' | 'undo' | null
  onAct: (action: 'apply' | 'undo') => void
}) {
  return (
    <>
      {apply
        ? <button type="button" className="exo-set-btn is-primary" disabled={!!busy} aria-busy={busy === 'apply'} onClick={() => onAct('apply')}>
          {busy === 'apply' ? <Spinner label="Writing Exo’s profile to the driver" /> : null}{apply}
        </button>
        : null}
      {undo
        ? <button type="button" className="exo-set-btn" disabled={!!busy} aria-busy={busy === 'undo'} onClick={() => onAct('undo')}>
          {busy === 'undo' ? <Spinner label="Putting your driver settings back" /> : null}Undo
        </button>
        : null}
    </>
  )
}

/** What Use says (Re-apply for what changed since), or null with nothing to apply; kept while it writes, as pressing it put the latency choice back. */
function applyWords(profile: GpuProfile, changed: boolean, writing: boolean) {
  if (profile.status !== 'ready' && profile.status !== 'changed' && !changed && !writing) return null
  return profile.status === 'changed' && !changed ? 'Re-apply' : 'Use Exo’s profile'
}

/** Where Exo's profile stands, ahead of the row's line: Applied, or what changed since. */
function appliedState(profile: GpuProfile) {
  if (profile.status === 'applied') return <><State on>Applied</State> · </>
  if (profile.status === 'changed') return <><State>{profile.driverUpdated ? 'Driver changed some settings' : 'Some settings changed'}</State> · </>
  return null
}

/** The GPU's sensors, read every 3 s while the tab is open and Exo is on screen. */
function SensorsRow() {
  const tick = usePageClock(3_000, Date.now)
  const [sensors, setSensors] = useState<GpuSensors | null>(null)
  const read = useCallback(() => host.gpuSensors().then(setSensors, e => report(e, 'DriverProfile.sensors')), [])
  useEffect(() => { void read() }, [read, tick])
  const line = sensors?.ok ? sensorLine(sensors) : ''
  if (!line) return null
  return <Row label="Right now" detail={line} />
}

function sensorLine(s: GpuSensors): string {
  const n = (value: number) => Math.round(value).toLocaleString()
  const temp = s.temperature != null ? `${n(s.temperature)} °C${s.hotspot != null ? `, hotspot ${n(s.hotspot)} °C` : ''}` : null
  const vram = s.vramUsed != null ? `${(s.vramUsed / 1024).toFixed(1)}${s.vramTotal ? ` of ${Math.round(s.vramTotal / 1024)}` : ''} GB VRAM` : null
  return [
    temp,
    s.watts != null ? `${n(s.watts)} W` : null,
    s.clock != null ? `${n(s.clock)} MHz` : null,
    s.usage != null ? `${n(s.usage)}% busy` : null,
    vram,
    s.fan != null ? `fan ${n(s.fan)} RPM` : null,
  ].filter(Boolean).join(' · ')
}
