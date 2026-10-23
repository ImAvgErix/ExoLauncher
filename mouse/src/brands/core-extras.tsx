import { useState, type ReactNode } from 'react'
import type { MouseLighting, MouseStatus } from '@openmouse/protocol/drivers/mouse-types'
import {
  RAZER_BUTTON_CONTROLS,
  RAZER_BUTTON_CONTROL_LABEL,
  RAZER_BUTTON_MAPPINGS,
  RAZER_LOCKED_BUTTON_CONTROL,
  RAZER_TOGGLE_CONTROLS,
  RAZER_TOGGLE_CONTROL_INFO,
} from '@openmouse/protocol/razer'
import { can } from '../mouse'
import type { Section } from '../section'
import { ColorField, Dropdown, NumberField, Panel, Row, Segmented, Slider, Switch } from '../ui'
import './core-extras.css'

/**
 * Settings many brands share that the generic panels cover only in part: the
 * full DPI stage table, every lighting zone, the DPI indicator, sensor and
 * wheel extras, Razer's button controls and the receiver. Logitech's sensor
 * surface, LightForce, signal and Razer low power belong to the Logitech group.
 * Every row needs both the status field and the driver's setter.
 */
export function CoreExtrasPanels(section: Section) {
  return (
    <>
      <Sensitivity {...section} />
      <Sensor {...section} />
      <ButtonsAndWheel {...section} />
      <Lighting {...section} />
      <DpiIndicator {...section} />
      <Receiver {...section} />
    </>
  )
}

/** When App.tsx's own panels should step aside for the ones here. */
export function coreExtrasReplaces(section: Section) {
  return {
    sensitivity: replacesSensitivity(section),
    liftOff: replacesLiftOff(section),
    lighting: lightingZones(section.status).length > 0 && can(section.client, 'setLighting'),
  }
}

// ------------------------------------------------------------------ shared bits


/** A slider over a fixed list of values the mouse accepts, by position. */
function ListSlider({ label, values, value, format, onCommit, disabled = false }: {
  label: string
  values: readonly number[]
  value: number | null | undefined
  format: (v: number) => string
  onCommit: (v: number) => void
  disabled?: boolean
}) {
  const index = Math.max(0, nearestIndex(values, value ?? values[0] ?? 0))
  return (
    <Slider label={label} min={0} max={Math.max(0, values.length - 1)} step={1} value={index}
      format={i => format(values[i] ?? 0)} onCommit={i => { const v = values[i]; if (v !== undefined) onCommit(v) }} disabled={disabled} />
  )
}

function nearestIndex(values: readonly number[], target: number): number {
  let best = -1
  let gap = Infinity
  values.forEach((v, i) => { const d = Math.abs(v - target); if (d < gap) { gap = d; best = i } })
  return best
}

/** Driver labels are title case; Exo shows them in sentence case. */
function sentence(text: string): string {
  return text ? text.charAt(0).toUpperCase() + text.slice(1).toLowerCase() : text
}

/** A dropdown whose options carry display labels apart from the values sent. */
function Choose<T extends string | number>({ label, options, value, onChange, disabled = false }: {
  label: string
  options: readonly { value: T; label: string }[]
  value: T | null | undefined
  onChange: (value: T) => void
  disabled?: boolean
}) {
  return <Dropdown label={label} options={options} value={value} onChange={onChange} disabled={disabled} />
}

function signed(v: number): string {
  return `${v > 0 ? '+' : ''}${v}°`
}

function seconds(v: number): string {
  if (v <= 0) return 'Never'
  return v < 60 ? `${v} s` : v % 60 === 0 ? `${v / 60} min` : `${(v / 60).toFixed(1)} min`
}

// ------------------------------------------------------------------ sensitivity

function replacesSensitivity({ status }: Section): boolean {
  return !!status.ui?.dpiStageEditor && (status.dpiStages?.length ?? 0) > 0
}

