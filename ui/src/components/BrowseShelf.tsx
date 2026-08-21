import { useId, type RefObject } from 'react'
import type { Game } from '../lib/host'
import { cn } from '../lib/utils'
import { WindowedGameGrid } from './WindowedGameGrid'

export type BrowseTransfer = { percent: number | null }

export type BrowseShelfProps = {
  /** Games are already filtered and sorted in their final visual order. */
  games: Game[]
  selectedId: string | null
  activeGameId: string | null
  onActiveGameChange: (gameId: string) => void
  onSelect: (game: Game) => void
  onActivate?: (game: Game) => void
  transferFor?: (game: Game) => BrowseTransfer | null
  loading?: boolean
  heading: string
  emptyMessage: string
  isDisabled?: (game: Game) => boolean
  queuedIds?: string[]
  scrollRootRef: RefObject<HTMLElement | null>
  className?: string
}

export function BrowseShelf({
  games,
  selectedId,
  activeGameId,
  onActiveGameChange,
  onSelect,
  onActivate,
  transferFor,
  loading = false,
  heading,
  emptyMessage,
  isDisabled,
  queuedIds = [],
  scrollRootRef,
  className,
}: BrowseShelfProps) {
  const headingId = useId()

  return (
    <section className={cn('min-w-0', className)} aria-labelledby={headingId}>
      <header className="exo-shelf-head">
      <h2 className="exo-shelf-title" id={headingId}>{heading}</h2>
      </header>

      {games.length === 0 ? (
        <p className="exo-shelf-empty" role={loading ? 'status' : undefined}>
          {loading ? 'Searching stores…' : emptyMessage}
        </p>
      ) : (
        <WindowedGameGrid
          games={games}
          selectedId={selectedId}
          activeGameId={activeGameId}
          onActiveGameChange={onActiveGameChange}
          onSelect={onSelect}
          onActivate={onActivate}
          transferFor={transferFor}
          isDisabled={isDisabled}
          queuedIds={queuedIds}
          loading={loading}
        layoutKey={heading}
          scrollRootRef={scrollRootRef}
          labelledBy={headingId}
        />
      )}
    </section>
  )
}
