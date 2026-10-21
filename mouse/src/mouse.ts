import { useCallback, useEffect, useRef, useState } from 'react'
import type { SupportedClient } from '@openmouse/protocol/drivers/registry'

/** Every brand's driver: about 1.3 MB, loaded after the page has drawn, once. */
const registry = () => import('@openmouse/protocol/drivers/registry')
import type { MouseStatus } from '@openmouse/protocol/drivers/mouse-types'
import { exoHid, onHostEvent } from './exo-hid'

/** One connectable device: the interface its best driver wants. */
export type Candidate = { id: string; device: HIDDevice; name: string }

type Phase = 'looking' | 'none' | 'opening' | 'ready' | 'error'

/** One setter call: the client method and its arguments. */
export type Step = [method: string, args: unknown[]]

export type MouseState = {
  phase: Phase
  candidates: Candidate[]
  current: Candidate | null
  brand: string
  status: MouseStatus | null
  /** The last change that did not take, in a sentence. */
  error: string | null
  busy: boolean
  /** When the mouse last answered a read (ms since epoch); 0 before the first. */
  readAt: number
}

/** A client method by name, when this mouse's driver has it. */
export function can(client: SupportedClient | null, method: string): boolean {
  return !!client && typeof (client as unknown as Record<string, unknown>)[method] === 'function'
}

function invoke(client: SupportedClient, method: string, args: unknown[]): Promise<unknown> {
  const fn = (client as unknown as Record<string, (...a: unknown[]) => Promise<unknown>>)[method]
  return fn.apply(client, args)
}

/** The best interface per physical product: receivers and mice expose several, one of which answers. */
async function findCandidates(): Promise<Candidate[]> {
  const [devices, drivers] = await Promise.all([exoHid().getDevices(), registry()])
  const best = new Map<string, { device: HIDDevice; score: number }>()
  for (const device of devices) {
    const score = drivers.clientSupportScore(device)
    if (score <= 0) continue
    const id = `${device.vendorId.toString(16)}:${device.productId.toString(16)}`
    const known = best.get(id)
    if (!known || score > known.score) best.set(id, { device, score })
  }
  return [...best.entries()]
    .sort((a, b) => b[1].score - a[1].score)
    .map(([id, { device }]) => ({ id, device, name: device.productName || 'Mouse' }))
}

const REFRESH_VISIBLE_MS = 4_000
/**
 * Behind the room (Exo in the background, a game in front) the mouse is read
 * only for its battery, for Exo's title bar, as often as OpenMouse Bridge
 * reads it: every five minutes, and never for a mouse with no battery.
 */
const REFRESH_HIDDEN_MS = 5 * 60_000
const SETTLE_MS = 250
const RETRY_VISIBLE_MS = 5_000
/** Setters after which the mouse is no longer talking to this PC (Logitech Easy-Switch). */
const LEAVES_THIS_PC = new Set(['requestHostSwitch'])
const CONFIRM_MS = 800

const sleep = (ms: number) => new Promise<void>(resolve => window.setTimeout(resolve, ms))

/**
 * The connected mouse: found, opened and read, then read again every few
 * seconds while the room is on screen (and only for its battery behind it).
 * Every change goes through `apply`, which reads the mouse back so what
 * shows is what the mouse took.
 */
