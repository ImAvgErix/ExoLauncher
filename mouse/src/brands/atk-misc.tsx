import { useEffect, useRef, useState, type ReactNode } from 'react'
import type { MouseStatus } from '@openmouse/protocol/drivers/mouse-types'
import { can } from '../mouse'
import type { Section } from '../section'
import { Dropdown, NumberField, Panel, Row, Segmented, Slider, Switch } from '../ui'
import './atk-misc.css'

/**
 * The smaller brands' own settings, adapted from OpenMouse (AGPL-3.0):
 * ATK / VXE (onboard bank, stored buttons, F1 Ultimate sensor mode,
 * scroll anti-mistouch, receiver and its light, R1 SE+ pairing), LunaFury
 * (Lightning trigger, per-button latency, wheel guard), Ninjutso (system mode,
 * optical engine, HyperClick, Slam-click), Incott (receiver light, rapid fire)
 * and K-snake (scroll direction, onboard macros). Every row shows only when
 * the mouse reported the value and its driver has the setter.
 */
export function AtkMiscPanels(section: Section) {
  return (
    <>
      <AtkPanels {...section} />
      <LunaFuryPanels {...section} />
      <NinjutsoPanels {...section} />
      <IncottPanels {...section} />
      <KsnakePanels {...section} />
    </>
  )
}

/** A select over numbers, for lists too long for a segmented control. */
function NumSelect({ label, options, value, onChange, disabled = false }: {
  label: string
  options: readonly { value: number; label: string }[]
  value: number
  onChange: (v: number) => void
  disabled?: boolean
}) {
  return <Dropdown label={label} options={options} value={value} onChange={onChange} disabled={disabled} />
}

const ms = (v: number) => `${v} ms`

// ------------------------------------------------------------------ ATK / VXE

/** The VXE R1 SE+ receiver, the one receiver whose pairing OpenMouse verified (CID 02, MID 20). */
const SE_PLUS_RECEIVER = { vendorId: 0x3554, productId: 0xf58e, cid: 0x02, mid: 0x20 } as const

const ATK_SENSOR = ['Basic', 'Shard', 'Shard max'] as const
const ATK_DONGLE_LIGHT = [
  { value: 0, label: 'Off' },
  { value: 1, label: 'Polling rate' },
  { value: 2, label: 'Battery' },
  { value: 3, label: 'Low battery' },
] as const

function AtkPanels(section: Section) {
  return (
    <>
      <AtkOnboard {...section} />
      <AtkSensor {...section} />
      <AtkReceiver {...section} />
    </>
  )
}

function AtkOnboard({ status, client, apply, busy }: Section) {
  const count = status.atkProfileCount
  const buttons = (status.atkButtonMappings ?? [])
    // Models without an underside button do not show its row.
    .filter(b => !(status.atkCatalogFeatures?.noBottomButton === true && b.id === 'bottom'))
  const rows = [
    count != null && status.activeProfile != null && (
      can(client, 'setR1ActiveProfile') && count > 1 ? (
        <Row key="bank" label="Onboard bank">
          <Segmented label="Onboard bank" options={Array.from({ length: count }, (_, i) => ({ value: i + 1, label: String(i + 1) }))}
            value={status.activeProfile} onChange={v => void apply('setR1ActiveProfile', v)} disabled={busy} />
        </Row>
      ) : (
        <Row key="bank" label="Onboard bank"><span className="m-state">{status.activeProfile} of {count}</span></Row>
      )
    ),
    // Read from the active bank; the driver has no verified write for these yet.
    ...buttons.map(b => (
      <Row key={b.id} label={b.name}>
        <span className="m-state">{b.checksumValid ? b.action : 'Unreadable'}</span>
      </Row>
    )),
  ].filter(Boolean)
  return rows.length ? <Panel title="Onboard">{rows}</Panel> : null
}

