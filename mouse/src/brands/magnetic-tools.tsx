import { useCallback, useEffect, useRef, useState } from 'react'
import type { MagneticButtonStatus, MagneticButtonsStatus, MouseStatus } from '@openmouse/protocol/drivers/mouse-types'
import { can, type Step } from '../mouse'
import type { Section } from '../section'
import { Dropdown, Panel, Row, Segmented, Slider, Switch } from '../ui'
import { ClickTest, DepthMeter, HitsSpeedTest, Overlay, PollingTest } from './magnetic-tools-tests'
import './magnetic-tools.css'

// Adapted from OpenMouse (AGPL-3.0): src/app/MagneticButtons.tsx,
// src/app/Superstrike.tsx, src/hits-presets.ts and the matching parts of
// src/device/controller.ts. Logic kept, interface rewritten for Exo.

/**
 * Magnetic (Hall-effect / TMR) main buttons, Logitech's HITS tuning, and the
 * in-page tests: polling rate, clicks and bounce, and HITS speed.
 */
export function MagneticToolsPanels(section: Section) {
  return (
    <>
      <MagneticPanel {...section} />
      <HitsPanel {...section} />
      <TestsPanel {...section} />
    </>
  )
}

/** A client method that is not a setter: a listener the driver hands out, never a write. */
function listen(client: Section['client'], method: string, listener: (left: number, right: number) => void): () => void {
  const fn = (client as unknown as Record<string, ((l: (a: number, b: number) => void) => () => void) | undefined> | null)?.[method]
  return fn ? fn.call(client, listener) : () => undefined
}

const TOGETHER = [{ value: 'both', label: 'Together' }, { value: 'apart', label: 'Separate' }] as const
const clamp = (v: number, min: number, max: number) => Math.min(max, Math.max(min, v))

/* ------------------------------------------------------------------ magnetic switches */

function sameMagnetic(a: MagneticButtonStatus, b: MagneticButtonStatus): boolean {
  return a.switchType === b.switchType && a.triggerPoint === b.triggerPoint && (a.releasePoint ?? null) === (b.releasePoint ?? null)
    && a.rapidTrigger === b.rapidTrigger && a.rapidTriggerEnabled === b.rapidTriggerEnabled
}

// Release 9 is how the mouse says "follows the trigger point", so a separate release never lands on it.
const RELEASE_FOLLOWS = 9

function MagneticPanel(section: Section) {
  const { status, client } = section
  const magnetic = status.magneticButtons
  const [mode, setMode] = useState<'both' | 'apart' | null>(null)
  const [calibrating, setCalibrating] = useState(false)
  if (!magnetic || magnetic.buttons.length !== 2 || !can(client, 'setMagneticTriggerPoint') || !can(client, 'setMagneticRapidTrigger')) return null
  const [left, right] = magnetic.buttons
  const shown = mode ?? (sameMagnetic(left, right) ? 'both' : 'apart')
  const canCalibrate = magnetic.calibration !== null && can(client, 'calibrateMagneticButtons')
  return (
    <Panel title="Magnetic switches">
      {magnetic.liveDepth && can(client, 'onButtonDepth') ? <LiveDepth section={section} magnetic={magnetic} /> : null}
      <Row label="Buttons">
        <Segmented label="Buttons" options={TOGETHER} value={shown} onChange={setMode} />
      </Row>
      {shown === 'both'
        ? <MagneticControls section={section} magnetic={magnetic} targets={[0, 1]} prefix="" />
        : (
          <>
            <MagneticControls section={section} magnetic={magnetic} targets={[0]} prefix="Left " />
            <MagneticControls section={section} magnetic={magnetic} targets={[1]} prefix="Right " />
          </>
        )}
      {canCalibrate ? (
        <Row label="Calibration" value={magnetic.calibration === 'needed' ? 'Needed' : magnetic.calibration === 'calibrated' ? 'Calibrated' : undefined}>
          <button type="button" className="m-button" disabled={section.busy} onClick={() => setCalibrating(true)}>Calibrate</button>
        </Row>
      ) : null}
      {calibrating ? <Calibration section={section} onClose={() => setCalibrating(false)} /> : null}
    </Panel>
  )
}

