import { useCallback, useEffect, useMemo, useRef, useState, type KeyboardEvent, type PointerEvent } from 'react'
import { host, onHostEvent, type Game } from '../../lib/host'
import { thumbUrl } from '../../lib/coverThumb'
import { usePageClock } from '../../lib/pageActivity'
import { report } from '../../lib/report'

type Tone = 'good' | 'warn' | 'primary' | 'muted' | 'bad'
type ShaderGame = {
  id: string
  name: string
  installDir?: string
  statusText?: string
  tone?: Tone
  queued?: boolean
  compiling?: boolean
  percent?: number
  /** The engine's last compile time, else its estimate once a scan's plan check learned what the game needs: "1 h 5 min", "12 min", "40 s". */
  compileTime?: string
  /** Counted in Compile (ready or needing a rebuild, not left out). */
  inAddAllReady?: boolean
  actions?: { compile?: boolean }
  /** The last per-game action that failed. */
  error?: string | null
}
type QueueItem = { id: string; name: string; stageText?: string; percent?: number; timeLeftSeconds?: number; error?: string | null }
type Queue = { current?: QueueItem | null; waiting: QueueItem[]; finished?: QueueItem[]; running: boolean; paused: boolean; canStart: boolean }
type Snapshot = {
  rev: number
  games: ShaderGame[]
  queue: Queue
  app: { scanning?: boolean; readyCount?: number }
}

const call = (method: string, params?: unknown) => host.shadersCall(method, params ?? {})
  .then(reply => { if (!reply.ok) throw new Error(reply.message || 'The shader engine did not answer.'); return reply.result })

const message = (error: unknown, fallback: string) => error instanceof Error && error.message ? error.message : fallback

/**
 * The engine's state while the board shows: read once, then kept live by the engine's own events
 * (a state event is the whole snapshot, a progress event the queue), newest rev wins. The engine
 * compiles the same whether or not anything shows; coming back reads it fresh.
 */
function useShaderEngine(active: boolean) {
  const [snap, setSnap] = useState<Snapshot | null>(null)
  const [missing, setMissing] = useState(false)
  const [failed, setFailed] = useState<string | null>(null)
  const take = useCallback((next: Snapshot) => setSnap(prev => prev && prev.rev > next.rev ? prev : next), [])
  const read = useCallback(() => call('snapshot').then(next => { take(next as Snapshot); setFailed(null) }).catch((error: unknown) => {
    report(error, 'ShadersBoard.snapshot', { level: 'warn' })
    setFailed(message(error, 'The shader engine did not answer.'))
  }), [take])
  // Once with the room, behind the splash: the board is filled the first time it shows.
  useEffect(() => {
    let live = true
    void host.shadersAddon().then(status => { if (live) { if (status.present) void read(); else setMissing(true) } }).catch(() => { if (live) setMissing(true) })
    return () => { live = false }
  }, [read])
  useEffect(() => {
    if (!active) return
    let live = true
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
      } else if (name === 'ready' || name === 'addon') void read()
    })
    return () => {
      live = false
      off()
      void call('ui.visible', { visible: false }).catch(() => { })
    }
  }, [active, read, take])
  return { snap, missing, failed, read }
}

/** "1 h 5 min", "12 min" or "40 s" (the engine's own format) in seconds, else undefined. */
function durationSeconds(text: string | undefined) {
  const match = text ? /^(?:(\d+) h)? ?(?:(\d+) min)?$|^(\d+) s$/.exec(text) : null
  if (!match || match[0] === '') return undefined
  return match[3] ? Number(match[3]) : Number(match[1] ?? 0) * 3600 + Number(match[2] ?? 0) * 60
}

/**
 * Seconds each game takes to compile on this PC: the host's (read again when a compile ends or a scan
 * changes the list; it sizes games the engine has no time for yet), overlaid with the engine's live
 * compile time, which it learns per game in the plan check a scan starts, before anything is compiled.
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
    const live = Object.fromEntries((games ?? []).flatMap(game => {
      const known = durationSeconds(game.compileTime)
      return known === undefined ? [] : [[game.id, known]]
    }))
    return { ...seconds, ...live }
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
 * System > Shaders, in Exo itself: Scan games finds what the engine can build, each game shows as
 * its poster with its state, and Compile queues every ready game in the order shown; the queue
 * plays out live on the posters (the one compiling first, with its progress, then the rest in order).
 * The waiting and ready posters drag (or move with Ctrl and the arrow keys) into the order to compile.
 */