/** F1 Ultimate only: the driver reports atkSensorMode as null everywhere else. */
function AtkSensor({ status, client, apply, busy }: Section) {
  const mode = status.atkSensorMode
  if (mode == null) return null
  const modes = status.atkCatalogFeatures?.noAthleticsMax === true ? [0, 1] : [0, 1, 2]
  const mistouch = status.atkAntiMistouchMs
  const rows = [
    can(client, 'setAtkSensorMode') && (
      <Row key="mode" label="Sensor mode">
        <Segmented label="Sensor mode" options={modes.map(v => ({ value: v, label: ATK_SENSOR[v] }))} value={mode}
          onChange={v => void apply('setAtkSensorMode', v)} disabled={busy} />
      </Row>
    ),
    mistouch != null && can(client, 'setAntiMistouchMs') && (
      <ToggleWindow key="mt" label="Scroll anti-mistouch" windowLabel="Anti-mistouch window" value={mistouch}
        min={100} max={1000} step={50} fallback={100} onChange={v => void apply('setAntiMistouchMs', v)} busy={busy} />
    ),
  ].filter(Boolean)
  return rows.length ? <Panel title="Sensor">{rows}</Panel> : null
}

/** An on/off switch whose window slider shows only while on; 0 means off. */
function ToggleWindow({ label, windowLabel, value, min, max, step, fallback, onChange, busy }: {
  label: string
  windowLabel: string
  value: number
  min: number
  max: number
  step: number
  fallback: number
  onChange: (v: number) => void
  busy: boolean
}) {
  return (
    <>
      <Row label={label}>
        <Switch label={label} checked={value > 0} onChange={on => onChange(on ? fallback : 0)} disabled={busy} />
      </Row>
      {value > 0 ? (
        <Row label={windowLabel}>
          <Slider label={windowLabel} min={min} max={max} step={step} value={Math.min(max, Math.max(min, value))} format={ms}
            onCommit={onChange} disabled={busy} />
        </Row>
      ) : null}
    </>
  )
}

function AtkReceiver({ status, client, apply, busy }: Section) {
  const receiver = status.atkReceiver
  const device = (client as unknown as { device?: HIDDevice } | null)?.device
  const pairable = !!device && device.vendorId === SE_PLUS_RECEIVER.vendorId && device.productId === SE_PLUS_RECEIVER.productId
    && can(client, 'startR1ReceiverPairing')
  const pairing = receiver?.pairingStatus === 1
  // Write-only on the dongle: null until Exo has set it this session, so no option shows as current.
  const light = status.atkSensorMode != null && status.atkDongleLight !== undefined && can(client, 'setDongleLight')
  const rows = [
    receiver && (
      <Row key="state" label="Mouse" value={receiver.rfId ? `RF ${receiver.rfId}` : undefined}>
        <span className="m-state">{receiver.online ? 'Online' : 'Offline'}</span>
      </Row>
    ),
    receiver && pairable && (
      <Row key="pair" label="Pairing"
        value={pairing ? `Pairing, ${receiver.pairingSecondsRemaining ?? 0} s left` : 'Cable out, 2.4 GHz on, then hold left, wheel and right'}>
        {pairing ? <span className="m-state">Waiting for the mouse</span> : (
          <button type="button" className="m-button" disabled={busy}
            onClick={() => void apply('startR1ReceiverPairing', SE_PLUS_RECEIVER.cid, SE_PLUS_RECEIVER.mid)}>
            Pair
          </button>
        )}
      </Row>
    ),
    light && (
      <Row key="light" label="Receiver light">
        <Segmented label="Receiver light" options={ATK_DONGLE_LIGHT} value={status.atkDongleLight}
          onChange={v => void apply('setDongleLight', v)} disabled={busy} />
      </Row>
    ),
  ].filter(Boolean)
  return rows.length ? <Panel title="Receiver">{rows}</Panel> : null
}

// ------------------------------------------------------------------ LunaFury

const LIGHTNING = [
  { value: 1, label: 'Left priority' },
  { value: 0, label: 'Off' },
  { value: 2, label: 'Right priority' },
] as const

