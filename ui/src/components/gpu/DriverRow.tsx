import { useCallback, useEffect, useState } from 'react'
import { host, onHostEvent, type DriverProgress, type DriverState } from '../../lib/host'
import { report } from '../../lib/report'
import { trimStop } from '../../lib/utils'
import { Row } from '../settings/parts'
import { Choice } from '../settings/Choice'
import { ConfirmDialog } from '../ui/Dialog'
import { Progress } from '../ui/Progress'

type Vendor = NonNullable<DriverState['vendor']>
type Latest = NonNullable<DriverState['latest']>

const NVIDIA_LICENSE = 'https://www.nvidia.com/en-us/drivers/nvidia-license/'
const AMD_LICENSE = 'https://www.amd.com/en/legal/eula.html'

/** What each vendor's update installs and leaves out, and whether Exo puts its profile back after: the consent's lines. */
const VENDOR: Record<Vendor, { name: string; installs: string; leavesOut?: string; restores: boolean }> = {
  nvidia: { name: 'NVIDIA', installs: 'The driver alone, as a clean install', leavesOut: 'NVIDIA app, telemetry, ShadowPlay, PhysX, HDMI/DisplayPort audio', restores: true },
  amd: { name: 'AMD', installs: 'The driver and AMD Software, which Fluid Motion Frames needs', restores: true },
  intel: { name: 'Intel', installs: 'The driver alone', leavesOut: 'Intel Graphics Software, Driver & Support Assistant, GameAssist, data collection', restores: false },
}

const STAGE: Record<DriverProgress['stage'], string> = {
  idle: '', downloading: 'Downloading', unpacking: 'Unpacking', waiting: 'Waiting', installing: 'Installing', done: 'Installed', failed: 'Stopped',
}

function size(bytes: number) {
  return bytes >= 1e9 ? `${(bytes / 1e9).toFixed(1)} GB` : `${Math.round(bytes / 1e6)} MB`
}

/** How much of the download is in: "420 MB of 1.0 GB" when its size is known, else "42%" (after the stage's word). */
function downloaded(percent: number, total: number | undefined) {
  return total ? `${size(total * percent / 100)} of ${size(total)}` : `${Math.round(percent)}%`
}

function day(iso: string | null | undefined) {
  if (!iso) return null
  const date = new Date(iso)
  return Number.isNaN(date.getTime()) ? null : date.toLocaleDateString(undefined, { month: 'long', day: 'numeric' })
}

const WORKING = new Set<DriverProgress['stage']>(['downloading', 'unpacking', 'waiting', 'installing'])

type Branch = 'game' | 'studio'
const BRANCHES = [{ value: 'game', label: 'Game Ready' }, { value: 'studio', label: 'Studio' }] as const
const KIND: Record<Latest['kind'], string> = {
  game: 'Game Ready driver', studio: 'Studio driver', hotfix: 'hotfix driver', security: 'security update driver', workstation: 'NVIDIA RTX Enterprise driver',
  recommended: 'Adrenalin', optional: 'optional Adrenalin', intel: 'Intel driver',
}

/** Game Ready or Studio, kept on this PC. */
function savedBranch(): Branch {
  try { return localStorage.getItem('exo.driverBranch') === 'studio' ? 'studio' : 'game' } catch { return 'game' }
}

/** The row's one line: what it is doing and how much while it works, then how it ended, else installed vs newest. */
function driverDetail(state: DriverState, vendor: Vendor, progress: DriverProgress | null, starting: boolean) {
  const latest = state.latest
  if (starting) return 'Starting the update'
  if (progress && WORKING.has(progress.stage))
    return `${progress.message ?? STAGE[progress.stage]}${progress.stage === 'downloading' ? ` · ${downloaded(progress.percent, latest?.size)}` : ''}`
  if (progress?.stage === 'done' || progress?.stage === 'failed') return trimStop(progress.message) || STAGE[progress.stage]
  if (!latest) return `${state.installed ?? 'No driver'} installed · ${vendor === 'nvidia' ? "NVIDIA's driver list couldn't be reached" : `${VENDOR[vendor].name}'s newest driver for this card couldn't be found`}`
  if (!state.update) return `${state.installed} · the newest ${KIND[latest.kind]} for your card`
  const released = day(latest.released)
  return `${state.installed ?? 'No driver'} installed · ${KIND[latest.kind]} ${latest.version} available${released ? `, ${released}` : ''}`
}

