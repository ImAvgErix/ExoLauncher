import { useEffect, useRef, useState } from 'react'
import type { MouseStatus } from '@openmouse/protocol/drivers/mouse-types'
import type { SupportedClient } from '@openmouse/protocol/drivers/registry'
import {
  BUNNY_HOP_LIMITS,
  LOGITECH_BUTTON_ACTIONS,
  capabilitiesForFormat,
  type LogitechButtonAction,
  type OnboardProfile,
} from '@openmouse/protocol/drivers/logitech/onboard-profiles'
import {
  LOGITECH_FRIENDLY_NAME_BYTES_PER_REPORT,
  LOGITECH_HAPTIC_PRESETS,
  LOGITECH_SMART_SHIFT_OFF,
  logitechControlName,
  type LogitechHapticPreset,
  type LogitechReprogrammableControl,
} from '@openmouse/protocol/logitech'
import { can } from '../mouse'
import type { Section } from '../section'
import { Dropdown, NumberField, Panel, Row, Segmented, Slider, Switch } from '../ui'
import './logitech.css'

/**
 * Logitech HID++ (and the few cross-brand cards that ride with it): wheel,
 * haptics, sensor surface and LightForce switches, onboard memory with bunny
 * hop and G-series button assignments, reprogrammable controls, Easy-Switch,
 * the device name, signal, Razer low power and the Wallhack sensor extras.
 * Every row shows only when the mouse reports the value and its driver has
 * the setter.
 */
export function LogitechPanels(section: Section) {
  return (
    <>
      <Scrolling {...section} />
      <Haptics {...section} />
      <Sensor {...section} />
      <Acceleration {...section} />
      <OnboardMemory {...section} />
      <Controls {...section} />
      <Connection {...section} />
      <LowPower {...section} />
    </>
  )
}

const isLogitech = (status: MouseStatus) => status.ui?.family === 'logitech-hidpp'

// ------------------------------------------------------------------ local controls


/** A long list with values that are not strings. */
function Choose<T extends string | number>({ label, options, value, onChange, disabled = false, describe }: {
  label: string
  options: readonly { value: T; label: string }[]
  value: T | null | undefined
  onChange: (value: T) => void
  disabled?: boolean
  /** How to name a current value that is not one of the options. */
  describe?: (value: T) => string
}) {
  return (
    <Dropdown label={label} options={options} value={value} onChange={onChange} disabled={disabled}
      placeholder={value == null ? 'Unknown' : describe ? describe(value) : String(value)} />
  )
}

/**
 * A read the status poll does not carry (buttons, onboard profiles): run once
 * per connection, and again on reload(). It starts only after a status read
 * has landed and nothing is being written, so it sits in the gap between
 * polls instead of interleaving with one. A mouse that will not answer is
 * tried twice and then left alone.
 */
function useSideRead<T>(client: SupportedClient | null, method: string, enabled: boolean, status: MouseStatus, busy: boolean) {
  const [data, setData] = useState<T | null>(null)
  const [round, setRound] = useState(0)
  const first = useRef(status)
  const owner = useRef<SupportedClient | null>(null)
  const done = useRef(-1)
  const tries = useRef(0)
  const running = useRef(false)

  // A new connection starts over: nothing read from the last mouse carries across.
  useEffect(() => {
    if (owner.current === client) return
    owner.current = client
    first.current = status
    done.current = -1
    tries.current = 0
    setData(null)
  }, [client, status])

  useEffect(() => {
    if (!enabled || !client || busy || running.current) return
    if (done.current === round || tries.current >= 2) return
    if (status === first.current && round === 0) return
    const fn = (client as unknown as Record<string, () => Promise<T>>)[method]
    if (typeof fn !== 'function') return
    running.current = true
    tries.current++
    const which = round
    void fn.call(client)
      .then(result => {
        if (owner.current !== client) return
        done.current = which
        tries.current = 0
        setData(result)
      })
      .catch(() => undefined)
      .finally(() => { running.current = false })
  }, [client, method, enabled, status, busy, round])

  const reload = () => { tries.current = 0; setRound(n => n + 1) }
  return { data, reload }
}