type LunaButton = 'left' | 'right' | 'middle'
const LUNA_BUTTONS: readonly { button: LunaButton; label: string; min: number; max: number }[] = [
  { button: 'left', label: 'Left button latency', min: 0, max: 15 },
  { button: 'right', label: 'Right button latency', min: 0, max: 15 },
  { button: 'middle', label: 'Middle button latency', min: 1, max: 30 },
]

function LunaFuryPanels({ status, client, apply, busy }: Section) {
  const luna = status.lunafury
  if (!luna) return null
  const lightning = luna.lightningMode
  const latency = (b: LunaButton) => luna[`${b}DebounceMs` as const]
  const guard = luna.wheelGuard
  const buttons = [
    lightning != null && can(client, 'setLunaFuryLightningMode') && (
      <Row key="lt" label="Lightning trigger">
        <Segmented label="Lightning trigger" options={LIGHTNING} value={lightning}
          onChange={v => void apply('setLunaFuryLightningMode', v)} disabled={busy} />
      </Row>
    ),
    ...LUNA_BUTTONS.map(({ button, label, min, max }) => {
      const v = latency(button)
      if (v == null || !can(client, 'setLunaFuryButtonDebounce')) return null
      // A side given Lightning priority runs at the trigger's own timing.
      const held = (button === 'left' && lightning === 1) || (button === 'right' && lightning === 2)
      return (
        <Row key={button} label={label} value={held ? 'Set by Lightning trigger' : undefined}>
          {held ? <span className="m-state">{ms(v)}</span> : (
            <Slider label={label} min={min} max={max} step={1} value={v} format={ms}
              onCommit={next => void apply('setLunaFuryButtonDebounce', button, next)} disabled={busy} />
          )}
        </Row>
      )
    }),
  ].filter(Boolean)
  const wheel = guard && can(client, 'setLunaFuryWheelGuard') ? (
    <Panel title="Scroll wheel">
      <Row label="Wheel anti-mistouch">
        <Switch label="Wheel anti-mistouch" checked={guard.enabled}
          onChange={on => void apply('setLunaFuryWheelGuard', { enabled: on, windowMs: on ? guard.windowMs || 100 : guard.windowMs })} disabled={busy} />
      </Row>
      {guard.enabled ? (
        <Row label="Wheel guard window">
          <Slider label="Wheel guard window" min={20} max={200} step={20} value={Math.min(200, Math.max(20, guard.windowMs))} format={ms}
            onCommit={v => void apply('setLunaFuryWheelGuard', { enabled: true, windowMs: v })} disabled={busy} />
        </Row>
      ) : null}
    </Panel>
  ) : null
  return (
    <>
      {buttons.length ? <Panel title="Button response">{buttons}</Panel> : null}
      {wheel}
    </>
  )
}

// ------------------------------------------------------------------ Ninjutso

const OPTICAL = [{ value: 'Standard', label: 'Standard' }, { value: 'Burst', label: 'Burst' }] as const
const SLAM = [{ value: 'Low', label: 'Low' }, { value: 'Medium', label: 'Medium' }, { value: 'High', label: 'High' }] as const