/**
 * The whole stage table: how many stages the DPI button cycles, each stage's
 * value and color, and which one is live. Stages are 0-based in every driver.
 */
function Sensitivity(section: Section) {
  const { status, client, apply, busy } = section
  if (!replacesSensitivity(section)) return null
  const editor = status.ui!.dpiStageEditor!
  const stages = status.dpiStages!
  const colors = status.dpiStageColors
  const showColors = (colors?.length ?? 0) >= stages.length
  const canColor = showColors && can(client, 'setDpiStageColor')
  const canValue = can(client, 'setDpiStageValue')
  const canActive = can(client, 'setActiveDpiStage')
  const canCount = editor.countEditable === true && can(client, 'setDpiStageCount')
  // X and Y per stage where the mouse stores both and the driver writes one axis while keeping the other.
  const axesSetter = ['setDpiStageAxes', 'setLunaFuryDpiStageAxes'].find(m => can(client, m))
  const separate = status.lunafury?.separateDpiAxes
  const canSeparate = separate != null && can(client, 'setLunaFurySeparateDpiAxes')
  const stagesY = canValue && axesSetter && separate !== false ? status.dpiStagesY : undefined
  const sliderMax = Math.max(editor.minDpi + editor.stepDpi, Math.min(editor.maxDpi, 6400))
  const counts = Array.from({ length: Math.max(1, editor.maxStages) }, (_, i) => i + 1)
  return (
    <Panel title="Sensitivity">
      {!canValue && can(client, 'setDpi') ? (
        <Row label="DPI">
          <NumberField label="DPI" min={editor.minDpi} max={editor.maxDpi} step={editor.stepDpi} value={status.dpi}
            onCommit={v => void apply('setDpi', v)} disabled={busy} />
        </Row>
      ) : null}
      {status.dpiY != null && status.dpiY !== status.dpi ? (
        <Row label="Vertical DPI"><span className="m-state">{status.dpiY}</span></Row>
      ) : null}
      {canCount ? (
        <Row label="Stages">
          <Segmented label="Stages" options={counts.map(n => ({ value: n, label: String(n) }))} value={stages.length}
            onChange={n => void apply('setDpiStageCount', n)} disabled={busy} />
        </Row>
      ) : null}
      {canSeparate ? (
        <Row label="Separate X and Y">
          <Switch label="Separate X and Y" checked={separate === true} onChange={on => void apply('setLunaFurySeparateDpiAxes', on)} disabled={busy} />
        </Row>
      ) : null}
      {stages.length > 1 ? (
        <Row label="Active stage">
          {canActive ? (
            <Segmented label="Active stage" options={stages.map((dpi, i) => ({ value: i, label: String(dpi), color: showColors ? colors![i] : undefined }))}
              value={status.activeDpiStage} onChange={i => void apply('setActiveDpiStage', i)} disabled={busy} />
          ) : (
            <span className="m-state">{status.activeDpiStage != null ? `Stage ${status.activeDpiStage + 1}` : 'Unknown'}</span>
          )}
        </Row>
      ) : null}
      {stages.map((dpi, i) => {
        const y = status.dpiStagesY?.[i]
        const editY = axesSetter && stagesY?.[i] != null ? stagesY[i] : null
        return (
          <Row key={i} label={`Stage ${i + 1}`} value={editY == null && separate !== false && y != null && y !== dpi ? `Vertical ${y}` : status.activeDpiStage === i && stages.length > 1 ? 'Active' : undefined}>
            <span className="cx-stage">
              {canColor ? (
                <ColorField label={`Stage ${i + 1} color`} value={colors![i]!} disabled={busy}
                  onCommit={hex => void apply('setDpiStageColor', i, hex)} />
              ) : showColors ? <span className="m-seg-dot" style={{ background: colors![i] }} /> : null}
              {editY != null ? (
                <span className="cx-pair">
                  <NumberField label={`Stage ${i + 1} X DPI`} min={editor.minDpi} max={editor.maxDpi} step={editor.stepDpi} suffix="X"
                    value={dpi} onCommit={v => void apply(axesSetter!, i, v, editY)} disabled={busy} />
                  <NumberField label={`Stage ${i + 1} Y DPI`} min={editor.minDpi} max={editor.maxDpi} step={editor.stepDpi} suffix="Y"
                    value={editY} onCommit={v => void apply(axesSetter!, i, dpi, v)} disabled={busy} />
                </span>
              ) : canValue ? (
                <>
                  <Slider label={`Stage ${i + 1} DPI`} min={editor.minDpi} max={sliderMax} step={editor.stepDpi}
                    value={Math.min(dpi, sliderMax)} onCommit={v => void apply('setDpiStageValue', i, v)} disabled={busy} />
                  <NumberField label={`Stage ${i + 1} DPI`} min={editor.minDpi} max={editor.maxDpi} step={editor.stepDpi}
                    value={dpi} onCommit={v => void apply('setDpiStageValue', i, v)} disabled={busy} />
                </>
              ) : <span className="m-state">{dpi}</span>}
            </span>
          </Row>
        )
      })}
    </Panel>
  )
}

