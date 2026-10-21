import type { CSSProperties } from 'react'
import { cn } from '../lib/utils'

/** Exo's mark in the app's chrome: the app icon's three bars (make-logo.py) in the ink. The intro shows the icon itself (BootGate). */
export function ExoMark({
  size = 28,
  className,
  title,
}: {
  size?: number
  className?: string
  title?: string
}) {
  return (
    <span
      className={cn('exo-mark', className)}
      style={{ width: size, height: size, '--exo-mark-size': `${size}px` } as CSSProperties}
      role={title ? 'img' : 'presentation'}
      aria-label={title}
      aria-hidden={title ? undefined : true}
    >
      <i className="exo-mark-bar exo-mark-bar-1" />
      <i className="exo-mark-bar exo-mark-bar-2" />
      <i className="exo-mark-bar exo-mark-bar-3" />
    </span>
  )
}
