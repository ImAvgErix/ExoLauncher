import { useEffect, useState, type ReactNode } from 'react'
import { call } from './exo-hid'
import { Panel, Progress, Spinner } from './ui'

/**
 * One read of the moving mouse, as Exo reports it: its polling rate (Hz), the gaps between reports Exo judged, how
 * many came a slot late or more, the longest and the 99.9th percentile gap (ms), whether the late ones come with the
 * screen's refresh, and the CPU that took its input (null when Exo couldn't tell).
 */
type Reading = { rate: number; checked: number; missed: number; longest: number; p999: number; refresh: boolean; cpu: number | null }

/**
 * Where the mouse's CPU stands, as Exo's `exo.mouseCore` answers: stage none (Exo set nothing), restart (set or put
 * back, waiting on Windows' restart) or set (in place since). detail says where it stands or what cleared it; before
 * and after are the reads either side of the restart; test is this session's last read, whose verdict fix offers a
 * quiet CPU, fine needs nothing, cannot says why, and retry didn't see enough.
 */
type Core = {
  stage: 'none' | 'restart' | 'set'
  detail?: string | null
  cpu?: number | null
  better?: boolean | null
  canUndo: boolean
  before?: Reading | null
  after?: Reading | null
  test?: { verdict: 'fix' | 'fine' | 'cannot' | 'retry'; detail: string; offer?: number | null; result: Reading } | null
}

type Action = 'measure' | 'apply' | 'undo'

/** Exo reads the mouse this long. */
const MEASURE_MS = 10_000

/** A read answers at once; an action waits on the 10 s read or on Windows' permission prompt. */
async function ask(action?: Action): Promise<Core> {
  const reply = await call('exo.mouseCore', action ? { action } : {}, action ? 120_000 : 15_000) as unknown as Partial<Core>
  if (reply.stage !== 'none' && reply.stage !== 'restart' && reply.stage !== 'set') throw new Error('Exo did not say where the CPU stands.')
  return reply as Core
}

/**
 * CPU core, in Performance: whether the mouse's input waits behind other work on a busy CPU, and if it does, a quiet
 * CPU of its own. Measure reads the mouse for 10 seconds while you move it in circles (the bar counts it down); the
 * verdict is one line. When the mouse's input lands on a busy CPU, Give it its own core moves its USB controller's
 * interrupts to a quiet one: Exo asks Windows once, and it takes hold when Windows restarts. Measured again after
 * that, the reads before and after stand side by side; Undo puts it back. Exo itself does the reading and the
 * change (`exo.mouseCore`); with no Exo to answer, the panel isn't there.
 */