function LiveDepth({ section, magnetic }: { section: Section; magnetic: MagneticButtonsStatus }) {
  const [depth, setDepth] = useState<[number, number]>([0, 0])
  const { client } = section
  useEffect(() => listen(client, 'onButtonDepth', (l, r) => setDepth([l, r])), [client])
  const span = magnetic.triggerPointRange.max
  const mark = (i: number) => {
    const trigger = magnetic.buttons[i]?.triggerPoint
    return trigger ? clamp(Math.round((trigger / span) * 10) - 1, 0, 9) : -1
  }
  return (
    <Row label="Press depth">
      <DepthMeter depth={depth} scale={100} marks={[mark(0), mark(1)]} />
    </Row>
  )
}

function MagneticControls({ section, magnetic, targets, prefix }: {
  section: Section
  magnetic: MagneticButtonsStatus
  targets: (0 | 1)[]
  prefix: string
}) {
  const { client, apply, applyAll, busy } = section
  const state = magnetic.buttons[targets[0]]
  const each = (method: string, args: (b: 0 | 1) => unknown[]) => {
    const steps: Step[] = targets.map(b => [method, args(b)])
    void (steps.length === 1 ? apply(method, ...steps[0][1]) : applyAll(steps))
  }
  const optical = state.switchType === 'optical'
  const { triggerPointRange: trigger, releasePointRange: release, rapidTriggerRange: rapid } = magnetic
  const name = (s: string) => (prefix ? `${prefix}${s.toLowerCase()}` : s)
  const rapidUnit = (v: number) => (magnetic.rapidTriggerUnit === 'ms' ? `${v} ms` : String(v))
  const rapidLevel = rapid ? clamp(state.rapidTrigger ?? rapid.min, rapid.min, rapid.max) : 0

  const rows = []
  // Both buttons' types go in one write, so an unread one is never guessed.
  if (magnetic.canChooseSwitchType && magnetic.buttons.every(b => b.switchType != null) && can(client, 'setMagneticSwitchTypes')) {
    const types = magnetic.buttons.map(b => b.switchType) as ['magnetic' | 'optical', 'magnetic' | 'optical']
    rows.push(
      <Row key="type" label={name('Switch')}>
        <Segmented
          label={name('Switch')}
          options={[
            { value: 'magnetic', label: 'Magnetic', disabled: magnetic.calibration === 'needed' },
            { value: 'optical', label: 'Optical' },
          ]}
          value={state.switchType}
          disabled={busy}
          onChange={type => {
            const next: ['magnetic' | 'optical', 'magnetic' | 'optical'] = [types[0], types[1]]
            for (const b of targets) next[b] = type
            void apply('setMagneticSwitchTypes', next)
          }}
        />
      </Row>,
    )
  }
  if (state.triggerPoint != null) {
    rows.push(
      <Row key="trigger" label={name('Actuation')}>
        <Slider label={name('Actuation')} min={trigger.min} max={trigger.max} step={1} value={clamp(state.triggerPoint, trigger.min, trigger.max)}
          format={v => String(v)} disabled={busy || optical} onCommit={v => each('setMagneticTriggerPoint', b => [b, v])} />
      </Row>,
    )
  } else {
    rows.push(<Row key="trigger" label={name('Actuation')}><span className="m-state">Unknown</span></Row>)
  }
  // An unread travel also reads as a null release, so null only means "follows" when the trigger was read.
  if (release && state.releasePoint !== undefined && state.triggerPoint != null && can(client, 'setMagneticReleasePoint')) {
    const separate = state.releasePoint != null
    const avoid = (v: number, from: number) => (v === RELEASE_FOLLOWS ? (from < RELEASE_FOLLOWS ? RELEASE_FOLLOWS + 1 : RELEASE_FOLLOWS - 1) : v)
    const first = clamp(avoid(state.triggerPoint ?? release.min, 0), release.min, release.max)
    rows.push(
      <Row key="release" label={name('Release point')} value={separate ? undefined : 'Follows actuation'}>
        <span className="mt-pair">
          {separate ? (
            <Slider label={name('Release point')} min={release.min} max={release.max} step={1} value={state.releasePoint as number}
              format={v => String(v)} disabled={busy || optical}
              onCommit={v => {
                const next = clamp(avoid(v, state.releasePoint as number), release.min, release.max)
                if (next !== state.releasePoint) each('setMagneticReleasePoint', b => [b, next])
              }} />
          ) : null}
          <Switch label={name('Separate release point')} checked={separate} disabled={busy || optical}
            onChange={on => each('setMagneticReleasePoint', b => [b, on ? first : null])} />
        </span>
      </Row>,
    )
  }
  if (rapid) {
    const on = state.rapidTriggerEnabled === true
    rows.push(
      <Row key="rapid" label={name('Rapid trigger')}>
        <span className="mt-pair">
          {on ? (
            <Slider label={name('Rapid trigger')} min={rapid.min} max={rapid.max} step={1} value={rapidLevel} format={rapidUnit}
              disabled={busy || optical} onCommit={v => each('setMagneticRapidTrigger', b => [b, true, v])} />
          ) : null}
          <Switch label={name('Rapid trigger')} checked={on} disabled={busy || optical}
            onChange={next => each('setMagneticRapidTrigger', b => [b, next, rapidLevel])} />
        </span>
      </Row>,
    )
  } else if (state.rapidTriggerEnabled != null) {
    // Read here, but this connection cannot change it (G-Wolves: cable only).
    rows.push(
      <Row key="rapid" label={name('Rapid trigger')}>
        <span className="m-state">{state.rapidTriggerEnabled ? (state.rapidTrigger != null ? `On, ${rapidUnit(state.rapidTrigger)}` : 'On') : 'Off'}</span>
      </Row>,
    )
  }
  return <>{rows}</>
}

