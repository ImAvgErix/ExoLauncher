/**
 * Exo OS baseline motion — short, compositor-friendly, no scale blur.
 */
import { AnimatePresence, motion, useReducedMotion } from 'motion/react'
import type { ReactNode } from 'react'
import { cn } from './lib/utils'

/** Match CSS --ease-out */
const ease = [0.23, 1, 0.32, 1] as const
const easeDrawer = [0.32, 0.72, 0, 1] as const
const easeSpring = { type: 'spring' as const, stiffness: 420, damping: 32, mass: 0.85 }

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
      transition={{ duration: 0.34, ease, delay }}
      style={{ willChange: 'transform, opacity' }}
    >
      {children}
    </motion.div>
  )
}

export function DetailRail({
  open,
  children,
}: {
  open: boolean
  children: ReactNode
}) {
  const reduce = useReducedMotion()
  // Soft rail — gradient seam + rounded cover (see DetailPanel / .exo-detail-rail).
  if (reduce) {
    return open ? (
      <div className="exo-detail-rail flex h-full w-full shrink-0 flex-col md:w-[292px]">
        {children}
      </div>
    ) : null
  }
  return (
    <AnimatePresence mode="wait" initial={false}>
      {open && (
        <motion.div
          key="detail"
          className="exo-detail-rail flex h-full w-full shrink-0 flex-col md:w-[292px]"
          initial={{ opacity: 0, x: 20, scale: 0.98 }}
          animate={{ opacity: 1, x: 0, scale: 1 }}
          exit={{ opacity: 0, x: 12, scale: 0.98 }}
          transition={{ duration: 0.34, ease: easeDrawer }}
          style={{ willChange: 'transform, opacity' }}
        >
          {children}
        </motion.div>
      )}
    </AnimatePresence>
  )
}

export function GridItem({
  children,
  index,
  className,
}: {
  children: ReactNode
  index: number
  className?: string
}) {
  const reduce = useReducedMotion()
  if (reduce) return <div className={className}>{children}</div>
  return (
    <motion.div
      className={cn(className)}
      initial={{ opacity: 0, y: 10 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{
        duration: 0.32,
        ease,
        delay: Math.min(index * 0.018, 0.16),
      }}
      style={{ willChange: 'transform, opacity' }}
    >
      {children}
    </motion.div>
  )
}

export function SoftPress({
  children,
  className,
}: {
  children: ReactNode
  className?: string
}) {
  const reduce = useReducedMotion()
  if (reduce) return <div className={className}>{children}</div>
  return (
    <motion.div
      className={className}
      whileHover={{ y: -2 }}
      whileTap={{ scale: 0.97 }}
      transition={{ duration: 0.18, ease }}
    >
      {children}
    </motion.div>
  )
}

/**
 * Card wrapper — hover lift only.
 * No shared layoutId: pin moves md↔lg and layout morph flashed covers.
 */
export function CardMotion({
  children,
  className,
}: {
  id?: string
  children: ReactNode
  className?: string
}) {
  const reduce = useReducedMotion()
  if (reduce) return <div className={className}>{children}</div>
  return (
    <motion.div
      className={className}
      whileHover={{ y: -3 }}
      transition={easeSpring}
      style={{ willChange: 'transform' }}
    >
      {children}
    </motion.div>
  )
}

/** Pin / CTA micro-interaction */
export function TapScale({
  children,
  className,
}: {
  children: ReactNode
  className?: string
}) {
  const reduce = useReducedMotion()
  if (reduce) return <div className={className}>{children}</div>
  return (
    <motion.div
      className={className}
      whileTap={{ scale: 0.9 }}
      transition={{ duration: 0.14, ease }}
    >
      {children}
    </motion.div>
  )
}
