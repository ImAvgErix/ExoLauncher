import { useEffect, useRef, useState, type CSSProperties, type ReactNode } from 'react'
import type { MouseStatus } from '@openmouse/protocol/drivers/mouse-types'
import { can, useMouse } from './mouse'
import type { Section } from './section'
import { FinalmousePanels, finalmouseCustomLod, finalmouseProfileKey, finalmouseUnread } from './Finalmouse'
import { AtkMiscPanels } from './brands/atk-misc'
import { CoreExtrasPanels, coreExtrasReplaces } from './brands/core-extras'
import { EndgamePulsarPanels } from './brands/endgame-pulsar'
import { LogitechPanels, logitechOwnsLiftOff } from './brands/logitech'
import { MagneticToolsPanels } from './brands/magnetic-tools'
import { Profiles } from './Profiles'
import { deviceImage } from './device-images'
import { signalLevel, signalWord, useMouseAlerts } from './alerts'
import { useSections } from './layout'
import { ColorField, NumberField, Panel, Row, Segmented, Select, Slider, Switch } from './ui'

export function App() {
  return <Room mouse={useMouse()} />
}

/** The room for whatever state the mouse is in; App feeds it the real mouse. */
export function Room({ mouse }: { mouse: ReturnType<typeof useMouse> }) {
  useMouseAlerts(mouse.phase === 'ready' ? mouse.status : null)
  if (mouse.phase === 'looking' || mouse.phase === 'opening') {
    return <Centered title={mouse.phase === 'opening' ? `Connecting to ${mouse.current?.name ?? 'your mouse'}` : 'Looking for your mouse'} working />
  }
  if (mouse.phase === 'none') {
    return <Centered title="No supported mouse found" line={mouse.error ?? 'Plug in your mouse or its receiver.'} />
  }
  if (mouse.phase === 'error' || !mouse.status) {
    return (
      <Centered title="Exo could not reach your mouse" line={mouse.error ?? undefined}>
        <button type="button" className="m-button" onClick={mouse.retry}>Try again</button>
      </Centered>
    )
  }
  const section: Section = { status: mouse.status, client: mouse.client, apply: mouse.apply, applyAll: mouse.applyAll, busy: mouse.busy }
  return (
    <main className="m-room">
      <Device status={mouse.status} brand={mouse.brand} readAt={mouse.readAt} busy={mouse.busy} error={mouse.error}>
        {mouse.candidates.length > 1 ? (
          <Segmented
            label="Device"
            options={mouse.candidates.map(c => ({ value: c.id, label: c.name }))}
            value={mouse.current?.id}
            onChange={mouse.choose}
          />
        ) : null}
      </Device>
      <Settings key={mouse.current?.id}>
        <Sensitivity {...section} />
        <CoreExtrasPanels {...section} />
        <Performance {...section} />
        <FinalmousePanels {...section} />
        <EndgamePulsarPanels {...section} />
        <AtkMiscPanels {...section} />
        <LogitechPanels {...section} />
        <MagneticToolsPanels {...section} />
        <Buttons {...section} />
        <Lighting {...section} />
        <Power {...section} />
        {mouse.current ? <Profiles device={finalmouseProfileKey(mouse.current.id)} status={mouse.status} client={mouse.client} applyAll={mouse.applyAll} busy={mouse.busy} /> : null}
      </Settings>
    </main>
  )
}

/** Columns a room can hold at most; one break ends each but the last. */
const BREAKS = [0, 1, 2]

/**
 * The settings in whole sections beside the mouse, with no scrolling
 * (layout.ts). Each panel rises in turn (Exo's arrive, 28 ms apart, after the
 * mouse) when a mouse opens.
 */
function Settings({ children }: { children: ReactNode }) {
  const { box, stack } = useSections()
  return (
    <div ref={box} className="m-settings">
      <div ref={stack} className="m-grid">
        {children}
        {BREAKS.map(i => <span key={i} className="m-break" aria-hidden="true" />)}
      </div>
    </div>
  )
}