// ------------------------------------------------------------------ scrolling

function Scrolling({ status, client, apply, busy }: Section) {
  const thr = status.smartShiftThreshold
  const smartOn = thr != null && thr !== LOGITECH_SMART_SHIFT_OFF
  const rows = [
    status.wheelMode != null && can(client, 'setWheelMode') && (
      <Row key="mode" label="Wheel" value={status.wheelRatchetEngaged == null ? undefined : status.wheelRatchetEngaged ? 'Ratcheted now' : 'Spinning freely now'}>
        <Segmented label="Wheel" options={[{ value: 'Ratchet', label: 'Ratchet' }, { value: 'Freespin', label: 'Free spin' }] as const}
          value={status.wheelMode} onChange={v => void apply('setWheelMode', v)} disabled={busy} />
      </Row>
    ),
    thr != null && can(client, 'setSmartShiftThreshold') && (
      <Row key="ss" label="SmartShift">
        <Switch label="SmartShift" checked={smartOn} onChange={on => void apply('setSmartShiftThreshold', on ? 50 : null)} disabled={busy} />
      </Row>
    ),
    thr != null && smartOn && can(client, 'setSmartShiftThreshold') && (
      <Row key="sst" label="SmartShift threshold">
        <Slider label="SmartShift threshold" min={1} max={Math.max(100, thr)} step={1} value={thr} format={v => String(v)}
          onCommit={v => void apply('setSmartShiftThreshold', v)} disabled={busy} />
      </Row>
    ),
    status.hiResScroll != null && can(client, 'setHiResScroll') && (
      <Row key="hr" label="High-resolution scrolling">
        <Switch label="High-resolution scrolling" checked={status.hiResScroll} onChange={v => void apply('setHiResScroll', v)} disabled={busy} />
      </Row>
    ),
    status.supportsInvertScroll === true && status.invertScroll != null && can(client, 'setInvertScroll') && (
      <Row key="inv" label="Invert scrolling">
        <Switch label="Invert scrolling" checked={status.invertScroll} onChange={v => void apply('setInvertScroll', v)} disabled={busy} />
      </Row>
    ),
    status.supportsThumbWheelInvert === true && status.thumbWheelInverted != null && can(client, 'setThumbWheelInverted') && (
      <Row key="thumb" label="Invert thumb wheel">
        <Switch label="Invert thumb wheel" checked={status.thumbWheelInverted} onChange={v => void apply('setThumbWheelInverted', v)} disabled={busy} />
      </Row>
    ),
  ].filter(Boolean)
  return rows.length ? <Panel title="Scrolling">{rows}</Panel> : null
}

// ------------------------------------------------------------------ haptics

const HAPTIC_PRESETS = Object.keys(LOGITECH_HAPTIC_PRESETS) as LogitechHapticPreset[]

function Haptics({ status, client, apply, busy }: Section) {
  const intensity = status.hapticIntensity
  const preset = HAPTIC_PRESETS.find(name => LOGITECH_HAPTIC_PRESETS[name] === intensity) ?? null
  const rows = [
    status.hapticEnabled != null && can(client, 'setHapticEnabled') && (
      <Row key="on" label="Haptic feedback">
        <Switch label="Haptic feedback" checked={status.hapticEnabled} onChange={v => void apply('setHapticEnabled', v)} disabled={busy} />
      </Row>
    ),
    intensity != null && can(client, 'setHapticIntensity') && (
      <Row key="strength" label="Strength" value={preset ? undefined : `${intensity} of 100`}>
        <Segmented label="Strength" options={HAPTIC_PRESETS.map(p => ({ value: p, label: p }))} value={preset}
          onChange={p => void apply('setHapticIntensity', LOGITECH_HAPTIC_PRESETS[p])} disabled={busy || status.hapticEnabled !== true} />
      </Row>
    ),
    status.hapticBatterySaving != null && can(client, 'setHapticBatterySaving') && (
      <Row key="save" label="Battery saving">
        <Switch label="Battery saving" checked={status.hapticBatterySaving} onChange={v => void apply('setHapticBatterySaving', v)} disabled={busy} />
      </Row>
    ),
  ].filter(Boolean)
  return rows.length ? <Panel title="Haptics">{rows}</Panel> : null
}

