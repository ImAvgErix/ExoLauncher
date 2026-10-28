import type { ReactNode } from 'react'
import { Apps, ChevronRight, Cpu, GraphicsCard, Monitor, Rocket, Shaders } from '../../brand/icons'
import type { GpuProfile, LauncherSettings, PcAbout } from '../../lib/host'
import { State } from '../settings/parts'
import { Skeleton } from '../ui/Skeleton'
import { BestSetup } from './BestSetup'
import type { PcLink, PcState } from './PcRoom'

/** Each page's glyph, in the page list and on Overview's rows. */
export const PAGE_ICON: Record<PcLink, ReactNode> = {
  overview: <Cpu size={18} />, graphics: <GraphicsCard size={18} />, shaders: <Shaders size={18} />,
  display: <Monitor size={18} />, performance: <Rocket size={18} />, apps: <Apps size={18} />,
}

/** Where a page stands: a few words for the page list (sub), a line for Overview's row and its state on the right. */
type Status = { sub?: string | null; line?: string | null; state?: { text: string; good?: boolean } | null }

const SYNC: Record<NonNullable<GpuProfile['vendor']>, string> = { nvidia: 'G-SYNC', amd: 'FreeSync', intel: 'Adaptive Sync' }

const PROFILE: Partial<Record<GpuProfile['status'], string>> = {
  applied: 'Exo’s profile on', changed: 'Exo’s profile changed since', ready: 'Exo’s profile not applied',
}