function Centered({ title, line, working = false, children }: { title: string; line?: string; working?: boolean; children?: ReactNode }) {
  return (
    <main className="m-center m-switch-in" role="status">
      <p className={`m-center-title${working ? ' m-work-text' : ''}`}>{title}</p>
      {line ? <p className="m-center-line">{line}</p> : null}
      {children}
    </main>
  )
}

/** Every version the mouse reports, one per line ("Mouse 1.1.1", "Receiver RF 1.1.1"); the room calls the dongle the receiver. */
function firmwareLines(status: MouseStatus): string[] {
  return (status.firmware ?? []).map(v => v.trim().replace(/^Dongle\b/i, 'Receiver')).filter(Boolean)
}

/** A received-signal figure some drivers put in the link line ("2.4 GHz receiver · -42 dBm"); the hero shows it with the signal. */
const DBM = /\s*[·,-]?\s*(-\d+)\s*dBm/

/** How the mouse is linked, once: the driver's own words without the dBm, or Wired/Wireless when it gives none. */
function linkLine(status: MouseStatus): string | null {
  return status.connectionDetail?.replace(DBM, '').trim() || status.connectionType || null
}

/**
 * The polling rate as a fact, when it is not a choice: the mouse fixes it
 * (the Starlight X's PerfectPolling) and no brand panel draws it. A rate the
 * user can pick stays in Performance.
 */
function fixedPollingRate(status: MouseStatus): number | null {
  const fixed = !!status.ui?.pollingReadOnly || status.supportedPollingRates?.length === 0
  return fixed && status.eggGlassMode !== true && status.pollingRateHz > 0 ? status.pollingRateHz : null
}

/** A read older than this (the room reads every few seconds) says when it was, so an asleep mouse's figures are not taken for live ones. */
const STALE_MS = 30_000

function ago(ms: number): string {
  const min = Math.round(ms / 60_000)
  return min < 1 ? 'under a minute ago' : min < 60 ? `${min} min ago` : `${Math.round(min / 60)} h ago`
}

/** The time since the last read, checked every 10 s while the hero is on screen. */
function useStale(readAt: number): string | null {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 10_000)
    return () => window.clearInterval(timer)
  }, [])
  const age = Math.max(now, readAt) - readAt
  return readAt > 0 && age > STALE_MS ? `Last read ${ago(age)}` : null
}

/** How long Exo's done (the dim, the green ring, the check) holds after a change lands. */
const DONE_MS = 3000

/**
 * A change just landed: true from the moment a write finishes without an
 * error until the done motion has played. Each landing restarts it.
 */
function useLanded(busy: boolean, error: string | null): number {
  const [landed, setLanded] = useState(0)
  const was = useRef(busy)
  const timer = useRef(0)
  useEffect(() => () => window.clearTimeout(timer.current), [])
  useEffect(() => {
    const finished = was.current && !busy
    was.current = busy
    if (!finished || error) return
    setLanded(n => n + 1)
    window.clearTimeout(timer.current)
    timer.current = window.setTimeout(() => setLanded(0), DONE_MS)
  }, [busy, error])
  return landed
}

/**
 * The mouse itself, as the room's hero: its picture (OpenMouse's render,
 * when there is one) in the width the settings leave, its name and how it is
 * linked, and a readout of what the mouse reports but no panel shows:
 * battery, signal, a fixed polling rate and firmware. Each fact shows once.
 * A light band crosses the picture while a change is being written and a
 * check draws when it lands; the last change that did not take floats at the
 * picture's foot, so nothing moves.
 */
