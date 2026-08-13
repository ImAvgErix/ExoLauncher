/**
 * Exo motion — short compositor tweens. No shared layoutId: that projection
 * stole clicks when opening and closing a game.
 */
import { AnimatePresence, motion, useReducedMotion } from 'motion/react'
import { useEffect, useRef, type ReactNode } from 'react'

const ease = [0.23, 1, 0.32, 1] as const

export function FadeIn({
  children,
  className,
  delay = 0,
}: {
  children: ReactNode
  className?: string
  delay?: number
}) {
  const reduce = useReducedMotion()
  if (reduce) return <div className={className}>{children}</div>
  return (
    <motion.div
      className={className}
      initial={{ opacity: 0, y: 8 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: 0.28, ease, delay }}
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
      initial={{ opacity: 0, y: -6 }}
      animate={{ opacity: 1, y: 0 }}
      exit={{ opacity: 0, y: -4 }}
      transition={{ duration: 0.22, ease }}
    >
      {children}
    </motion.div>
  )
}

export function GameOverlay({
  open,
  label,
  children,
  scrim,
  onExitComplete,
}: {
  open: boolean
  label: string
  children: ReactNode
  scrim: ReactNode
  onExitComplete?: () => void
}) {
  const reduce = useReducedMotion()
  const ref = useRef<HTMLDivElement>(null)
  const wasOpen = useRef(open)
  useEffect(() => {
    if (open) ref.current?.focus({ preventScroll: true })
  }, [open])
  useEffect(() => {
    if (wasOpen.current && !open && reduce) onExitComplete?.()
    wasOpen.current = open
  }, [open, reduce, onExitComplete])
  if (reduce) {
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
        {children}
      </div>
    ) : null
  }
  return (
    <AnimatePresence onExitComplete={onExitComplete}>
      {open && (
        <motion.div
          ref={ref}
          className="exo-game-overlay"
          role="dialog"
          aria-modal="true"
          aria-label={label}
          tabIndex={-1}
          initial={{ opacity: 0 }}
          animate={{ opacity: 1 }}
          exit={{ opacity: 0, pointerEvents: 'none' }}
          transition={{ duration: 0.16, ease }}
        >
          {scrim}
          {children}
        </motion.div>
      )}
    </AnimatePresence>
  )
}
