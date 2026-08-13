import { cn } from '../lib/utils'

/** Canonical Exo mark. Idle is the original E. Alive is three equal bars on a diagonal so WebView2 can wave them. */
export function ExoMark({
  size = 28,
  alive = false,
  className,
  title,
}: {
  size?: number
  alive?: boolean
  className?: string
  title?: string
}) {
  return (
    <span
      className={cn('exo-mark', alive && 'is-alive', className)}
      style={{ width: size, height: size }}
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