// ------------------------------------------------------------------ sensor

const SURFACE = [{ value: 'On', label: 'On' }, { value: 'Off', label: 'Off' }, { value: 'Auto', label: 'Auto' }] as const
const LIGHTFORCE = [{ value: 'Hybrid', label: 'Hybrid' }, { value: 'Optical', label: 'Optical only' }] as const
/** Wallhack's scanning mode (firmware 52+): byte 0 high frame rate, 1 pinned to high performance. */
const SCANNING = [{ value: 'HIGH', label: 'High' }, { value: 'ACCEL', label: 'Accel' }] as const

/**
 * Whether this group draws lift-off distance itself: on Logitech mice whose
 * lift-off only works with the gaming surface on, the two belong together.
 * App.tsx's generic lift-off row should step aside when this is true.
 */
export function logitechOwnsLiftOff(status: MouseStatus): boolean {
  return !!status.ui?.lodRequiresSurface && status.gamingSurfaceMode != null
}

function Sensor({ status, client, apply, busy }: Section) {
  const lods = status.supportedLiftOffDistances ?? []
  const surfaceOff = !!status.ui?.lodRequiresSurface && status.gamingSurfaceMode === 'Off'
  const rows = [
    status.gamingSurfaceMode != null && can(client, 'setGamingSurfaceMode') && (
      <Row key="surface" label="Gaming surface">
        <Segmented label="Gaming surface" options={SURFACE} value={status.gamingSurfaceMode}
          onChange={v => void apply('setGamingSurfaceMode', v)} disabled={busy} />
      </Row>
    ),
    logitechOwnsLiftOff(status) && !surfaceOff && status.liftOffDistance != null && lods.length > 0 && can(client, 'setLiftOffDistance') && (
      <Row key="lod" label="Lift-off distance">
        <Segmented label="Lift-off distance" options={lods.map(v => ({ value: v, label: v }))} value={status.liftOffDistance}
          onChange={v => void apply('setLiftOffDistance', v)} disabled={busy} />
      </Row>
    ),
    status.lightforceSwitchMode != null && can(client, 'setLightforceSwitchMode') && (
      <Row key="lf" label="LightForce switches">
        <Segmented label="LightForce switches" options={LIGHTFORCE} value={status.lightforceSwitchMode}
          onChange={v => void apply('setLightforceSwitchMode', v)} disabled={busy} />
      </Row>
    ),
    status.sensorScanningMode != null && can(client, 'setSensorScanningMode') && (
      <Row key="scan" label="Sensor scanning">
        <Segmented label="Sensor scanning" options={SCANNING} value={status.sensorScanningMode}
          onChange={v => void apply('setSensorScanningMode', v)} disabled={busy} />
      </Row>
    ),
  ].filter(Boolean)
  return rows.length ? <Panel title="Sensor">{rows}</Panel> : null
}

// ------------------------------------------------------------------ dpi acceleration (Wallhack)

const CURVES = [
  { value: 'classic', label: 'Classic' },
  { value: 'natural', label: 'Natural' },
  { value: 'jump', label: 'Jump' },
  { value: 'custom', label: 'Custom' },
] as const

function Acceleration({ status, client, apply, busy }: Section) {
  const enabled = status.dynamicSensitivityEnabled
  if (enabled == null || !can(client, 'setDynamicSensitivityEnabled')) return null
  return (
    <Panel title="DPI acceleration">
      <Row label="DPI acceleration">
        <Switch label="DPI acceleration" checked={enabled} onChange={v => void apply('setDynamicSensitivityEnabled', v)} disabled={busy} />
      </Row>
      {status.dynamicSensitivityMode != null && can(client, 'setDynamicSensitivityMode') ? (
        <Row label="Curve">
          <Segmented label="Curve" options={CURVES} value={status.dynamicSensitivityMode}
            onChange={v => void apply('setDynamicSensitivityMode', v)} disabled={busy || !enabled} />
        </Row>
      ) : null}
      {status.dynamicSensitivitySpeedReporting != null && can(client, 'setDynamicSensitivitySpeedReporting') ? (
        <Row label="Motion speed reporting">
          <Switch label="Motion speed reporting" checked={status.dynamicSensitivitySpeedReporting}
            onChange={v => void apply('setDynamicSensitivitySpeedReporting', v)} disabled={busy} />
        </Row>
      ) : null}
    </Panel>
  )
}