export function CpuCore() {
  const [core, setCore] = useState<Core | null>(null)
  const [absent, setAbsent] = useState(false)
  const [busy, setBusy] = useState<Action | null>(null)
  const [failed, setFailed] = useState<{ action: Action; text: string } | null>(null)
  const [left, setLeft] = useState(MEASURE_MS)

  useEffect(() => {
    let live = true
    ask().then(next => { if (live) setCore(next) }, () => { if (live) setAbsent(true) })
    return () => { live = false }
  }, [])

  const act = async (action: Action) => {
    if (busy) return
    setBusy(action)
    setFailed(null)
    let timer = 0
    if (action === 'measure') {
      const end = performance.now() + MEASURE_MS
      setLeft(MEASURE_MS)
      timer = window.setInterval(() => setLeft(Math.max(0, end - performance.now())), 200)
    }
    try {
      setCore(await ask(action))
    } catch (e) {
      setFailed({ action, text: e instanceof Error ? e.message : 'Exo could not do that.' })
      // What Exo refused leaves the state as it was, or changed part of it: read where it stands now.
      await ask().then(setCore, () => undefined)
    } finally {
      window.clearInterval(timer)
      setBusy(null)
    }
  }

  if (absent) return null
  const test = core?.test ?? null
  const stage = core?.stage ?? 'none'
  const offer = test?.verdict === 'fix' && stage !== 'restart' ? test.offer ?? null : null
  const measuring = busy === 'measure'
  const seconds = Math.ceil(left / 1000)
  const late = failed?.action === 'measure' ? failed.text
    : measuring ? (seconds > 0 ? `Move your mouse in circles · ${seconds} s` : 'Checking its reports')
      : verdict(core)
  const own = failed && failed.action !== 'measure' ? failed.text
    : stage !== 'none' ? core?.detail ?? null
      : offer !== null ? `CPU ${offer} is quiet. Windows asks once; restart to finish.` : null
  const compare = core?.before && core.after ? { before: core.before, after: core.after } : null
  return (
    <Panel title="CPU core">
      <Line label="Late reports" line={late} bad={failed?.action === 'measure'}>
        {measuring ? (
          <span className="m-core-bar">
            <Progress value={1 - left / MEASURE_MS} label={`Measuring your mouse · ${seconds} of ${MEASURE_MS / 1000} s left`} />
          </span>
        ) : stage === 'restart' ? null : (
          <button type="button" className="m-button" disabled={!!busy} onClick={() => void act('measure')}>
            {test || compare ? 'Measure again' : 'Measure'}
          </button>
        )}
      </Line>
      {offer !== null || stage !== 'none' || core?.canUndo ? (
        <Line label="Own core" line={own} bad={!!failed && failed.action !== 'measure'}>
          <span className="m-inline">
            {offer !== null ? (
              <button type="button" className="m-button is-primary" disabled={!!busy} aria-busy={busy === 'apply'} onClick={() => void act('apply')}>
                {busy === 'apply' ? <Spinner label={`Giving your mouse CPU ${offer}`} /> : null}Give it its own core
              </button>
            ) : null}
            {core?.canUndo ? (
              <button type="button" className="m-button" disabled={!!busy} aria-busy={busy === 'undo'} onClick={() => void act('undo')}>
                {busy === 'undo' ? <Spinner label="Putting your mouse's CPU back" /> : null}Undo
              </button>
            ) : null}
          </span>
        </Line>
      ) : null}
      {compare ? (
        <>
          <Line label="Before" line={where(compare.before)}><span className="m-state">{count(compare.before)}</span></Line>
          <Line label="After" line={where(compare.after)}>
            <span className={`m-state${core?.better ? ' is-good' : ''}`}>{count(compare.after)}</span>
          </Line>
        </>
      ) : null}
    </Panel>
  )
}

/** A row whose one line under its label may wrap, so a long verdict never pushes its control off the panel. */
function Line({ label, line, bad = false, children }: { label: string; line?: string | null; bad?: boolean; children?: ReactNode }) {
  return (
    <div className="m-row m-core-row">
      <span className="m-row-label">
        {label}
        {line ? <span className={`m-row-value${bad ? ' is-bad' : ''}`} role={bad ? 'alert' : undefined}>{line}</span> : null}
      </span>
      {children ? <span className="m-row-control">{children}</span> : null}
    </div>
  )
}

/** Exo's own words open with the figures; the line says them its own way and keeps only the why. */
const FIGURES = /^\d+ Hz, (?:every report on time|[\d,]+ of [\d,]+ reports late, the longest [\d.]+ ms)\.(?: They come with your screen's refresh\.)?\s*/

const n = (v: number) => v.toLocaleString('en-US')
const count = (r: Reading) => `${n(r.missed)} of ${n(r.checked)} late`
const where = (r: Reading) => `${r.cpu === null ? 'CPU not found' : `CPU ${r.cpu}`} · longest ${r.longest.toFixed(1)} ms`

/** The last read's verdict in one line, or what the row does before there is one. */
function verdict(core: Core | null): string {
  const test = core?.test
  if (!test) return core?.stage === 'none' && core.detail ? core.detail : "Whether your mouse's input waits on a busy CPU"
  const r = test.result
  if (test.verdict === 'fix') return `${count(r)} on CPU ${r.cpu ?? '?'}, a busy one`
  const why = test.detail.replace(FIGURES, '') || test.detail
  if (test.verdict === 'retry') return why
  return `${r.missed === 0 ? 'Every report on time' : count(r)}. ${why}`
}
