import { memo, useId, type MouseEvent, type ReactNode, type RefObject } from 'react'
import type { Game, InstallProgress } from '../lib/host'
import { WindowedGameGrid } from './WindowedGameGrid'
import type { GameMenuAt } from './shell/GameMenu'

export type BrowseShelfProps = {
  /** Games are already filtered and sorted in their final visual order. */
  games: Game[]
  selectedId: string | null
  activeGameId: string | null
  onActiveGameChange: (gameId: string) => void
  onSelect: (game: Game, event?: MouseEvent) => void
  onMenu?: (at: GameMenuAt) => void
  onMoveFolder?: (folderId: string, toId: string, after: boolean) => void
  transferFor?: (game: Game) => InstallProgress | null
  heading: string
  /** The count beside the heading, when it isn't the grid's cards (a folder is one card of several games). */
  count?: number
  actions?: ReactNode
  queuedIds?: string[]
  /** Select mode: the picked games' ids; null outside it. */
  pickedIds?: ReadonlySet<string> | null
  scrollRootRef: RefObject<HTMLElement | null>
  cardWidth: number | null
}

export const BrowseShelf = memo(function BrowseShelf({
  games,
  selectedId,
  activeGameId,
  onActiveGameChange,
  onSelect,
  onMenu,
  onMoveFolder,
  transferFor,
  heading,
  count,
  actions,
  queuedIds = [],
  pickedIds,
  scrollRootRef,
  cardWidth,
}: BrowseShelfProps) {
  const headingId = useId()

  return (
    <section className="min-w-0" aria-labelledby={headingId}>
      <header className="exo-shelf-head">
        <h2 className="exo-shelf-title" id={headingId}>{heading}{games.length > 0 && <span className="exo-shelf-count" aria-hidden>{count ?? games.length}</span>}</h2>
        {actions ? <div className="exo-shelf-actions">{actions}</div> : null}
      </header>

      {games.length === 0 ? null : (
        <WindowedGameGrid
          games={games}
          selectedId={selectedId}
          activeGameId={activeGameId}
          onActiveGameChange={onActiveGameChange}
          onSelect={onSelect}
          onMenu={onMenu}
          onMoveFolder={onMoveFolder}
          transferFor={transferFor}
          queuedIds={queuedIds}
          pickedIds={pickedIds}
          layoutKey={heading}
          scrollRootRef={scrollRootRef}
          labelledBy={headingId}
          cardWidth={cardWidth}
        />
      )}
    </section>
  )
})
