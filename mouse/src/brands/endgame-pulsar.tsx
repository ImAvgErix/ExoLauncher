import { useEffect, useState } from 'react'
import type { MouseStatus } from '@openmouse/protocol/drivers/mouse-types'
import { EGG_BUTTON_ACTION_OPTIONS } from '@openmouse/protocol/endgame-gear-op1'
import { can } from '../mouse'
import type { Section } from '../section'
import { Dropdown, Panel, Row, Segmented, Select, Slider, Switch } from '../ui'
import './endgame-pulsar.css'

/**
 * Endgame Gear (K26/W26 "OP1" platform and the OT platform) and Pulsar Pro
 * protocol settings, adapted from OpenMouse's EggFilterCard, EggSpdtCard,
 * EggPollingCard, EggCpiCard, EggButtonCard and PulsarProCard (AGPL-3.0),
 * plus the OP1 driver's own lift-off, sensor and handedness setters.
 */
export function EndgamePulsarPanels(section: Section) {
  return (
    <>
      <EggSensor {...section} />
      <CpiStages {...section} />
      <EggSwitches {...section} />
      <EggButtons {...section} />
      <EggMulticlick {...section} />
      <OtSensor {...section} />
      <PulsarPro {...section} />
    </>
  )
}

/** Fields the OP1 and OT drivers add to MouseStatus. */
type EggStatus = MouseStatus & {
  eggCpiMin?: number
  eggCpiMax?: number
  eggCpiStepLow?: number
  eggCpiStepHigh?: number
  eggLodIndex?: number
  eggLodOptions?: string[]
  eggAngleTuning?: number
  eggForceMaxFps?: boolean
  eggLedLiftOffDisabled?: boolean
  eggLeftHanded?: boolean
  eggMotionSyncAt8k?: boolean
  eggButtonActions?: Array<{ key: string }>
  otCpiStages?: Array<{ x: number; y: number; active: boolean }>
  otCpiMin?: number
  otCpiMax?: number
  otSlamClickFilter?: boolean
  otMotionSyncResponsive?: boolean
  otMotionSyncSmooth?: boolean
}

const has = <T,>(v: T | null | undefined): v is T => v !== null && v !== undefined

// ------------------------------------------------------------------ OP1 sensor

function EggSensor({ status: base, client, apply, applyAll, busy }: Section) {
  const status = base as EggStatus
  const glass = status.eggGlassMode === true
  const lods = status.eggLodOptions
  const divider = status.eggPollingDivider
  const rows = [
    has(lods) && lods.length > 0 && has(status.eggLodIndex) && can(client, 'setEggLodIndex') && (
      <Row key="lod" label="Lift-off distance" value={glass ? 'Glass mode' : undefined}>
        {lods.length <= 4 ? (
          <Segmented label="Lift-off distance" options={lods.map((l, i) => ({ value: i, label: l }))} value={status.eggLodIndex}
            onChange={v => void apply('setEggLodIndex', v)} disabled={busy} />
        ) : (
          <Select label="Lift-off distance" options={lods} value={lods[status.eggLodIndex] ?? ''}
            onChange={v => { const i = lods.indexOf(v); if (i >= 0) void apply('setEggLodIndex', i) }} disabled={busy} />
        )}
      </Row>
    ),
    status.eggSupportsGlassMode === true && has(status.eggGlassMode) && can(client, 'setGlassMode') && (
      <Row key="glass" label="Glass mode">
        <Switch label="Glass mode" checked={glass} onChange={v => void apply('setGlassMode', v)} disabled={busy} />
      </Row>
    ),
    glass && (
      <Row key="glass-poll" label="Polling rate">
        <span className="m-state">Set by glass mode</span>
      </Row>
    ),
    !glass && has(divider) && can(client, 'setCustomPollingDivider') && (
      <Row key="div" label="Polling divider">
        <Slider label="Polling divider" min={1} max={255} step={1} value={divider} format={v => `${formatHz(8000 / v)} Hz`}
          onCommit={v => {
            // setPollingRate turns motion sync off at 8000 Hz on mice that cannot run both; the divider setter does not.
            if (v === 1 && status.eggMotionSyncAt8k === false && status.motionSync === true && can(client, 'setMotionSync')) {
              void applyAll([['setMotionSync', [false]], ['setCustomPollingDivider', [1]]])
            } else void apply('setCustomPollingDivider', v)
          }} disabled={busy} />
      </Row>
    ),
    has(status.eggAngleTuning) && can(client, 'setSensorAngleTuning') && (
      <Row key="angle" label="Angle tuning">
        <Slider label="Angle tuning" min={-127} max={127} step={1} value={status.eggAngleTuning} format={v => String(v)}
          onCommit={v => void apply('setSensorAngleTuning', v)} disabled={busy} />
      </Row>
    ),
    has(status.eggForceMaxFps) && can(client, 'setForceMaxSensorFps') && (
      <Row key="fps" label="Force max sensor FPS">
        <Switch label="Force max sensor FPS" checked={status.eggForceMaxFps} onChange={v => void apply('setForceMaxSensorFps', v)} disabled={busy} />
      </Row>
    ),
    has(status.eggLedLiftOffDisabled) && can(client, 'setLedLiftOffDisabled') && (
      <Row key="led" label="Light off when lifted">
        <Switch label="Light off when lifted" checked={status.eggLedLiftOffDisabled} onChange={v => void apply('setLedLiftOffDisabled', v)} disabled={busy} />
      </Row>
    ),
    has(status.slamclickFilter) && can(client, 'setSlamclickFilter') && (
      <Row key="slam" label="Slamclick filter">
        <Switch label="Slamclick filter" checked={status.slamclickFilter} onChange={v => void apply('setSlamclickFilter', v)} disabled={busy} />
      </Row>
    ),
    has(status.motionJitterFilter) && can(client, 'setMotionJitterFilter') && (
      <Row key="jitter" label="Motion jitter filter">
        <Switch label="Motion jitter filter" checked={status.motionJitterFilter} onChange={v => void apply('setMotionJitterFilter', v)} disabled={busy} />
      </Row>
    ),
  ].filter(Boolean)
  return rows.length ? <Panel title="Sensor">{rows}</Panel> : null
}

