import { useCallback, useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react'
import { host, type DriverState, type Game, type GpuProfile, type HdrState, type Interrupts, type LauncherSettings, type PcHealth } from '../../lib/host'
import { useHdrState } from '../../lib/hdr'
import { report } from '../../lib/report'
import { pushPage } from '../../lib/viewTransition'
import { AppsPanel } from '../apps/AppsPanel'
import { RUNTIMES_SEARCH, RuntimesPanel } from '../runtimes/RuntimesPanel'
import { Row, State, SwitchRow } from '../settings/parts'
import { ShadersBoard } from '../shaders/ShadersBoard'
import { RoomNav, type RoomPage } from '../ui/RoomNav'
import { UpscalersTab } from '../upscalers/UpscalersSheet'
import { DisplayTab } from './DisplayTab'
import { InterruptRows } from './InterruptRows'
import { PAGE_ICON, PcOverview, pcStatus } from './PcOverview'
import { ProfileTab } from './ProfileTab'
import { ThisPcTab } from './ThisPcTab'

/** The PC room's pages (DESIGN.md › PC). */
export type PcPage = 'overview' | 'graphics' | 'display' | 'performance' | 'apps'
/** Where a link opens the room: a page, or Graphics at its Shaders (a game's page links to its graphics and its shaders). */
export type PcLink = PcPage | 'shaders'

/** Each page: its id, its name in the page list and the words Ctrl+K finds it by. */
export const PC_PAGES: readonly { page: PcPage; label: string; keywords: readonly string[] }[] = [
  { page: 'overview', label: 'Overview', keywords: ['this pc', 'about', 'graphics card', 'gpu', 'best settings', 'specs'] },
  { page: 'graphics', label: 'Graphics', keywords: ['driver', 'nvidia', 'radeon', 'intel', 'driver profile', 'g-sync', 'upscalers', 'dlss', 'fsr', 'xess', 'shaders', 'compile'] },
  { page: 'display', label: 'Display', keywords: ['screen', 'monitor', 'refresh rate', 'hz', 'hdr', 'color', 'scaling', 'night shift', 'night light'] },
  { page: 'performance', label: 'Performance', keywords: ['power plan', 'game mode', 'gpu scheduling', 'resizable bar', 'rebar', 'memory', 'ram', 'chipset', 'interrupts', 'msi mode'] },
  { page: 'apps', label: 'Apps', keywords: ['uninstall', 'programs', 'remove', 'leftovers', 'clean up', ...RUNTIMES_SEARCH.flatMap(entry => entry.keywords)] },
]

const pageOf = (link: PcLink): PcPage => link === 'shaders' ? 'graphics' : link

/** The page the room was left on, for this run: the room comes back to it, even after it gave its tree back. */
let lastPage: PcPage = 'overview'

/**
 * The PC room (DESIGN.md › PC): this PC and what Exo tunes on it, in the room frame: the page list on the left
 * (Overview, Graphics, Display, Performance, Apps, each with where it stands), one page at a time beside it. A page is
 * built the first time it shows and kept after, hidden, so each keeps its own scroll and whatever it is doing.
 * `jump` opens a page (a game's graphics), or Graphics held at Shaders (a game's shaders).
 */
export function PcRoom({ active, jump, games, settings, onSave }: {
  active: boolean
  jump: { tab: PcLink } | null
  games: readonly Game[]
  settings: LauncherSettings | null
  onSave: (patch: Partial<LauncherSettings>) => Promise<boolean>
}) {
  const [page, setPage] = useState<PcPage>(lastPage)
  const [built, setBuilt] = useState<ReadonlySet<PcPage>>(() => new Set([lastPage]))
  // What a link asked to bring into view; a new object each time, so the same link twice shows twice.
  const [target, setTarget] = useState<{ tab: PcLink } | null>(null)
  const stage = useRef<HTMLDivElement>(null)
  const state = usePcState(active, page)

  const show = useCallback((link: PcLink, reveal: boolean) => {
    const next = pageOf(link)
    lastPage = next
    setPage(next)
    setBuilt(current => current.has(next) ? current : new Set([...current, next]))
    if (reveal) setTarget({ tab: link })
  }, [])

  // A link takes the room to its page once, before the room is painted.
  const jumped = useRef<typeof jump>(null)
  useLayoutEffect(() => {
    if (!jump || jumped.current === jump) return
    jumped.current = jump
    show(jump.tab, true)
  }, [jump, show])

  // Once the room is on screen: the page from its top, or Shaders kept at the top while what is above it loads.
  const revealed = useRef<typeof target>(null)
  useLayoutEffect(() => {
    if (!active || !target || revealed.current === target) return
    revealed.current = target
    const scroller = stage.current?.querySelector<HTMLElement>(`[data-pc-page="${pageOf(target.tab)}"]`)
    if (!scroller) return
    if (target.tab === 'shaders') return holdInView(scroller, '[data-pc-part="shaders"]')
    scroller.scrollTop = 0
  }, [active, target])

  const status = pcStatus(state, settings)
  const pages: RoomPage[] = PC_PAGES.map(({ page: id, label }) => ({ id, label, sub: status[id]?.sub, icon: PAGE_ICON[id] }))
  const on = (id: PcPage) => active && page === id
  const pick = (id: string) => {
    const next = PC_PAGES.find(entry => entry.page === id)?.page
    if (next) void pushPage(() => show(next, false), '.exo-pc-stage', '.exo-pc .exo-room-nav-item[aria-current="page"]')
  }

  return (
    <main className="exo-pc" aria-label="PC">
      <RoomNav label="PC" pages={pages} current={page} onPick={pick} />
      <div ref={stage} className="exo-pc-stage">
        <Page id="overview" built={built} current={page}>
          <PcOverview active={on('overview')} state={state} status={status} settings={settings} onSave={onSave} onOpen={link => show(link, link === 'shaders')} />
        </Page>
        <Page id="graphics" built={built} current={page}>
          <div className="exo-pc-row">
            <Panel title="Driver and profile"><ProfileTab active={on('graphics')} /></Panel>
            <Panel title="Upscalers"><UpscalersTab active={on('graphics')} games={games} /></Panel>
          </div>
          <Panel title="Shaders" part="shaders"><ShadersBoard active={on('graphics')} games={games} /></Panel>
        </Page>
        <Page id="display" built={built} current={page}>
          <Panel title="Screen"><DisplayTab active={on('display')} /></Panel>
          <Panel title="HDR"><HdrTab active={on('display')} settings={settings} onSave={onSave} /></Panel>
        </Page>
        <Page id="performance" built={built} current={page}>
          <div className="exo-pc-row">
            <Panel title="Setup"><ThisPcTab active={on('performance')} /></Panel>
            <Panel title="Interrupts"><InterruptRows active={on('performance')} /></Panel>
          </div>
        </Page>
        <Page id="apps" built={built} current={page}>
          <div className="exo-pc-row">
            <Panel title="Apps"><AppsPanel active={on('apps')} /></Panel>
            <Panel title="Game runtimes"><RuntimesPanel active={on('apps')} /></Panel>
          </div>
        </Page>
      </div>
    </main>
  )
}

/** One page: built the first time it shows, then kept, hidden while another shows. It scrolls on its own. */
function Page({ id, built, current, children }: { id: PcPage; built: ReadonlySet<PcPage>; current: PcPage; children: ReactNode }) {
  if (!built.has(id)) return null
  return (
    <section className="exo-pc-page" data-pc-page={id} aria-label={PC_PAGES.find(entry => entry.page === id)?.label} hidden={id !== current}>
      {children}
    </section>
  )
}

/** A panel of glass with its name over its rows; one with nothing to show (no HDR, no Lucid) isn't drawn. */
function Panel({ title, part, children }: { title: string; part?: 'shaders'; children: ReactNode }) {
  return (
    <section className="exo-system-panel" aria-label={title} data-pc-part={part}>
      <h2>{title}</h2>
      <div className="exo-system-panel-body">{children}</div>
    </section>
  )
}

/**
 * Keeps a part of a page at its top (12px under it, as at rest) while what is above it loads and grows, until you
 * scroll, press or type there, or after 3 s. Returns what stops it.
 */
function holdInView(scroller: HTMLElement, selector: string) {
  const part = scroller.querySelector<HTMLElement>(selector)
  if (!part) return
  const place = () => { scroller.scrollTop += part.getBoundingClientRect().top - scroller.getBoundingClientRect().top - 12 }
  place()
  const watch = new ResizeObserver(place)
  for (const child of scroller.children) watch.observe(child)
  const events = ['wheel', 'pointerdown', 'keydown'] as const
  const stop = () => {
    watch.disconnect()
    window.clearTimeout(timer)
    for (const name of events) scroller.removeEventListener(name, stop)
  }
  for (const name of events) scroller.addEventListener(name, stop, { passive: true })
  const timer = window.setTimeout(stop, 3000)
  return stop
}

/** What the page list and Overview say about each page, read from the host. */
export type PcState = {
  profile: GpuProfile | null
  driver: DriverState | null
  hdr: HdrState | null
  shaders: Awaited<ReturnType<typeof host.shadersAddon>> | null
  health: PcHealth | null
  interrupts: Interrupts | null
  /** How many apps Apps lists. */
  apps: number | null
}

const quiet = (where: string) => (error: unknown) => report(error, `PcRoom.${where}`, { level: 'warn' })

/**
 * The card, its driver and the screen, read with the room behind the splash so the Overview is filled the first time
 * it shows; then everything each time the room shows, on each page change (a page may have changed what it says) and
 * when Exo's window comes back. Apps is read once and again after its page, where an app may have gone.
 */
function usePcState(active: boolean, page: PcPage): PcState {
  const [profile, setProfile] = useState<GpuProfile | null>(null)
  const [driver, setDriver] = useState<DriverState | null>(null)
  const [shaders, setShaders] = useState<PcState['shaders']>(null)
  const [health, setHealth] = useState<PcHealth | null>(null)
  const [interrupts, setInterrupts] = useState<Interrupts | null>(null)
  const [apps, setApps] = useState<number | null>(null)
  const hdr = useHdrState(active)
  const read = useRef(false)
  useEffect(() => {
    if (!active && read.current) return
    read.current = true
    let live = true
    const load = () => {
      host.gpuProfile().then(next => { if (live) setProfile(next) }, quiet('profile'))
      host.gpuDriver('state').then(next => { if (live) setDriver(next) }, quiet('driver'))
      if (!active) return
      host.shadersAddon().then(next => { if (live) setShaders(next) }, quiet('shaders'))
      host.systemHealth().then(next => { if (live) setHealth(next) }, quiet('health'))
      host.systemInterrupts().then(next => { if (live) setInterrupts(next) }, quiet('interrupts'))
    }
    load()
    if (active) window.addEventListener('focus', load)
    return () => { live = false; window.removeEventListener('focus', load) }
  }, [active, page])

  const left = useRef(page)
  const appsRead = useRef(false)
  useEffect(() => {
    const was = left.current
    left.current = page
    if (!active || (appsRead.current && was !== 'apps')) return
    appsRead.current = true
    host.appsList().then(next => setApps(next.programs.length), quiet('apps'))
  }, [active, page])

  return { profile, driver, hdr, shaders, health, interrupts, apps }
}

const VERDICT: Record<string, string> = {
  great: 'Bright enough for real HDR',
  good: 'Fine for HDR',
  weak: 'Too dim for good HDR',
  unknown: 'Brightness not reported',
}

/** PC › Display › HDR: the main screen's verdict, HDR for HDR games, and calibration; one line on a screen without HDR. */
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
