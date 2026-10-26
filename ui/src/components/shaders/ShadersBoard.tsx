import { useCallback, useEffect, useMemo, useRef, useState, type KeyboardEvent, type PointerEvent } from 'react'
import { host, onHostEvent, type Game } from '../../lib/host'
import { thumbUrl } from '../../lib/coverThumb'
import { usePageClock, usePageVisible } from '../../lib/pageActivity'
import { report } from '../../lib/report'

type Tone = 'good' | 'warn' | 'primary' | 'muted' | 'bad'
/**
 * How long a compile takes on this PC, in seconds: learned from the game's own compile on this driver, else rough (another
 * driver's, its engine's or a default rate stands in). lowSeconds: it may take as little as this (a stale game whose cache
 * may still hold most of it).
 */
type Estimate = { seconds: number; lowSeconds?: number; learned: boolean }
type ShaderGame = {
  id: string
  name: string
  installDir?: string
  statusText?: string
  tone?: Tone
  queued?: boolean
  compiling?: boolean
  percent?: number
  /** The engine's estimate of its next compile, once a plan says what it compiles. */
  estimate?: Estimate | null
  /** Counted in Compile (ready or needing a rebuild, not left out). */
  inAddAllReady?: boolean
  /** compileText: what Compile does for it ("Compile", "Compile without ray tracing (partial)", "Compile carefully"). */
  actions?: { compile?: boolean; compileText?: string }
  /** The last per-game action that failed. */
  error?: string | null
}
type QueueItem = { id: string; name: string; stageText?: string; percent?: number; timeLeftSeconds?: number; error?: string | null }
type Queue = { current?: QueueItem | null; waiting: QueueItem[]; finished?: QueueItem[]; running: boolean; paused: boolean; canStart: boolean }
type Snapshot = {
  rev: number
  games: ShaderGame[]
  queue: Queue
  app: {
    scanning?: boolean
    readyCount?: number
    /** Windows' Advanced Shader Delivery on this PC: works, else the one thing it needs ("Needs Gaming Services …"). */
    gpu?: { shaderDelivery?: { works: boolean; text: string } }
  }
}

const call = (method: string, params?: unknown) => host.shadersCall(method, params ?? {})
  .then(reply => { if (!reply.ok) throw new Error(reply.message || 'The shader engine did not answer.'); return reply.result })

const message = (error: unknown, fallback: string) => error instanceof Error && error.message ? error.message : fallback

/**
 * The newest state wins. The last state the engine left when it exited has rev 0; a starting engine's first states
 * (scanning, no games listed yet) don't replace it, as it lists them already.
 */
const stale = (prev: Snapshot | null, next: Snapshot) =>
  !!prev && (prev.rev > next.rev || (prev.rev === 0 && !!next.app.scanning && next.games.length === 0 && prev.games.length > 0))

/** Each engine counts its revs from 1: what an earlier one sent reads as rev 0, so the running one's states win. */
const rebase = (prev: Snapshot | null) => prev && prev.rev > 0 ? { ...prev, rev: 0 } : prev

/**
 * The engine's state: the last one it left when it exited, until the board is on screen; then the engine runs, read
 * once and kept live by its own events (a state event is the whole snapshot, a progress event the queue). The engine
 * compiles the same whether or not anything shows, and exits by itself a minute after nothing needs it.
 */
