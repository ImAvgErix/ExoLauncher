import { useEffect, useRef, useState } from 'react'
import { GraphicsCard } from '../../brand/icons'
import { host, type DriverState, type Game, type GpuProfile, type HdrState, type LauncherSettings } from '../../lib/host'
import { useGlide } from '../../lib/glide'
import { useHdrState } from '../../lib/hdr'
import { Row, State, SwitchRow } from '../settings/parts'
import { ShadersBoard } from '../shaders/ShadersBoard'
import { UpscalersTab } from '../upscalers/UpscalersSheet'
import { DisplayTab } from './DisplayTab'
import { ProfileTab } from './ProfileTab'
import { pushPage } from '../../lib/viewTransition'

/** The System room's pages. */
export type GpuTab = 'graphics' | 'display' | 'shaders'

const PAGES: { id: GpuTab; label: string }[] = [
  { id: 'graphics', label: 'Graphics' },
  { id: 'display', label: 'Display' },
  { id: 'shaders', label: 'Shaders' },
]

/**
 * The System room: one slim header with your card and screen and the room's pages, then a page
 * that fits the window without scrolling: Graphics (driver and Exo's profile, HDR, upscalers in
 * your games), Display, and Shaders (scan, compile, the live queue). Every page stays loaded.
 */
export function GpuHub({ active, tab, onTab, games, settings, onSave }: {
  active: boolean
  tab: GpuTab
  onTab: (tab: GpuTab) => void
  games: readonly Game[]
  settings: LauncherSettings | null
  onSave: (patch: Partial<LauncherSettings>) => Promise<boolean>
}) {
  const { profile, driver } = useSystemState(active)
  const glide = useGlide<HTMLDivElement>()
  return (
    <section className="exo-system" aria-label="System">
      <header className="exo-system-head">
        <span className="exo-system-head-icon" aria-hidden="true"><GraphicsCard size={20} /></span>
        <span className="exo-system-head-id">
          <strong>{profile?.gpu || driver?.card || 'Graphics card'}</strong>
          <span>{[driver?.installed ? `Driver ${driver.installed}` : null, profile?.screen.name, profile?.screen.hz ? `${profile.screen.hz} Hz` : null, profile?.vendor === 'nvidia' ? (profile.screen.vrr ? 'G-SYNC' : 'Fixed refresh') : null].filter(Boolean).join(' · ')}</span>
        </span>
        <div ref={glide} className="exo-shelf-chips exo-glide" role="tablist" aria-label="System">
          {PAGES.map(page => (
            <button key={page.id} type="button" role="tab" className="exo-shelf-chip" aria-selected={tab === page.id} aria-pressed={tab === page.id} onClick={() => void pushPage(() => onTab(page.id), '.exo-system-page:not([hidden])', '.exo-system-head [aria-selected="true"]')}>{page.label}</button>
          ))}
        </div>
      </header>
      <div className="exo-system-page exo-motion-switch" hidden={tab !== 'graphics'}>
        <div className="exo-system-stack">
          <Panel title="Driver and profile"><ProfileTab active={active && tab === 'graphics'} /></Panel>
          <Panel title="HDR"><HdrTab active={active && tab === 'graphics'} settings={settings} onSave={onSave} /></Panel>
        </div>
        <Panel title="Upscalers"><UpscalersTab active={active && tab === 'graphics'} games={games} /></Panel>
      </div>
      <div className="exo-system-page is-one exo-motion-switch" hidden={tab !== 'display'}>
        <Panel title="Display"><DisplayTab active={active && tab === 'display'} /></Panel>
      </div>
      <div className="exo-system-page is-one exo-motion-switch" hidden={tab !== 'shaders'}>
        <ShadersBoard active={active && tab === 'shaders'} games={games} />
      </div>
    </section>
  )
}

function Panel({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section className="exo-system-panel" aria-label={title}>
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