function Calibration({ section, onClose }: { section: Section; onClose: () => void }) {
  const [phase, setPhase] = useState<'idle' | 'running' | 'done' | 'failed'>('idle')
  const [progress, setProgress] = useState({ message: '', left: 0, right: 0, step: 0, steps: 0 })
  const abort = useRef<AbortController | null>(null)
  useEffect(() => () => abort.current?.abort(), [])
  const run = async () => {
    abort.current = new AbortController()
    setPhase('running')
    setProgress({ message: 'Starting', left: 0, right: 0, step: 0, steps: 0 })
    const result = await section.apply('calibrateMagneticButtons', (p: typeof progress) => setProgress({ ...p }), abort.current.signal)
    abort.current = null
    setPhase(result ? 'done' : 'failed')
  }
  const close = () => { abort.current?.abort(); onClose() }
  return (
    <Overlay title="Calibration" onClose={close}>
      {phase === 'running' ? (
        <>
          <p className="mt-message">{progress.steps ? `Step ${progress.step} of ${progress.steps}. ` : ''}{progress.message}</p>
          <Row label="Press depth"><DepthMeter depth={[progress.left, progress.right]} scale={100} /></Row>
        </>
      ) : null}
      {phase === 'done' ? <p className="mt-message">Calibrated.</p> : null}
      {phase === 'failed' ? <p className="mt-message is-bad">Calibration did not finish.</p> : null}
      <div className="mt-actions">
        {phase === 'running'
          ? <button type="button" className="m-button" onClick={() => abort.current?.abort()}>Cancel</button>
          : <button type="button" className="m-button" disabled={section.busy} onClick={() => void run()}>{phase === 'idle' ? 'Start' : 'Run again'}</button>}
      </div>
    </Overlay>
  )
}

/* ------------------------------------------------------------------ HITS (Logitech hall-effect) */

type Tuning = NonNullable<MouseStatus['analogButtonTuning']>
type HitsValues = { actuation: number; rapidTrigger: number; rapidTriggerEnabled: boolean; haptics: number }
type HitsPreset = { name: string; left: HitsValues; right: HitsValues }

const side = (actuation: number, rapidTrigger: number, rapidTriggerEnabled: boolean, haptics: number): HitsValues =>
  ({ actuation, rapidTrigger, rapidTriggerEnabled, haptics })
const both = (a: number, r: number, on: boolean, h: number) => ({ left: side(a, r, on, h), right: side(a, r, on, h) })

/** Starting points from OpenMouse. Default is what Reset goes back to. */
const BUILT_IN: readonly HitsPreset[] = [
  { name: 'Default', ...both(5, 2, false, 3) },
  { name: 'Competitive', ...both(2, 1, true, 0) },
  { name: 'Balanced', ...both(5, 2, true, 3) },
  { name: 'Casual', ...both(7, 3, false, 3) },
]

/** The pro player profiles G HUB lists: actuation, rapid trigger, haptics. */
const PRO: readonly HitsPreset[] = [
  { name: 'Whzy', left: side(1, 1, true, 2), right: side(4, 1, false, 2) },
  { name: 'ShowMaker', ...both(2, 1, true, 2) },
  { name: 'MrFaliN', left: side(4, 2, true, 3), right: side(5, 2, true, 5) },
  { name: 'Gryffinn', ...both(2, 2, true, 2) },
  { name: 'm0NESY', ...both(5, 4, true, 5) },
]

const PRESET_KEY = 'exo-mouse-hits-presets'