function useShaderEngine(shown: boolean) {
  const [snap, setSnap] = useState<Snapshot | null>(null)
  const [missing, setMissing] = useState(false)
  const [failed, setFailed] = useState<string | null>(null)
  const take = useCallback((next: Snapshot) => setSnap(prev => stale(prev, next) ? prev : next), [])
  const read = useCallback(() => call('snapshot').then(next => { take(next as Snapshot); setFailed(null) }).catch((error: unknown) => {
    report(error, 'ShadersBoard.snapshot', { level: 'warn' })
    setFailed(message(error, 'The shader engine did not answer.'))
  }), [take])
  // Once with the room, as it's first built: the last state, so the board is filled by the time it's scrolled to,
  // without starting the engine.
  useEffect(() => {
    let live = true
    void host.shadersAddon().then(status => {
      if (!live) return
      if (!status.present) setMissing(true)
      else if (status.snapshot) take(status.snapshot as Snapshot)
    }).catch(() => { if (live) setMissing(true) })
    return () => { live = false }
  }, [take])
  useEffect(() => {
    if (!shown) return
    let live = true
    // The engine may have exited and started again since the board last showed.
    setSnap(rebase)
    void host.shadersAddon().then(status => {
      if (!live) return
      if (!status.present) { setMissing(true); return }
      void call('ui.visible', { visible: true }).catch(() => { })
      void read()
    }).catch(() => { if (live) setMissing(true) })
    const off = onHostEvent('shaders.event', ({ name, data }) => {
      if (name === 'state') take(data as Snapshot)
      else if (name === 'progress') {
        const next = data as { rev: number; queue: Queue }
        setSnap(prev => prev && next.rev > prev.rev ? { ...prev, rev: next.rev, queue: next.queue } : prev)
      } else if (name === 'ready') { setSnap(rebase); void read() }
      else if (name === 'addon') void read()
    })
    return () => {
      live = false
      off()
      void call('ui.visible', { visible: false }).catch(() => { })
    }
  }, [shown, read, take])
  return { snap, missing, failed, read }
}

/** On screen: its room shown, Exo not hidden or minimized, and the board in (or near) the room's view. */
function useShown(element: HTMLElement | null, active: boolean) {
  const visible = usePageVisible()
  const [near, setNear] = useState(false)
  useEffect(() => {
    if (!element || !active) return
    const watch = new IntersectionObserver(([entry]) => setNear(entry.isIntersecting), { rootMargin: '200px 0px' })
    watch.observe(element)
    return () => { watch.disconnect(); setNear(false) }
  }, [element, active])
  return active && visible && near
}

/**
 * How long each game takes to compile on this PC: the engine's live estimate (learned per game in the plan
 * check a scan starts, before anything is compiled), with the host's rough sizing for the games the engine
 * has none for yet (read again when a compile ends or a scan changes the list).
 */
function useEstimates(active: boolean, key: string, games: readonly ShaderGame[] | undefined) {
  const [seconds, setSeconds] = useState<Record<string, number>>({})
  useEffect(() => {
    if (!active) return
    let live = true
    void host.shadersEstimates().then(reply => { if (live && reply.ok) setSeconds(reply.seconds) })
      .catch((error: unknown) => report(error, 'ShadersBoard.estimates', { level: 'warn' }))
    return () => { live = false }
  }, [active, key])
  return useMemo(() => {
    const sized = Object.fromEntries(Object.entries(seconds).map(([id, value]): [string, Estimate] => [id, { seconds: value, learned: false }]))
    const live = Object.fromEntries((games ?? []).flatMap(game => game.estimate ? [[game.id, game.estimate] as const] : []))
    return { ...sized, ...live }
  }, [seconds, games])
}

const ORDER_KEY = 'exo.shadersOrder'

/** The order Compile queues the ready games in, as you last dragged them. */
function useSavedOrder() {
  const [order, setOrder] = useState<string[]>(() => {
    try {
      const saved: unknown = JSON.parse(localStorage.getItem(ORDER_KEY) ?? '[]')
      return Array.isArray(saved) ? saved.filter((id): id is string => typeof id === 'string') : []
    } catch { return [] }
  })
  const save = useCallback((next: string[]) => {
    setOrder(next)
    try { localStorage.setItem(ORDER_KEY, JSON.stringify(next)) } catch { /* kept for this session */ }
  }, [])
  return [order, save] as const
}

const norm = (value: string) => value.toLowerCase().replace(/[^a-z0-9]/g, '')