// ------------------------------------------------------------------ onboard memory

/** G-series mice whose button positions are known; slot 0 left, 1 right, 2 wheel, 3 back, 4 forward. */
const BUTTON_LAYOUTS = new Set(['PROX3SUPERSTRIKE', 'PROX2SUPERSTRIKE'])
const BUTTON_SLOTS = [
  { button: 0, label: 'Primary click', locked: true },
  { button: 1, label: 'Secondary click', locked: true },
  { button: 2, label: 'Middle click', locked: false },
  { button: 4, label: 'Forward', locked: false },
  { button: 3, label: 'Back', locked: false },
] as const
const LAYERS = [{ value: 'primary', label: 'Standard' }, { value: 'g-shift', label: 'G-Shift' }] as const
const ACTION_OPTIONS = LOGITECH_BUTTON_ACTIONS.map(a => ({ value: a as LogitechButtonAction | 'Custom', label: a }))

function profileName(p: OnboardProfile): string {
  return p.name?.trim() || `Profile ${p.sector}`
}

function OnboardMemory({ status, client, apply, applyAll, busy }: Section) {
  const format = status.onboardProfileFormat
  const logitech = isLogitech(status)
  const readable = logitech && format != null && can(client, 'readOnboardProfiles')
  const { data: profiles, reload } = useSideRead<OnboardProfile[]>(client, 'readOnboardProfiles', readable, status, busy)
  const [layer, setLayer] = useState<'primary' | 'g-shift'>('primary')

  const mode = status.deviceMode
  const hasMode = logitech && mode != null && mode !== 'Unknown' && can(client, 'setOnboardMode')
  const onboard = mode === 'Onboard'
  const current = onboard ? profiles?.find(p => p.sector === status.activeProfile) ?? null : null
  const selectable = (profiles ?? []).filter(p => p.enabled)
  const writable = format?.writable === true && can(client, 'writeActiveProfile')

  const selectProfile = (sector: number) => void applyAll([
    ...(onboard ? [] : [['setOnboardMode', ['Onboard']] as [string, unknown[]]]),
    ['setCurrentProfile', [sector]],
  ])
  const writeProfile = async (values: Record<string, unknown>) => {
    if (!current) return
    const took = await apply('writeActiveProfile', { sector: current.sector, ...values })
    if (took) reload()
  }

  const bunny = current && format && capabilitiesForFormat(format.id).bunnyHop ? current.bunnyHoppingMs ?? 0 : null
  const buttonMap = current && writable && BUTTON_LAYOUTS.has(status.name.replace(/\s+/g, '').toUpperCase())
  const assignments = current ? (layer === 'primary' ? current.buttonAssignments : current.gShiftAssignments) : []

  const memoryRows = [
    hasMode && (
      <Row key="mode" label="Running from">
        <Segmented label="Running from" options={[{ value: 'Onboard', label: 'Onboard memory' }, { value: 'Host', label: 'This PC' }] as const}
          value={mode} onChange={v => void apply('setOnboardMode', v)} disabled={busy} />
      </Row>
    ),
    selectable.length > 0 && can(client, 'setCurrentProfile') && (
      format?.verified ? (
        <Row key="profile" label="Profile">
          {selectable.length <= 3 ? (
            <Segmented label="Profile" options={selectable.map(p => ({ value: p.sector, label: profileName(p) }))}
              value={onboard ? status.activeProfile : null} onChange={selectProfile} disabled={busy} />
          ) : (
            <Choose label="Profile" options={selectable.map(p => ({ value: p.sector, label: profileName(p) }))}
              value={onboard ? status.activeProfile : null} onChange={selectProfile} disabled={busy} />
          )}
        </Row>
      ) : (
        <Row key="profile" label="Profile">
          <span className="m-state">{current ? profileName(current) : 'This PC'}</span>
        </Row>
      )
    ),
    bunny != null && (
      writable ? (
        <Row key="bunny" label="Bunny hop" value={bunny ? undefined : 'Off'}>
          <span className="lg-pair">
            {bunny ? (
              <NumberField label="Bunny hop" min={BUNNY_HOP_LIMITS.minMs} max={BUNNY_HOP_LIMITS.maxMs} step={BUNNY_HOP_LIMITS.stepMs}
                value={bunny} suffix="ms" onCommit={v => void writeProfile({ bunnyHoppingMs: v })} disabled={busy} />
            ) : null}
            <Switch label="Bunny hop" checked={bunny !== 0} onChange={on => void writeProfile({ bunnyHoppingMs: on ? BUNNY_HOP_LIMITS.minMs : 0 })} disabled={busy} />
          </span>
        </Row>
      ) : (
        <Row key="bunny" label="Bunny hop">
          <span className="m-state">{bunny ? `${bunny} ms` : 'Off'}</span>
        </Row>
      )
    ),
  ].filter(Boolean)

  return (
    <>
      {memoryRows.length ? <Panel title="Onboard memory">{memoryRows}</Panel> : null}
      {buttonMap ? (
        <Panel title="Button assignments">
          <Row label="Layer" value={current ? profileName(current) : undefined}>
            <Segmented label="Layer" options={LAYERS} value={layer} onChange={setLayer} disabled={busy} />
          </Row>
          {BUTTON_SLOTS.map(slot => {
            const assignment = assignments.find(a => a.button === slot.button)
            const action = assignment?.action ?? null
            const locked = slot.locked && layer === 'primary'
            return (
              <Row key={slot.button} label={slot.label}>
                {locked || !assignment ? (
                  <span className="m-state">{action ?? 'Unknown'}</span>
                ) : (
                  <Choose label={slot.label} options={ACTION_OPTIONS} value={action}
                    onChange={next => {
                      if (next === 'Custom') return
                      void writeProfile({ buttonAssignments: [{ layer, button: slot.button, binding: next }] })
                    }} disabled={busy} />
                )}
              </Row>
            )
          })}
        </Panel>
      ) : null}
    </>
  )
}

