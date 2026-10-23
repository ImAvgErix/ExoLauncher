import { useCallback, useEffect, useRef, useState } from 'react'
import { Slider } from '../ui/Slider'
import { host, type ColorControl, type DisplayChange, type DisplayScreen, type DisplayState, type OutputColor, type ScalingMode } from '../../lib/host'
import { report } from '../../lib/report'
import { GraphicsControl } from '../graphics/GraphicsControl'
import { Row, State, SwitchRow } from '../settings/parts'
import { Choice } from '../settings/Choice'

const FORMATS: { id: OutputColor['format']; label: string }[] = [
  { id: 'rgb', label: 'RGB' },
  { id: '444', label: '4:4:4' },
  { id: '422', label: '4:2:2' },
  { id: '420', label: '4:2:0' },
]

const FORMAT_NAME: Record<string, string> = { rgb: 'RGB', 444: 'YCbCr 4:4:4', 422: 'YCbCr 4:2:2', 420: 'YCbCr 4:2:0' }

function describe(color: OutputColor) {
  return `${FORMAT_NAME[color.format]} ${color.bpc}-bit${color.format === 'rgb' ? (color.full ? ', full range' : ', limited range') : ''}`
}

/**
 * System > Display: per screen, the refresh rate and what the cable carries (format, depth,
 * range), with "Best for this screen" (on NVIDIA also No scaling on the GPU, over games). A change applies and keeps
 * itself, so the next can follow at once: Exo offers only the modes the screen reports it takes.
 */
export function DisplayTab({ active }: { active: boolean }) {
  const [state, setState] = useState<DisplayState | null>(null)
  const [selected, setSelected] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<{ key: string; text: string } | null>(null)
  const busyRef = useRef(false)
  busyRef.current = busy

  /** Runs one action; the line it failed with (also shown under the row keyed by key), or null. */
  const run = useCallback(async (action: 'state' | 'set' | 'adjust' | 'video' | 'override', change?: DisplayChange, key = 'state') => {
    setBusy(true)
    try {
      const next = await host.gpuDisplay(action, change)
      setState(next)
      const text = next.ok ? null : (next.message ?? 'Not changed').replace(/\.$/, '')
      setError(text === null ? null : { key, text })
      return text
    } catch (e) {
      report(e, `DisplayTab.${action}`)
      setError({ key, text: 'Not changed' })
      return 'Not changed'
    } finally {
      setBusy(false)
    }
  }, [])

  // Read again each time the tab is shown: a screen may have changed since.
  useEffect(() => {
    if (!busyRef.current) void run('state')
  }, [run, active])

  if (!state) return null
  if (state.displays.length === 0) return <p className="exo-hub-sub">Windows did not list any screen.</p>
  const screen = state.displays.find(item => item.id === selected) ?? state.displays.find(item => item.main) ?? state.displays[0]
  const set = (key: string, change: Omit<DisplayChange, 'display'>) => {
    if (!busy) void run('set', { display: screen.id, ...change }, key)
  }

  return (
    <div className="exo-display-cols">
      <div>
      {state.displays.length > 1 && (
        <Row label="Screen">
          <GraphicsControl
            row={{ key: 'screen', label: 'Screen', detail: '', choice: screen.id, choices: state.displays.map(item => ({ id: item.id, label: item.name })) }}
            busy={false}
            onPick={id => { setSelected(id); setError(null) }}
          />
        </Row>
      )}
      <ScreenRows screen={screen} busy={busy} error={error} onSet={set} />
      {/* Video sits under the screen's own rows, so the two columns come out about even. */}
      {state.video && (
        <VideoRows
          video={state.video}
          busy={busy}
          error={error}
          onVideo={(key, change) => { if (!busy) void run('video', { display: screen.id, ...change }, key) }}
        />
      )}
      </div>
      <div>
      {screen.scaling && (
        <ScalingRows scaling={screen.scaling} busy={busy} errorFor={key => (error?.key === key ? error.text : null)} onSet={set}
          onOverride={on => busy ? Promise.resolve(null) : run('override', { display: screen.id, override: on }, 'override')} />
      )}
      {screen.adjust && (
        <ColorRows
          key={screen.id}
          adjust={screen.adjust}
          busy={busy}
          error={error}
          onColor={(key, change) => { if (!busy) void run('adjust', { display: screen.id, ...change }, key) }}
        />
      )}
      </div>
    </div>
  )
}