/** Exo's own cover for an engine game: the same id, else the same folder, else the same title. */
function coverFinder(games: readonly Game[]) {
  const installed = games.filter(game => game.installed && game.coverUrl)
  const byId = new Map(installed.map(game => [game.id, game]))
  const byTitle = new Map(installed.map(game => [norm(game.title), game]))
  return (item: { id: string; name: string; installDir?: string }) => {
    const dir = item.installDir?.toLowerCase()
    const game = byId.get(item.id)
      ?? (dir ? installed.find(g => g.path && dir.startsWith(g.path.toLowerCase())) : undefined)
      ?? byTitle.get(norm(item.name))
    return game?.coverUrl ? thumbUrl(game.coverUrl, 240) : null
  }
}

const wallClock = () => Date.now()

/**
 * System > Shaders, in Exo itself: games are scanned on their own (each time the engine starts, and when Exo's games
 * change) and on Refresh, and only those that still need compiling show, each as its poster with its state; Compile
 * queues every ready game in the order shown, and the queue plays out live on the posters (the one compiling first,
 * with its progress, then the rest). The waiting and ready posters drag (or move with Ctrl and the arrow keys) into
 * the order to compile. The engine runs while the board is on screen, and shows its last state before that.
 */
export function ShadersBoard({ active, games }: { active: boolean; games: readonly Game[] }) {
  const [root, setRoot] = useState<HTMLElement | null>(null)
  const { snap, missing, failed, read } = useShaderEngine(useShown(root, active))
  const cover = useMemo(() => coverFinder(games), [games])
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [order, saveOrder] = useSavedOrder()
  // The order a drop asked the engine for, shown until the engine's next snapshot has it.
  const [pending, setPending] = useState<string[] | null>(null)
  const [moved, setMoved] = useState('')
  const estimates = useEstimates(active, `${snap?.games.length ?? 0}:${snap?.queue.finished?.length ?? 0}:${snap?.app.scanning ? 1 : 0}`, snap?.games)
  const run = (key: string, ...steps: Step[]) => {
    setBusy(key)
    setError(null)
    void steps.reduce<Promise<unknown>>((chain, [method, params]) => chain.then(() => call(method, params)), Promise.resolve())
      .catch((caught: unknown) => {
        report(caught, `ShadersBoard.${key}`, { level: 'warn' })
        setError(message(caught, 'That did not work. Try again.'))
      })
      .finally(() => { setBusy(null); void read().finally(() => setPending(null)) })
  }
  if (missing) return <p className="exo-shaders-empty">Exo Shaders is not installed on this PC.</p>
  // Before the first snapshot: nothing while it is read, else why it failed and Try again. The same element as the
  // board's, so its observer (useShown) doesn't see it leave the screen when the snapshot comes.
  if (!snap) return (
    <div ref={setRoot} className="exo-shaders-board" aria-busy={failed ? undefined : true}>
      {failed
        ? <header className="exo-shaders-bar">
          <span className="exo-shaders-line is-bad" role="alert">{failed}</span>
          <button type="button" className="exo-set-btn" onClick={() => void read()}>Try again</button>
        </header>
        : null}
    </div>
  )
  const { queue } = snap
  const current = queue.current ?? null
  const view = arrange(snap, order, pending)
  const { lane, waitingIds } = view

  /** Puts a lane poster at a new place: the engine's queue for a waiting game, the saved order for a ready one. */
  const move = (id: string, target: number) => {
    const waiting = waitingIds.length
    const from = lane.indexOf(id)
    if (from < 0) return
    // A waiting game stays among the waiting; a ready one dropped among them joins the queue there.
    const to = Math.max(0, Math.min(from < waiting ? waiting - 1 : lane.length - 1, target))
    if (to === from) return
    const next = lane.filter(other => other !== id)
    next.splice(to, 0, id)
    setMoved(`${view.byId.get(id)?.name ?? 'The game'} is ${to === 0 ? 'first' : `number ${to + 1}`} of ${next.length}.`)
    if (to >= waiting) {
      const ready = next.slice(waiting)
      saveOrder([...ready, ...order.filter(other => !ready.includes(other))])
      return
    }
    setPending(next)
    run(`move:${id}`, ...(from < waiting ? [] : [['queue.add', { id }] as Step]), ['queue.move', { id, index: to }])
  }

  return (
    <div ref={setRoot} className="exo-shaders-board">
      <Bar snap={snap} view={view} estimates={estimates} busy={busy} run={run} />
      <Delivery snap={snap} />
      {error || failed ? <p className="exo-shaders-error" role="alert">{error ?? failed}</p> : null}
      {current && typeof current.percent === 'number'
        ? <div className="exo-shaders-progress" role="progressbar" aria-label={current.name} aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(current.percent)}>
          <span style={{ transform: `scaleX(${Math.min(1, Math.max(0, current.percent / 100))})` }} />
        </div>
        : null}
      <Posters
        posters={view.posters}
        lane={lane}
        onMove={move}
        render={(game, drop) => {
          const at = lane.indexOf(game.id)
          return (
            <Poster game={game} cover={cover(game)} current={current?.id === game.id ? current : null} estimate={estimates[game.id]}
              place={at >= 0 && at < waitingIds.length ? at + 1 : undefined} orderable={at >= 0} drop={drop}
              error={game.error || queue.finished?.find(item => item.id === game.id && item.error)?.error || null}
              onCompile={() => run(`game:${game.id}`, game.queued ? ['queue.remove', { id: game.id }] : ['game.compile', { id: game.id }])}
              onKey={event => {
                const to = at >= 0 ? reorderKey(event, at, lane.length - 1) : undefined
                if (to === undefined) return
                event.preventDefault()
                move(game.id, to)
              }} />
          )
        }}
      />
      <span id="exo-shaders-move-help" className="sr-only">Drag it, or press Ctrl and an arrow key, to change where it is in the queue.</span>
      <span className="sr-only" role="status" aria-live="polite">{moved}</span>
    </div>
  )
}

