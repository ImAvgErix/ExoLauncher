/**
 * Exo motion — short compositor tweens. No shared layoutId: that projection
 * stole clicks when opening and closing a game.
 */
import { AnimatePresence, motion, useReducedMotion } from 'motion/react'
import { useEffect, useRef, type ReactNode } from 'react'

const ease = [0.23, 1, 0.32, 1] as const
const focusableSelector = [
  'button:not(:disabled):not([tabindex="-1"])',
  'a[href]:not([tabindex="-1"])',
  'input:not(:disabled):not([tabindex="-1"])',
  'select:not(:disabled):not([tabindex="-1"])',
  'textarea:not(:disabled):not([tabindex="-1"])',
  '[tabindex]:not([tabindex="-1"])',
].join(', ')

export function FadeIn({
  children,
  className,
  delay = 0,
  y = 8,
}: {
  children: ReactNode
  className?: string
  delay?: number
  y?: number
}) {
  const reduce = useReducedMotion()
  if (reduce) return <div className={className}>{children}</div>
  return (
    <motion.div
      className={className}
      initial={{ opacity: 0, transform: `translateY(${y}px)` }}
      animate={{ opacity: 1, transform: 'translateY(0)' }}
      transition={{ duration: 0.2, ease, delay }}
    >
      {children}
    </motion.div>
  )
}

export function GridItem({
  children,
  className,
}: {
  children: ReactNode
  index: number
  className?: string
}) {
  return <div className={className}>{children}</div>
}

export function BannerIn({
  children,
  className,
  role,
}: {
  children: ReactNode
  className?: string
  role?: string
}) {
  const reduce = useReducedMotion()
  if (reduce) return <div className={className} role={role}>{children}</div>
  return (
    <motion.div
      className={className}
      role={role}
      initial={{ opacity: 0, transform: 'translateY(-6px)' }}
      animate={{ opacity: 1, transform: 'translateY(0)' }}
      exit={{ opacity: 0, transform: 'translateY(-4px)' }}
      transition={{ duration: 0.22, ease }}
    >
      {children}
    </motion.div>
  )
}

export function GameOverlay({
  open,
  instant = false,
  label,
  children,
  scrim,
  onExitComplete,
}: {
  open: boolean
  instant?: boolean
  label: string
  children: ReactNode
  scrim: ReactNode
  onExitComplete?: () => void
}) {
  const reduce = useReducedMotion()
  const ref = useRef<HTMLDivElement>(null)
  const wasOpen = useRef(open)
  const previousFocus = useRef<HTMLElement | null>(null)
  useEffect(() => {
    if (!open) {
      if (wasOpen.current) previousFocus.current?.focus({ preventScroll: true })
      return
    }
    if (!wasOpen.current) {
      previousFocus.current = document.activeElement instanceof HTMLElement ? document.activeElement : null
    }
    const frame = window.requestAnimationFrame(() => {
      const stage = ref.current?.querySelector<HTMLElement>('.exo-game-overlay-stage')
      const first = stage?.querySelector<HTMLElement>(focusableSelector)
      ;(first ?? ref.current)?.focus({ preventScroll: true })
    })
    const trapFocus = (event: KeyboardEvent) => {
      if (event.key !== 'Tab' || !ref.current) return
      const focusable = Array.from(
        ref.current.querySelectorAll<HTMLElement>(focusableSelector),
      ).filter((item) => !item.hidden && item.getAttribute('aria-hidden') !== 'true')
      if (focusable.length === 0) {
        event.preventDefault()
        ref.current.focus({ preventScroll: true })
        return
      }
      const first = focusable[0]
      const last = focusable[focusable.length - 1]
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault()
        last.focus()
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault()
        first.focus()
      }
    }
    document.addEventListener('keydown', trapFocus)
    return () => {
      window.cancelAnimationFrame(frame)
      document.removeEventListener('keydown', trapFocus)
    }
  }, [open])
  useEffect(() => {
    if (wasOpen.current && !open && (reduce || instant)) onExitComplete?.()
    wasOpen.current = open
  }, [open, reduce, instant, onExitComplete])
  if (reduce || instant) {
    return open ? (
      <div
        ref={ref}
        className="exo-game-overlay"
        role="dialog"
        aria-modal="true"
        aria-label={label}
        tabIndex={-1}
      >
        {scrim}
        <div className="exo-game-overlay-stage">{children}</div>
      </div>
    ) : null
  }
  return (
    <AnimatePresence onExitComplete={onExitComplete}>
      {open && (
        // The scrim carries backdrop-filter. Fading its ancestor made the
        // compositor recompute a full-window blur on every frame, which is what
        // made opening a card feel late. Only the card animates now; the blur is
        // rasterized once and left alone.
        <div
          ref={ref}
          className="exo-game-overlay"
          role="dialog"
          aria-modal="true"
          aria-label={label}
          tabIndex={-1}
        >
          {scrim}
          <motion.div
            className="exo-game-overlay-stage"
            initial={{ opacity: 0, transform: 'scale(0.96)' }}
            animate={{ opacity: 1, transform: 'scale(1)' }}
            exit={{ opacity: 0, transform: 'scale(0.97)' }}
            transition={{ duration: 0.16, ease }}
          >
            {children}
          </motion.div>
        </div>
      )}
    </AnimatePresence>
  )
}