// ------------------------------------------------------------------ sensor

const LOD_LEVELS = ['Low', 'Medium', 'High'] as const
type Lod = (typeof LOD_LEVELS)[number]
const SENSOR_MODES = [{ value: 'Eco', label: 'Eco' }, { value: 'High', label: 'High' }, { value: 'Ultra', label: 'Ultra' }] as const
// Razer's names (Focus Pro): Smart Tracking, or a lift-off and landing of your own.
const LOD_MODE = [{ value: 'single', label: 'Smart Tracking' }, { value: 'asymmetric', label: 'Asymmetric Cut-off' }] as const

function replacesLiftOff({ status, client }: Section): boolean {
  if (status.finalmousePawLodMm != null) return false
  return (status.asymmetricLiftOff != null && can(client, 'setLiftOff'))
    || (status.liftOffScale != null && can(client, 'setLiftOffScale'))
}

function isPulsarPro(status: MouseStatus): boolean {
  return status.connectionDetail?.includes('Pulsar Pro protocol') === true
}

/** Profile command 0x13 by each brand's own name for it (their hubs; Exo's generic name otherwise). */
function performanceLabel(status: MouseStatus): string {
  if (status.brand === 'CRDRAKO') return 'Competition mode'
  if (status.brand === 'Lamzu') return '20K FPS'
  if (status.brand === 'LunaFury') return 'Tracking mode'
  if (status.brand === 'Teevolution') return 'Highest performance'
  if (status.brand === 'Pulsar') return isPulsarPro(status) ? 'Turbo mode' : 'Highest performance'
  return 'Performance mode'
}

/** Command 0x0b by each brand's own name for it. */
function hyperLabel(status: MouseStatus): string {
  if (status.brand === 'Lamzu') return 'Competition mode'
  if (status.brand === 'LunaFury') return 'Competitive mode'
  if (status.brand === 'WLMouse' || status.brand === 'CRDRAKO') return 'High-speed mode'
  return 'Hyper mode'
}