function NinjutsoPanels({ status, client, apply, busy }: Section) {
  if (status.brand !== 'Ninjutso') return null
  const modes = status.ninjutsoSystemModes
  // Two-mode mice hold their system mode above 1000 Hz and on the cable; Ultra fixes the optical engine.
  const systemLocked = modes?.length === 2 && (status.pollingRateHz > 1000 || status.connectionType === 'Wired')
  const opticalLocked = status.ninjutsoSystemMode === 'Ultra'
  const sensor = [
    modes?.length && status.ninjutsoSystemMode != null && can(client, 'setNinjutsoSystemMode') && (
      <Row key="sys" label="System mode">
        {systemLocked ? <span className="m-state">{status.ninjutsoSystemMode}</span> : (
          <Segmented label="System mode" options={modes.map(v => ({ value: v, label: v }))} value={status.ninjutsoSystemMode}
            onChange={v => void apply('setNinjutsoSystemMode', v)} disabled={busy} />
        )}
      </Row>
    ),
    status.ninjutsoOpticalEngine != null && can(client, 'setNinjutsoOpticalEngine') && (
      <Row key="opt" label="Optical engine">
        {opticalLocked ? <span className="m-state">{status.ninjutsoOpticalEngine}</span> : (
          <Segmented label="Optical engine" options={OPTICAL} value={status.ninjutsoOpticalEngine}
            onChange={v => void apply('setNinjutsoOpticalEngine', v)} disabled={busy} />
        )}
      </Row>
    ),
  ].filter(Boolean)
  const clicks = [
    status.ninjutsoHyperClick != null && can(client, 'setNinjutsoHyperClick') && (
      <Row key="hyper" label="HyperClick">
        <Switch label="HyperClick" checked={status.ninjutsoHyperClick} onChange={v => void apply('setNinjutsoHyperClick', v)} disabled={busy} />
      </Row>
    ),
    status.ninjutsoSlamClick != null && can(client, 'setNinjutsoSlamClick') && (
      <Row key="slam" label="Slam-click">
        <Segmented label="Slam-click" options={SLAM} value={status.ninjutsoSlamClick}
          onChange={v => void apply('setNinjutsoSlamClick', v)} disabled={busy} />
      </Row>
    ),
  ].filter(Boolean)
  return (
    <>
      {sensor.length ? <Panel title="Sensor">{sensor}</Panel> : null}
      {clicks.length ? <Panel title="Clicks">{clicks}</Panel> : null}
    </>
  )
}

// ------------------------------------------------------------------ Incott

const INCOTT_LED = [
  { value: 0, label: 'Polling rate' },
  { value: 1, label: 'Battery' },
  { value: 2, label: 'Low battery' },
] as const
// 0 is not off: the button keeps firing while it is held.
const FIRE_TIMES = [{ value: 0, label: 'Hold' }, { value: 1, label: '1' }, { value: 2, label: '2' }, { value: 3, label: '3' }] as const

function IncottPanels({ status, client, apply, busy }: Section) {
  const led = status.incottReceiverLedMode
  const times = status.incottFireKeyTimes
  const interval = status.incottFireKeyIntervalMs
  const fire = times != null && interval != null && can(client, 'setFireKey')
  // Both halves ride in one command, so a change to either sends the pair.
  const setFire = (t: number, i: number) => void apply('setFireKey', t, i)
  return (
    <>
      {led != null && can(client, 'setReceiverLed') ? (
        <Panel title="Receiver">
          <Row label="Receiver light">
            <Segmented label="Receiver light" options={INCOTT_LED} value={led} onChange={v => void apply('setReceiverLed', v)} disabled={busy} />
          </Row>
        </Panel>
      ) : null}
      {fire ? (
        <Panel title="Rapid fire">
          <Row label="Clicks per press">
            <Segmented label="Clicks per press" options={FIRE_TIMES} value={times} onChange={v => setFire(v, interval)} disabled={busy} />
          </Row>
          <Row label="Interval">
            <NumberField label="Interval" min={0} max={255} step={1} value={interval} suffix="ms" onCommit={v => setFire(times, v)} disabled={busy} />
          </Row>
        </Panel>
      ) : null}
    </>
  )
}

// ------------------------------------------------------------------ K-snake

const SCROLL = [{ value: 'Forward', label: 'Normal' }, { value: 'Reverse', label: 'Reversed' }] as const

/** The Noir mice the shared K-snake transport also drives. */
function isNoirKsnake(status: MouseStatus): boolean {
  const name = status.name.trim().toUpperCase()
  return (status.brand === 'Noir Gear' && status.name === 'M2-NEX')
    || ((name === 'NOIR S1' || name === 'NOIR E1') && (status.brand === 'Noir Gear' || status.brand === 'K-snake'))
}