function formatHz(v: number): string {
  return Number.isInteger(v) ? String(v) : v.toFixed(1)
}

// ------------------------------------------------------------------ CPI stages (OP1 and OT)

type StageRules = { min: number; max: number; stepLow: number; stepHigh: number }

/** The step that applies at a value: the fine step up to 10,000, the coarse one above. */
function snapCpi(value: number, rules: StageRules): number {
  const clamped = Math.max(rules.min, Math.min(rules.max, Math.round(value)))
  const step = clamped <= 10_000 ? rules.stepLow : rules.stepHigh
  return Math.max(rules.min, Math.min(rules.max, Math.floor((clamped + step / 2) / step) * step))
}

function CpiStages({ status: base, client, apply, busy }: Section) {
  const status = base as EggStatus
  // OP1: four stored stages, the first eggCpiLevels of them enabled.
  if (has(status.eggCpiStages) && has(status.eggCpiLevels) && can(client, 'setCpiStage')) {
    const rules: StageRules = {
      min: status.eggCpiMin ?? 50,
      max: status.eggCpiMax ?? 26_000,
      stepLow: status.eggCpiStepLow ?? 50,
      stepHigh: status.eggCpiStepHigh ?? 50,
    }
    const levels = status.eggCpiLevels
    return (
      <Panel title="CPI stages">
        {can(client, 'setCpiLevels') ? (
          <Row label="Stages">
            <Segmented label="Stages" options={[1, 2, 3, 4].map(n => ({ value: n, label: String(n) }))} value={levels}
              onChange={v => void apply('setCpiLevels', v)} disabled={busy} />
          </Row>
        ) : null}
        {status.eggCpiStages.slice(0, levels).map((stage, i) => (
          <Row key={i} label={`Stage ${i + 1}`}>
            <AxisPair stage={stage} rules={rules} disabled={busy} onCommit={(x, y) => void apply('setCpiStage', i, x, y)} />
          </Row>
        ))}
      </Panel>
    )
  }
  // OT: every stage stored; the selected one is marked.
  if (has(status.otCpiStages) && status.otCpiStages.length > 0 && can(client, 'setCpiStage')) {
    const rules: StageRules = { min: status.otCpiMin ?? 50, max: status.otCpiMax ?? 26_000, stepLow: 50, stepHigh: 100 }
    return (
      <Panel title="CPI stages">
        {status.otCpiStages.map((stage, i) => (
          <Row key={i} label={`Stage ${i + 1}`} value={stage.active ? 'Selected' : undefined}>
            <AxisPair stage={stage} rules={rules} disabled={busy} onCommit={(x, y) => void apply('setCpiStage', i, x, y)} />
          </Row>
        ))}
      </Panel>
    )
  }
  return null
}

/** X and Y, each written with the other axis's current value, snapped to the mouse's steps. */
function AxisPair({ stage, rules, disabled, onCommit }: {
  stage: { x: number; y: number }
  rules: StageRules
  disabled: boolean
  onCommit: (x: number, y: number) => void
}) {
  return (
    <span className="egp-axes">
      <CpiField label="X" value={stage.x} rules={rules} disabled={disabled} onCommit={x => onCommit(x, snapCpi(stage.y, rules))} />
      <CpiField label="Y" value={stage.y} rules={rules} disabled={disabled} onCommit={y => onCommit(snapCpi(stage.x, rules), y)} />
    </span>
  )
}