export function ShadersBoard({ active, games }: { active: boolean; games: readonly Game[] }) {
  const { snap, missing, failed, read } = useShaderEngine(active)
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
  if (!snap) return <Unread failed={failed} onRetry={() => void read()} />
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
    <div className="exo-shaders-board">
      <Bar snap={snap} view={view} estimates={estimates} busy={busy} run={run} />
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
            <Poster game={game} cover={cover(game)} current={current?.id === game.id ? current : null} seconds={estimates[game.id]}
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
  const rest = snap.games.filter(game => !inQueue.has(game.id) && !inLane.has(game.id)).sort((a, b) => rank(a) - rank(b))
  const posters = [current ? byId.get(current.id) : undefined, ...lane.map(id => byId.get(id)), ...rest].filter((game): game is ShaderGame => !!game)
  return { byId, waitingIds, readyIds: lane.filter(id => !waitingIds.includes(id)), lane, posters }
}

/** Ctrl and an arrow key move a poster one place, Ctrl+Home and Ctrl+End to either end of the lane. */
function reorderKey(event: KeyboardEvent<HTMLButtonElement>, at: number, last: number): number | undefined {
  if (!event.ctrlKey || event.altKey || event.metaKey) return undefined
  return ({ ArrowLeft: at - 1, ArrowUp: at - 1, ArrowRight: at + 1, ArrowDown: at + 1, Home: 0, End: last } as Record<string, number>)[event.key]
}

/** What is happening with the time it leaves, Scan games, and Compile or Pause and Stop. */
function Bar({ snap, view, estimates, busy, run }: {
  snap: Snapshot
  view: View
  estimates: Record<string, number>
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
        {working || lane.length ? eta(queueSeconds(current, working ? waitingIds : lane, estimates), now, working, queue.paused) : null}
      </span>
      <button type="button" className="exo-set-btn" disabled={!!snap.app.scanning || busy === 'scan'} onClick={() => run('scan', ['scan'])}>
        {snap.app.scanning || busy === 'scan' ? 'Scanning' : 'Scan games'}
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
  /** Where in the lane the pointer would drop: before the first poster it is ahead of, else after the last. */
  const slotAt = (x: number, y: number) => {
    const items = Array.from(list.current?.querySelectorAll<HTMLElement>('[data-lane]') ?? [])
    const index = items.findIndex(item => {
      const box = item.getBoundingClientRect()
      return y < box.top || (y <= box.bottom && x < box.left + box.width / 2)
    })
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
    }
    const box = list.current?.getBoundingClientRect()
    if (box && list.current) {
      const edge = event.clientY < box.top + 48 ? -1 : event.clientY > box.bottom - 48 ? 1 : 0
      if (edge) list.current.scrollBy({ top: edge * 16 })
    }
    setDrag({ ...drag, to: slotAt(event.clientX, event.clientY) })
  }
  const up = (event: PointerEvent<HTMLLIElement>) => {
    if (!drag || event.pointerId !== drag.pointer) return
    if (drag.to !== null) {
      const from = lane.indexOf(drag.id)
      onMove(drag.id, drag.to > from ? drag.to - 1 : drag.to)
    }
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
            onPointerCancel={() => setDrag(null)}>
            {render(game, drop)}
          </li>
        )
      })}
    </ul>
  )
}

/** Before the first snapshot: nothing while it is read, else why it failed and Try again. */
function Unread({ failed, onRetry }: { failed: string | null; onRetry: () => void }) {
  if (!failed) return <div className="exo-shaders-board" aria-busy="true" />
  return (
    <div className="exo-shaders-board">
      <header className="exo-shaders-bar">
        <span className="exo-shaders-line is-bad" role="alert">{failed}</span>
        <button type="button" className="exo-set-btn" onClick={onRetry}>Try again</button>
      </header>
    </div>
  )
}

/** The bar's line while nothing compiles: what Compile would queue, else what a scan found (or that none has run yet). */
function idleLine(snap: Snapshot, toCompile: number) {
  if (snap.games.length === 0) return snap.app.scanning ? 'Looking for games' : 'Scan games to find what to compile'
  return toCompile ? `${toCompile} ${toCompile === 1 ? 'game' : 'games'} ready to compile` : 'Every game is compiled'
}

/**
 * What is left of the queue, in seconds: the compiling game's time left at its live rate (else its
 * share of its usual time), then each game after it at its usual time on this PC. Unknown games
 * make the total a floor.
 */