/**
 * Windows' precompiled shaders (Advanced Shader Delivery): on for this PC, and how many games came with theirs
 * (Exo Shaders leaves those out, as Precompiled), or the one thing this PC still needs for them.
 */
function Delivery({ snap }: { snap: Snapshot }) {
  const delivery = snap.app.gpu?.shaderDelivery
  if (!delivery) return null
  const precompiled = snap.games.filter(game => game.statusText === 'Precompiled').length
  const line = delivery.works
    ? `On${precompiled ? ` · ${precompiled} ${precompiled === 1 ? 'game came' : 'games came'} precompiled, nothing to compile` : ''}`
    : delivery.text
  return (
    <p className="exo-shaders-delivery">
      <span className={`exo-set-state${delivery.works ? ' is-on' : ''}`}>Windows' precompiled shaders</span>
      <span>{line}</span>
    </p>
  )
}

type Step = [string, unknown?]
type View = ReturnType<typeof arrange>

/**
 * The board's order: the compiling game, then the lane (the waiting games in the engine's order, then
 * the ready ones in the saved order: what Compile queues), then everything else by its state.
 */
function arrange(snap: Snapshot, order: string[], pending: string[] | null) {
  const current = snap.queue.current ?? null
  const byId = new Map(snap.games.map(game => [game.id, game]))
  const waitingIds = snap.queue.waiting.map(item => item.id).filter(id => id !== current?.id)
  const inQueue = new Set([current?.id, ...waitingIds])
  const saved = new Map(order.map((id, index) => [id, index]))
  const readyIds = snap.games
    .filter(game => game.inAddAllReady && !inQueue.has(game.id))
    .map((game, index) => ({ id: game.id, at: saved.get(game.id) ?? order.length + index }))
    .sort((a, b) => a.at - b.at)
    .map(game => game.id)
  const engineLane = [...waitingIds, ...readyIds]
  const lane = pending && pending.length === engineLane.length && pending.every(id => engineLane.includes(id)) ? pending : engineLane
  const inLane = new Set(lane)
  // Only what still needs doing: compiled games and ones the engine can't build stay out of the way.
  const rest = snap.games.filter(game => !inQueue.has(game.id) && !inLane.has(game.id) && (!!game.error || game.tone === 'warn' || game.tone === 'primary')).sort((a, b) => rank(a) - rank(b))
  const posters = [current ? byId.get(current.id) : undefined, ...lane.map(id => byId.get(id)), ...rest].filter((game): game is ShaderGame => !!game)
  return { byId, waitingIds, readyIds: lane.filter(id => !waitingIds.includes(id)), lane, posters }
}