function CpiField({ label, value, rules, disabled, onCommit }: {
  label: string
  value: number
  rules: StageRules
  disabled: boolean
  onCommit: (v: number) => void
}) {
  const [text, setText] = useState(String(value))
  useEffect(() => setText(String(value)), [value])
  const commit = () => {
    const n = Number(text)
    if (!text || !Number.isFinite(n)) { setText(String(value)); return }
    const snapped = snapCpi(n, rules)
    setText(String(snapped))
    if (snapped !== value) onCommit(snapped)
  }
  return (
    <span className="m-number">
      <input
        aria-label={label}
        inputMode="numeric"
        value={text}
        disabled={disabled}
        onChange={e => setText(e.target.value.replace(/[^\d]/g, ''))}
        onBlur={commit}
        onKeyDown={e => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur() }}
      />
      <span className="m-number-suffix">{label}</span>
    </span>
  )
}

// ------------------------------------------------------------------ OP1 main switches and buttons

const SPDT = [
  { value: 'Off', label: 'Off' },
  { value: 'GX Safe', label: 'GX Safe' },
  { value: 'GX Speed', label: 'GX Speed' },
] as const

function EggSwitches({ status: base, client, apply, busy }: Section) {
  const status = base as EggStatus
  const spdt = can(client, 'setSpdtMode')
  const rows = [
    spdt && has(status.leftSpdtMode) && (
      <Row key="l" label="Left click">
        <Segmented label="Left click" options={SPDT} value={status.leftSpdtMode} onChange={v => void apply('setSpdtMode', 'left', v)} disabled={busy} />
      </Row>
    ),
    spdt && has(status.rightSpdtMode) && (
      <Row key="r" label="Right click">
        <Segmented label="Right click" options={SPDT} value={status.rightSpdtMode} onChange={v => void apply('setSpdtMode', 'right', v)} disabled={busy} />
      </Row>
    ),
    has(status.eggLeftHanded) && can(client, 'setLeftHanded') && (
      <Row key="hand" label="Left-handed">
        <Switch label="Left-handed" checked={status.eggLeftHanded} onChange={v => void apply('setLeftHanded', v)} disabled={busy} />
      </Row>
    ),
  ].filter(Boolean)
  return rows.length ? <Panel title="Main switches">{rows}</Panel> : null
}

const EGG_BUTTON_NAMES = ['Left', 'Right', 'Middle', 'Forward', 'Back', 'Wheel up', 'Wheel down'] as const

/** The driver's actions that need no parameters, in sentence case. */
const EGG_ACTION_LABEL: Record<string, string> = {
  'mouse-left': 'Left click',
  'mouse-right': 'Right click',
  'mouse-middle': 'Middle click',
  'mouse-back': 'Back',
  'mouse-forward': 'Forward',
  'scroll-up': 'Wheel up',
  'scroll-down': 'Wheel down',
  'cpi-loop': 'CPI loop',
  'media-play': 'Play or pause',
  'media-next': 'Next track',
  'media-previous': 'Previous track',
  'media-mute': 'Mute',
  'media-volume-up': 'Volume up',
  'media-volume-down': 'Volume down',
  'browser-home': 'Browser home',
  'file-explorer': 'File explorer',
  disabled: 'Disabled',
}
const EGG_ACTIONS = EGG_BUTTON_ACTION_OPTIONS.filter(o => o.key in EGG_ACTION_LABEL)

/** The listed action key for a button; null for a keyboard shortcut, fixed CPI or preserved custom action. */
function eggActionKey(label: string, action: { key: string } | undefined): string | null {
  if (action) return action.key in EGG_ACTION_LABEL ? action.key : null
  return EGG_ACTIONS.find(o => o.label === label)?.key ?? null
}

function eggCurrentLabel(label: string): string {
  return label === 'Custom (preserved)' ? 'Custom' : label
}