function Device({ status, brand, readAt, busy, error, children }: { status: MouseStatus; brand: string; readAt: number; busy: boolean; error: string | null; children?: ReactNode }) {
  const image = deviceImage(status.name)
  const [missing, setMissing] = useState<string | null>(null)
  const landed = useLanded(busy, error)
  const stale = useStale(readAt)
  const shown = image && missing !== image ? image : null
  const meta = [status.name.toLowerCase().includes(brand.toLowerCase()) ? null : brand, linkLine(status), stale].filter(Boolean).join(' · ')
  const firmware = firmwareLines(status)
  const polling = fixedPollingRate(status)
  return (
    <aside className="m-hero m-arrive" style={{ '--i': 0 } as CSSProperties}>
      {children ? <div className="m-hero-pick">{children}</div> : null}
      <div className={`m-hero-art${busy ? ' m-work' : ''}`}>
        {shown ? (
          <>
            <img className="m-hero-wash" src={shown} alt="" draggable={false} aria-hidden="true" />
            <img className="m-hero-image" src={shown} alt="" draggable={false} onError={() => setMissing(shown)} />
          </>
        ) : <span className="m-hero-image is-empty" />}
        {landed ? (
          <span key={landed} className="m-done" aria-hidden="true">
            <svg className="m-done-check" viewBox="0 0 24 24"><path d="M5 12l5 5l10 -10" pathLength="1" /></svg>
          </span>
        ) : null}
        {error ? <p className="m-error" role="alert">{error}</p> : null}
      </div>
      <div className="m-hero-id">
        <h1 className="m-name">{status.name}</h1>
        {meta ? <p className="m-meta">{meta}</p> : null}
      </div>
      <div className="m-stats">
        <Battery status={status} />
        <Signal status={status} />
        {polling ? (
          <div className="m-stat">
            <span className="m-stat-label">Polling rate<span className="m-stat-detail">Fixed</span></span>
            <span className="m-stat-figure">{polling >= 1000 ? `${polling / 1000}K` : polling}<span className="m-stat-unit">Hz</span></span>
          </div>
        ) : null}
        {firmware.length ? (
          <div className="m-stat">
            <span className="m-stat-label">Firmware</span>
            {firmware.map(line => <span key={line} className="m-stat-line">{line}</span>)}
          </div>
        ) : null}
      </div>
    </aside>
  )
}

/**
 * The charge as a figure over a gauge, red when low. Beside its name, what
 * the mouse says of it: Charging in green while it charges, Full or Almost
 * full, otherwise the cell's voltage when the driver reads it.
 */
function Battery({ status }: { status: MouseStatus }) {
  const battery = status.batteryPercent
  if (battery == null) return null
  const state = status.batteryState
  const charging = state.startsWith('Charging')
  const mv = status.batteryVoltageMv
  const detail = charging ? <span className="m-stat-good">{state}</span>
    : state === 'Full' || state === 'Almost full' ? <span className="m-stat-detail">{state}</span>
      : mv ? <span className="m-stat-detail">{(mv / 1000).toFixed(2)} V</span> : null
  return (
    <div className={`m-stat${battery <= 15 && !charging ? ' is-low' : ''}`} aria-label={`Battery ${battery}%${charging ? ', charging' : ''}`}>
      <span className="m-stat-label">Battery{detail}</span>
      <span className="m-stat-figure">{battery}<span className="m-stat-unit">%</span></span>
      <span className="m-gauge" aria-hidden="true">
        <span className="m-gauge-fill" style={{ width: `${Math.max(2, Math.min(100, battery))}%` }} />
      </span>
    </div>
  )
}