function fits(v: HitsValues | undefined, t: Tuning): boolean {
  if (typeof v !== 'object' || v === null) return false
  return Number.isInteger(v.actuation) && v.actuation >= 1 && v.actuation <= t.maxActuation
    && Number.isInteger(v.rapidTrigger) && v.rapidTrigger >= 1 && v.rapidTrigger <= t.maxRapidTrigger
    && Number.isInteger(v.haptics) && v.haptics >= 0 && v.haptics <= t.maxHaptics
    && typeof v.rapidTriggerEnabled === 'boolean'
}
const presetFits = (p: Pick<HitsPreset, 'left' | 'right'>, t: Tuning) => fits(p.left, t) && fits(p.right, t)

/** A rapid trigger step that is switched off does nothing, so it is not compared. */
function matches(a: Pick<HitsPreset, 'left' | 'right'>, b: Pick<HitsPreset, 'left' | 'right'>): boolean {
  const s = (x: HitsValues, y: HitsValues) => x.actuation === y.actuation && x.haptics === y.haptics
    && x.rapidTriggerEnabled === y.rapidTriggerEnabled && (!x.rapidTriggerEnabled || x.rapidTrigger === y.rapidTrigger)
  return s(a.left, b.left) && s(a.right, b.right)
}

const WIDEST: Tuning = { maxActuation: 63, maxRapidTrigger: 63, maxHaptics: 63, buttons: [] }

function loadPresets(): HitsPreset[] {
  try {
    const parsed: unknown = JSON.parse(localStorage.getItem(PRESET_KEY) ?? '[]')
    if (!Array.isArray(parsed)) return []
    return parsed.filter((e): e is HitsPreset => typeof e === 'object' && e !== null
      && typeof (e as HitsPreset).name === 'string' && (e as HitsPreset).name !== '' && presetFits(e as HitsPreset, WIDEST))
  } catch {
    return []
  }
}

function storePresets(list: HitsPreset[]): boolean {
  try {
    localStorage.setItem(PRESET_KEY, JSON.stringify(list))
    return true
  } catch {
    return false
  }
}

function values(b: Tuning['buttons'][number]): HitsValues {
  return { actuation: b.actuation, rapidTrigger: b.rapidTrigger, haptics: b.haptics, rapidTriggerEnabled: b.rapidTriggerEnabled !== false }
}

/**
 * The live write for each button, then (on a mouse running from a writable
 * onboard profile) one profile write so the values survive a power cycle,
 * as OpenMouse's controller does.
 */
function hitsSteps(status: MouseStatus, client: Section['client'], entries: { button: 0 | 1; tuning: HitsValues }[], keepSwitch: boolean): Step[] {
  const wire = (t: HitsValues) => keepSwitch
    ? { actuation: t.actuation, rapidTrigger: t.rapidTrigger, haptics: t.haptics }
    : { actuation: t.actuation, rapidTrigger: t.rapidTrigger, haptics: t.haptics, rapidTriggerEnabled: t.rapidTriggerEnabled }
  const steps: Step[] = entries.map(e => ['setAnalogButtonTuning', [e.button, wire(e.tuning)]])
  if (status.deviceMode === 'Onboard' && status.onboardProfileFormat?.writable === true && can(client, 'persistAnalogButtonTuning')) {
    steps.push(['persistAnalogButtonTuning', [entries.map(e => ({ button: e.button, ...wire(e.tuning) }))]])
  }
  return steps
}

/** Writes HITS tuning for the given buttons; null for a button leaves it as it is. */
function useHitsWriter(section: Section) {
  const { status, client, applyAll } = section
  const tuning = status.analogButtonTuning
  // A mouse that does not report the rapid trigger switch keeps its own state.
  const keepSwitch = !tuning || tuning.buttons.some(b => b.rapidTriggerEnabled === undefined)
  return (next: [HitsValues | null, HitsValues | null]) => {
    const entries = ([0, 1] as const).flatMap(b => (next[b] ? [{ button: b, tuning: next[b] as HitsValues }] : []))
    if (entries.length) void applyAll(hitsSteps(status, client, entries, keepSwitch))
  }
}