/** Ctrl and an arrow key move a poster one place, Ctrl+Home and Ctrl+End to either end of the lane. */
function reorderKey(event: KeyboardEvent<HTMLButtonElement>, at: number, last: number): number | undefined {
  if (!event.ctrlKey || event.altKey || event.metaKey) return undefined
  return ({ ArrowLeft: at - 1, ArrowUp: at - 1, ArrowRight: at + 1, ArrowDown: at + 1, Home: 0, End: last } as Record<string, number>)[event.key]
}

/** What is happening with the time it leaves, Refresh (a scan for games), and Compile or Pause and Stop. */
function Bar({ snap, view, estimates, busy, run }: {
  snap: Snapshot
  view: View
  estimates: Record<string, Estimate>
  busy: string | null
  run: (key: string, ...steps: Step[]) => void
}) {
  const now = usePageClock(30_000, wallClock)
  const { queue } = snap
  const current = queue.current ?? null
  const { waitingIds, readyIds, lane } = view
  const working = !!current || queue.running
  return (
    <header className="exo-shaders-bar">
      <span className="exo-shaders-line">
        {current
          ? <><strong>{current.name}</strong>{current.stageText ? ` · ${current.stageText}` : ''}{waitingIds.length ? ` · ${waitingIds.length} waiting` : ''}</>
          : working ? `${waitingIds.length} waiting` : idleLine(snap, lane.length)}
        {working || lane.length ? eta(queueTime(current, working ? waitingIds : lane, estimates), now, working, queue.paused) : null}
      </span>
      <button type="button" className="exo-set-btn" disabled={!!snap.app.scanning || busy === 'scan'} onClick={() => run('scan', ['scan'])}>
        {snap.app.scanning || busy === 'scan' ? 'Refreshing' : 'Refresh'}
      </button>
      {working
        ? <>
          <button type="button" className="exo-set-btn" onClick={() => run('pause', [queue.paused ? 'queue.resume' : 'queue.pause'])}>{queue.paused ? 'Resume' : 'Pause'}</button>
          <button type="button" className="exo-set-btn" onClick={() => run('stop', ['queue.stop'])}>Stop</button>
        </>
        : <button type="button" className="exo-set-btn is-primary" disabled={(readyIds.length === 0 && !queue.canStart) || busy === 'compile'}
          onClick={() => run('compile', ...(readyIds.length ? [['queue.add', { ids: readyIds }] as Step] : []), ['queue.start'])}>Compile</button>}
    </header>
  )
}

type Drop = 'before' | 'after' | 'lifted' | null
type Dragging = { id: string; pointer: number; x: number; y: number; to: number | null }

/**
 * The poster grid, with pointer dragging of the lane's posters: past a few pixels a press becomes a
 * drag, a white bar shows where it will land, the list scrolls at its edges, and the click that ends
 * a drag does nothing else.
 */