/** The wireless signal as a word over four segments, its dBm beside the name when the mouse gives it; nothing on a wired mouse or one that does not report it. */
function Signal({ status }: { status: MouseStatus }) {
  const level = signalLevel(status)
  if (level == null) return null
  const bars = Math.max(1, Math.round(level * 4))
  const dbm = DBM.exec(status.connectionDetail ?? '')?.[1]
  return (
    <div className={`m-stat${level <= 0.25 ? ' is-low' : ''}`} aria-label={`Signal ${signalWord(level).toLowerCase()}${dbm ? `, ${dbm} dBm` : ''}`}>
      <span className="m-stat-label">Signal{dbm ? <span className="m-stat-detail">{dbm} dBm</span> : null}</span>
      <span className="m-stat-figure">{signalWord(level)}</span>
      <span className="m-gauge is-steps" aria-hidden="true">
        {[1, 2, 3, 4].map(i => <span key={i} className={`m-gauge-step${i <= bars ? ' is-on' : ''}`} />)}
      </span>
    </div>
  )
}

// ------------------------------------------------------------------ sensitivity

/** Brand fields the protocol's MouseStatus type may not name; read loosely, only to decide what to hide. */
const has = (status: MouseStatus, key: string) => (status as unknown as Record<string, unknown>)[key] != null

function Sensitivity(section: Section) {
  const { status, client, apply, busy } = section
  // Endgame Gear sets CPI per stage on its own grid; the core DPI card replaces this one where it applies.
  if (coreExtrasReplaces(section).sensitivity || has(status, 'eggCpiStages') || has(status, 'otCpiStages')) return null
  const editor = status.ui?.dpiStageEditor
  const min = editor?.minDpi ?? 50
  const max = Math.max(editor?.maxDpi ?? 26_000, status.dpi)
  const step = editor?.stepDpi ?? 50
  const stages = status.dpiStages ?? []
  const canDpi = can(client, 'setDpi')
  if (!canDpi && stages.length === 0) return null
  return (
    <Panel title="Sensitivity">
      {canDpi ? (
        <Row label="DPI">
          <span className="m-dpi">
            <Slider label="DPI" min={min} max={Math.min(max, 6400)} step={step} value={Math.min(status.dpi, 6400)} onCommit={v => void apply('setDpi', v)} disabled={busy} />
            <NumberField label="DPI" min={min} max={max} step={editor?.stepDpi ?? 1} value={status.dpi} onCommit={v => void apply('setDpi', v)} disabled={busy} />
          </span>
        </Row>
      ) : null}
      {stages.length > 0 ? (
        <Row label="Stages">
          <Segmented
            label="DPI stage"
            options={stages.map((dpi, i) => ({ value: i, label: String(dpi), color: status.dpiStageColors?.[i], disabled: !can(client, 'setActiveDpiStage') }))}
            value={status.activeDpiStage}
            onChange={i => void apply('setActiveDpiStage', i)}
            disabled={busy}
          />
        </Row>
      ) : null}
    </Panel>
  )
}

// ------------------------------------------------------------------ performance

const POLLING = [125, 250, 500, 1000, 2000, 4000, 8000]
/** The Starlight X's lift-off presets, raw 2 and 3 on the wire (xpanel). */
const PAW_PRESETS = [{ value: 1, label: '1 mm' }, { value: 2, label: '2 mm' }] as const
const DEBOUNCE = [0, 1, 2, 4, 8, 12, 16]

function hz(v: number): string {
  return v >= 1000 ? `${v / 1000}K` : String(v)
}

