import { useEffect, useRef, useState } from 'react'
import { host, type ControllerCalibration, type ControllerState, type StickRest } from '../../lib/host'
import { report } from '../../lib/report'
import { Row, State } from '../settings/parts'
import { confirm, Dialog } from '../ui/Dialog'
import { Spinner } from '../ui/Spinner'
import { ErrorNote } from '../ui/ErrorNote'

/**
 * The sticks' calibration rows (DESIGN.md › Devices › controller): Calibrate opens the controller's own routine, and
 * once Exo has calibrated it, how long that lasts (until unplugged; kept on a DualSense when asked; an Edge gets it back
 * each time it connects, always) and Undo, which writes back what the controller had before.
 */
export function CalibrationRows({ id, edge, usb, live, state, onChanged }: {
  id: string
  edge: boolean
  usb: boolean
  live: ControllerState | null
  state: ControllerCalibration | undefined
  onChanged: () => void
}) {
  const [open, setOpen] = useState(false)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  async function step(name: 'undo' | 'keep') {
    setBusy(name)
    setError(null)
    try {
      const result = await host.controllerCalibrate(id, name)
      if (!result.ok) setError(result.message ?? 'The controller didn’t take it. Try again.')
      onChanged()
    } catch (cause) {
      report(cause, 'Calibration.step')
      setError('The controller didn’t take it. Try again.')
    } finally {
      setBusy(null)
    }
  }
  async function keep() {
    const sure = await confirm({
      title: 'Keep it after unplugging?',
      body: 'The DualSense saves this calibration in place of its own. Keep the cable in until Exo says it’s done. Undo puts its own back.',
      action: 'Keep it',
    })
    if (sure === true) await step('keep')
  }
  const calibrated = state?.calibrated === true
  return (
    <>
      <Row label="Calibration" error={error} detail={calibrationLine(usb, edge, state)}>
        {calibrated && state?.canUndo ? (
          <button type="button" className="exo-set-btn" disabled={busy !== null} onClick={() => void step('undo')}>
            {busy === 'undo' ? <Spinner label="Putting it back" /> : null}Undo
          </button>
        ) : null}
        <button type="button" className="exo-set-btn" disabled={!usb || busy !== null} onClick={() => setOpen(true)}>{calibrated ? 'Again' : 'Calibrate'}</button>
      </Row>
      {calibrated && !edge && state && !state.kept ? (
        <Row label="Keep it after unplugging">
          <button type="button" className="exo-set-btn" disabled={busy !== null} onClick={() => void keep()}>
            {busy === 'keep' ? <Spinner label="Keeping it" /> : null}Keep
          </button>
        </Row>
      ) : null}
      {open ? <CalibrateDialog id={id} edge={edge} live={live} onClose={() => { setOpen(false); onChanged() }} /> : null}
    </>
  )
}

/** Only what can't be seen otherwise: why Calibrate is off, or how long Exo's calibration lasts. */
function calibrationLine(usb: boolean, edge: boolean, state: ControllerCalibration | undefined) {
  if (!usb) return 'Connect it with a cable to calibrate it'
  if (!state?.calibrated) return undefined
  if (edge) return 'Calibrated by Exo'
  return state.kept ? 'Calibrated by Exo and kept by the controller' : 'Calibrated by Exo until it’s unplugged'
}

type Phase = 'intro' | 'before' | 'center' | 'range' | 'ending' | 'after' | 'done'

/** Steps of 10°: how much of its circle a stick has swept at more than half its reach. */
const BINS = 36

/**
 * The sticks' calibration, start to finish: Exo measures the centers, the controller takes its center with the sticks
 * left alone and Exo corrects what's left from its own measurement, then the range while both are rolled around their
 * edge (an Edge's fitted by Exo from how far each stick went; a DualSense's by its own routine). Exo won't end it before
 * both have gone all the way round, and measures the centers again for before and after. Leaving midway puts back the
 * values the controller had.
 */