function Posters({ posters, lane, onMove, render }: {
  posters: ShaderGame[]
  lane: string[]
  onMove: (id: string, to: number) => void
  render: (game: ShaderGame, drop: Drop) => React.ReactNode
}) {
  const list = useRef<HTMLUListElement>(null)
  const [drag, setDrag] = useState<Dragging | null>(null)
  const dragged = useRef(false)
  /** The lane posters' boxes, measured once as a drag starts; re-measured only if the room scrolls mid-drag. */
  const rects = useRef<DOMRect[] | null>(null)
  const measureLane = () => {
    rects.current = Array.from(list.current?.querySelectorAll<HTMLElement>('[data-lane]') ?? []).map(item => item.getBoundingClientRect())
  }
  /** Where in the lane the pointer would drop: before the first poster it is ahead of, else after the last. */
  const slotAt = (x: number, y: number) => {
    const items = rects.current ?? []
    const index = items.findIndex(box => y < box.top || (y <= box.bottom && x < box.left + box.width / 2))
    return index < 0 ? items.length : index
  }
  const down = (event: PointerEvent<HTMLLIElement>, id: string) => {
    dragged.current = false
    if (event.button !== 0 || !lane.includes(id)) return
    setDrag({ id, pointer: event.pointerId, x: event.clientX, y: event.clientY, to: null })
  }
  const moveTo = (event: PointerEvent<HTMLLIElement>) => {
    if (!drag || event.pointerId !== drag.pointer) return
    if (drag.to === null) {
      if (Math.hypot(event.clientX - drag.x, event.clientY - drag.y) < 6) return
      try { event.currentTarget.setPointerCapture(event.pointerId) } catch { /* a pointer already up; the drag still follows the list */ }
      dragged.current = true
      measureLane()
    }
    // The System room scrolls the list (it's one page); near its top or bottom edge it scrolls on.
    const scroller = list.current?.closest<HTMLElement>('.exo-system') ?? list.current
    const box = scroller?.getBoundingClientRect()
    if (box && scroller) {
      const edge = event.clientY < box.top + 48 ? -1 : event.clientY > box.bottom - 48 ? 1 : 0
      if (edge) { scroller.scrollBy({ top: edge * 16 }); measureLane() }
    }
    setDrag({ ...drag, to: slotAt(event.clientX, event.clientY) })
  }
  const up = (event: PointerEvent<HTMLLIElement>) => {
    if (!drag || event.pointerId !== drag.pointer) return
    if (drag.to !== null) {
      const from = lane.indexOf(drag.id)
      onMove(drag.id, drag.to > from ? drag.to - 1 : drag.to)
    }
    rects.current = null
    setDrag(null)
  }
  const target = drag?.to ?? null
  const from = drag ? lane.indexOf(drag.id) : -1
  const shows = target !== null && target !== from && target !== from + 1
  return (
    <ul ref={list} className={`exo-shaders-posters${drag?.to != null ? ' is-dragging' : ''}`}
      onClickCapture={event => { if (dragged.current) { dragged.current = false; event.preventDefault(); event.stopPropagation() } }}>
      {posters.map(game => {
        const at = lane.indexOf(game.id)
        const drop: Drop = drag?.to != null && game.id === drag.id ? 'lifted'
          : shows && at === target ? 'before'
          : shows && target === lane.length && at === lane.length - 1 ? 'after' : null
        return (
          <li key={game.id} data-lane={at >= 0 ? at : undefined}
            className={`exo-shaders-slot${drop ? ` is-${drop}` : ''}`}
            onPointerDown={event => down(event, game.id)}
            onPointerMove={moveTo}
            onPointerUp={up}
            onPointerCancel={() => { rects.current = null; setDrag(null) }}>
            {render(game, drop)}
          </li>
        )
      })}
    </ul>
  )
}

/** The bar's line while nothing compiles: what Compile would queue, else what a scan found (or that none has run yet). */
function idleLine(snap: Snapshot, toCompile: number) {
  if (snap.app.scanning) return 'Looking for games'
  return toCompile ? `${toCompile} ${toCompile === 1 ? 'game' : 'games'} ready to compile` : 'Every game is compiled'
}

/** How far a rough estimate may be off either way: another driver's, another game's or a default rate stands in. */
const ROUGH = 1.5

