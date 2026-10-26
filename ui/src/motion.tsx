/** Small opacity transitions keep game details at their final size from the first frame. */
import { useEffect, useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { focusableSelector, trapTab } from './lib/focusTrap'
import { useDismiss } from './lib/dismiss'

/** Longest the closing fade may hold the sheet when no transitionend arrives. */
const EXIT_FALLBACK_MS = 150

export function GameOverlay({ open, instant = false, keep = false, label, children, scrim }: {
  open: boolean
  instant?: boolean
  /** Mounted from the start and kept while closed (hidden), so it opens already loaded. */
  keep?: boolean
  label: string
  children: ReactNode
  scrim: ReactNode
}) {
  const ref = useRef<HTMLDivElement>(null)
  const stage = useRef<HTMLDivElement>(null)
  const presence = useRef<HTMLDivElement>(null)
  const wasOpen = useRef(false)
  const previousFocus = useRef<HTMLElement | null>(null)
  // Mounted while open and through the closing fade. It comes in under the
  // page's own entrance and CSS fades it out (.is-closing); an instant sheet skips that.
  const [present, setPresent] = useState(open)
  if (open && !present) setPresent(true)
  if (!open && present && instant) setPresent(false)
  const closing = present && !open
  // A press outside the page does what the scrim does (Escape too, below): a locked sheet's scrim is disabled.
  useDismiss(stage, () => ref.current?.querySelector<HTMLButtonElement>(':scope > .exo-game-overlay-scrim')?.click(), { modal: true, on: open })

  useEffect(() => {
    if (!open) {
      if (wasOpen.current) {
        previousFocus.current?.focus({ preventScroll: true })
        previousFocus.current = null
      }
      return
    }
    if (!wasOpen.current) previousFocus.current = document.activeElement instanceof HTMLElement ? document.activeElement : null
    // Escape does what the scrim does, so every sheet closes the same way and a
    // locked sheet (disabled scrim) stays. Anything inside that handled Escape
    // first (a dialog over it, a menu) or focus in another layer keeps it open.
    const closeOnEscape = (event: KeyboardEvent) => {
      const overlay = ref.current
      if (event.defaultPrevented || event.isComposing || !overlay || overlay.getClientRects().length === 0) return
      const focus = document.activeElement
      if (focus && focus !== document.body && !overlay.contains(focus)) return
      const scrim = overlay.querySelector<HTMLButtonElement>(':scope > .exo-game-overlay-scrim')
      if (!scrim || scrim.disabled) return
      event.preventDefault()
      scrim.click()
    }
    const frame = window.requestAnimationFrame(() => {
      // A page can name where focus starts (data-autofocus); else the primary action, else the first control.
      const first = ref.current?.querySelector<HTMLElement>('[data-autofocus]')
        ?? ref.current?.querySelector<HTMLElement>('.exo-primary-action:not(:disabled)')
        ?? ref.current?.querySelector<HTMLElement>(focusableSelector)
      ;(first ?? ref.current)?.focus({ preventScroll: true })
    })
    const trapFocus = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        closeOnEscape(event)
        return
      }
      if (ref.current) trapTab(ref.current, event)
    }
    document.addEventListener('keydown', trapFocus)
    return () => {
      window.cancelAnimationFrame(frame)
      document.removeEventListener('keydown', trapFocus)
    }
  }, [open])

  useEffect(() => {
    wasOpen.current = open
  }, [open])

  useEffect(() => {
    const node = presence.current
    if (!closing || !node) return
    const finish = () => setPresent(false)
    const onEnd = (event: TransitionEvent) => {
      if (event.target === node && event.propertyName === 'opacity') finish()
    }
    node.addEventListener('transitionend', onEnd)
    const timer = window.setTimeout(finish, EXIT_FALLBACK_MS)
    return () => {
      node.removeEventListener('transitionend', onEnd)
      window.clearTimeout(timer)
    }
  }, [closing])

  if (!present && !keep) return null
  return createPortal(
    <div ref={presence} hidden={!present} inert={!present} className={`exo-game-overlay-presence${closing ? ' is-closing' : ''}${instant ? ' is-instant' : ''}`}>
      <div ref={ref} className="exo-game-overlay" role="dialog" aria-modal="true" aria-label={label} tabIndex={-1}>
        {scrim}
        <div ref={stage} className="exo-game-overlay-stage">{children}</div>
      </div>
    </div>,
    document.body,
  )
}