function Sensor(section: Section) {
  const { status, client, apply, busy } = section
  const rows: ReactNode[] = []

  if (status.sensorMode != null && status.brand !== 'Teevolution' && can(client, 'setSensorMode')) {
    rows.push(
      <Row key="sm" label="Sensor sampling">
        {status.sensorModeEditable === false
          ? <span className="m-state">{status.sensorMode}</span>
          : <Segmented label="Sensor sampling" options={SENSOR_MODES} value={status.sensorMode} onChange={v => void apply('setSensorMode', v)} disabled={busy} />}
      </Row>,
    )
  }
  const perfHidden = status.brand === 'Endgame Gear' || status.brand === 'Finalmouse' || (status.brand === 'LunaFury' && status.hyperMode !== true)
  if (status.performanceMode != null && !perfHidden && can(client, 'setPerformanceMode')) {
    const label = performanceLabel(status)
    rows.push(
      <Row key="pm" label={label}>
        {/* Lamzu's 20K FPS works only while Competition mode is on. */}
        <Switch label={label} checked={status.performanceMode} onChange={v => void apply('setPerformanceMode', v)} disabled={busy || (status.brand === 'Lamzu' && status.hyperMode === false)} />
      </Row>,
    )
  }
  if (status.hyperMode != null && can(client, 'setHyperMode')) {
    const label = hyperLabel(status)
    rows.push(
      <Row key="hm" label={label}>
        <Switch label={label} checked={status.hyperMode} onChange={v => void apply('setHyperMode', v)} disabled={busy} />
      </Row>,
    )
  }
  if (status.turboMode != null && can(client, 'setTurboMode')) {
    rows.push(
      <Row key="tm" label="Turbo mode">
        <Switch label="Turbo mode" checked={status.turboMode} onChange={v => void apply('setTurboMode', v)} disabled={busy || status.hyperMode === false} />
      </Row>,
    )
  }
  if (status.angleTuning != null && !isPulsarPro(status) && status.atkSensorMode == null && can(client, 'setAngleTuning')) {
    // OpenMouse offers -30 to +30; a wider value set elsewhere shows as it is.
    const angle = status.angleTuning
    rows.push(
      <Row key="at" label="Sensor angle">
        {angle < -30 || angle > 30
          ? <span className="m-state">{signed(angle)}</span>
          : <Slider label="Sensor angle" min={-30} max={30} step={1} value={angle} format={signed}
              onCommit={v => void apply('setAngleTuning', v)} disabled={busy} />}
      </Row>,
    )
  }
  if (replacesLiftOff(section)) rows.push(...liftOffRows(section))
  return rows.length ? <Panel title="Sensor">{rows}</Panel> : null
}

/**
 * Lift-off for mice the generic three-stop row cannot describe: a separate
 * cut-off and landing pair (Razer) or a continuous height. Landing always
 * stays below lift-off, as the vendor software keeps it.
 */
function liftOffRows({ status, client, apply, busy }: Section): ReactNode[] {
  const rows: ReactNode[] = []
  const pair = status.asymmetricLiftOff
  const scale = status.liftOffScale
  const ui = status.ui ?? {}
  const canSingle = can(client, 'setLiftOffDistance')
  if (pair && can(client, 'setLiftOff')) {
    // A mouse can hold a pair the vendor software never writes (unset, or landing above
    // lift-off); the controls start from the nearest pair the mouse takes, so choosing
    // Asymmetric always sends a valid one.
    const liftOff = Math.min(pair.liftOffRange.max, Math.max(pair.liftOffRange.min, pair.landingRange.min + 1, pair.liftOff))
    const startLanding = Math.min(pair.landingRange.max, Math.max(pair.landingRange.min, Math.min(pair.landing, liftOff - 1)))
    if (canSingle) {
      rows.push(
        <Row key="lodm" label="Tracking">
          <Segmented label="Tracking" options={LOD_MODE} value={pair.enabled == null ? null : pair.enabled ? 'asymmetric' : 'single'}
            onChange={v => void (v === 'asymmetric'
              ? apply('setLiftOff', liftOff, startLanding)
              : apply('setLiftOffDistance', status.liftOffDistance ?? 'Medium'))}
            disabled={busy} />
        </Row>,
      )
    }
    if (pair.enabled === true || !canSingle) {
      rows.push(
        <Row key="lodl" label="Lift-off distance">
          <Slider label="Lift-off distance" min={pair.liftOffRange.min} max={pair.liftOffRange.max} step={1} value={liftOff} format={String}
            onCommit={v => void apply('setLiftOff', v, Math.max(pair.landingRange.min, Math.min(startLanding, v - 1)))} disabled={busy} />
        </Row>,
        <Row key="lodg" label="Landing distance">
          <Slider label="Landing distance" min={pair.landingRange.min} max={pair.landingRange.max} step={1} value={startLanding} format={String}
            onCommit={v => void apply('setLiftOff', liftOff, Math.min(v, liftOff - 1))} disabled={busy} />
        </Row>,
      )
      return rows
    }
  } else if (scale && can(client, 'setLiftOffScale')) {
    const span = Math.max(1, scale.max - scale.min)
    const mm = (code: number) => scale.minMillimetres + ((code - scale.min) * (scale.maxMillimetres - scale.minMillimetres)) / span
    rows.push(
      <Row key="lods" label="Lift-off distance">
        <Slider label="Lift-off distance" min={scale.min} max={scale.max} step={1} value={scale.value} format={c => `${mm(c).toFixed(1)} mm`}
          onCommit={v => void apply('setLiftOffScale', v)} disabled={busy} />
      </Row>,
    )
    return rows
  }
  if (status.liftOffDistance != null && canSingle) {
    const supported = status.supportedLiftOffDistances
    const levels = LOD_LEVELS.filter(level => {
      if (level === 'Low' && (ui.hideLodLow || status.brand === 'Endgame Gear')) return false
      if (supported) return supported.includes(level)
      return !(level === 'Low' && status.brand === 'Logitech')
    })
    rows.push(
      <Row key="lod" label="Lift-off distance">
        <Segmented<Lod> label="Lift-off distance" options={levels.map(v => ({ value: v, label: v }))} value={status.liftOffDistance}
          onChange={v => void apply('setLiftOffDistance', v)} disabled={busy} />
      </Row>,
    )
  }
  return rows
}