function KsnakePanels(section: Section) {
  const { status, client, apply, busy } = section
  const ksnake = status.ui?.family === 'ksnake'
  const scroll = ksnake && status.scrollDirection != null && can(client, 'setScrollDirection')
  return (
    <>
      {scroll ? (
        <Panel title="Scroll wheel">
          <Row label="Scroll direction">
            <Segmented label="Scroll direction" options={SCROLL} value={status.scrollDirection}
              onChange={v => void apply('setScrollDirection', v)} disabled={busy} />
          </Row>
        </Panel>
      ) : null}
      {(ksnake || isNoirKsnake(status)) && can(client, 'setMacros') ? <KsnakeMacros {...section} /> : null}
    </>
  )
}

/** Vendor macro step: type 1 modifier, 2 keyboard, 3 mouse; action 1 press, 2 release. */
type MacroStep = { type: 1 | 2 | 3; action: 1 | 2; delayMs: number; code: number }
type MacroSlot = { steps: MacroStep[] }

const MACRO_SLOTS = 32
const MAX_DELAY = 65535

const MODIFIERS: Record<number, string> = {
  1: 'Left ctrl', 2: 'Left shift', 4: 'Left alt', 8: 'Left win',
  16: 'Right ctrl', 32: 'Right shift', 64: 'Right alt', 128: 'Right win',
}
const MOUSE_BUTTONS: Record<number, string> = { 1: 'Left button', 2: 'Right button', 4: 'Middle button', 8: 'Back button', 16: 'Forward button' }
const KEYS: Record<number, string> = {
  ...Object.fromEntries('ABCDEFGHIJKLMNOPQRSTUVWXYZ'.split('').map((l, i) => [4 + i, l])),
  ...Object.fromEntries(['1', '2', '3', '4', '5', '6', '7', '8', '9', '0'].map((d, i) => [30 + i, d])),
  40: 'Enter', 41: 'Escape', 42: 'Backspace', 43: 'Tab', 44: 'Space', 45: 'Minus', 46: 'Equals',
  47: 'Left bracket', 48: 'Right bracket', 49: 'Backslash', 51: 'Semicolon', 52: 'Quote', 53: 'Backtick',
  54: 'Comma', 55: 'Period', 56: 'Slash', 57: 'Caps lock',
  ...Object.fromEntries(Array.from({ length: 12 }, (_, i) => [58 + i, `F${i + 1}`])),
  70: 'Print screen', 71: 'Scroll lock', 72: 'Pause', 73: 'Insert', 74: 'Home', 75: 'Page up', 76: 'Delete',
  77: 'End', 78: 'Page down', 79: 'Right', 80: 'Left', 81: 'Down', 82: 'Up', 83: 'Num lock',
  84: 'Num /', 85: 'Num *', 86: 'Num -', 87: 'Num +', 88: 'Num enter',
  ...Object.fromEntries(Array.from({ length: 9 }, (_, i) => [89 + i, `Num ${i + 1}`])),
  98: 'Num 0', 99: 'Num .',
}
const CODES: Record<MacroStep['type'], Record<number, string>> = { 1: MODIFIERS, 2: KEYS, 3: MOUSE_BUTTONS }
const FIRST_CODE: Record<MacroStep['type'], number> = { 1: 1, 2: 4, 3: 1 }