export function useMouse() {
  const [state, setState] = useState<MouseState>({ phase: 'looking', candidates: [], current: null, brand: '', status: null, error: null, busy: false, readAt: 0 })
  const client = useRef<SupportedClient | null>(null)
  const writing = useRef(0)
  // Hidden until the room says otherwise: Exo starts this frame behind a closed room, and
  // its first visibility message can land before this listener exists.
  const [visible, setVisible] = useState(false)
  const [wanted, setWanted] = useState<string | null>(null)
  const [scan, setScan] = useState(0)
  /** One connect at a time: a rescan waits for the attempt before it, so two never share the device. */
  const chain = useRef<Promise<void>>(Promise.resolve())

  // The host says when the room is on screen; arrivals and departures rescan.
  useEffect(() => {
    onHostEvent('room.visible', data => setVisible(data === true))
    const rescan = () => setScan(n => n + 1)
    exoHid().addEventListener('connect', rescan)
    exoHid().addEventListener('disconnect', rescan)
    return () => {
      exoHid().removeEventListener('connect', rescan)
      exoHid().removeEventListener('disconnect', rescan)
    }
  }, [])

  useEffect(() => {
    let live = true
    chain.current = chain.current.then(async () => {
      if (!live) return
      let candidates: Candidate[]
      try {
        candidates = await findCandidates()
      } catch (e) {
        // A listing that failed (Exo slow to answer) says nothing about the open mouse: it stays, and the list is asked again.
        if (client.current?.device.opened) { window.setTimeout(() => setScan(n => n + 1), RETRY_VISIBLE_MS); return }
        if (live) setState(s => ({ ...s, phase: 'error', error: message(e) }))
        return
      }
      if (!live) return
      const pick = candidates.find(c => c.id === wanted) ?? candidates[0] ?? null
      const same = client.current && pick && client.current.device === pick.device && client.current.device.opened
      if (same) { setState(s => ({ ...s, candidates })); return }
      if (client.current) {
        const old = client.current
        client.current = null
        await old.close().catch(() => undefined)
      }
      if (!pick) { setState({ phase: 'none', candidates, current: null, brand: '', status: null, error: null, busy: false, readAt: 0 }); return }
      setState({ phase: 'opening', candidates, current: pick, brand: '', status: null, error: null, busy: false, readAt: 0 })
      const drivers = await registry()
      const next = drivers.createSupportedClient(pick.device)
      if (!next) { setState(s => ({ ...s, phase: 'error', error: 'No driver answers for this mouse.' })); return }
      try {
        if (can(next, 'open')) await invoke(next, 'open', [])
        const status = await next.readStatus()
        if (!live) { await next.close().catch(() => undefined); return }
        client.current = next
        setState({ phase: 'ready', candidates, current: pick, brand: drivers.deviceBrand(next), status, error: null, busy: false, readAt: Date.now() })
      } catch (e) {
        await next.close().catch(() => undefined)
        if (live) setState(s => ({ ...s, phase: 'error', error: message(e) }))
      }
    }).catch(() => undefined)
    return () => { live = false }
  }, [scan, wanted])

  const refresh = useCallback(async () => {
    const c = client.current
    if (!c || writing.current > 0) return
    try {
      const status = await c.readStatus()
      if (client.current === c && writing.current === 0) setState(s => ({ ...s, status, readAt: Date.now() }))
    } catch {
      // A wireless mouse asleep or out of range: the last status stays.
    }
  }, [])

  // A mouse Exo could not reach (asleep, out of range, or Exo still starting) is tried
  // again by itself: at once when the room opens, every few seconds while it is on
  // screen, and at the battery cadence behind it.
  useEffect(() => {
    if (state.phase !== 'error') return
    const timer = window.setTimeout(() => setScan(n => n + 1), visible ? RETRY_VISIBLE_MS : REFRESH_HIDDEN_MS)
    return () => window.clearTimeout(timer)
  }, [state.phase, visible])
  const phase = useRef(state.phase)
  phase.current = state.phase
  useEffect(() => { if (visible && phase.current === 'error') setScan(n => n + 1) }, [visible])

  const hasBattery = state.status?.batteryPercent != null
  useEffect(() => {
    if (state.phase !== 'ready' || (!visible && !hasBattery)) return
    // A status read is a burst of requests to the mouse: never faster than its driver asks for.
    const own = (client.current as unknown as { pollIntervalMs?: number } | null)?.pollIntervalMs ?? 0
    const timer = window.setInterval(() => void refresh(), visible ? Math.max(REFRESH_VISIBLE_MS, own) : REFRESH_HIDDEN_MS)
    if (visible) void refresh()
    return () => window.clearInterval(timer)
  }, [state.phase, visible, hasBattery, refresh])

  /**
   * Runs setters in order (a failing one does not stop the rest), then reads the mouse back
   * once; resolves to what the mouse took, or null with the first failure as the error.
   */
  const applyAll = useCallback(async (steps: Step[]): Promise<MouseStatus | null> => {
    const c = client.current
    if (!c) return null
    writing.current++
    setState(s => ({ ...s, busy: true, error: null }))
    try {
      let failed: unknown = null
      for (const [method, args] of steps) {
        if (!can(c, method)) continue
        try { await invoke(c, method, args) } catch (e) { failed ??= e }
      }
      if (steps.some(([method]) => LEAVES_THIS_PC.has(method))) {
        if (failed) throw failed
        return null
      }
      // The mouse takes a change at once but answers a read sent straight after it with
      // the old values; a short settle reads what it took, and a second read confirms it.
      await sleep(SETTLE_MS)
      const status = await c.readStatus()
      if (client.current === c) setState(s => ({ ...s, status, readAt: Date.now() }))
      window.setTimeout(() => void refresh(), CONFIRM_MS)
      if (failed) throw failed
      return status
    } catch (e) {
      setState(s => ({ ...s, error: message(e) }))
      return null
    } finally {
      writing.current--
      setState(s => ({ ...s, busy: writing.current > 0 }))
    }
  }, [refresh])

  /** One setter, then the mouse read back. */
  const apply = useCallback((method: string, ...args: unknown[]) => applyAll([[method, args]]), [applyAll])

  return { ...state, client: client.current, apply, applyAll, choose: setWanted, retry: () => setScan(n => n + 1) }
}

function message(e: unknown): string {
  const text = e instanceof Error ? e.message : String(e)
  return text.replace(/\s+/g, ' ').trim() || 'The mouse did not answer.'
}