// ------------------------------------------------------------------ buttons and wheel

const DIRECTION = [{ value: 'Forward', label: 'Normal' }, { value: 'Reverse', label: 'Reversed' }] as const
const PRIMARY = [{ value: 'Left', label: 'Left' }, { value: 'Right', label: 'Right' }] as const

function ButtonsAndWheel({ status, client, apply, busy }: Section) {
  const rows: ReactNode[] = []
  const razer = status.razerButtonMappings
  if (razer) {
    if (can(client, 'setButtonMapping')) {
      for (const control of RAZER_BUTTON_CONTROLS) {
        const current = razer[control]
        if (!current) continue
        const name = sentence(RAZER_BUTTON_CONTROL_LABEL[control])
        rows.push(
          <Row key={`rb-${control}`} label={name}>
            {control === RAZER_LOCKED_BUTTON_CONTROL
              ? <span className="m-state">{sentence(current)}</span>
              : <Choose<string> label={name} options={RAZER_BUTTON_MAPPINGS.map(m => ({ value: m, label: sentence(m) }))} value={current}
                  onChange={v => void apply('setButtonMapping', control, v)} disabled={busy} />}
          </Row>,
        )
      }
    }
    if (can(client, 'setToggleControl')) {
      for (const control of RAZER_TOGGLE_CONTROLS) {
        const current = razer[control]
        if (!current) continue
        const info = RAZER_TOGGLE_CONTROL_INFO[control]
        const name = sentence(info.label)
        rows.push(
          <Row key={`rt-${control}`} label={name}>
            <Segmented label={name} options={[{ value: info.enabledLabel, label: info.enabledLabel === info.label ? 'On' : sentence(info.enabledLabel) }, { value: 'Disabled', label: 'Off' }]}
              value={current} onChange={v => void apply('setToggleControl', control, v)} disabled={busy} />
          </Row>,
        )
      }
    }
  }
  if (status.primaryButton != null && can(client, 'setPrimaryButton')) {
    rows.push(
      <Row key="pb" label="Primary button">
        <Segmented label="Primary button" options={PRIMARY} value={status.primaryButton} onChange={v => void apply('setPrimaryButton', v)} disabled={busy} />
      </Row>,
    )
  }
  if (status.scrollDirection != null && status.ui?.family !== 'ksnake' && can(client, 'setScrollDirection')) {
    rows.push(
      <Row key="sd" label="Scroll direction">
        <Segmented label="Scroll direction" options={DIRECTION} value={status.scrollDirection} onChange={v => void apply('setScrollDirection', v)} disabled={busy} />
      </Row>,
    )
  }
  if (status.buttonCombination != null && can(client, 'setButtonCombination')) {
    rows.push(
      <Row key="bc" label="Button combination">
        <Switch label="Button combination" checked={status.buttonCombination} onChange={v => void apply('setButtonCombination', v)} disabled={busy} />
      </Row>,
    )
  }
  return rows.length ? <Panel title={razer ? 'Button controls' : 'Buttons and wheel'}>{rows}</Panel> : null
}