const MODIFIER_BY_EVENT: Record<string, number> = {
  ControlLeft: 1, ShiftLeft: 2, AltLeft: 4, MetaLeft: 8, OSLeft: 8,
  ControlRight: 16, ShiftRight: 32, AltRight: 64, MetaRight: 128, OSRight: 128,
}
const KEY_BY_EVENT: Record<string, number> = {
  ...Object.fromEntries('ABCDEFGHIJKLMNOPQRSTUVWXYZ'.split('').map((l, i) => [`Key${l}`, 4 + i])),
  ...Object.fromEntries(['1', '2', '3', '4', '5', '6', '7', '8', '9', '0'].map((d, i) => [`Digit${d}`, 30 + i])),
  Enter: 40, Escape: 41, Backspace: 42, Tab: 43, Space: 44, Minus: 45, Equal: 46, BracketLeft: 47, BracketRight: 48,
  Backslash: 49, Semicolon: 51, Quote: 52, Backquote: 53, Comma: 54, Period: 55, Slash: 56, CapsLock: 57,
  ...Object.fromEntries(Array.from({ length: 12 }, (_, i) => [`F${i + 1}`, 58 + i])),
  PrintScreen: 70, ScrollLock: 71, Pause: 72, Insert: 73, Home: 74, PageUp: 75, Delete: 76, End: 77, PageDown: 78,
  ArrowRight: 79, ArrowLeft: 80, ArrowDown: 81, ArrowUp: 82, NumLock: 83, NumpadDivide: 84, NumpadMultiply: 85,
  NumpadSubtract: 86, NumpadAdd: 87, NumpadEnter: 88,
  ...Object.fromEntries(Array.from({ length: 9 }, (_, i) => [`Numpad${i + 1}`, 89 + i])),
  Numpad0: 98, NumpadDecimal: 99,
}
/** Browser MouseEvent.button to the vendor's mouse bit. */
const MOUSE_BY_BUTTON: Record<number, number> = { 0: 1, 1: 4, 2: 2, 3: 8, 4: 16 }

const TYPE_OPTIONS = [{ value: 1, label: 'Modifier' }, { value: 2, label: 'Key' }, { value: 3, label: 'Mouse' }] as const
const ACTION_OPTIONS = [{ value: 1, label: 'Press' }, { value: 2, label: 'Release' }] as const
const TIMING = [{ value: 'fast', label: 'Fast' }, { value: 'recorded', label: 'Recorded' }, { value: 'fixed', label: 'Fixed' }] as const
type Timing = (typeof TIMING)[number]['value']

function codeLabel(step: MacroStep): string {
  return CODES[step.type][step.code] ?? `Code ${step.code}`
}

function retime(steps: readonly MacroStep[], timing: Timing, fixed: number): MacroStep[] {
  return steps.map(s => ({ ...s, delayMs: timing === 'fast' ? 0 : timing === 'fixed' ? fixed : s.delayMs }))
}

function blankTable(): MacroSlot[] {
  return Array.from({ length: MACRO_SLOTS }, () => ({ steps: [] }))
}

/**
 * K-snake / M2-NEX onboard macros. The receiver gives no reliable read of the
 * macro store, so slots start blank here and a save rewrites the whole
 * 32-slot table: the slots saved this session, the rest empty. The button
 * that runs a macro is assigned under Buttons.
 */