function EggButtons({ status: base, client, apply, busy }: Section) {
  const status = base as EggStatus
  const mappings = status.eggButtonMappings
  if (!has(mappings) || !has(status.eggMulticlickFilters) || !can(client, 'setButtonMapping')) return null
  // The primary click stays fixed: left normally, right when left-handed.
  const fixed = status.eggLeftHanded ? 1 : 0
  return (
    <Panel title="Buttons">
      {mappings.slice(0, EGG_BUTTON_NAMES.length).map((current, i) => {
        const key = eggActionKey(current, status.eggButtonActions?.[i])
        const shown = key ? EGG_ACTION_LABEL[key] : eggCurrentLabel(current)
        return (
          <Row key={i} label={EGG_BUTTON_NAMES[i]}>
            {i === fixed ? <span className="m-state">{shown}</span> : (
              <Dropdown label={EGG_BUTTON_NAMES[i]} options={EGG_ACTIONS.map(o => ({ value: o.key as string, label: EGG_ACTION_LABEL[o.key] }))}
                value={key ?? null} placeholder={shown} disabled={busy}
                onChange={v => void apply('setButtonMapping', i, { key: v })} />
            )}
          </Row>
        )
      })}
    </Panel>
  )
}

function EggMulticlick({ status, client, apply, busy }: Section) {
  const filters = status.eggMulticlickFilters
  if (!has(filters) || !has(status.eggButtonMappings) || !can(client, 'setMulticlickFilter')) return null
  return (
    <Panel title="Multiclick filter">
      {filters.slice(0, 5).map((value, i) => {
        // GX mode replaces the multiclick filter on the main switches.
        const gx = (i === 0 && status.leftSpdtMode != null && status.leftSpdtMode !== 'Off')
          || (i === 1 && status.rightSpdtMode != null && status.rightSpdtMode !== 'Off')
        return (
          <Row key={i} label={EGG_BUTTON_NAMES[i]}>
            {gx ? <span className="m-state">GX mode</span> : (
              <Slider label={`${EGG_BUTTON_NAMES[i]} multiclick filter`} min={0} max={25} step={1} value={value} format={v => String(v)}
                onCommit={v => void apply('setMulticlickFilter', i, v)} disabled={busy} />
            )}
          </Row>
        )
      })}
    </Panel>
  )
}

// ------------------------------------------------------------------ OT platform

function OtSensor({ status: base, client, apply, busy }: Section) {
  const status = base as EggStatus
  const rows = [
    has(status.otSlamClickFilter) && can(client, 'setSlamClickFilter') && (
      <Row key="slam" label="Slamclick filter">
        <Switch label="Slamclick filter" checked={status.otSlamClickFilter} onChange={v => void apply('setSlamClickFilter', v)} disabled={busy} />
      </Row>
    ),
    has(status.otMotionSyncResponsive) && can(client, 'setMotionSyncResponsive') && (
      <Row key="msr" label="Motion sync, responsive">
        <Switch label="Motion sync, responsive" checked={status.otMotionSyncResponsive} onChange={v => void apply('setMotionSyncResponsive', v)} disabled={busy} />
      </Row>
    ),
    has(status.otMotionSyncSmooth) && can(client, 'setMotionSyncSmooth') && (
      <Row key="mss" label="Motion sync, smooth">
        <Switch label="Motion sync, smooth" checked={status.otMotionSyncSmooth} onChange={v => void apply('setMotionSyncSmooth', v)} disabled={busy} />
      </Row>
    ),
  ].filter(Boolean)
  return rows.length ? <Panel title="Sensor">{rows}</Panel> : null
}

// ------------------------------------------------------------------ Pulsar Pro

const PRO_PROFILES = [1, 2, 3, 4, 5, 6].map(n => ({ value: n, label: String(n) }))

function PulsarPro({ status, client, apply, busy }: Section) {
  // Same test OpenMouse uses: only the Pulsar Pro protocol driver says this.
  if (!status.connectionDetail?.includes('Pulsar Pro protocol')) return null
  const rows = [
    has(status.wheelAcceleration) && can(client, 'setWheelAcceleration') && (
      <Row key="wheel" label="Wheel acceleration">
        <Switch label="Wheel acceleration" checked={status.wheelAcceleration} onChange={v => void apply('setWheelAcceleration', v)} disabled={busy} />
      </Row>
    ),
    has(status.angleTuning) && can(client, 'setAngleTuning') && (
      <Row key="angle" label="Angle tuning">
        <Slider label="Angle tuning" min={-30} max={30} step={1} value={status.angleTuning} format={v => `${v}°`}
          onCommit={v => void apply('setAngleTuning', v)} disabled={busy} />
      </Row>
    ),
    // The generic profile row needs profileCount, which this driver does not report.
    has(status.activeProfile) && !status.profileCount && can(client, 'setProfile') && (
      <Row key="profile" label="Onboard profile">
        <Segmented label="Onboard profile" options={PRO_PROFILES} value={status.activeProfile} onChange={v => void apply('setProfile', v)} disabled={busy} />
      </Row>
    ),
  ].filter(Boolean)
  return rows.length ? <Panel title="Pulsar Pro">{rows}</Panel> : null
}
