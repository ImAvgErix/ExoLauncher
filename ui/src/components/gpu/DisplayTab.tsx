import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { Slider } from '../ui/Slider'
import { host, type ColorControl, type DisplayChange, type DisplayScreen, type DisplayState, type OutputColor, type ScalingMode } from '../../lib/host'
import { report } from '../../lib/report'
import { GraphicsControl } from '../graphics/GraphicsControl'
import { Row, State, SwitchRow } from '../settings/parts'
import { Choice } from '../settings/Choice'
import { Spinner } from '../ui/Spinner'

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

/** A menu with the ring before it while its change is written (DESIGN.md › Waits). */
function Busy({ on, label, children }: { on: boolean; label: string; children: ReactNode }) {
  return <span className="exo-display-busy">{on ? <Spinner label={label} /> : null}{children}</span>
}

/**
 * PC › Display › Screen: per screen, the refresh rate and what the cable carries (format, depth,
 * range), with "Best for this screen" (on NVIDIA also No scaling on the GPU, over games). A change applies and keeps
 * itself, so the next can follow at once: Exo offers only the modes the screen reports it takes.
 */
export function DisplayTab({ active }: { active: boolean }) {
  const [state, setState] = useState<DisplayState | null>(null)
  const [selected, setSelected] = useState<string | null>(null)
  // The row whose change is being written ('state' while the screens are read), or null.
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<{ key: string; text: string } | null>(null)
  const busyRef = useRef<string | null>(null)
  busyRef.current = busy

  /** Runs one action; the line it failed with (also shown under the row keyed by key), or null. */
  const run = useCallback(async (action: 'state' | 'set' | 'adjust' | 'video' | 'override', change?: DisplayChange, key = 'state') => {
    setBusy(key)
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
      setBusy(null)
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
  busy: string | null
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
        <ColorSlider key={control.key} control={control} busy={!!busy} error={error?.key === control.key ? error.text : null} onCommit={value => onColor(control.key, { key: control.key, value })} />
      ))}
      <Row label="Default color" error={error?.key === 'reset' ? error.text : null}>
        {changed
          ? <button type="button" className="exo-set-btn" disabled={!!busy} aria-busy={busy === 'reset'} onClick={() => onColor('reset', nvidia === null ? { reset: true } : { nvidia: false })}>
            {busy === 'reset' ? <Spinner label="Putting the screen's color back" /> : null}Restore
          </button>
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
  busy: string | null
  error: string | null
  onSet: (key: string, change: Omit<DisplayChange, 'display'>) => void
}) {
  return (
    <Row label="Output color" detail={auto ? describe(color) : undefined} error={error}>
      <Busy on={busy === 'policy'} label="Changing the output color">
        <Choice
          label="Output color"
          value={auto ? 'default' : 'nvidia'}
          choices={[{ value: 'default', label: 'Default' }, { value: 'nvidia', label: 'NVIDIA settings' }]}
          disabled={!!busy}
          onChange={value => onSet('policy', value === 'default' ? { auto: true } : { format: color.format, bpc: color.bpc, full: color.full })}
        />
      </Busy>
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
  busy: string | null
  error: { key: string; text: string } | null
  onVideo: (key: string, change: Omit<DisplayChange, 'display'>) => void
}) {
  return (
    <>
      {video.superResolution !== null && (
        <Row label="RTX Video Super Resolution" error={error?.key === 'vsr' ? error.text : null}>
          <Busy on={busy === 'vsr'} label="Changing RTX Video Super Resolution">
            <Choice label="RTX Video Super Resolution" value={String(video.superResolution)} choices={SUPER_RESOLUTION} disabled={!!busy}
              onChange={value => onVideo('vsr', { superResolution: Number(value) })} />
          </Busy>
        </Row>
      )}
      {video.hdr !== null && (
        <Row label="RTX Video HDR" error={error?.key === 'vhdr' ? error.text : null}>
          <Busy on={busy === 'vhdr'} label="Changing RTX Video HDR">
            <Choice label="RTX Video HDR" value={video.hdr > 0 ? 'on' : 'off'} choices={[{ value: 'off', label: 'Off' }, { value: 'on', label: 'On' }]} disabled={!!busy}
              onChange={value => onVideo('vhdr', { hdr: value === 'on' })} />
          </Busy>
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
  busy: string | null
  errorFor: (key: string) => string | null
  onSet: (key: string, change: Omit<DisplayChange, 'display'>) => void
  onOverride: (on: boolean) => Promise<string | null>
}) {
  return (
    <>
      <Row label="Scaling" error={errorFor('scaling')}>
        <Busy on={busy === 'scaling'} label="Changing the scaling">
          <Choice label="Scaling" value={scaling.mode} choices={SCALING} disabled={!!busy}
            onChange={mode => onSet('scaling', { scaling: mode as ScalingMode, scalingGpu: scaling.gpu })} />
        </Busy>
      </Row>
      {scaling.mode !== 'integer' && scaling.mode !== 'default' && (
        <Row label="Perform scaling on" error={errorFor('scalingOn')}>
          <Busy on={busy === 'scalingOn'} label="Changing where scaling runs">
            <Choice label="Perform scaling on" value={scaling.gpu ? 'gpu' : 'display'}
              choices={[{ value: 'gpu', label: 'GPU' }, { value: 'display', label: 'Display' }]} disabled={!!busy}
              onChange={on => onSet('scalingOn', { scaling: scaling.mode as ScalingMode, scalingGpu: on === 'gpu' })} />
          </Busy>
        </Row>
      )}
      {scaling.over !== null && (
        <SwitchRow id="display-override" label="Override the scaling set by games"
          checked={scaling.over} disabled={!!busy} onChange={onOverride} />
      )}
    </>
  )
}

/**
 * "165 Hz, RGB 10-bit, full range, no scaling on the GPU". The host's why follows only where the
 * cable or port holds the screen off RGB, the one case it says how to fix.
 */
function bestLine(best: DisplayScreen['best']) {
  const line = [
    `${best.refresh} Hz`,
    best.color ? describe(best.color) : null,
    best.scaling ? 'no scaling on the GPU' : null,
  ].filter(Boolean).join(', ')
  return best.why && best.color && best.color.format !== 'rgb' ? `${line}. ${best.why}` : line
}

function ScreenRows({ screen, busy, error, onSet }: {
  screen: DisplayScreen
  busy: string | null
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
      <Row label="Best for this screen" detail={bestText} error={errorFor('best')}>
        {screen.isBest
          ? <State on>In use</State>
          : <button type="button" className="exo-set-btn" disabled={!!busy} aria-busy={busy === 'best'} onClick={() => onSet('best', { best: true })}>
            {busy === 'best' ? <Spinner label="Setting the screen up" /> : null}Use
          </button>}
      </Row>
      {screen.refreshes.length > 1 && (
        <Row label="Refresh rate" error={errorFor('refresh')}>
          <Busy on={busy === 'refresh'} label="Changing the refresh rate">
            <GraphicsControl
              row={{ key: 'refresh', label: 'Refresh rate', detail: '', choice: String(screen.refresh), choices: screen.refreshes.map(hz => ({ id: String(hz), label: `${hz} Hz` })) }}
              busy={!!busy}
              onPick={hz => onSet('refresh', { refresh: Number(hz) })}
            />
          </Busy>
        </Row>
      )}
      {screen.vendor === 'nvidia' && color && <OutputColorRow auto={screen.auto} color={color} busy={busy} error={errorFor('policy')} onSet={onSet} />}
      {!screen.vendor || !color ? (
        <Row label="Color" detail="Exo can't reach this screen's graphics driver, so only the refresh rate changes here" />
      ) : screen.auto ? null : (
        <>
          <Row label="Color format" error={errorFor('format')}>
            {formats.length > 1 ? (
              <Busy on={busy === 'format'} label="Changing the color format">
                <GraphicsControl
                  row={{ key: 'format', label: 'Color format', detail: '', choice: color.format, choices: formats }}
                  busy={!!busy}
                  onPick={pickFormat}
                />
              </Busy>
            ) : <State on>{FORMAT_NAME[color.format]}</State>}
          </Row>
          <Row label="Color depth" error={errorFor('bpc')}>
            {depths.length > 1 ? (
              <Busy on={busy === 'bpc'} label="Changing the color depth">
                <GraphicsControl
                  row={{ key: 'bpc', label: 'Color depth', detail: '', choice: String(color.bpc), choices: depths.map(bpc => ({ id: String(bpc), label: `${bpc}-bit` })) }}
                  busy={!!busy}
                  onPick={bpc => {
                    const full = screen.supported.some(item => item.format === color.format && item.bpc === Number(bpc) && item.full === color.full) ? color.full : !color.full
                    onSet('bpc', { format: color.format, bpc: Number(bpc), full: color.format === 'rgb' && full })
                  }}
                />
              </Busy>
            ) : <State on>{`${color.bpc}-bit`}</State>}
          </Row>
          {color.format === 'rgb' && (
            <Row label="Dynamic range" error={errorFor('range')}>
              {ranges.length > 1 ? (
                <Busy on={busy === 'range'} label="Changing the dynamic range">
                  <GraphicsControl
                    row={{ key: 'range', label: 'Dynamic range', detail: '', choice: color.full ? 'full' : 'limited', choices: [{ id: 'full', label: 'Full' }, { id: 'limited', label: 'Limited' }] }}
                    busy={!!busy}
                    onPick={range => onSet('range', { format: 'rgb', bpc: color.bpc, full: range === 'full' })}
                  />
                </Busy>
              ) : <State on={color.full}>{color.full ? 'Full' : 'Limited'}</State>}
            </Row>
          )}
        </>
      )}
    </>
  )
}
