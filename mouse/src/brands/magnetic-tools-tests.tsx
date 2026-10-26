import { useEffect, useRef, useState, type MouseEvent as ReactMouseEvent, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { Progress, Row, Segmented } from '../ui'
import './magnetic-tools.css'

// Adapted from OpenMouse (AGPL-3.0): src/hits-test.ts, src/ui/polling-stats.ts,
// src/app/HitsTestDialog.tsx, src/app/BounceCheckDialog.tsx and the polling
// sampler of src/app/HardwareTestPage.tsx. Logic kept, interface rewritten.

/* ------------------------------------------------------------------ shared pieces */

/** A sheet over the room. Escape or a click outside closes it. */
export function Overlay({ title, onClose, children }: { title: string; onClose: () => void; children: ReactNode }) {
  const close = useRef(onClose)
  close.current = onClose
  useEffect(() => {
    const key = (e: KeyboardEvent) => { if (e.key === 'Escape') close.current() }
    window.addEventListener('keydown', key)
    return () => window.removeEventListener('keydown', key)
  }, [])
  return createPortal(
    <div className="mt-overlay" role="dialog" aria-modal="true" aria-label={title} onMouseDown={e => { if (e.target === e.currentTarget) onClose() }}>
      <div className="mt-sheet">
        <div className="mt-sheet-head">
          <h2 className="m-panel-title">{title}</h2>
          <button type="button" className="m-link" onClick={onClose}>Close</button>
        </div>
        {children}
      </div>
    </div>,
    document.body,
  )
}


/** Left and right press depth as ten segments each; `scale` is the depth of a full press. */
export function DepthMeter({ depth, scale, marks }: { depth: [number, number]; scale: number; marks?: [number, number] }) {
  return (
    <span className="mt-meters">
      {(['Left', 'Right'] as const).map((side, i) => (
        <span key={side} className="mt-meter" role="meter" aria-label={`${side} press depth`} aria-valuemin={0} aria-valuemax={scale} aria-valuenow={depth[i]}>
          <span>{side}</span>
          <span className="mt-meter-bar">
            {Array.from({ length: 10 }, (_, step) => (
              <i key={step} data-on={(step * scale) / 10 < depth[i]} data-mark={marks ? step === marks[i] : false} />
            ))}
          </span>
        </span>
      ))}
    </span>
  )
}

/* ------------------------------------------------------------------ maths (OpenMouse) */

type DepthSample = { t: number; depth: number }

/** What a conventional switch is compared against: HITS step 5. */
const CONVENTIONAL_STEP = 5

function savedMs(samples: readonly DepthSample[], actuation: number): number | null {
  const reached = (step: number) => samples.find(s => s.depth >= step)?.t
  const fired = reached(actuation)
  const conventional = reached(CONVENTIONAL_STEP)
  if (fired === undefined || conventional === undefined) return null
  return conventional - fired
}

function stepTimes(samples: readonly DepthSample[], start: number): (number | null)[] {
  return Array.from({ length: 10 }, (_, i) => {
    const hit = samples.find(s => s.depth >= i + 1)
    return hit ? hit.t - start : null
  })
}

/** Two presses of one button closer than this are a switch bouncing, not a finger. */
const BOUNCE_GAP_MS = 30

function bounceReport(times: readonly number[]) {
  const gaps = times.slice(1).map((time, i) => time - times[i]).filter(gap => gap < BOUNCE_GAP_MS)
  return { clicks: times.length, bounces: gaps.length, longestGap: gaps.length ? Math.max(...gaps) : null }
}

function pollingResults(intervals: number[]) {
  const filtered = intervals.filter(ms => ms > 0.05 && ms <= 1000)
  if (filtered.length < 2) return null
  const sortedInts = [...filtered].sort((a, b) => a - b)
  const medianInterval = sortedInts[Math.floor(sortedInts.length / 2)] ?? 1
  const hz = filtered.map(ms => 1000 / ms).filter(h => h > 0 && h < 20000).sort((a, b) => a - b)
  const sumMs = filtered.reduce((s, v) => s + v, 0)
  const avg = (filtered.length * 1000) / sumMs
  const medianHz = 1000 / medianInterval
  const minValid = medianInterval * 0.9
  const peak = Math.min(filtered.reduce((mx, ms) => (ms >= minValid ? Math.max(mx, 1000 / ms) : mx), 0), medianHz) || medianHz
  const low5 = hz[Math.max(0, Math.floor(hz.length * 0.05))] ?? 0
  const tolerance = medianInterval * 0.5
  const inBand = filtered.filter(ms => ms >= medianInterval - tolerance && ms <= medianInterval + tolerance).length
  const stability = (inBand / filtered.length) * 100
  const dropouts = filtered.filter(ms => ms > medianInterval * 2.5).length
  return { avg, peak, low5, stability, dropouts, events: hz.length }
}

function rollingHz(intervals: number[]): number {
  const recent = intervals.slice(-24)
  if (recent.length < 3) return 0
  const sorted = recent.map(ms => (ms > 0 ? 1000 / ms : 0)).sort((a, b) => a - b)
  return sorted[Math.floor(sorted.length / 2)] ?? 0
}

const ms = (v: number) => `${Math.round(v)} ms`
const hzText = (v: number) => (v > 0 ? `${Math.round(v).toLocaleString()} Hz` : '-')

/* ------------------------------------------------------------------ polling rate */

const SAMPLE_MS = 5000

/** Five seconds of pointer movement, timed per report (coalesced events included). */
export function PollingTest({ reportedHz }: { reportedHz: number | null }) {
  const [phase, setPhase] = useState<'idle' | 'run' | 'done'>('idle')
  const [live, setLive] = useState(0)
  const [elapsed, setElapsed] = useState(0)
  const [result, setResult] = useState<ReturnType<typeof pollingResults>>(null)

  useEffect(() => {
    if (phase !== 'run') return
    const intervals: number[] = []
    let last = 0
    let started = 0
    const onMove = (event: PointerEvent) => {
      const subs = typeof event.getCoalescedEvents === 'function' && event.getCoalescedEvents().length > 0 ? event.getCoalescedEvents() : [event]
      for (const ev of subs) {
        if (last > 0) {
          const delta = ev.timeStamp - last
          if (delta >= 0.05 && delta <= 1000) intervals.push(delta)
        }
        last = ev.timeStamp
      }
      if (!started) started = performance.now()
    }
    window.addEventListener('pointermove', onMove, { passive: true })
    const tick = window.setInterval(() => {
      if (!started) return
      const time = performance.now() - started
      setElapsed(Math.min(SAMPLE_MS, time))
      setLive(rollingHz(intervals))
      if (time >= SAMPLE_MS) {
        setResult(pollingResults(intervals))
        setPhase('done')
      }
    }, 120)
    return () => {
      window.removeEventListener('pointermove', onMove)
      window.clearInterval(tick)
    }
  }, [phase])

  const start = () => { setResult(null); setLive(0); setElapsed(0); setPhase('run') }
  const shown = phase === 'done' && result && result.events >= 40 ? result : null

  return (
    <>
      <div className={`mt-pad${phase === 'run' ? ' is-big' : ''}`} onPointerDown={() => phase !== 'run' && start()}>
        {phase === 'run' ? (elapsed === 0 ? 'Move the mouse' : hzText(live))
          : phase === 'done' ? (shown ? 'Click to run again' : 'Not enough movement. Click to run again')
            : 'Click, then move the mouse in circles'}
        {/* The Exo bar runs along the pad's own foot, there only while the run lasts: an empty track under the
            pad read as a stray line. The report keeps its place, so nothing re-lays out mid-test. */}
        {phase === 'run' ? <Progress edge size="small" value={elapsed / SAMPLE_MS} label={`Testing the polling rate · ${Math.floor(elapsed / 1000)} of ${SAMPLE_MS / 1000} s`} /> : null}
      </div>
      <Row label="Average"><span className="m-state">{shown ? hzText(shown.avg) : '-'}</span></Row>
      <Row label="Peak"><span className="m-state">{shown ? hzText(shown.peak) : '-'}</span></Row>
      <Row label="Lowest 5%"><span className="m-state">{shown ? hzText(shown.low5) : '-'}</span></Row>
      <Row label="Stability"><span className="m-state">{shown ? `${shown.stability.toFixed(0)}%` : '-'}</span></Row>
      <Row label="Dropouts"><span className="m-state">{shown ? shown.dropouts : '-'}</span></Row>
      <Row label="Set on the mouse"><span className="m-state">{reportedHz ? hzText(reportedHz) : '-'}</span></Row>
    </>
  )
}

/* ------------------------------------------------------------------ clicks and bounce */

const BUTTONS = [
  { button: 0, label: 'Left' },
  { button: 2, label: 'Right' },
  { button: 1, label: 'Middle' },
  { button: 3, label: 'Back' },
  { button: 4, label: 'Forward' },
] as const

/**
 * Every button press in the field, counted per button. A second press within
 * 30 ms is a switch bouncing, which no finger can do.
 */
export function ClickTest() {
  const [presses, setPresses] = useState<Record<number, number[]>>({})
  const [lastButton, setLastButton] = useState<string | null>(null)
  const stop = (e: { preventDefault: () => void }) => e.preventDefault()
  return (
    <>
      <div
        className={`mt-pad${lastButton ? ' is-big' : ''}`}
        onMouseDown={e => {
          e.preventDefault()
          const time = e.timeStamp
          const b = e.button
          setPresses(p => ({ ...p, [b]: [...(p[b] ?? []), time] }))
          setLastButton(BUTTONS.find(x => x.button === b)?.label ?? `Button ${b + 1}`)
        }}
        onMouseUp={stop}
        onAuxClick={stop}
        onContextMenu={stop}
      >
        {lastButton ?? 'Click here as fast as you can'}
      </div>
      {BUTTONS.map(({ button, label }) => {
        const r = bounceReport(presses[button] ?? [])
        const detail = r.clicks === 0 ? '-' : `${r.clicks} ${r.clicks === 1 ? 'click' : 'clicks'}, ${r.bounces} bounced${r.longestGap !== null ? `, longest ${ms(r.longestGap)}` : ''}`
        return <Row key={button} label={label}><span className="m-state" style={r.bounces ? { color: 'var(--bad)' } : undefined}>{detail}</span></Row>
      })}
      <div className="mt-actions">
        <button type="button" className="m-button" onClick={() => { setPresses({}); setLastButton(null) }}>Clear</button>
      </div>
    </>
  )
}

/* ------------------------------------------------------------------ HITS speed */

type Round = { saved: number | null; steps: (number | null)[] }

/**
 * Click when the field turns green. The press-depth stream times how much
 * sooner the chosen actuation fires than a conventional switch at step 5.
 * `subscribe` hands back live depth (0 to 10) per button; the caller keeps the
 * stream armed while this is open.
 */
export function HitsSpeedTest({ subscribe, actuation, maxActuation, onAdjust, busy }: {
  subscribe: (listener: (left: number, right: number) => void) => () => void
  actuation: [number, number]
  maxActuation: number
  onAdjust: (side: 0 | 1, value: number) => void
  busy: boolean
}) {
  const [side, setSide] = useState<0 | 1>(0)
  const [phase, setPhase] = useState<'idle' | 'wait' | 'go' | 'settle' | 'done'>('idle')
  const [note, setNote] = useState('')
  const [round, setRound] = useState<Round | null>(null)
  const [depth, setDepth] = useState<[number, number]>([0, 0])
  const samples = useRef<DepthSample[]>([])
  const goAt = useRef(0)
  const timer = useRef<number | undefined>(undefined)
  const sideRef = useRef(side)
  sideRef.current = side
  const actuationRef = useRef(actuation)
  actuationRef.current = actuation

  useEffect(() => {
    const stop = subscribe((left, right) => {
      samples.current.push({ t: performance.now(), depth: sideRef.current === 0 ? left : right })
      setDepth([left, right])
    })
    return () => { stop(); window.clearTimeout(timer.current) }
  }, [subscribe])

  const start = () => {
    window.clearTimeout(timer.current)
    setNote('')
    setPhase('wait')
    timer.current = window.setTimeout(() => {
      goAt.current = performance.now()
      samples.current = []
      setPhase('go')
    }, 1000 + Math.random() * 2000)
  }

  const press = (event: ReactMouseEvent) => {
    event.preventDefault()
    if (event.button !== (side === 0 ? 0 : 2)) return
    if (phase === 'idle' || phase === 'done') { setRound(null); start() }
    else if (phase === 'wait') { window.clearTimeout(timer.current); setNote('Too early'); setPhase('idle') }
    else if (phase === 'go') {
      setPhase('settle')
      // The rest of the press's depth reports arrive just after the click.
      timer.current = window.setTimeout(() => {
        setRound({ saved: savedMs(samples.current, actuationRef.current[sideRef.current]), steps: stepTimes(samples.current, goAt.current) })
        setPhase('done')
      }, 300)
    }
  }

  const value = actuation[side]
  const at = (step: number) => round?.steps[step - 1] ?? null
  const label = phase === 'idle' ? (note || 'Click to start') : phase === 'wait' ? 'Wait for green' : phase === 'go' || phase === 'settle' ? 'Click' : null

  return (
    <>
      <Row label="Button">
        <Segmented label="Button" options={[{ value: 0, label: 'Left' }, { value: 1, label: 'Right' }]} value={side}
          onChange={v => { setSide(v as 0 | 1); setRound(null); setPhase('idle'); window.clearTimeout(timer.current) }} />
      </Row>
      <Row label="Actuation" value={`Step ${value} of ${maxActuation}`}>
        <span className="mt-links">
          <button type="button" className="m-button" disabled={busy || value <= 1} onClick={() => onAdjust(side, value - 1)} aria-label="Lower actuation">Lower</button>
          <button type="button" className="m-button" disabled={busy || value >= maxActuation} onClick={() => onAdjust(side, value + 1)} aria-label="Raise actuation">Higher</button>
        </span>
      </Row>
      <Row label="Press depth">
        <DepthMeter depth={depth} scale={10} marks={[actuation[0] - 1, actuation[1] - 1]} />
      </Row>
      <div
        className={`mt-pad${phase === 'wait' ? ' is-wait' : phase === 'go' || phase === 'settle' ? ' is-go is-big' : phase === 'done' ? ' is-big' : ''}`}
        onMouseDown={press}
        onMouseUp={e => e.preventDefault()}
        onContextMenu={e => e.preventDefault()}
      >
        {phase === 'done' && round ? (round.saved === null ? '-' : `${round.saved > 0 ? '+' : ''}${Math.round(round.saved)} ms saved`) : label}
      </div>
      {phase === 'done' && round ? (
        <>
          <Row label="Your actuation"><span className="m-state">{at(value) === null ? '-' : ms(at(value) as number)}</span></Row>
          <Row label="Conventional switch"><span className="m-state">{at(CONVENTIONAL_STEP) === null ? '-' : ms(at(CONVENTIONAL_STEP) as number)}</span></Row>
          <div className="mt-ladder" aria-label="Time to reach each step">
            {Array.from({ length: 10 }, (_, i) => i + 1).map(step => (
              <span key={step} data-mark={step === value ? 'hits' : step === CONVENTIONAL_STEP ? 'conventional' : undefined}>
                <span>{step}</span>
                <span>{at(step) === null ? '-' : Math.round(at(step) as number)}</span>
              </span>
            ))}
          </div>
        </>
      ) : null}
    </>
  )
}