// ------------------------------------------------------------------ reprogrammable controls (0x1B04)

function Controls({ status, client, apply, busy }: Section) {
  const readable = isLogitech(status) && can(client, 'readButtons') && can(client, 'setButtonMapping')
  const { data, reload } = useSideRead<LogitechReprogrammableControl[]>(client, 'readButtons', readable, status, busy)
  if (!readable || !data || data.length === 0) return null
  const controls = data.filter(c => !c.virtual)
  if (controls.length === 0) return null
  const nameOf = (id: number) => data.find(c => c.controlId === id)?.name ?? logitechControlName(id)
  const diverted = controls.filter(c => c.diverted)
  const remap = async (controlId: number, target: number) => {
    await apply('setButtonMapping', controlId, target)
    reload()
  }
  const restore = async () => {
    await apply('clearButtonDiversion')
    reload()
  }
  return (
    <Panel title="Buttons">
      {controls.map(c => (
        <Row key={c.controlId} label={c.name}>
          {c.reprogrammable && c.remappableTo.length > 0 ? (
            <Choose label={c.name} options={c.remappableTo.map(t => ({ value: t, label: nameOf(t) }))} value={c.mappedTo}
              describe={nameOf} onChange={t => void remap(c.controlId, t)} disabled={busy} />
          ) : (
            <span className="m-state">{c.taskName}</span>
          )}
        </Row>
      ))}
      {diverted.length > 0 && can(client, 'clearButtonDiversion') ? (
        <Row label="Held by another app" value={diverted.map(c => c.name).join(', ')}>
          <button type="button" className="m-button" onClick={() => void restore()} disabled={busy}>Give back</button>
        </Row>
      ) : null}
    </Panel>
  )
}

// ------------------------------------------------------------------ connection