function queueSeconds(current: QueueItem | null, after: string[], estimates: Record<string, number>) {
  let seconds = 0
  let unknown = 0
  if (current) {
    const usual = estimates[current.id]
    if (typeof current.timeLeftSeconds === 'number') seconds += current.timeLeftSeconds
    else if (usual !== undefined) seconds += usual * (1 - Math.min(100, current.percent ?? 0) / 100)
    else unknown++
  }
  for (const id of after) {
    const usual = estimates[id]
    if (usual === undefined) unknown++
    else seconds += usual
  }
  return { seconds, floor: unknown > 0 }
}

/** " · about 2 h 10 min left, done around 4:30 AM": rounded to what the estimate can promise. */
function eta({ seconds, floor }: { seconds: number; floor: boolean }, now: number, running: boolean, paused: boolean) {
  if (seconds <= 0) return null
  const { span, rounded } = roundSpan(seconds)
  const done = new Date(now + rounded * 60_000)
  const today = new Date(now).toDateString() === done.toDateString()
  const clock = `${today ? '' : done.toLocaleDateString(undefined, { weekday: 'short' }) + ' '}${done.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })}`
  return ` · ${floor ? 'over' : 'about'} ${span}${running ? ' left' : ''}${paused || floor ? '' : `, done around ${clock}`}`
}

/** Seconds as the board says them, rounded to what an estimate can promise: "under a minute", "15 min", "2 h 10 min". */
function roundSpan(seconds: number) {
  const minutes = seconds / 60
  const step = minutes < 10 ? 1 : minutes < 60 ? 5 : minutes < 600 ? 10 : 60
  const rounded = Math.max(step, Math.round(minutes / step) * step)
  const h = Math.floor(rounded / 60), m = rounded % 60
  return { rounded, span: seconds < 60 ? 'under a minute' : h ? (m ? `${h} h ${m} min` : `${h} h`) : `${m} min` }
}

function rank(game: ShaderGame) {
  if (game.tone === 'warn' || game.tone === 'primary') return 1000
  if (game.tone === 'good') return 2000
  return 3000
}

/** The poster's state: its progress while it compiles, else its place in the queue or its state, with how long it takes. */
function posterLine(game: ShaderGame, current: QueueItem | null, place: number | undefined, seconds: number | undefined) {
  if (current) return typeof current.percent === 'number' ? `${Math.round(current.percent)}%` : current.stageText ?? 'Compiling'
  const time = seconds && (place || game.inAddAllReady) ? ` · ${roundSpan(seconds).span}` : ''
  return place ? `${place === 1 ? 'Next' : `#${place}`} in queue${time}` : `${game.statusText ?? ''}${time}`
}

function Poster({ game, cover, current, seconds, place, orderable, drop, error, onCompile, onKey }: {
  game: ShaderGame
  cover: string | null
  current: QueueItem | null
  /** How long it takes to compile here, for a game Compile would queue or has queued. */
  seconds: number | undefined
  place: number | undefined
  orderable: boolean
  drop: Drop
  error: string | null
  onCompile: () => void
  onKey: (event: KeyboardEvent<HTMLButtonElement>) => void
}) {
  const can = game.queued || game.actions?.compile === true
  const line = posterLine(game, current, place, seconds)
  const tone = current || place ? 'live' : game.tone ?? 'muted'
  return (
    <div className={`exo-shaders-poster is-${tone}${drop === 'lifted' ? ' is-lifted' : ''}`}>
      <button type="button" disabled={!can} onClick={onCompile} onKeyDown={onKey}
        aria-label={`${game.queued ? 'Remove from queue' : 'Compile'} ${game.name}`}
        aria-describedby={orderable ? 'exo-shaders-move-help' : undefined}>
        <span className="exo-shaders-art">
          {cover ? <img src={cover} alt="" loading="lazy" decoding="async" draggable={false} /> : <span className="exo-shaders-art-name">{game.name}</span>}
          {current && typeof current.percent === 'number'
            ? <span className="exo-shaders-art-fill" style={{ transform: `scaleY(${1 - Math.min(1, current.percent / 100)})` }} />
            : null}
        </span>
        <span className="exo-shaders-name">{game.name}</span>
        <span className="exo-shaders-state">{line}</span>
        {error ? <span className="exo-shaders-state is-bad">{error}</span> : null}
      </button>
    </div>
  )
}