// ------------------------------------------------------------------ lighting

function lightingZones(status: MouseStatus): MouseLighting[] {
  if (status.lightingZones?.length) return status.lightingZones
  return status.lighting ? [status.lighting] : []
}

function zoneName(zone: MouseLighting): string {
  return sentence(zone.group ? `${zone.group} ${zone.zone}` : zone.zone)
}

/**
 * Every lit zone: effect, colors, speed and brightness. The driver takes the
 * whole zone (it finds the zone by name or LED id), so each change sends the
 * zone as the mouse reported it with the one field changed.
 */
function Lighting({ status, client, apply, busy }: Section) {
  const zones = lightingZones(status)
  const [picked, setPicked] = useState(() => (zones.some(z => z.hardwareZoneId != null) && zones.length > 1 ? 1 : 0))
  if (!zones.length || !can(client, 'setLighting')) return null
  const index = Math.min(picked, zones.length - 1)
  const light = zones[index]!
  const set = (patch: Partial<MouseLighting>) => {
    const next = { ...light, ...patch }
    if (next.mode) void apply('setLighting', next)
  }
  const mode = light.mode
  const usesColor = mode != null && light.colorModes.includes(mode)
  const usesColor2 = mode != null && light.dualColorModes.includes(mode)
  const usesSpeed = mode != null && light.reactiveModes.includes(mode) && light.speeds.length > 0
  const speeds = [...light.speeds].sort((a, b) => a - b)
  const levels = light.brightnessLevels ?? []
  return (
    <Panel title="Lighting">
      {zones.length > 1 ? (
        <Row label="Zone">
          {zones.length <= 4
            ? <Segmented label="Zone" options={zones.map((z, i) => ({ value: i, label: zoneName(z) }))} value={index} onChange={setPicked} />
            : <Choose label="Zone" options={zones.map((z, i) => ({ value: i, label: zoneName(z) }))} value={index} onChange={setPicked} />}
        </Row>
      ) : null}
      <Row label="Effect" value={zones.length > 1 ? undefined : zoneName(light)}>
        {light.modes.length <= 4
          ? <Segmented label="Effect" options={light.modes.map(m => ({ value: m, label: sentence(m) }))} value={mode} onChange={m => set({ mode: m })} disabled={busy} />
          : <Choose label="Effect" options={light.modes.map(m => ({ value: m, label: sentence(m) }))} value={mode} onChange={m => set({ mode: m })} disabled={busy} />}
      </Row>
      {usesColor ? (
        <Row label={usesColor2 ? 'Colors' : 'Color'}>
          <span className="cx-pair">
            <ColorField label="Color" value={light.color ?? '#ffffff'} onCommit={hex => set({ color: hex })} disabled={busy} />
            {usesColor2 ? <ColorField label="Second color" value={light.color2 ?? '#ff0000'} onCommit={hex => set({ color2: hex })} disabled={busy} /> : null}
          </span>
        </Row>
      ) : null}
      {usesSpeed ? (
        <Row label="Speed">
          {speeds.length <= 6
            ? <Segmented label="Speed" options={speeds.map(s => ({ value: s, label: String(s) }))} value={light.speed} onChange={s => set({ speed: s })} disabled={busy} />
            : <ListSlider label="Speed" values={speeds} value={light.speed} format={String} onCommit={s => set({ speed: s })} disabled={busy} />}
        </Row>
      ) : null}
      {levels.length > 0 && mode !== 'Off' ? (
        <Row label="Brightness">
          {levels.length <= 5
            ? <Segmented label="Brightness" options={levels.map(v => ({ value: v, label: `${v}%` }))} value={light.brightness ?? null} onChange={v => set({ brightness: v })} disabled={busy} />
            : <ListSlider label="Brightness" values={levels} value={light.brightness} format={v => `${v}%`} onCommit={v => set({ brightness: v })} disabled={busy} />}
        </Row>
      ) : null}
    </Panel>
  )
}

