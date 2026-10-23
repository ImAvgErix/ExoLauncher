import { memo, useEffect, useRef, useState, type CSSProperties } from 'react'
import { CoverArt } from '../CoverArt'
import { cn } from '../../lib/utils'
import { useLens } from '../../lib/glass'
import type { FolderTile } from '../../lib/folders'
import type { Game } from '../../lib/host'
import type { GameMenuAt } from './GameMenu'

/**
 * A Library folder on the grid, exactly a game's cover in size: its back is its color, its first three games' covers
 * (in the folder's order or its sort) stand in it like papers, and its front is Exo's Liquid Glass tinted with its
 * color, carrying its name and how many games it holds. A folder with its own picture shows that instead of its back
 * and papers, under a shorter front. Hovering opens it a little: the front tips toward you and the covers rise and fan
 * out. Pressing it opens the folder (FolderPanel) out of this card, which stays empty until it closes back into it and
 * the front swings shut; right-click is its menu.
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
  const { games, art } = tile.folder
  const flap = useRef<HTMLSpanElement>(null)
  const frame = useRef<HTMLDivElement>(null)
  useLens(flap)
  const fresh = useNewPapers(games.slice(0, 3).map(game => game.id))
  // The folder leans toward the pointer and its glass catches a light where the pointer is: CSS variables set
  // straight on the frame, once a frame, so moving never re-renders the card.
  const lean = useRef(0)
  const follow = (event: React.PointerEvent) => {
    const el = frame.current
    if (!el || event.pointerType !== 'mouse') return
    const box = el.getBoundingClientRect()
    const x = Math.min(1, Math.max(0, (event.clientX - box.left) / box.width))
    const y = Math.min(1, Math.max(0, (event.clientY - box.top) / box.height))
    cancelAnimationFrame(lean.current)
    lean.current = requestAnimationFrame(() => {
      el.style.setProperty('--lean-x', `${((x - 0.5) * 10).toFixed(2)}deg`)
      el.style.setProperty('--lean-y', `${(-(y - 0.5) * 8).toFixed(2)}deg`)
      el.style.setProperty('--lx', `${(x * 100).toFixed(1)}%`)
      // The front covers the lower 58% of the folder (34% under a picture).
      const front = art ? 0.34 : 0.58
      el.style.setProperty('--ly', `${(((y - 1 + front) / front) * 100).toFixed(1)}%`)
    })
  }
  const settle = () => {
    cancelAnimationFrame(lean.current)
    for (const name of ['--lean-x', '--lean-y']) frame.current?.style.removeProperty(name)
  }
  return (
    <article
      role={position != null ? 'listitem' : undefined}
      aria-posinset={position}
      aria-setsize={setSize}
      className={cn('exo-tile exo-folder-tile group relative w-full', arriveIndex != null && 'exo-motion-arrive')}
      data-color={tile.folder.color}
      data-art={art ? '' : undefined}
      style={arriveIndex != null ? { '--i': arriveIndex } as CSSProperties : undefined}
      onPointerMove={follow}
      onPointerLeave={settle}
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
        <div ref={frame} className="exo-tile-frame exo-folder-frame" data-vt-art>
          <span className="exo-folder-art" aria-hidden="true">
            {art ? <img className="exo-folder-picture" src={art} alt="" draggable={false} decoding="async" /> : <>
              <span className="exo-folder-plate" />
              {games.slice(0, 3).map((game, index, papers) => (
                <span key={game.id} className={cn('exo-folder-paper', fresh.has(game.id) && 'is-new')} style={{ '--n': index - (papers.length - 1) / 2 } as CSSProperties}>
                  <CoverArt game={game} className="absolute inset-0 h-full w-full" />
                </span>
              ))}
            </>}
            <span ref={flap} className="exo-glass exo-folder-flap">
              <span className="exo-folder-light" />
              <span className="exo-folder-label">{tile.title}</span>
              <span className="exo-folder-count">{games.length} {games.length === 1 ? 'game' : 'games'}</span>
            </span>
          </span>
        </div>
        {/* The name is on the folder; the line under it stays, so the row's cards end level. */}
        <div className="exo-card-meta" aria-hidden="true" />
      </button>
    </article>
  )
})

/**
 * The covers that just arrived in the folder (a game put in), for their drop-in; none on the folder's first showing.
 * Each is new for the length of its animation.
 */
function useNewPapers(ids: string[]) {
  const seen = useRef<Set<string> | null>(null)
  const [fresh, setFresh] = useState<ReadonlySet<string>>(() => new Set())
  const key = ids.join('|')
  useEffect(() => {
    const now = key ? key.split('|') : []
    const before = seen.current
    seen.current = new Set(now)
    if (!before) return
    const added = now.filter(id => !before.has(id))
    if (!added.length) return
    setFresh(new Set(added))
    const timer = window.setTimeout(() => setFresh(new Set()), 760)
    return () => window.clearTimeout(timer)
  }, [key])
  return fresh
}