/**
 * Color sliders. NVIDIA: like NVIDIA Control Panel's "Adjust desktop color settings", only while
 * "Use NVIDIA color settings" is picked (the host keeps them and puts them back when Exo starts).
 * AMD: the driver's Custom Color. None of these can blank a screen, so they apply at once.
 */
function ColorRows({ adjust, busy, error, onColor }: {
  adjust: NonNullable<DisplayScreen['adjust']>
  busy: boolean
  error: { key: string; text: string } | null
  onColor: (key: string, change: Omit<DisplayChange, 'display'>) => void
}) {
  // NVIDIA: moving a slider turns "Use NVIDIA color settings" on; Restore hands color back to Windows,
  // with the calibration the screen had before. AMD (nvidia null): Restore puts the sliders to normal.
  const nvidia = adjust.nvidia
  const changed = adjust.controls.some(control => control.value !== control.normal)
  return (
    <>
      {adjust.controls.map(control => (
        <ColorSlider key={control.key} control={control} busy={busy} error={error?.key === control.key ? error.text : null} onCommit={value => onColor(control.key, { key: control.key, value })} />
      ))}
      <Row label="Default color" detail={nvidia === null ? "Puts every slider back to the driver's normal value" : 'Hands color back to Windows, with your calibration as it was'} error={error?.key === 'reset' ? error.text : null}>
        {changed
          ? <button type="button" className="exo-set-btn" disabled={busy} onClick={() => onColor('reset', nvidia === null ? { reset: true } : { nvidia: false })}>Restore</button>
          : <State on>In use</State>}
      </Row>
    </>
  )
}

function colorText(control: ColorControl, value: number) {
  return control.key === 'gamma' ? (value / 100).toFixed(2) : `${value}${control.unit}`
}

function ColorSlider({ control, busy, error, onCommit }: { control: ColorControl; busy: boolean; error: string | null; onCommit: (value: number) => void }) {
  const [draft, setDraft] = useState<number | null>(null)
  const shown = draft ?? control.value
  const id = `display-color-${control.key}`
  const commit = () => {
    if (draft !== null && draft !== control.value) onCommit(draft)
    setDraft(null)
  }
  return (
    <Row label={control.label} labelId={id} detail={`Normal ${colorText(control, control.normal)}`} error={error}>
      <div className="exo-capture-quality">
        <Slider
          min={control.min}
          max={control.max}
          step={control.step}
          value={shown}
          disabled={busy}
          aria-labelledby={id}
          aria-valuetext={colorText(control, shown)}
          onChange={event => setDraft(Number(event.target.value))}
          onPointerUp={commit}
          onKeyUp={commit}
        />
        <span style={{ minWidth: 40, textAlign: 'right' }}>{colorText(control, shown)}</span>
      </div>
    </Row>
  )
}

/** NVIDIA Control Panel's "Use default color settings" (the driver picks) or "Use NVIDIA color settings" (format, depth, range below). */
function OutputColorRow({ auto, color, busy, error, onSet }: {
  auto: boolean
  color: OutputColor
  busy: boolean
  error: string | null
  onSet: (key: string, change: Omit<DisplayChange, 'display'>) => void
}) {
  return (
    <Row label="Output color" detail={auto ? `The driver picks: ${describe(color)}` : 'Format, depth and range below'} error={error}>
      <Choice
        label="Output color"
        value={auto ? 'default' : 'nvidia'}
        choices={[{ value: 'default', label: 'Default' }, { value: 'nvidia', label: 'NVIDIA settings' }]}
        disabled={busy}
        onChange={value => onSet('policy', value === 'default' ? { auto: true } : { format: color.format, bpc: color.bpc, full: color.full })}
      />
    </Row>
  )
}

const SUPER_RESOLUTION = [
  { value: '0', label: 'Off' },
  { value: '5', label: 'Auto' },
  { value: '1', label: 'Quality 1' },
  { value: '2', label: 'Quality 2' },
  { value: '3', label: 'Quality 3' },
  { value: '4', label: 'Quality 4' },
]