// ------------------------------------------------------------------ dpi indicator

const LED_MODES = [{ value: 0, label: 'Off' }, { value: 1, label: 'Steady' }, { value: 2, label: 'Breathing' }] as const

/**
 * The light that shows the DPI stage. Effect, brightness and speed go to the
 * mouse together, so each change carries the other two as the mouse has them.
 */
function DpiIndicator({ status, client, apply, busy }: Section) {
  const hint = status.ui?.dpiLighting
  if (!hint || status.dpiLedMode == null || !can(client, 'setDpiLighting')) return null
  const mode = status.dpiLedMode
  const brightness = status.dpiLedBrightness ?? hint.brightness[0] ?? 5
  const speed = status.dpiLedSpeed ?? hint.speed[0] ?? 3
  const write = (m: number, b: number, s: number) => void apply('setDpiLighting', m, b, s)
  const sleeps = hint.sleepTimeouts ?? []
  return (
    <Panel title="DPI indicator">
      <Row label="Effect">
        <Segmented label="Effect" options={LED_MODES.filter(o => hint.modes.includes(o.value))} value={mode}
          onChange={m => write(m, brightness, speed)} disabled={busy} />
      </Row>
      {mode !== 0 && status.dpiLedBrightness != null && hint.brightness.length > 1 ? (
        <Row label="Brightness">
          <ListSlider label="Brightness" values={hint.brightness} value={status.dpiLedBrightness} format={String}
            onCommit={b => write(mode, b, speed)} disabled={busy} />
        </Row>
      ) : null}
      {mode === 2 && status.dpiLedSpeed != null && hint.speed.length > 1 ? (
        <Row label="Speed">
          <ListSlider label="Speed" values={hint.speed} value={status.dpiLedSpeed} format={String}
            onCommit={s => write(mode, brightness, s)} disabled={busy} />
        </Row>
      ) : null}
      {sleeps.length > 0 && status.dpiLedSleepTimeout != null && can(client, 'setDpiLedSleepTimeout') ? (
        <Row label="Light off after">
          <Choose label="Light off after" options={sleeps.map(s => ({ value: s, label: seconds(s) }))} value={status.dpiLedSleepTimeout}
            onChange={s => void apply('setDpiLedSleepTimeout', s)} disabled={busy} />
        </Row>
      ) : null}
    </Panel>
  )
}

// ------------------------------------------------------------------ receiver

function Receiver({ status, client, apply, busy }: Section) {
  const rows: ReactNode[] = []
  if (status.dongleLedEnabled != null && can(client, 'setDongleLed')) {
    rows.push(
      <Row key="led" label="Receiver light">
        <Switch label="Receiver light" checked={status.dongleLedEnabled} onChange={v => void apply('setDongleLed', v)} disabled={busy} />
      </Row>,
    )
  }
  if (status.longRangeMode != null && can(client, 'setLongRangeMode')) {
    rows.push(
      <Row key="lr" label="Ultra long range">
        <Switch label="Ultra long range" checked={status.longRangeMode} onChange={v => void apply('setLongRangeMode', v)} disabled={busy} />
      </Row>,
    )
  }
  return rows.length ? <Panel title="Wireless">{rows}</Panel> : null
}