function KsnakeMacros({ apply, busy }: Section) {
  const [table, setTable] = useState<MacroSlot[]>(blankTable)
  const [slot, setSlot] = useState(0)
  const [draft, setDraft] = useState<MacroStep[]>([])
  const [timing, setTiming] = useState<Timing>('fast')
  const [fixed, setFixed] = useState(10)
  const [recording, setRecording] = useState(false)
  const [live, setLive] = useState<MacroStep[]>([])
  const liveRef = useRef<MacroStep[]>([])
  const clock = useRef<number | null>(null)
  const original = useRef<MacroStep[]>([])
  const capture = useRef<HTMLDivElement>(null)
  const timingRef = useRef({ timing, fixed })
  timingRef.current = { timing, fixed }

  useEffect(() => { setDraft(table[slot]!.steps.map(s => ({ ...s }))) }, [table, slot])

  const stop = (keep: boolean) => {
    const { timing: t, fixed: f } = timingRef.current
    setDraft(keep ? retime(liveRef.current, t, f) : original.current)
    setRecording(false)
    liveRef.current = []
    setLive([])
    clock.current = null
  }

  useEffect(() => {
    if (!recording) return undefined
    // Off the Record button, so the first key pressed is captured rather than ignored as UI input.
    capture.current?.focus()
    const isControl = (target: EventTarget | null) => target instanceof Element && !!target.closest('[data-macro-control]')
    const push = (step: MacroStep) => {
      const now = performance.now()
      const previous = clock.current
      clock.current = now
      const next = [...liveRef.current]
      // The firmware runs a step's delay after that step, so the gap measured now belongs to the step before.
      if (next.length > 0 && previous !== null) {
        next[next.length - 1] = { ...next[next.length - 1]!, delayMs: Math.min(MAX_DELAY, Math.max(0, Math.round(now - previous))) }
      }
      next.push({ ...step, delayMs: 0 })
      liveRef.current = next
      setLive(next)
    }
    const fromKey = (e: KeyboardEvent, action: 1 | 2): MacroStep | null => {
      const modifier = MODIFIER_BY_EVENT[e.code]
      if (modifier !== undefined) return { type: 1, action, delayMs: 0, code: modifier }
      const code = KEY_BY_EVENT[e.code]
      return code === undefined ? null : { type: 2, action, delayMs: 0, code }
    }
    const fromMouse = (e: MouseEvent, action: 1 | 2): MacroStep | null => {
      const code = MOUSE_BY_BUTTON[e.button]
      return code === undefined ? null : { type: 3, action, delayMs: 0, code }
    }
    const take = (e: Event, step: MacroStep | null) => {
      if (!step) return
      e.preventDefault()
      e.stopPropagation()
      push(step)
    }
    const keydown = (e: KeyboardEvent) => {
      if (isControl(e.target)) return
      if (e.code === 'Escape') { e.preventDefault(); e.stopPropagation(); stop(true); return }
      if (!e.repeat) take(e, fromKey(e, 1))
    }
    const keyup = (e: KeyboardEvent) => { if (!isControl(e.target)) take(e, fromKey(e, 2)) }
    const mousedown = (e: MouseEvent) => { if (!isControl(e.target)) take(e, fromMouse(e, 1)) }
    const mouseup = (e: MouseEvent) => { if (!isControl(e.target)) take(e, fromMouse(e, 2)) }
    const menu = (e: MouseEvent) => { if (!isControl(e.target)) { e.preventDefault(); e.stopPropagation() } }
    window.addEventListener('keydown', keydown, true)
    window.addEventListener('keyup', keyup, true)
    window.addEventListener('mousedown', mousedown, true)
    window.addEventListener('mouseup', mouseup, true)
    window.addEventListener('contextmenu', menu, true)
    return () => {
      window.removeEventListener('keydown', keydown, true)
      window.removeEventListener('keyup', keyup, true)
      window.removeEventListener('mousedown', mousedown, true)
      window.removeEventListener('mouseup', mouseup, true)
      window.removeEventListener('contextmenu', menu, true)
    }
    // stop reads the latest timing through a ref; the listeners only need to follow `recording`.
  }, [recording])

  const start = () => {
    original.current = draft.map(s => ({ ...s }))
    liveRef.current = []
    setLive([])
    clock.current = performance.now()
    setRecording(true)
  }

  const changeTiming = (t: Timing) => {
    setTiming(t)
    if (t !== 'recorded') setDraft(d => retime(d, t, fixed))
  }
  const changeFixed = (v: number) => {
    setFixed(v)
    if (timing === 'fixed') setDraft(d => retime(d, 'fixed', v))
  }
  const update = (index: number, change: Partial<MacroStep>) =>
    setDraft(d => d.map((s, i) => (i === index ? { ...s, ...change } : s)))

  const save = async () => {
    const next = table.map((s, i) => ({ steps: i === slot ? draft.map(step => ({ ...step })) : s.steps.map(step => ({ ...step })) }))
    const took = await apply('setMacros', next)
    if (took) setTable(next)
  }

  const locked = busy || recording
  const dirty = JSON.stringify(draft) !== JSON.stringify(table[slot]!.steps)
  const slotOptions = table.map((s, i) => {
    const n = i === slot ? draft.length : s.steps.length
    return { value: i, label: `Macro ${i + 1}${n ? ` · ${n} ${n === 1 ? 'step' : 'steps'}` : ''}` }
  })

  return (
    <Panel title="Macros">
      <Row label="Slot">
        <NumSelect label="Macro slot" options={slotOptions} value={slot} onChange={setSlot} disabled={locked} />
      </Row>
      <Row label="Timing">
        <span className="am-controls" data-macro-control>
          <Segmented label="Timing" options={TIMING} value={timing} onChange={changeTiming} disabled={locked} />
          {timing === 'fixed' ? (
            <NumberField label="Fixed delay" min={0} max={MAX_DELAY} step={1} value={fixed} suffix="ms" onCommit={changeFixed} disabled={locked} />
          ) : null}
        </span>
      </Row>
      <div ref={capture} tabIndex={-1} className={`am-capture${recording ? ' am-recording' : ''}`}>
        <Row label="Record" value={recording ? `Recording, ${live.length} ${live.length === 1 ? 'event' : 'events'}` : undefined}>
          <span className="am-controls" data-macro-control>
            {recording ? (
              <>
                <button type="button" className="m-link" onClick={() => stop(false)}>Cancel</button>
                <button type="button" className="m-button" onClick={() => stop(true)}>Stop</button>
              </>
            ) : (
              <button type="button" className="m-button" onClick={start} disabled={busy}>Record</button>
            )}
          </span>
        </Row>
      </div>
      {(recording ? live : draft).map((step, i) => (
        <MacroStepRow key={i} index={i} step={step} readOnly={recording} disabled={locked}
          onChange={change => update(i, change)} onRemove={() => setDraft(d => d.filter((_, j) => j !== i))} />
      ))}
      <Row label={draft.length ? `${draft.length} ${draft.length === 1 ? 'step' : 'steps'}` : 'No steps'}>
        <span className="am-controls">
          <button type="button" className="m-link" disabled={locked}
            onClick={() => setDraft(d => [...d, { type: 2, action: 1, delayMs: timing === 'fixed' ? fixed : 0, code: FIRST_CODE[2] }])}>
            Add step
          </button>
          <button type="button" className="m-link" disabled={locked || draft.length === 0} onClick={() => setDraft([])}>Clear</button>
          <button type="button" className="m-button" disabled={locked || !dirty} onClick={() => void save()}>Save</button>
        </span>
      </Row>
    </Panel>
  )
}

