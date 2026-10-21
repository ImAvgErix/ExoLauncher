import { useCallback, useEffect, useId, useRef, useState } from 'react'
import { host, onHostEvent, type DriverProgress, type DriverState } from '../../lib/host'
import { report } from '../../lib/report'
import { trimStop } from '../../lib/utils'
import { Row, State } from '../settings/parts'
import { Choice } from '../settings/Choice'

const NVIDIA_LICENSE = 'https://www.nvidia.com/en-us/drivers/nvidia-license/'

const STAGE: Record<DriverProgress['stage'], string> = {
  idle: '', downloading: 'Downloading', unpacking: 'Unpacking', waiting: 'Waiting', installing: 'Installing', done: 'Installed', failed: 'Stopped',
}

function size(bytes: number) {
  return bytes >= 1e9 ? `${(bytes / 1e9).toFixed(1)} GB` : `${Math.round(bytes / 1e6)} MB`
}

function day(iso: string | null | undefined) {
  if (!iso) return null
  const date = new Date(iso)
  return Number.isNaN(date.getTime()) ? null : date.toLocaleDateString(undefined, { month: 'long', day: 'numeric' })
}

const WORKING = new Set<DriverProgress['stage']>(['downloading', 'unpacking', 'waiting', 'installing'])

type Branch = 'game' | 'studio'
const BRANCHES = [{ value: 'game', label: 'Game Ready' }, { value: 'studio', label: 'Studio' }] as const
const KIND: Record<NonNullable<DriverState['latest']>['kind'], string> = {
  game: 'Game Ready driver', studio: 'Studio driver', hotfix: 'hotfix driver', security: 'security update driver', workstation: 'NVIDIA RTX Enterprise driver',
}

/** Game Ready or Studio, kept on this PC. */
function savedBranch(): Branch {
  try { return localStorage.getItem('exo.driverBranch') === 'studio' ? 'studio' : 'game' } catch { return 'game' }
}

/** The row's one line: progress while it works, then how it ended, else installed vs newest. */
function driverDetail(state: DriverState, progress: DriverProgress | null, starting: boolean) {
  const latest = state.latest
  if (starting) return 'Starting the update'
  if (progress && WORKING.has(progress.stage))
    return `${progress.message ?? STAGE[progress.stage]}${progress.stage === 'downloading' ? ` · ${progress.percent}%` : ''}`
  if (progress?.stage === 'done' || progress?.stage === 'failed') return trimStop(progress.message) || STAGE[progress.stage]
  if (!latest) return `${state.installed ?? 'No driver'} installed · NVIDIA's driver list couldn't be reached`
  if (!state.update) return `${state.installed} · the newest ${KIND[latest.kind]} for your card`
  const released = day(latest.released)
  return `${state.installed ?? 'No driver'} installed · ${KIND[latest.kind]} ${latest.version} available${released ? `, ${released}` : ''}`
}

/**
 * System > Graphics > Driver and profile, above the driver profile: the NVIDIA driver installed and NVIDIA's newest Game Ready
 * driver for this card. Update (or Reinstall, to strip what an earlier install added) asks first, then
 * downloads from NVIDIA and clean-installs the driver alone: no NVIDIA app, telemetry, ShadowPlay, PhysX
 * or HDMI/DisplayPort audio. Exo puts its profile back afterwards.
 */