/**
 * PC › Graphics › Driver and profile, above the driver profile: the installed driver and the vendor's newest for this
 * card. Update (or Reinstall) asks first, then downloads from the vendor and installs. NVIDIA: a clean install of the driver
 * alone, Game Ready or Studio. AMD: AMD Software: Adrenalin Edition with its driver. Intel: the driver without Intel's extras.
 * Exo puts its profile back afterwards.
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
  // "Close your game first" and "Leave your Discord call first" go away when the game or the call does.
  useEffect(() => active ? onHostEvent('game.runState', () => void load()) : undefined, [active, load])
  useEffect(() => active ? onHostEvent('discord.voice', () => void load()) : undefined, [active, load])
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

  const vendor = state?.vendor
  if (!state || !vendor) return null
  const working = starting || (progress !== null && WORKING.has(progress.stage))
  const latest = state.latest

  return (
    <>
      {/* NVIDIA's Game Ready or Studio sits beside Update: the driver it installs. */}
      <Row label={`${VENDOR[vendor].name} driver`} detail={driverDetail(state, vendor, progress, starting)} error={error ?? (working ? null : state.blocked ?? null)}>
        {vendor === 'nvidia' && !state.workstation && (
          <Choice label="Driver type" value={branch} choices={BRANCHES} disabled={working}
            onChange={value => {
              const next: Branch = value === 'studio' ? 'studio' : 'game'
              try { localStorage.setItem('exo.driverBranch', next) } catch { /* kept for this session */ }
              setBranch(next)
            }} />
        )}
        {(working || latest) && (
          <DriverButton update={!!state.update} working={working} progress={progress} label={driverDetail(state, vendor, progress, starting)}
            disabled={!!state.blocked} onPress={() => setAsking(true)} />
        )}
      </Row>
      {asking && latest && <DriverConsent vendor={vendor} latest={latest} update={!!state.update} onCancel={() => setAsking(false)} onInstall={() => void install()} />}
    </>
  )
}

/** Update or Reinstall. While the update runs it keeps its words and carries the bar: the download's share, else gliding. */
function DriverButton({ update, working, progress, label, disabled, onPress }: {
  update: boolean
  working: boolean
  progress: DriverProgress | null
  /** What it is doing and how much, for the bar. */
  label: string
  disabled: boolean
  onPress: () => void
}) {
  return (
    <button type="button" className={`exo-set-btn${update ? ' is-primary' : ''}`} disabled={working || disabled} aria-busy={working} onClick={onPress}>
      {update ? 'Update' : 'Reinstall'}
      {working ? <Progress edge value={progress?.stage === 'downloading' ? progress.percent / 100 : null} label={label} /> : null}
    </button>
  )
}

/** What the update downloads, installs and leaves out, and the vendor's license, before anything happens. */
function DriverConsent({ vendor, latest, update, onCancel, onInstall }: {
  vendor: Vendor
  latest: Latest
  update: boolean
  onCancel: () => void
  onInstall: () => void
}) {
  const { name, installs, leavesOut, restores } = VENDOR[vendor]
  const link = (url: string, label: string) => <button type="button" className="exo-set-link" onClick={() => void host.openUrl(url)}>{label}</button>
  const version = vendor === 'amd' ? `AMD Software: Adrenalin Edition ${latest.version}${latest.kind === 'optional' ? ' (optional)' : ''}`
    : vendor === 'intel' ? `Intel's graphics driver ${latest.version}, driver only`
      : `NVIDIA ${KIND[latest.kind]} ${latest.version}, driver only`
  return (
    <ConfirmDialog title={`${update ? 'Update' : 'Reinstall'} the ${name} driver?`} action="Install" form onConfirm={onInstall} onClose={onCancel}>
      <p>{version}</p>
      <ul className="exo-graphics-import-list">
        <li><span>Downloads from {name}</span><span className="exo-graphics-import-value">{latest.size ? size(latest.size) : 'About 1 GB'}</span></li>
        <li><span>Installs</span><span className="exo-graphics-import-value">{installs}</span></li>
        {leavesOut && <li><span>Leaves out</span><span className="exo-graphics-import-value">{leavesOut}</span></li>}
        {restores && <li><span>Afterwards</span><span className="exo-graphics-import-value">Exo puts your GPU profile back</span></li>}
      </ul>
      <p className="exo-graphics-import-extra">
        Windows asks for administrator permission. Installing means you accept {vendor === 'nvidia' ? link(NVIDIA_LICENSE, "NVIDIA's license")
          : vendor === 'amd' ? link(AMD_LICENSE, "AMD's license")
            : <>the license on {latest.page ? link(latest.page, "Intel's download page") : "Intel's download page"}</>}. The screen may go black for a moment.
      </p>
    </ConfirmDialog>
  )
}