function HitsPanel(section: Section) {
  const { status, client, busy } = section
  const tuning = status.analogButtonTuning
  const [mode, setMode] = useState<'both' | 'apart' | null>(null)
  const [presets, setPresets] = useState<HitsPreset[]>(loadPresets)
  const [naming, setNaming] = useState<string | null>(null)
  const write = useHitsWriter(section)
  if (!tuning || tuning.buttons.length !== 2 || !can(client, 'setAnalogButtonTuning')) return null

  const now = { left: values(tuning.buttons[0]), right: values(tuning.buttons[1]) }
  const shown = mode ?? (matches(now, { left: now.right, right: now.left }) && now.left.rapidTrigger === now.right.rapidTrigger ? 'both' : 'apart')
  const mine = presets.find(p => matches(p, now))
  const builtIn = [...BUILT_IN, ...PRO].find(p => matches(p, now))
  const selected = mine ? `mine:${mine.name}` : builtIn ? `built:${builtIn.name}` : ''

  const load = (p: Pick<HitsPreset, 'left' | 'right'>) => { if (presetFits(p, tuning)) write([{ ...p.left }, { ...p.right }]) }
  const save = (name: string) => {
    const clean = name.trim().slice(0, 40)
    if (!clean) return
    const next = [...presets.filter(p => p.name !== clean), { name: clean, ...now }].slice(-20)
    if (storePresets(next)) setPresets(next)
    setNaming(null)
  }
  const remove = (name: string) => {
    const next = presets.filter(p => p.name !== name)
    if (storePresets(next)) setPresets(next)
  }

  return (
    <Panel title="HITS">
      <Row label="Preset">
        <Dropdown label="Preset" value={selected} disabled={busy} placeholder="Custom"
          options={[
            { value: '', label: 'Custom' },
            ...BUILT_IN.filter(p => presetFits(p, tuning)).map(p => ({ value: `built:${p.name}`, label: p.name, group: 'Built in' })),
            ...PRO.filter(p => presetFits(p, tuning)).map(p => ({ value: `built:${p.name}`, label: p.name, group: 'Pro players' })),
            ...presets.map(p => ({ value: `mine:${p.name}`, label: p.name, group: 'Yours' })),
          ]}
          onChange={v => {
            const p = v.startsWith('mine:') ? presets.find(x => `mine:${x.name}` === v) : [...BUILT_IN, ...PRO].find(x => `built:${x.name}` === v)
            if (p) load(p)
          }} />
      </Row>
      {naming !== null ? (
        <Row label="Save as">
          <span className="mt-pair">
            <input className="m-text" aria-label="Preset name" maxLength={40} value={naming} autoFocus
              onChange={e => setNaming(e.target.value)}
              onKeyDown={e => { if (e.key === 'Enter') save(naming); if (e.key === 'Escape') setNaming(null) }} />
            <button type="button" className="m-button" disabled={!naming.trim()} onClick={() => save(naming)}>Save</button>
            <button type="button" className="m-link" onClick={() => setNaming(null)}>Cancel</button>
          </span>
        </Row>
      ) : null}
      <Row label="Buttons">
        <Segmented label="Buttons" options={TOGETHER} value={shown} onChange={setMode} />
      </Row>
      {shown === 'both'
        ? <HitsControls tuning={tuning} state={now.left} prefix="" busy={busy} onChange={v => write([v, v])} />
        : (
          <>
            <HitsControls tuning={tuning} state={now.left} prefix="Left " busy={busy} onChange={v => write([v, null])} />
            <HitsControls tuning={tuning} state={now.right} prefix="Right " busy={busy} onChange={v => write([null, v])} />
          </>
        )}
      <div className="m-row">
        <span className="m-row-label" />
        <span className="mt-links">
          {shown === 'apart' ? (
            <>
              <button type="button" className="m-link" disabled={busy} onClick={() => load({ left: now.left, right: now.left })}>Copy left to right</button>
              <button type="button" className="m-link" disabled={busy} onClick={() => load({ left: now.right, right: now.left })}>Swap</button>
            </>
          ) : null}
          <button type="button" className="m-link" disabled={busy} onClick={() => load(BUILT_IN[0])}>Reset</button>
          {mine ? <button type="button" className="m-link" onClick={() => remove(mine.name)}>Delete preset</button> : null}
          <button type="button" className="m-link" onClick={() => setNaming('')}>Save preset</button>
        </span>
      </div>
    </Panel>
  )
}