function Performance(section: Section) {
  const { status, client, apply, busy } = section
  const ui = status.ui ?? {}
  // Rows a brand panel draws itself, or that the mouse refuses in its current mode.
  const brandPolling = status.eggGlassMode === true
  const brandLiftOff = has(status, 'eggLodOptions') || coreExtrasReplaces(section).liftOff || logitechOwnsLiftOff(status)
  const brandDebounce = status.atkSensorMode != null || status.lunafury != null
  const rates = status.supportedPollingRates ?? POLLING
  // A fixed rate (Starlight X PerfectPolling) is a value, not a choice.
  const fixedRate = !!ui.pollingReadOnly || rates.length === 0
  const lods = (status.supportedLiftOffDistances ?? ['Low', 'Medium', 'High']).filter(v => !((ui.hideLodLow || ui.family === 'egg-ot') && v === 'Low'))
  const processing = !ui.hideProcessingCard
  const paw = status.finalmousePawLodMm
  // A Starlight X always has its own lift-off row; one whose distance was not read shows the presets unpicked.
  const slx = !!status.finalmouseIsSlx
  const custom = finalmouseCustomLod(status)
  // A fixed rate (fixedPollingRate) is a fact the hero shows, not a row here.
  const rows = [
    !brandPolling && !fixedRate && can(client, 'setPollingRate') && (
      <Row key="poll" label="Polling rate">
        <Segmented label="Polling rate" options={rates.map(r => ({ value: r, label: hz(r) }))} value={status.pollingRateHz}
          onChange={v => void apply('setPollingRate', v)} disabled={busy} />
      </Row>
    ),
    !brandLiftOff && status.liftOffDistance != null && paw == null && !slx && can(client, 'setLiftOffDistance') && (
      <Row key="lod" label="Lift-off distance">
        <Segmented label="Lift-off distance" options={lods.map(v => ({ value: v, label: v }))} value={status.liftOffDistance}
          onChange={v => void apply('setLiftOffDistance', v)} disabled={busy} />
      </Row>
    ),
    (paw != null || slx) && can(client, 'setPawLodMm') && (
      <Row key="paw" label="Lift-off distance" value={paw == null ? finalmouseUnread(status) : custom.needs ?? undefined}>
        <PawLiftOff {...section} paw={paw ?? null} custom={custom.offered && can(client, 'setLiftOffScale')} />
      </Row>
    ),
    processing && !ui.hideMotionSync && status.motionSync != null && can(client, 'setMotionSync') && (
      <Row key="ms" label="Motion sync">
        <Switch label="Motion sync" checked={status.motionSync} onChange={v => void apply('setMotionSync', v)} disabled={busy} />
      </Row>
    ),
    processing && !ui.hideAngleSnapping && status.angleSnapping != null && can(client, 'setAngleSnapping') && (
      <Row key="as" label="Angle snapping">
        <Switch label="Angle snapping" checked={status.angleSnapping} onChange={v => void apply('setAngleSnapping', v)} disabled={busy} />
      </Row>
    ),
    processing && !ui.hideRippleControl && status.rippleControl != null && can(client, 'setRippleControl') && (
      <Row key="rc" label="Ripple control">
        <Switch label="Ripple control" checked={status.rippleControl} onChange={v => void apply('setRippleControl', v)} disabled={busy} />
      </Row>
    ),
    !brandDebounce && status.debounceMs != null && can(client, 'setDebounceTime') && (
      <Row key="db" label="Click debounce">
        <Segmented label="Click debounce" options={withCurrent(DEBOUNCE, status.debounceMs).map(v => ({ value: v, label: `${v} ms` }))}
          value={status.debounceMs} onChange={v => void apply('setDebounceTime', v)} disabled={busy} />
      </Row>
    ),
    status.powerModes?.length && can(client, 'setPowerMode') && (
      <Row key="pm" label="Power mode">
        <Segmented label="Power mode" options={status.powerModes.map(v => ({ value: v, label: v }))} value={status.powerMode}
          onChange={v => void apply('setPowerMode', v)} disabled={busy} />
      </Row>
    ),
    status.profileCount && status.profileCount > 1 && can(client, 'setProfile') && (
      <Row key="pr" label="Profile">
        <Segmented label="Profile" options={Array.from({ length: status.profileCount }, (_, i) => ({ value: i + 1, label: status.profileNames?.[i] || String(i + 1) }))}
          value={status.activeProfile} onChange={v => void apply('setProfile', v)} disabled={busy} />
      </Row>
    ),
  ].filter(Boolean)
  return rows.length ? <Panel title="Performance">{rows}</Panel> : null
}