/** A time in seconds as the least and the most it may take; learned: this PC's own compiles of the games taught it. */
type Span = { low: number; high: number; learned: boolean }

/**
 * An estimate as a span: a learned one is its time, a rough one spreads by ROUGH, and a stale game's
 * runs from its new pipelines alone to every one (its cache may no longer hold the rest).
 */
function spanOf({ seconds, lowSeconds, learned }: Estimate, share = 1): Span {
  const sure = learned && lowSeconds === undefined
  return {
    low: (lowSeconds ?? (sure ? seconds : seconds / ROUGH)) * share,
    high: (lowSeconds !== undefined || sure ? seconds : seconds * ROUGH) * share,
    learned: sure,
  }
}

/**
 * What is left of the queue: the compiling game's time left at its live rate (else its share of
 * its estimate), then each game after it. Unknown games make the total a floor.
 */
function queueTime(current: QueueItem | null, after: string[], estimates: Record<string, Estimate>) {
  const total: Span = { low: 0, high: 0, learned: true }
  let unknown = 0
  const add = (span: Span) => { total.low += span.low; total.high += span.high; total.learned &&= span.learned }
  if (current) {
    const usual = estimates[current.id]
    if (typeof current.timeLeftSeconds === 'number') add({ low: current.timeLeftSeconds, high: current.timeLeftSeconds, learned: true })
    else if (usual) add(spanOf(usual, 1 - Math.min(100, current.percent ?? 0) / 100))
    else unknown++
  }
  for (const id of after) {
    const usual = estimates[id]
    if (usual) add(spanOf(usual))
    else unknown++
  }
  return { ...total, floor: unknown > 0 }
}

/**
 * " · about 2 h 10 min left, done around 4:30 AM" when this PC's own compiles taught every time; else
 * what it may take as a rounded range and "rough estimate", never a clock time it can't keep.
 */
function eta({ low, high, learned, floor }: Span & { floor: boolean }, now: number, running: boolean, paused: boolean) {
  if (high <= 0) return null
  const left = running ? ' left' : ''
  if (floor) return low >= 60 ? ` · over ${roundSpan(low).span}${left}, rough estimate` : null
  if (!learned) return ` · ${rangeText(low, high)}${left}, rough estimate`
  if (high < 60) return ` · under a minute${left}`
  const { span, rounded } = roundSpan(high)
  const done = new Date(now + rounded * 60_000)
  const today = new Date(now).toDateString() === done.toDateString()
  const clock = `${today ? '' : done.toLocaleDateString(undefined, { weekday: 'short' }) + ' '}${done.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })}`
  return ` · about ${span}${left}${paused ? '' : `, done around ${clock}`}`
}

/** Seconds as the board says them, rounded to what an estimate can promise: "under a minute", "15 min", "2 h 10 min". */
function roundSpan(seconds: number) {
  const minutes = seconds / 60
  const step = minutes < 10 ? 1 : minutes < 60 ? 5 : minutes < 600 ? 10 : 60
  const rounded = Math.max(step, Math.round(minutes / step) * step)
  return { rounded, span: seconds < 60 ? 'under a minute' : minuteText(rounded) }
}

/** A rough time as a rounded range: "under a minute", "up to 6 min", "15–30 min", "1–2 h", "1 h–2 h 30 min". */
function rangeText(low: number, high: number) {
  if (high < 60) return 'under a minute'
  const minutes = high / 60
  const step = minutes < 10 ? 1 : minutes < 60 ? 5 : minutes < 600 ? 30 : 60
  const top = Math.max(step, Math.round(minutes / step) * step)
  const bottom = Math.round(low / 60 / step) * step
  if (bottom <= 0) return `up to ${minuteText(top)}`
  if (bottom >= top) return `about ${minuteText(top)}`
  if (top < 60) return `${bottom}–${minuteText(top)}`
  return bottom % 60 === 0 && top % 60 === 0 ? `${bottom / 60}–${top / 60}\u00a0h` : `${minuteText(bottom)}–${minuteText(top)}`
}

