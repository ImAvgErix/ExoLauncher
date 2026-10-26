import type { ReactNode } from 'react'
import { Apps, ChevronRight, Cpu, GraphicsCard, Monitor, Rocket, Shaders } from '../../brand/icons'
import type { GpuProfile, LauncherSettings } from '../../lib/host'
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
  return games.length ? { line: 'Nothing to compile', state: { text: 'Up to date', good: true } } : { line: 'No games checked yet' }
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
  if (!fixes.length) return { sub: 'All good', line: 'Nothing to fix', state: { text: 'All good', good: true } }
  return { sub: `${fixes.length} to fix`, line: fixes.join(', '), state: { text: `${fixes.length} to fix` } }
}

const LINKS: { link: Exclude<PcLink, 'overview'>; label: string }[] = [
  { link: 'graphics', label: 'Graphics' }, { link: 'shaders', label: 'Shaders' }, { link: 'display', label: 'Display' },
  { link: 'performance', label: 'Performance' }, { link: 'apps', label: 'Apps' },
]

/**
 * PC › Overview: this PC's About card, as Apple's System Settings draws it, in Exo's glass (the card's glyph on a large
 * tile of the ink, its name, then what it runs as label and value rows); beside it Best settings for this PC, then one
 * row per page with where it stands, which opens it (Shaders opens Graphics at its Shaders).
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

/** The About card: what the room has read of the card and the screen, the words in the shine until it has. */
function Hero({ state: { profile, driver, hdr } }: { state: PcState }) {
  const screen = profile?.screen
  const facts: [string, string | null | undefined][] = [
    ['Driver', driver?.installed],
    ['Screen', screen?.name],
    ['Refresh rate', screen?.hz ? `${screen.hz} Hz` : null],
    ['Variable refresh', profile?.vendor && screen?.hz ? (screen.vrr ? SYNC[profile.vendor] : 'None reported') : null],
    ['HDR', hdr ? (hdr.supported ? (hdr.peakNits ? `Up to ${hdr.peakNits} nits` : 'Yes') : 'No') : null],
  ]
  const known = facts.filter((fact): fact is [string, string] => !!fact[1])
  return (
    <section className="exo-system-panel exo-pc-hero" aria-label="This PC">
      <span className="exo-pc-hero-mark" aria-hidden="true"><GraphicsCard size={44} /></span>
      {profile || driver ? (
        <>
          <h1 className="exo-pc-hero-name">{profile?.gpu || driver?.card || 'Graphics card'}</h1>
          <dl className="exo-pc-hero-facts">
            {known.map(([label, value]) => <div key={label}><dt>{label}</dt><dd>{value}</dd></div>)}
          </dl>
        </>
      ) : <Skeleton label="Reading this PC" rows={3} className="exo-pc-hero-wait" />}
    </section>
  )
}