/**
 * The Starlight X's lift-off as one control: its two presets in a pill and,
 * when its firmware has it, xpanel's custom distance (1.0-1.7 mm in 0.1 mm
 * steps, raw 10-17) on a slider beside them. Whichever the mouse runs is the
 * lit one; the other rests dim, the slider reading Custom.
 */
function PawLiftOff({ status, apply, busy, paw, custom }: Section & { paw: number | null; custom: boolean }) {
  const isCustom = paw != null && !!status.finalmousePawLodCustom
  const raw = Math.min(17, Math.max(10, Math.round((paw ?? 1) * 10)))
  return (
    <span className="m-lod">
      {custom ? (
        <span className={isCustom ? undefined : 'm-dim'}>
          <Slider label="Custom lift-off" min={10} max={17} step={1} value={raw}
            format={v => (isCustom || v !== raw ? `${(v / 10).toFixed(1)} mm` : 'Custom')}
            onCommit={v => void apply('setLiftOffScale', v)} disabled={busy} />
        </span>
      ) : null}
      <Segmented label="Lift-off distance" options={PAW_PRESETS} value={isCustom ? null : paw}
        onChange={v => void apply('setPawLodMm', v)} disabled={busy} />
    </span>
  )
}

/** A preset list that also holds the value the mouse reports, in order. */
function withCurrent(list: number[], current: number): number[] {
  return list.includes(current) ? list : [...list, current].sort((a, b) => a - b)
}

// ------------------------------------------------------------------ buttons, lighting, power

function Buttons({ status, client, apply, busy }: Section) {
  const mappings = status.buttonMappings
  const options = status.buttonOptions
  if (!mappings || !options?.length || !can(client, 'setButtonMapping')) return null
  return (
    <Panel title="Buttons">
      {Object.entries(mappings).map(([button, action]) => (
        <Row key={button} label={button}>
          <Select label={button} options={options} value={action} disabled={busy || !!status.fixedButtons?.includes(button)}
            onChange={v => void apply('setButtonMapping', button, v)} />
        </Row>
      ))}
    </Panel>
  )
}

function Lighting(section: Section) {
  const { status, client, apply, busy } = section
  const light = status.lighting
  if (!light || !can(client, 'setLighting') || coreExtrasReplaces(section).lighting) return null
  const set = (patch: Partial<typeof light>) => void apply('setLighting', { ...light, ...patch })
  return (
    <Panel title="Lighting">
      <Row label={light.zone}>
        <Segmented label="Effect" options={light.modes.map(m => ({ value: m, label: m }))} value={light.mode} onChange={m => set({ mode: m })} disabled={busy} />
      </Row>
      {light.mode && light.colorModes.includes(light.mode) ? (
        <Row label="Color">
          <ColorField label="Color" value={light.color ?? '#ffffff'} disabled={busy} onCommit={color => set({ color })} />
        </Row>
      ) : null}
      {light.brightnessLevels?.length ? (
        <Row label="Brightness">
          <Segmented label="Brightness" options={light.brightnessLevels.map(v => ({ value: v, label: `${v}%` }))} value={light.brightness ?? null}
            onChange={v => set({ brightness: v })} disabled={busy} />
        </Row>
      ) : null}
    </Panel>
  )
}

const SLEEP = [30, 60, 120, 300, 600, 900]

function seconds(v: number): string {
  return v < 60 ? `${v} s` : `${Math.round(v / 60)} min`
}

function Power({ status, client, apply, busy }: Section) {
  if (status.sleepTimeout == null || !can(client, 'setSleepTimeout') || status.ui?.hideSleepCard) return null
  return (
    <Panel title="Power">
      <Row label="Sleep after">
        <Segmented label="Sleep after" options={withCurrent(SLEEP, status.sleepTimeout).map(v => ({ value: v, label: seconds(v) }))}
          value={status.sleepTimeout} onChange={v => void apply('setSleepTimeout', v)} disabled={busy} />
      </Row>
    </Panel>
  )
}

