import { memo, type CSSProperties } from 'react'
import { CoverArt } from '../CoverArt'
import { cn } from '../../lib/utils'
import type { FolderTile } from '../../lib/folders'
import type { Game } from '../../lib/host'
import type { GameMenuAt } from './GameMenu'

/**
 * A Library folder on the grid, the size of a game's card: a real folder in its color (a back with its tab, a front
 * flap with the count) holding its first three games' covers like papers. Hovering opens it a little: the flap tips
 * toward you and the covers rise and fan out. Pressing it opens the folder (FolderPanel) out of this card; right-click
 * is its menu.
 */
export const FolderCard = memo(function FolderCard({ tile, tabIndex, position, setSize, arriveIndex, onActiveGameChange, onSelect, onMenu }: {
  tile: FolderTile
  tabIndex?: number
  position?: number
  setSize?: number
  arriveIndex?: number
  onActiveGameChange?: (gameId: string) => void
  onSelect: (game: Game) => void
  onMenu?: (at: GameMenuAt) => void
}) {
  const { games } = tile.folder
  return (
    <article
      role={position != null ? 'listitem' : undefined}
      aria-posinset={position}
      aria-setsize={setSize}
      className={cn('exo-tile exo-folder-tile group relative w-full', arriveIndex != null && 'exo-motion-arrive')}
      data-color={tile.folder.color}
      style={arriveIndex != null ? { '--i': arriveIndex } as CSSProperties : undefined}
    >
      <button
        type="button"
        data-game-id={tile.id}
        tabIndex={tabIndex}
        onFocus={onActiveGameChange ? () => onActiveGameChange(tile.id) : undefined}
        onClick={(event) => {
          if (event.detail > 1) return
          onSelect(tile)
        }}
        onContextMenu={(event) => {
          event.preventDefault()
          if (!onMenu) return
          const box = event.currentTarget.getBoundingClientRect()
          const keyboard = event.clientX === 0 && event.clientY === 0
          onMenu({ game: tile, x: keyboard ? box.left + 12 : event.clientX, y: keyboard ? box.top + 12 : event.clientY, from: event.currentTarget })
        }}
        className="exo-tile-hit"
        aria-label={`${tile.title}, folder, ${games.length} ${games.length === 1 ? 'game' : 'games'}`}
        aria-haspopup="dialog"
      >
        <div className="exo-tile-frame exo-folder-frame" data-vt-art>
          <span className="exo-folder-art" aria-hidden="true">
            <span className="exo-folder-plate" />
            {games.slice(0, 3).map((game, index, papers) => (
              <span key={game.id} className="exo-folder-paper" style={{ '--n': index - (papers.length - 1) / 2 } as CSSProperties}>
                <CoverArt game={game} className="absolute inset-0 h-full w-full" />
              </span>
            ))}
            <span className="exo-folder-flap">
              <span className="exo-folder-count">{games.length}</span>
            </span>
          </span>
        </div>
        <div className="exo-card-meta">
          <div className="exo-card-title">{tile.title}</div>
        </div>
      </button>
    </article>
  )
})