/**
 * NVIDIA Control Panel's RTX video enhancement: Super resolution (Auto or quality 1-4, higher looks
 * sharper and costs more of the GPU) and RTX Video HDR. Shown only where the card has them; they
 * change video in browsers and players only, so they apply at once.
 */
function VideoRows({ video, busy, error, onVideo }: {
  video: NonNullable<DisplayState['video']>
  busy: boolean
  error: { key: string; text: string } | null
  onVideo: (key: string, change: Omit<DisplayChange, 'display'>) => void
}) {
  return (
    <>
      {video.superResolution !== null && (
        <Row label="RTX Video Super Resolution" detail="Sharpens video in browsers and players with AI. Higher quality uses more of the GPU" error={error?.key === 'vsr' ? error.text : null}>
          <Choice label="RTX Video Super Resolution" value={String(video.superResolution)} choices={SUPER_RESOLUTION} disabled={busy}
            onChange={value => onVideo('vsr', { superResolution: Number(value) })} />
        </Row>
      )}
      {video.hdr !== null && (
        <Row label="RTX Video HDR" detail="Turns SDR video into HDR in browsers and players. Needs HDR on in Windows" error={error?.key === 'vhdr' ? error.text : null}>
          <Choice label="RTX Video HDR" value={video.hdr > 0 ? 'on' : 'off'} choices={[{ value: 'off', label: 'Off' }, { value: 'on', label: 'On' }]} disabled={busy}
            onChange={value => onVideo('vhdr', { hdr: value === 'on' })} />
        </Row>
      )}
    </>
  )
}

const SCALING: { value: ScalingMode; label: string }[] = [
  { value: 'aspect', label: 'Aspect ratio' },
  { value: 'full', label: 'Full-screen' },
  { value: 'none', label: 'No scaling' },
  { value: 'integer', label: 'Integer scaling' },
]

/** NVIDIA Control Panel's Adjust desktop size and position > Scaling: the mode, and where it runs (integer runs on the GPU). */
function ScalingRows({ scaling, busy, errorFor, onSet, onOverride }: {
  scaling: NonNullable<DisplayScreen['scaling']>
  busy: boolean
  errorFor: (key: string) => string | null
  onSet: (key: string, change: Omit<DisplayChange, 'display'>) => void
  onOverride: (on: boolean) => Promise<string | null>
}) {
  return (
    <>
      <Row label="Scaling" detail="How a game or desktop below the screen's own resolution fills it" error={errorFor('scaling')}>
        <Choice label="Scaling" value={scaling.mode} choices={SCALING} disabled={busy}
          onChange={mode => onSet('scaling', { scaling: mode as ScalingMode, scalingGpu: scaling.gpu })} />
      </Row>
      {scaling.mode !== 'integer' && scaling.mode !== 'default' && (
        <Row label="Perform scaling on" detail="The GPU keeps it the same on every screen; the display uses its own scaler" error={errorFor('scalingOn')}>
          <Choice label="Perform scaling on" value={scaling.gpu ? 'gpu' : 'display'}
            choices={[{ value: 'gpu', label: 'GPU' }, { value: 'display', label: 'Display' }]} disabled={busy}
            onChange={on => onSet('scalingOn', { scaling: scaling.mode as ScalingMode, scalingGpu: on === 'gpu' })} />
        </Row>
      )}
      {scaling.over !== null && (
        <SwitchRow id="display-override" label="Override the scaling set by games" detail="Games and programs can't pick their own scaling; yours always wins"
          checked={scaling.over} disabled={busy} onChange={onOverride} />
      )}
    </>
  )
}

/** "165 Hz, RGB 10-bit, full range, no scaling on the GPU, games can't change it". */
function bestLine(best: DisplayScreen['best']) {
  return [
    `${best.refresh} Hz`,
    best.color ? describe(best.color) : null,
    best.scaling ? 'no scaling on the GPU' : null,
    best.over ? "games can't change it" : null,
  ].filter(Boolean).join(', ')
}

