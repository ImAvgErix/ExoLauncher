import { useKeepShine } from '../../lib/shine'
import { cn } from '../../lib/utils'

/**
 * Content on its way (DESIGN.md › Waits): clear skeleton cards of its shape stand in its place and shine; no words, no
 * bar, no spinner. Rows by default (a list's: two lines of words, after a round picture with `avatar`), or `tiles`
 * square tiles of a grid. Screen readers hear `label` as a status.
 */
export function Skeleton({ label, rows = 3, avatar = false, tiles, className }: {
  label: string
  rows?: number
  avatar?: boolean
  /** A grid of this many square tiles instead of rows. */
  tiles?: number
  className?: string
}) {
  useKeepShine()
  return (
    <div className={cn('exo-skeleton-list', tiles ? 'is-tiles' : null, className)} role="status" aria-label={label}>
      {tiles
        ? Array.from({ length: tiles }, (_, i) => <span key={i} className="exo-skeleton exo-shine" aria-hidden="true" />)
        : Array.from({ length: rows }, (_, i) => (
          <span key={i} className="exo-skeleton-row" aria-hidden="true">
            {avatar ? <span className="exo-skeleton exo-shine is-round" /> : null}
            <span className="exo-skeleton-lines"><span className="exo-skeleton exo-shine" /><span className="exo-skeleton exo-shine" /></span>
          </span>
        ))}
    </div>
  )
}

/** A row's state still being read (Checking): a word-sized skeleton in the row's control column, shining. */
export function SkeletonWord({ label }: { label: string }) {
  return <span className="exo-skeleton exo-shine exo-skeleton-word" role="status" aria-label={label} />
}
