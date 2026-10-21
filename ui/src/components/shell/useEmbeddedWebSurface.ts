import { useCallback, useLayoutEffect, useRef } from 'react'
import { cutouts, onCutouts, type Cutout } from '../../lib/hostedCutouts'
import { report } from '../../lib/report'

/** The room's rect, and Exo's open menus over it (the host cuts them out of the hosted window). */
export type WebSurfaceBounds = { left: number; top: number; width: number; height: number; cutouts: Cutout[] }
type Options<T> = {
  active: boolean
  identity: string
  attempt: number
  send: (bounds: WebSurfaceBounds) => Promise<T>
  hide: () => Promise<unknown>
  onStatus: (status: T) => void
  failure: () => T
}

/** One native layout request in flight, with the latest size retained during loading. */
export function useEmbeddedWebSurface<T extends { ok: boolean }>(options: Options<T>) {
  const surface = useRef<HTMLDivElement>(null)
  const current = useRef(options)
  current.current = options
  const statusEpoch = useRef(0)
  const publishStatus = useCallback((status: T) => {
    statusEpoch.current++
    current.current.onStatus(status)
  }, [])

  const { active, identity, attempt } = options
  useLayoutEffect(() => {
    const hide = current.current.hide
    if (!active) { void hide().catch(() => {}); return }
    const node = surface.current
    if (!node) return
    let live = true, sending = false, frame = 0, lastBounds = ''
    let pending: WebSurfaceBounds | null = null
    async function flush() {
      if (sending) return
      sending = true
      try {
        while (live && pending) {
          const bounds = pending
          pending = null
          const epoch = statusEpoch.current
          try {
            const result = await current.current.send(bounds)
            if (!live || pending || epoch !== statusEpoch.current) continue
            if (!result.ok) lastBounds = ''
            current.current.onStatus(result)
          } catch (error) {
            report(error, `${identity}.setBounds`, { level: 'warn' })
            lastBounds = ''
            if (live && !pending && epoch === statusEpoch.current) current.current.onStatus(current.current.failure())
          }
        }
      } finally { sending = false }
    }
    function measure(force = false) {
        if (!live || document.hidden) return
        const rect = node!.getBoundingClientRect()
        if (rect.width < 1 || rect.height < 1) return
        const bounds = { left: rect.left, top: rect.top, width: rect.width, height: rect.height, cutouts: cutouts() }
        const key = JSON.stringify(bounds)
        if (!force && key === lastBounds) return
        lastBounds = key
        pending = bounds
        void flush()
    }
    function publish(force = false) {
      cancelAnimationFrame(frame)
      frame = requestAnimationFrame(() => measure(force))
    }
    const resize = () => publish()
    const resume = () => publish(true)
    // Send the first valid room rectangle before paint instead of waiting an
    // extra animation frame. Subsequent resize bursts still coalesce.
    measure()
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(resize)
    observer?.observe(node)
    const offCutouts = onCutouts(resize)
    window.addEventListener('resize', resize)
    window.addEventListener('online', resume)
    window.addEventListener('focus', resume)
    document.addEventListener('visibilitychange', resume)
    return () => {
      live = false
      pending = null
      cancelAnimationFrame(frame)
      observer?.disconnect()
      offCutouts()
      window.removeEventListener('resize', resize)
      window.removeEventListener('online', resume)
      window.removeEventListener('focus', resume)
      document.removeEventListener('visibilitychange', resume)
      void hide().catch(() => {})
    }
  }, [active, identity, attempt])
  return { surface, publishStatus }
}
