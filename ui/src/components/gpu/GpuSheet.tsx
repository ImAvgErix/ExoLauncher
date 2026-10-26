import { useEffect, useRef, useState } from 'react'
import { GraphicsCard } from '../../brand/icons'
import { host, type DriverState, type Game, type GpuProfile, type HdrState, type LauncherSettings } from '../../lib/host'
import { useHdrState } from '../../lib/hdr'
import { Row, State, SwitchRow } from '../settings/parts'
import { ShadersBoard } from '../shaders/ShadersBoard'
import { UpscalersTab } from '../upscalers/UpscalersSheet'
import { BestSetup } from './BestSetup'
import { DisplayTab } from './DisplayTab'
import { ProfileTab } from './ProfileTab'
import { InterruptRows } from './InterruptRows'
import { ThisPcTab } from './ThisPcTab'

/** A part of the System room a link can open it at (a game's page: its graphics, its shaders). */
export type GpuTab = 'graphics' | 'display' | 'shaders'

/**
 * The System room, one page: a slim header with your card and screen, then the driver and
 * Exo's profile, HDR, what to fix in this PC's setup and upscalers in your games, then the screen's settings, then the games whose shaders still need
 * compiling.
 * `jump` scrolls to a part when a link opens the room there.
 */
export function GpuHub({ active, jump, games, settings, onSave }: {
  active: boolean
  jump: { tab: GpuTab } | null
  games: readonly Game[]
  settings: LauncherSettings | null
  onSave: (patch: Partial<LauncherSettings>) => Promise<boolean>
}) {
  const { profile, driver } = useSystemState(active)
  const room = useRef<HTMLElement>(null)
  useEffect(() => {
    if (!active || !jump) return
    const part = room.current?.querySelector(`[data-system-part="${jump.tab}"]`)
    if (jump.tab === 'graphics') room.current?.scrollTo({ top: 0 })
    else part?.scrollIntoView({ block: 'start' })
  }, [active, jump])
  return (
    <section ref={room} className="exo-system" aria-label="System" data-room-scroll>
      <header className="exo-system-head">
        <span className="exo-system-head-icon" aria-hidden="true"><GraphicsCard size={20} /></span>
        <span className="exo-system-head-id">
          <strong>{profile?.gpu || driver?.card || 'Graphics card'}</strong>
          <span>{[driver?.installed ? `Driver ${driver.installed}` : null, profile?.screen.name, profile?.screen.hz ? `${profile.screen.hz} Hz` : null, profile?.vendor === 'nvidia' ? (profile.screen.vrr ? 'G-SYNC' : 'Fixed refresh') : null].filter(Boolean).join(' · ')}</span>
        </span>
      </header>
      <div className="exo-system-page">
        <div className="exo-system-stack" data-system-part="graphics">
          <Panel title="Driver and profile"><ProfileTab active={active} /></Panel>
          <Panel title="HDR"><HdrTab active={active} settings={settings} onSave={onSave} /></Panel>
          <Panel title="This PC"><BestSetup active={active} settings={settings} onSave={onSave} /><ThisPcTab active={active} /><InterruptRows active={active} /></Panel>
        </div>
        <Panel title="Upscalers"><UpscalersTab active={active} games={games} /></Panel>
      </div>
      <div className="exo-system-page is-one">
        <Panel title="Display" part="display"><DisplayTab active={active} /></Panel>
      </div>
      <div className="exo-system-page is-one" data-system-part="shaders">
        <Panel title="Shaders"><ShadersBoard active={active} games={games} /></Panel>
      </div>
    </section>
  )
}

function Panel({ title, part, children }: { title: string; part?: GpuTab; children: React.ReactNode }) {
  return (
    <section className="exo-system-panel" aria-label={title} data-system-part={part}>
      <h2>{title}</h2>
      <div className="exo-system-panel-body">{children}</div>
    </section>
  )
}

/** The card, its driver and Exo's profile on it: read with the room behind the splash, so the
    header is filled the first time it shows, and again each time it shows. */
function useSystemState(active: boolean) {
  const [profile, setProfile] = useState<GpuProfile | null>(null)
  const [driver, setDriver] = useState<DriverState | null>(null)
  const read = useRef(false)
  useEffect(() => {
    if (!active && read.current) return
    read.current = true
    let live = true
    host.gpuProfile().then(next => { if (live) setProfile(next) }).catch(() => { })
    host.gpuDriver('state').then(next => { if (live) setDriver(next) }).catch(() => { })
    return () => { live = false }
  }, [active])
  return { profile, driver }
}

const VERDICT: Record<string, string> = {
  great: 'Bright enough for real HDR',
  good: 'Fine for HDR',
  weak: 'Too dim for good HDR',
  unknown: 'Brightness not reported',
}

/** GPU > HDR: the main screen's verdict, HDR for HDR games, and calibration; one line on a screen without HDR. */
function HdrTab({ active, settings, onSave }: {
  active: boolean
  settings: LauncherSettings | null
  onSave: (patch: Partial<LauncherSettings>) => Promise<boolean>
}) {
  const screen = useHdrState(active)
  if (!screen) return null
  if (!screen.supported) return <p className="exo-hub-sub">The main screen cannot show HDR.</p>
  return <HdrRows screen={screen} settings={settings} onSave={onSave} />
}

function HdrRows({ screen, settings, onSave }: {
  screen: HdrState
  settings: LauncherSettings | null
  onSave: (patch: Partial<LauncherSettings>) => Promise<boolean>
}) {
  const nits = screen.peakNits ? `up to ${screen.peakNits} nits` : null
  const weak = screen.verdict === 'weak'
  return (
    <>
      <Row label={screen.name || 'Main screen'} detail={['HDR', nits].filter(Boolean).join(', ')}>
        <State on={!weak}>{VERDICT[screen.verdict ?? 'unknown']}</State>
      </Row>
      <SwitchRow
        id="set-hdr-games"
        label="HDR for HDR games"
        detail={weak
          ? 'On this screen HDR usually looks worse than SDR. If you turn it on, HDR comes on only while a game with real HDR runs.'
          : 'HDR comes on only while a game with real HDR runs, and goes back off after. Games without it stay SDR, so nothing looks washed out.'}
        checked={settings?.hdrForGames === true}
        disabled={!settings}
        onChange={async value => await onSave({ hdrForGames: value }) ? null : 'Could not save. Try again.'}
      />
      <SwitchRow
        id="set-hdr-sdr-gamma"
        label="SDR looks right in HDR"
        detail="While HDR is on, the desktop, overlays and SDR windows use gamma 2.2 like an SDR screen, so blacks stay black instead of washed out. HDR itself is untouched."
        checked={settings?.hdrSdrGamma !== false}
        disabled={!settings}
        onChange={async value => await onSave({ hdrSdrGamma: value }) ? null : 'Could not save. Try again.'}
      />
      <Row
        label="HDR calibration"
        detail={screen.calibrated
          ? 'Windows knows how bright this screen really gets, so games set HDR right'
          : 'Games set their HDR brightness from this. Windows HDR Calibration measures your screen in a minute.'}
      >
        {screen.calibrated
          ? <State on>Calibrated</State>
          : <button type="button" className="exo-set-btn" onClick={() => void host.openUrl('ms-windows-store://pdp/?productid=9N7F2SM5D1LR')}>Calibrate</button>}
      </Row>
    </>
  )
}