function MacroStepRow({ index, step, readOnly, disabled, onChange, onRemove }: {
  index: number
  step: MacroStep
  readOnly: boolean
  disabled: boolean
  onChange: (change: Partial<MacroStep>) => void
  onRemove: () => void
}): ReactNode {
  const label = `${index + 1}. ${step.action === 1 ? 'Press' : 'Release'} ${codeLabel(step)}`
  if (readOnly) {
    return (
      <Row label={label}>
        <span className="m-state">{step.delayMs ? ms(step.delayMs) : ''}</span>
      </Row>
    )
  }
  const codes = Object.entries(CODES[step.type]).map(([value, text]) => ({ value: Number(value), label: text }))
  return (
    <div className="am-step">
      <Row label={label}>
        <span className="am-controls">
          <NumSelect label={`Step ${index + 1} input`} options={TYPE_OPTIONS} value={step.type}
            onChange={v => onChange({ type: v as MacroStep['type'], code: FIRST_CODE[v as MacroStep['type']] })} disabled={disabled} />
          <NumSelect label={`Step ${index + 1} code`} options={codes} value={step.code} onChange={v => onChange({ code: v })} disabled={disabled} />
          <NumSelect label={`Step ${index + 1} action`} options={ACTION_OPTIONS} value={step.action}
            onChange={v => onChange({ action: v as MacroStep['action'] })} disabled={disabled} />
          <NumberField label={`Step ${index + 1} delay`} min={0} max={MAX_DELAY} step={1} value={step.delayMs} suffix="ms"
            onCommit={v => onChange({ delayMs: v })} disabled={disabled} />
          <button type="button" className="m-link" onClick={onRemove} disabled={disabled}>Remove</button>
        </span>
      </Row>
    </div>
  )
}