/** Whole minutes as words. A number keeps its unit on its line (no-break spaces): a poster's narrow caption broke "50" from "min". */
function minuteText(minutes: number) {
  const h = Math.floor(minutes / 60), m = minutes % 60
  return h ? (m ? `${h}\u00a0h ${m}\u00a0min` : `${h}\u00a0h`) : `${m}\u00a0min`
}

/**
 * A game's compile time on its poster: "about 25 min" when this PC's compiles of it taught it, else a rounded range. One
 * piece on the narrow caption (no-break spaces, a word joiner each side of the dash): it wrapped as "15–" over "30 min".
 */
function timeText(estimate: Estimate) {
  const { low, high, learned } = spanOf(estimate)
  const text = !learned ? rangeText(low, high) : high < 60 ? 'under a minute' : `about ${roundSpan(high).span}`
  return text.replaceAll(' ', '\u00a0').replaceAll('–', '\u2060–\u2060')
}

function rank(game: ShaderGame) {
  if (game.tone === 'warn' || game.tone === 'primary') return 1000
  if (game.tone === 'good') return 2000
  return 3000
}

/** What a click compiles when it isn't the plain Compile, in the engine's words ("Compile without ray tracing (partial)", "Compile carefully"); else null. */
function otherCompile(game: ShaderGame) {
  const text = game.actions?.compileText
  return text && text !== 'Compile' && text.startsWith('Compile') ? text : null
}

/** The poster's state: its progress while it compiles, else its place in the queue or its state, with how long it takes. */
function posterLine(game: ShaderGame, current: QueueItem | null, place: number | undefined, estimate: Estimate | undefined) {
  if (current) return typeof current.percent === 'number' ? `${Math.round(current.percent)}%` : current.stageText ?? 'Compiling'
  const time = estimate && (place || game.inAddAllReady) ? ` · ${timeText(estimate)}` : ''
  return place ? `${place === 1 ? 'Next' : `#${place}`} in queue${time}` : `${game.statusText ?? ''}${time}`
}

function Poster({ game, cover, current, estimate, place, orderable, drop, error, onCompile, onKey }: {
  game: ShaderGame
  cover: string | null
  current: QueueItem | null
  /** How long it takes to compile here, for a game Compile would queue or has queued. */
  estimate: Estimate | undefined
  place: number | undefined
  orderable: boolean
  drop: Drop
  error: string | null
  onCompile: () => void
  onKey: (event: KeyboardEvent<HTMLButtonElement>) => void
}) {
  const can = game.queued || game.actions?.compile === true
  const line = posterLine(game, current, place, estimate)
  const tone = current || place ? 'live' : game.tone ?? 'muted'
  const other = can && !game.queued && !current ? otherCompile(game) : null
  return (
    <div className={`exo-shaders-poster is-${tone}${drop === 'lifted' ? ' is-lifted' : ''}`}>
      <button type="button" disabled={!can} onClick={onCompile} onKeyDown={onKey}
        aria-label={game.queued ? `Remove ${game.name} from the queue` : (otherCompile(game) ?? 'Compile').replace('Compile', `Compile ${game.name}`)}
        aria-describedby={orderable ? 'exo-shaders-move-help' : undefined}>
        <span className="exo-shaders-art">
          {cover ? <img src={cover} alt="" loading="lazy" decoding="async" draggable={false} /> : <span className="exo-shaders-art-name">{game.name}</span>}
          {current && typeof current.percent === 'number'
            ? <span className="exo-shaders-art-fill" style={{ transform: `scaleY(${1 - Math.min(1, current.percent / 100)})` }} />
            : null}
        </span>
        <span className="exo-shaders-name">{game.name}</span>
        <span className="exo-shaders-state">{line}</span>
        {other ? <span className="exo-shaders-state">{other}</span> : null}
        {error ? <span className="exo-shaders-state is-bad">{error}</span> : null}
      </button>
    </div>
  )
}