function HitsControls({ tuning, state, prefix, busy, onChange }: {
  tuning: Tuning
  state: HitsValues
  prefix: string
  busy: boolean
  onChange: (v: HitsValues) => void
}) {
  const name = (s: string) => (prefix ? `${prefix}${s.toLowerCase()}` : s)
  const reportsSwitch = tuning.buttons.every(b => b.rapidTriggerEnabled !== undefined)
  const rapidOn = !reportsSwitch || state.rapidTriggerEnabled
  return (
    <>
      <Row label={name('Actuation')}>
        <Slider label={name('Actuation')} min={1} max={tuning.maxActuation} step={1} value={clamp(state.actuation, 1, tuning.maxActuation)}
          format={v => String(v)} disabled={busy} onCommit={v => onChange({ ...state, actuation: v })} />
      </Row>
      <Row label={name('Rapid trigger')}>
        <span className="mt-pair">
          {rapidOn ? (
            <Slider label={name('Rapid trigger')} min={1} max={tuning.maxRapidTrigger} step={1} value={clamp(state.rapidTrigger, 1, tuning.maxRapidTrigger)}
              format={v => String(v)} disabled={busy} onCommit={v => onChange({ ...state, rapidTrigger: v })} />
          ) : null}
          {reportsSwitch ? (
            <Switch label={name('Rapid trigger')} checked={state.rapidTriggerEnabled} disabled={busy}
              onChange={on => onChange({ ...state, rapidTriggerEnabled: on })} />
          ) : null}
        </span>
      </Row>
      <Row label={name('Click haptics')}>
        <Slider label={name('Click haptics')} min={0} max={tuning.maxHaptics} step={1} value={clamp(state.haptics, 0, tuning.maxHaptics)}
          format={v => (v === 0 ? 'Off' : String(v))} disabled={busy} onCommit={v => onChange({ ...state, haptics: v })} />
      </Row>
    </>
  )
}

/* ------------------------------------------------------------------ tests */

type Test = 'polling' | 'clicks' | 'hits'

function TestsPanel(section: Section) {
  const { status, client } = section
  const [open, setOpen] = useState<Test | null>(null)
  const tuning = status.analogButtonTuning
  const hits = !!tuning && tuning.buttons.length === 2 && can(client, 'setAnalogButtonTuning')
    && can(client, 'onAnalogPress') && can(client, 'startAnalogPressStream') && can(client, 'stopAnalogPressStream')
  const close = useCallback(() => setOpen(null), [])
  const run = (test: Test, name: string) => <button type="button" className="m-button" onClick={() => setOpen(test)}>{name}</button>
  // The polling and click tests live on the page, ready to use: their pads are what you test on.
  // HITS speed needs the press stream armed, so it stays a button that opens over the room.
  return (
    <>
      <Panel title="Polling test"><PollingTest reportedHz={status.pollingRateHz || null} /></Panel>
      <Panel title="Click test"><ClickTest /></Panel>
      {hits ? (
        <Panel title="HITS speed" actions={run('hits', 'Start')}>
          {open === 'hits' ? <HitsTest section={section} onClose={close} /> : null}
        </Panel>
      ) : null}
    </>
  )
}

/** Arms the mouse's press-depth stream while open (re-armed every 20 s, as G HUB's page does) and disarms on close. */
function HitsTest({ section, onClose }: { section: Section; onClose: () => void }) {
  const { client, status, busy } = section
  const apply = useRef(section.apply)
  apply.current = section.apply
  const write = useHitsWriter(section)
  useEffect(() => {
    void apply.current('startAnalogPressStream')
    const keepalive = window.setInterval(() => void apply.current('startAnalogPressStream'), 20_000)
    // A right-click test would otherwise pop the context menu.
    const menu = (e: MouseEvent) => e.preventDefault()
    window.addEventListener('contextmenu', menu)
    return () => {
      window.clearInterval(keepalive)
      window.removeEventListener('contextmenu', menu)
      void apply.current('stopAnalogPressStream')
    }
  }, [])
  const subscribe = useCallback((listener: (l: number, r: number) => void) => listen(client, 'onAnalogPress', listener), [client])
  const tuning = status.analogButtonTuning as Tuning
  const left = values(tuning.buttons[0])
  const right = values(tuning.buttons[1])
  const together = matches({ left, right }, { left: right, right: left }) && left.rapidTrigger === right.rapidTrigger
  return (
    <Overlay title="HITS speed" onClose={onClose}>
      <HitsSpeedTest
        subscribe={subscribe}
        actuation={[left.actuation, right.actuation]}
        maxActuation={tuning.maxActuation}
        busy={busy}
        onAdjust={(b, v) => {
          if (together) write([{ ...left, actuation: v }, { ...right, actuation: v }])
          else write(b === 0 ? [{ ...left, actuation: v }, null] : [null, { ...right, actuation: v }])
        }}
      />
    </Overlay>
  )
}