/** Easy-Switch and the device's name; the signal is the hero's. */
function Connection(section: Section) {
  const { status } = section
  const hasEasy = status.hostCount != null && status.currentHost != null && can(section.client, 'requestHostSwitch')
  const hasName = status.friendlyName != null && status.friendlyNameMaxLength != null && can(section.client, 'setFriendlyName')
  if (!hasEasy && !hasName) return null
  return (
    <Panel title="Connection">
      {hasEasy ? <EasySwitch {...section} /> : null}
      {hasName ? <FriendlyName {...section} /> : null}
    </Panel>
  )
}

function EasySwitch({ status, apply, busy }: Section) {
  const [pending, setPending] = useState<number | null>(null)
  const count = status.hostCount ?? 0
  const current = status.currentHost ?? 0
  const paired = status.hostSlotsPaired
  if (pending !== null) {
    return (
      <Row label={`Switch to computer ${pending + 1}?`}>
        <span className="lg-pair">
          <button type="button" className="m-link" onClick={() => setPending(null)}>Cancel</button>
          <button type="button" className="m-button" disabled={busy}
            onClick={() => { const slot = pending; setPending(null); void apply('requestHostSwitch', slot) }}>Switch</button>
        </span>
      </Row>
    )
  }
  return (
    <Row label="Easy-Switch" value={`Computer ${current + 1} of ${count}`}>
      <Segmented
        label="Easy-Switch"
        options={Array.from({ length: count }, (_, slot) => ({
          value: slot,
          label: String(slot + 1),
          disabled: slot !== current && paired?.[slot] !== true,
        }))}
        value={current}
        onChange={setPending}
        disabled={busy}
      />
    </Row>
  )
}

function FriendlyName({ status, apply, busy }: Section) {
  const saved = status.friendlyName ?? ''
  // The mouse caps the length, and the driver sends the name in one 15-byte report.
  const max = Math.min(status.friendlyNameMaxLength ?? 0, LOGITECH_FRIENDLY_NAME_BYTES_PER_REPORT)
  const [name, setName] = useState(saved)
  useEffect(() => setName(saved), [saved])
  const trimmed = name.trim()
  // The driver takes printable ASCII only.
  const valid = trimmed.length > 0 && trimmed !== saved && trimmed.length <= max && /^[\x20-\x7e]*$/.test(trimmed)
  const rename = () => { if (valid) void apply('setFriendlyName', trimmed) }
  return (
    <Row label="Name">
      <span className="lg-name">
        <input className="m-text" aria-label="Name" maxLength={max} value={name} disabled={busy}
          onChange={e => setName(e.target.value)} onKeyDown={e => { if (e.key === 'Enter') rename() }} />
        <button type="button" className="m-button" disabled={busy || !valid} onClick={rename}>Rename</button>
      </span>
    </Row>
  )
}


// ------------------------------------------------------------------ low power (Razer)

type LowPowerClient = { getLowPowerOptions?: () => number[]; getLowPowerPollingCeiling?: () => number }

/** Razer's low power mode: whole percentages from the driver's list; refused above its polling ceiling. */
function LowPower({ status, client, apply, busy }: Section) {
  const current = status.lowBatteryWarning
  if (current == null || !can(client, 'setLowPowerThreshold') || !can(client, 'getLowPowerOptions')) return null
  const own = client as unknown as LowPowerClient
  const offered = own.getLowPowerOptions?.() ?? []
  if (!offered.length || current < offered[0]! || current > offered[offered.length - 1]!) return null
  const values = offered.includes(current) ? offered : [...offered, current].sort((a, b) => a - b)
  const ceiling = own.getLowPowerPollingCeiling?.() ?? Infinity
  const tooFast = status.pollingRateHz > ceiling
  return (
    <Panel title="Low power mode">
      <Row label="Turn on below">
        {tooFast ? <span className="m-state">Off above {ceiling.toLocaleString()} Hz</span> : (
          <Choose label="Turn on below" options={values.map(v => ({ value: v, label: `${v}%` }))} value={current}
            onChange={v => void apply('setLowPowerThreshold', v)} disabled={busy} />
        )}
      </Row>
    </Panel>
  )
}