export function DriverRow({ active }: { active: boolean }) {
  const [state, setState] = useState<DriverState | null>(null)
  const [progress, setProgress] = useState<DriverProgress | null>(null)
  const [asking, setAsking] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [branch, setBranch] = useState<Branch>(savedBranch)
  // Between Install and the host's first progress event, so the button can't be pressed again.
  const [starting, setStarting] = useState(false)

  const load = useCallback(async () => {
    try {
      const next = await host.gpuDriver('state', branch)
      setState(next)
      if (next.progress) setProgress(next.progress)
    } catch (e) { report(e, 'DriverRow.state') }
  }, [branch])

  useEffect(() => { void load() }, [load, active])
  // "Close your game first" goes away when the game does.
  useEffect(() => active ? onHostEvent('game.runState', () => void load()) : undefined, [active, load])
  useEffect(() => onHostEvent('gpu.driver', next => {
    setStarting(false)
    setProgress(next)
    if (next.stage === 'done' || next.stage === 'failed') void load()
  }), [load])

  const install = async () => {
    setAsking(false)
    setError(null)
    setStarting(true)
    try {
      const answer = await host.gpuDriver('update', branch)
      if (!answer.ok) {
        setStarting(false)
        setError(trimStop(answer.message) || 'Not started')
      }
    } catch (e) {
      report(e, 'DriverRow.update')
      setStarting(false)
      setError('Not started')
    }
  }

  if (!state?.nvidia) return null
  const working = starting || (progress !== null && WORKING.has(progress.stage))
  const latest = state.latest

  return (
    <>
      <Row label="NVIDIA driver" detail={driverDetail(state, progress, starting)} error={error ?? (working ? null : state.blocked ?? null)}>
        {working
          ? <State>{starting || !progress ? 'Starting' : STAGE[progress.stage]}…</State>
          : latest && (
            <button type="button" className={`exo-set-btn${state.update ? ' is-primary' : ''}`} disabled={!!state.blocked || asking}
              onClick={() => setAsking(true)}>
              {state.update ? 'Update' : 'Reinstall'}
            </button>
          )}
      </Row>
      {state.workstation ? null : (
        <Row label="Driver type" detail={branch === 'studio'
          ? 'Studio: tested longer with creative apps, released less often'
          : 'Game Ready: tuned for new games on release day, with hotfixes when NVIDIA ships one'}>
          <Choice label="Driver type" value={branch} choices={BRANCHES} disabled={working}
            onChange={value => {
              const next: Branch = value === 'studio' ? 'studio' : 'game'
              try { localStorage.setItem('exo.driverBranch', next) } catch { /* kept for this session */ }
              setBranch(next)
            }} />
        </Row>
      )}
      {asking && latest && (
        <DriverConsent version={`${KIND[latest.kind]} ${latest.version}`} bytes={latest.size}
          onCancel={() => setAsking(false)} onInstall={() => void install()} />
      )}
    </>
  )
}

/** What the update downloads, installs and leaves out, and NVIDIA's license, before anything happens. */
function DriverConsent({ version, bytes, onCancel, onInstall }: {
  version: string
  bytes: number
  onCancel: () => void
  onInstall: () => void
}) {
  const headingId = useId()
  const cancel = useRef<HTMLButtonElement>(null)
  useEffect(() => { cancel.current?.focus() }, [])
  return (
    <div className="exo-graphics-import" role="group" aria-labelledby={headingId}>
      <p id={headingId} className="exo-graphics-import-title">Install NVIDIA {version}, driver only?</p>
      <ul className="exo-graphics-import-list">
        <li><span>Downloads from NVIDIA</span><span className="exo-graphics-import-value">{bytes ? size(bytes) : 'About 1 GB'}</span></li>
        <li><span>Installs</span><span className="exo-graphics-import-value">The driver alone, as a clean install</span></li>
        <li><span>Leaves out</span><span className="exo-graphics-import-value">NVIDIA app, telemetry, ShadowPlay, PhysX, HDMI/DisplayPort audio</span></li>
        <li><span>Afterwards</span><span className="exo-graphics-import-value">Exo puts your GPU profile back</span></li>
      </ul>
      <p className="exo-graphics-import-extra">
        Windows asks for administrator permission. Installing means you accept <button type="button" className="exo-set-link" onClick={() => void host.openUrl(NVIDIA_LICENSE)}>NVIDIA's license</button>. The screen may go black for a moment.
      </p>
      <span className="exo-graphics-import-actions">
        <button ref={cancel} type="button" className="exo-set-btn" onClick={onCancel}>Cancel</button>
        <button type="button" className="exo-set-btn is-primary" onClick={onInstall}>Install</button>
      </span>
    </div>
  )
}