function CalibrateDialog({ id, edge, live, onClose }: { id: string; edge: boolean; live: ControllerState | null; onClose: () => void }) {
  const [phase, setPhase] = useState<Phase>('intro')
  const [error, setError] = useState<string | null>(null)
  const [before, setBefore] = useState<StickRest[] | null>(null)
  const [after, setAfter] = useState<StickRest[] | null>(null)
  const swept = useRef({ left: new Set<number>(), right: new Set<number>() })
  const [, redraw] = useState(0)
  const inRange = useRef(false)

  // While the range runs, which tenths of each circle the sticks have reached.
  useEffect(() => {
    if (phase !== 'range' || !live) return
    const mark = (x: number, y: number, into: Set<number>) => {
      const dx = (x - 127.5) / 127.5, dy = (y - 127.5) / 127.5
      if (Math.hypot(dx, dy) < 0.5) return
      const before = into.size
      into.add(Math.floor(((Math.atan2(dy, dx) + Math.PI) / (2 * Math.PI)) * BINS) % BINS)
      if (into.size !== before) redraw(n => n + 1)
    }
    mark(live.lx, live.ly, swept.current.left)
    mark(live.rx, live.ry, swept.current.right)
  }, [live, phase])

  // Gone midway (Escape, the scrim, the page): the range is closed and the controller gets its old values back.
  useEffect(() => () => { if (inRange.current) void host.controllerCalibrate(id, 'range-cancel').catch(() => { }) }, [id])

  async function rest() {
    const result = await host.controllerTest(id, 'rest', 3)
    if (!result.ok) throw new Error(result.message ?? 'The controller stopped answering.')
    return [result.left, result.right].filter(Boolean) as StickRest[]
  }

  async function start() {
    setError(null)
    try {
      setPhase('before')
      setBefore(await rest())
      setPhase('center')
      const center = await host.controllerCalibrate(id, 'center')
      if (!center.ok) throw new Error(center.message ?? 'The controller didn’t take it.')
      const begun = await host.controllerCalibrate(id, 'range-begin')
      if (!begun.ok) throw new Error(begun.message ?? 'The controller didn’t start.')
      inRange.current = true
      swept.current = { left: new Set(), right: new Set() }
      setPhase('range')
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The calibration stopped.')
      setPhase('intro')
    }
  }

  async function finish() {
    setError(null)
    setPhase('ending')
    try {
      const end = await host.controllerCalibrate(id, 'range-end')
      if (!end.ok) {
        setError(end.message ?? 'The controller didn’t take it.')
        setPhase(end.more ? 'range' : 'intro')
        if (!end.more) inRange.current = false
        return
      }
      inRange.current = false
      setPhase('after')
      setAfter(await rest())
      setPhase('done')
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The calibration stopped.')
      setPhase('done')
    }
  }

  const share = (set: Set<number>) => Math.round((set.size / BINS) * 100)
  const rolling = phase === 'range' || phase === 'ending'
  const working = phase === 'before' || phase === 'center' || phase === 'after'
  return (
    <Dialog title={TITLE[phase]} onClose={onClose} className="exo-calibrate"
      actions={<CalibrateActions phase={phase} onClose={onClose} onStart={() => void start()} onFinish={() => void finish()} />}>
      {phase === 'intro' ? (
        <p>{edge
          ? 'First the centers, with the sticks left alone: your Edge takes its own, then Exo measures them and corrects what’s left. Then roll both sticks firmly around their edge: Exo measures how far each one goes in every direction and sets each so a normal push gives full output, the same all the way round. It lasts until you unplug it, and Exo gives it back each time it connects.'
          : 'First the centers, with the sticks left alone: your DualSense takes its own, then Exo measures them and corrects what’s left. Then its range, while you roll both sticks around their edge. It lasts until you unplug it, unless you keep it.'}</p>
      ) : null}
      {working ? <p>Put the controller down and don’t touch the sticks.</p> : null}
      {rolling ? (
        <>
          <p>Slowly, all the way around, both directions, a few times. Then press Done.</p>
          <div className="exo-calibrate-sweep">
            <Sweep label="Left stick" share={share(swept.current.left)} />
            <Sweep label="Right stick" share={share(swept.current.right)} />
          </div>
        </>
      ) : null}
      {phase === 'done' && before && after ? (
        <dl className="exo-calibrate-result">
          {after.map((stick, i) => (
            <div key={i}>
              <dt>{i === 0 ? 'Left stick' : 'Right stick'} center</dt>
              <dd>{offset(before[i])} → {offset(stick)}</dd>
            </div>
          ))}
        </dl>
      ) : null}
      {error ? <ErrorNote className="exo-dialog-error" message={error} /> : null}
    </Dialog>
  )
}

const TITLE: Record<Phase, string> = {
  intro: 'Calibrate the sticks', before: 'Leave both sticks alone', center: 'Leave both sticks alone', after: 'Leave both sticks alone',
  range: 'Roll both sticks', ending: 'Roll both sticks', done: 'Calibrated',
}

function CalibrateActions({ phase, onClose, onStart, onFinish }: { phase: Phase; onClose: () => void; onStart: () => void; onFinish: () => void }) {
  if (phase === 'intro') {
    return (
      <>
        <button type="button" className="exo-set-btn" data-autofocus onClick={onClose}>Cancel</button>
        <button type="button" className="exo-set-btn is-primary" onClick={onStart}>Start</button>
      </>
    )
  }
  if (phase === 'range' || phase === 'ending') {
    return (
      <>
        <button type="button" className="exo-set-btn" onClick={onClose}>Cancel</button>
        <button type="button" className="exo-set-btn is-primary" disabled={phase === 'ending'} onClick={onFinish}>
          {phase === 'ending' ? <Spinner label="Finishing" /> : null}Done
        </button>
      </>
    )
  }
  if (phase === 'done') return <button type="button" className="exo-set-btn is-primary" data-autofocus onClick={onClose}>Done</button>
  return <button type="button" className="exo-set-btn" disabled><Spinner label="Calibrating" />Calibrating</button>
}

const offset = (rest: StickRest | undefined) => rest ? `${Math.max(Math.abs(rest.offsetX), Math.abs(rest.offsetY)).toFixed(1)}% off` : '–'

function Sweep({ label, share }: { label: string; share: number }) {
  return (
    <div className="exo-calibrate-stick">
      <span>{label}</span>
      <div className="exo-trigger" role="progressbar" aria-label={label} aria-valuemin={0} aria-valuemax={100} aria-valuenow={share}>
        <span style={{ transform: `translateX(${share - 100}%)` }} />
      </div>
      <State on={share >= 90}>{share >= 90 ? 'All the way round' : `${share}%`}</State>
    </div>
  )
}