const count = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`

/** What the page list and Overview say about each page, from what the room read (PcRoom › usePcState). */
export function pcStatus(state: PcState, settings: LauncherSettings | null): Record<PcLink, Status | null> {
  const { profile, driver, apps } = state
  return {
    overview: { sub: profile?.gpu || driver?.card },
    graphics: graphicsStatus(state),
    shaders: shadersStatus(state),
    display: displayStatus(state, settings),
    performance: performanceStatus(state),
    apps: apps === null ? null : { sub: count(apps, 'app', 'apps'), line: `${count(apps, 'app', 'apps')} on this PC` },
  }
}

/** The driver against the vendor's newest, and Exo's profile on the card. */
function graphicsStatus({ driver, profile }: PcState): Status | null {
  if (!driver && !profile) return null
  const installed = driver?.installed ? `Driver ${driver.installed}` : null
  const newer = driver?.update && driver.latest ? `Driver ${driver.latest.version} available` : null
  return {
    sub: driver?.update ? 'Driver update available' : installed,
    line: [newer ?? installed, profile?.ok ? PROFILE[profile.status] : null].filter(Boolean).join(' · '),
    state: driver?.update ? { text: 'Update available' } : driver?.latest ? { text: 'Up to date', good: true } : null,
  }
}

/** Exo Shaders' last state, read without starting it: compiling, games ready to compile, or nothing. */
function shadersStatus({ shaders }: PcState): Status | null {
  if (!shaders) return null
  if (!shaders.present) return { line: 'Exo Shaders is not installed' }
  const snapshot = shaders.snapshot as { games?: { inAddAllReady?: boolean }[]; queue?: { current?: { name: string } | null } } | undefined
  const compiling = snapshot?.queue?.current?.name
  if (compiling) return { line: `Compiling ${compiling}`, state: { text: 'Compiling', good: true } }
  const games = snapshot?.games ?? []
  const ready = games.filter(game => game.inAddAllReady).length
  if (ready) return { line: `${count(ready, 'game', 'games')} ready to compile`, state: { text: `${ready} waiting` } }
  return games.length ? { state: { text: 'Up to date', good: true } } : { line: 'No games checked yet' }
}

/** The main screen and its refresh, and HDR: on for HDR games, on, off, or a screen without it. */
function displayStatus({ profile, hdr }: PcState, settings: LauncherSettings | null): Status | null {
  if (!profile && !hdr) return null
  const hz = profile?.screen.hz ? `${profile.screen.hz} Hz` : null
  const on = hdr?.supported && (settings?.hdrForGames || hdr.on)
  const word = !hdr ? null : !hdr.supported ? 'No HDR' : settings?.hdrForGames ? 'HDR for games' : hdr.on ? 'HDR on' : 'HDR off'
  return {
    sub: [hz, word].filter(Boolean).join(' · '),
    line: [profile?.screen.name, hz].filter(Boolean).join(' · '),
    state: word ? { text: word, good: !!on } : null,
  }
}

/** What Setup and Interrupts still have to fix, by name. */
function performanceStatus({ health, interrupts }: PcState): Status | null {
  if (!health) return null
  const fixes = health.items.filter(item => !item.applied && !item.current && !item.busy).map(item => item.label)
  if (interrupts && !interrupts.restart && interrupts.devices.some(device => device.plan === 'change')) fixes.push('Interrupts')
  if (!fixes.length) return { sub: 'All good', state: { text: 'All good', good: true } }
  return { sub: `${fixes.length} to fix`, line: fixes.join(', '), state: { text: `${fixes.length} to fix` } }
}

const LINKS: { link: Exclude<PcLink, 'overview'>; label: string }[] = [
  { link: 'graphics', label: 'Graphics' }, { link: 'shaders', label: 'Shaders' }, { link: 'display', label: 'Display' },
  { link: 'performance', label: 'Performance' }, { link: 'apps', label: 'Apps' },
]

/**
 * PC › Overview: this PC's About card, as Apple's System Settings draws it, in Exo's glass (the processor's glyph on a
 * large tile of the ink, the PC's name, then what it's made of and runs as label and value rows); beside it Best settings
 * for this PC, then one row per page with where it stands, which opens it (Shaders opens Graphics at its Shaders).
 */
export function PcOverview({ active, state, status, settings, onSave, onOpen }: {
  active: boolean
  state: PcState
  status: Record<PcLink, Status | null>
  settings: LauncherSettings | null
  onSave: (patch: Partial<LauncherSettings>) => Promise<boolean>
  onOpen: (link: PcLink) => void
}) {
  return (
    <div className="exo-pc-overview">
      <Hero state={state} />
      <div className="exo-pc-overview-side">
        <section className="exo-system-panel" aria-label="Best settings for this PC">
          <div className="exo-system-panel-body"><BestSetup active={active} settings={settings} onSave={onSave} /></div>
        </section>
        <nav className="exo-system-panel exo-pc-links" aria-label="Pages">
          {LINKS.map(({ link, label }) => {
            const at = status[link]
            return (
              <button key={link} type="button" className="exo-pc-link" onClick={() => onOpen(link)}>
                <span className="exo-room-nav-icon" aria-hidden="true">{PAGE_ICON[link]}</span>
                <span className="exo-pc-link-text">
                  <span className="exo-pc-link-label">{label}</span>
                  {at?.line ? <span className="exo-pc-link-line">{at.line}</span> : null}
                </span>
                {at?.state ? <State on={at.state.good}>{at.state.text}</State> : null}
                <ChevronRight size={16} className="exo-pc-link-go" />
              </button>
            )
          })}
        </nav>
      </div>
    </div>
  )
}

type Fact = [string, string | null | undefined]

const onOff = (on: boolean | null | undefined) => on == null ? null : on ? 'On' : 'Off'
const size = (gb: number) => gb >= 1000 ? `${+(gb / 1024).toFixed(1)} TB` : `${Math.round(gb)} GB`
const rate = (mbps: number | null | undefined) => !mbps ? null : mbps >= 1000 ? `${+(mbps / 1000).toFixed(1)} Gbps` : `${Math.round(mbps)} Mbps`
const month = (day: string | null | undefined) => day ? new Date(`${day}T00:00:00`).toLocaleDateString(undefined, { month: 'long', year: 'numeric' }) : null

/** Cores and threads: "6 cores, 12 threads", "8 cores" without SMT, the threads alone when SMBIOS has no core count. */
function cores({ cores, threads }: PcAbout) {
  if (!cores) return `${threads} threads`
  return cores === threads ? count(cores, 'core', 'cores') : `${count(cores, 'core', 'cores')}, ${threads} threads`
}

/** The speed the memory runs at, with the kit's rating when it runs below it (EXPO/XMP off). */
function memorySpeed({ memorySpeed: runs, memoryRated: rated }: PcAbout) {
  if (!runs) return null
  return rated && rated > runs ? `${runs} MT/s, rated ${rated}` : `${runs} MT/s`
}

/** A drive's free space first (what a game needs), then its disk; a hard drive says so. */
function drive({ model, kind, freeGb }: PcAbout['drives'][number]) {
  return [`${size(freeGb)} free`, model ?? kind, model && kind === 'Hard drive' ? kind : null].filter(Boolean).join(' · ')
}

const joined = (parts: (string | null | undefined)[], between = ' · ') => parts.filter(Boolean).join(between) || null

/**
 * The processor, then what it's paired with: memory, each drive, the motherboard and BIOS. What belongs together shares
 * a row ("16 GB DDR4 · 3600 MT/s · 2 of 4 slots"), so the whole card fits one screen without scrolling.
 */
function madeOf(about: PcAbout | null): Fact[][] {
  if (!about) return []
  const { memoryGb, memorySlots, memorySlotsUsed } = about
  return [
    [['Processor', joined([about.processor, cores(about)])]],
    [
      ['Memory', memoryGb ? joined([joined([`${memoryGb} GB`, about.memoryType], ' '), memorySpeed(about),
        memorySlots && memorySlotsUsed ? `${memorySlotsUsed} of ${memorySlots} slots` : null]) : null],
      ...about.drives.map((disk): Fact => [`Drive ${disk.letter}`, drive(disk)]),
    ],
    [['Motherboard', about.motherboard], ['BIOS', about.bios ? joined([about.bios, month(about.biosDate)]) : null]],
  ]
}

/** The graphics card: its name and video memory, the driver and DirectX, and Resizable BAR (what games ask of it). */
function graphics({ about, profile, driver }: PcState): Fact[] {
  return [
    ['Graphics', joined([profile?.gpu || about?.graphics || driver?.card, about?.videoMemoryGb ? `${Math.round(about.videoMemoryGb)} GB` : null])],
    ['Driver', joined([driver?.installed, about?.directX ? `DirectX ${about.directX}` : null])],
    ['Resizable BAR', onOff(about?.resizableBar)],
  ]
}

/** Windows and its build, what anti-cheat and Windows 11 ask of the PC on one row, and the connection in use. */
function windows(about: PcAbout | null): Fact[] {
  if (!about) return []
  const security = [about.secureBoot == null ? null : `Secure Boot ${onOff(about.secureBoot)!.toLowerCase()}`, about.tpm ? `TPM ${about.tpm}` : null,
    about.memoryIntegrity == null ? null : `Memory integrity ${onOff(about.memoryIntegrity)!.toLowerCase()}`]
  return [
    ['Windows', joined([about.windows ? joined([about.windows, about.windowsVersion], ' ') : null, about.build])],
    ['Security', joined(security)],
    ['Network', about.network ? joined([about.network, rate(about.networkMbps)]) : null],
  ]
}

/** The main screen on one row: its name, refresh, variable refresh as the driver reports it, and HDR. */
function screen({ profile, hdr }: PcState): Fact[] {
  const main = profile?.screen
  const hdrWords = hdr ? (hdr.supported ? (hdr.peakNits ? `HDR up to ${hdr.peakNits} nits` : 'HDR') : 'No HDR') : null
  return [['Screen', joined([main?.name, main?.hz ? `${main.hz} Hz` : null, profile?.vendor && main?.vrr ? SYNC[profile.vendor] : null, hdrWords])]]
}
/**
 * The About card: this PC by what it's made of (processor, graphics, memory, drives, motherboard and BIOS), then
 * Windows, its security and connection, then the main screen, in groups between hairlines; the words in the shine until
 * the room has read them.
 */
function Hero({ state }: { state: PcState }) {
  const { about, profile, driver } = state
  const [processor, ...paired] = madeOf(about)
  const groups = [processor ?? [], graphics(state), ...paired, windows(about), screen(state)]
  const known = groups.map(group => group.filter((fact): fact is [string, string] => !!fact[1])).filter(group => group.length > 0)
  return (
    <section className="exo-system-panel exo-pc-hero" aria-label="This PC">
      <span className="exo-pc-hero-mark" aria-hidden="true"><Cpu size={44} /></span>
      {about || profile || driver ? (
        <>
          <h1 className="exo-pc-hero-name">{about?.name || profile?.gpu || driver?.card || 'This PC'}</h1>
          {known.map(group => (
            <dl key={group[0][0]} className="exo-pc-hero-facts">
              {group.map(([label, value]) => <div key={label}><dt>{label}</dt><dd>{value}</dd></div>)}
            </dl>
          ))}
        </>
      ) : <Skeleton label="Reading this PC" rows={3} className="exo-pc-hero-wait" />}
    </section>
  )
}