function ScreenRows({ screen, busy, error, onSet }: {
  screen: DisplayScreen
  busy: boolean
  error: { key: string; text: string } | null
  onSet: (key: string, change: Omit<DisplayChange, 'display'>) => void
}) {
  const color = screen.color
  const best = screen.best
  const errorFor = (key: string) => (error?.key === key ? error.text : null)
  const bestText = bestLine(best)

  const formats = FORMATS.filter(format => screen.supported.some(item => item.format === format.id))
  const depths = color ? [...new Set(screen.supported.filter(item => item.format === color.format).map(item => item.bpc))].sort((a, b) => a - b) : []
  const ranges = color?.format === 'rgb' ? [...new Set(screen.supported.filter(item => item.format === 'rgb' && item.bpc === color.bpc).map(item => item.full))] : []

  // A new format keeps the depth and range when it can, else takes its deepest and the range it has.
  const pickFormat = (format: string) => {
    if (!color) return
    const options = screen.supported.filter(item => item.format === format)
    const bpc = options.some(item => item.bpc === color.bpc) ? color.bpc : Math.max(...options.map(item => item.bpc))
    const full = format === 'rgb' && (options.some(item => item.bpc === bpc && item.full === color.full) ? color.full : options.some(item => item.bpc === bpc && item.full))
    onSet('format', { format, bpc, full })
  }

  return (
    <>
      <Row
        label="Best for this screen"
        detail={best.why ? `${bestText}. ${best.why}` : bestText}
        error={errorFor('best')}
      >
        {screen.isBest
          ? <State on>In use</State>
          : <button type="button" className="exo-set-btn" disabled={busy} onClick={() => onSet('best', { best: true })}>Use</button>}
      </Row>
      {screen.refreshes.length > 1 && (
        <Row label="Refresh rate" detail="How many times a second the screen draws" error={errorFor('refresh')}>
          <GraphicsControl
            row={{ key: 'refresh', label: 'Refresh rate', detail: '', choice: String(screen.refresh), choices: screen.refreshes.map(hz => ({ id: String(hz), label: `${hz} Hz` })) }}
            busy={busy}
            onPick={hz => onSet('refresh', { refresh: Number(hz) })}
          />
        </Row>
      )}
      {screen.vendor === 'nvidia' && color && <OutputColorRow auto={screen.auto} color={color} busy={busy} error={errorFor('policy')} onSet={onSet} />}
      {!screen.vendor || !color ? (
        <Row label="Color" detail="Exo can't reach this screen's graphics driver, so only the refresh rate changes here" />
      ) : screen.auto ? null : (
        <>
          <Row label="Color format" detail="RGB is sharpest. The 4:x:x formats are YCbCr, for TVs and cables that can't carry RGB" error={errorFor('format')}>
            {formats.length > 1 ? (
              <GraphicsControl
                row={{ key: 'format', label: 'Color format', detail: '', choice: color.format, choices: formats }}
                busy={busy}
                onPick={pickFormat}
              />
            ) : <State on>{FORMAT_NAME[color.format]}</State>}
          </Row>
          <Row label="Color depth" detail="Bits per color on the cable, up to what the panel itself shows. More means smoother gradients" error={errorFor('bpc')}>
            {depths.length > 1 ? (
              <GraphicsControl
                row={{ key: 'bpc', label: 'Color depth', detail: '', choice: String(color.bpc), choices: depths.map(bpc => ({ id: String(bpc), label: `${bpc}-bit` })) }}
                busy={busy}
                onPick={bpc => {
                  const full = screen.supported.some(item => item.format === color.format && item.bpc === Number(bpc) && item.full === color.full) ? color.full : !color.full
                  onSet('bpc', { format: color.format, bpc: Number(bpc), full: color.format === 'rgb' && full })
                }}
              />
            ) : <State on>{`${color.bpc}-bit`}</State>}
          </Row>
          {color.format === 'rgb' && (
            <Row label="Dynamic range" detail="Full for PC monitors. Limited only for a TV that crushes blacks on Full" error={errorFor('range')}>
              {ranges.length > 1 ? (
                <GraphicsControl
                  row={{ key: 'range', label: 'Dynamic range', detail: '', choice: color.full ? 'full' : 'limited', choices: [{ id: 'full', label: 'Full' }, { id: 'limited', label: 'Limited' }] }}
                  busy={busy}
                  onPick={range => onSet('range', { format: 'rgb', bpc: color.bpc, full: range === 'full' })}
                />
              ) : <State on={color.full}>{color.full ? 'Full' : 'Limited'}</State>}
            </Row>
          )}
        </>
      )}
    </>
  )
}
