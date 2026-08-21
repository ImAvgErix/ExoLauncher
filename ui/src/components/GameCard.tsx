import { resolvePrimaryAction, type Game } from '../lib/host'
import { cn, visibleInstallPercent } from '../lib/utils'
import { CoverArt } from './CoverArt'
import { type CSSProperties } from 'react'

export function GameCard({
  game,
  selected,
  onSelect,
  onActivate,
  preload = false,
  disabled = false,
  transfer = null,
  queued = false,
  tabIndex,
  onFocus,
  gridPosition,
}: {
  game: Game
  selected: boolean
  onSelect: () => void
  /** Double-click / Enter play-or-stop for this exact card. */
  onActivate?: () => void
  preload?: boolean
  disabled?: boolean
  transfer?: { percent: number | null } | null
  queued?: boolean
  tabIndex?: number
  onFocus?: () => void
  gridPosition?: { row: number; column: number }
}) {
  const installed = !!game.installed
  const primaryAction = resolvePrimaryAction(game)
  const hasUpdate =
    primaryAction === 'update' ||
    !!game.updateAvailable ||
    !!game.variants?.some((variant) => variant.updateAvailable)
  const isPlaying = !!(
    game.canStop ||
    game.isRunning ||
    game.variants?.some((variant) => variant.canStop || variant.isRunning)
  )
  const transferring = !!transfer
  const progressPercent = visibleInstallPercent(transfer?.percent)
  const canActivate = !!onActivate && (isPlaying || primaryAction === 'play' || primaryAction === 'install' || primaryAction === 'update')
  const titleClass = game.title.length > 62 ? 'is-very-long' : game.title.length > 40 ? 'is-long' : null

  return (
    <article
      role={gridPosition ? 'gridcell' : undefined}
      aria-colindex={gridPosition?.column}
      className={cn(
        'exo-tile group relative w-full',
        selected && 'is-selected',
        !installed && !transferring && 'is-dim',
        hasUpdate && !isPlaying && 'is-update',
      )}
    >
      <button
        type="button"
        data-game-id={game.id}
        data-controller-target=""
        data-controller-safe=""
        tabIndex={tabIndex}
        onFocus={onFocus}
        onClick={onSelect}
        onDoubleClick={() => {
          if (!disabled && canActivate) onActivate?.()
        }}
        disabled={disabled}
        className="exo-tile-hit"
        aria-label={
          transferring
            ? `${game.title} (downloading)`
            : isPlaying
            ? `${game.title} (playing)`
            : hasUpdate
              ? `${game.title} (update)`
              : game.title
        }
        aria-pressed={selected}
      >
        <div className="exo-tile-frame">
          <div className="exo-tile-media">
            <CoverArt game={game} preload={preload} className="absolute inset-0 h-full w-full" />
          </div>
          {transferring && (
            <span
              className={`exo-tile-progress${progressPercent == null ? ' is-unknown' : ''}`}
              style={progressPercent == null ? undefined : { '--progress': progressPercent / 100 } as CSSProperties}
              aria-hidden
            />
          )}
          {isPlaying && (
            <div className="absolute left-2 top-2 z-[5]">
              <span className="exo-badge is-good">Playing</span>
            </div>
          )}
          {!isPlaying && transferring && (
            <div className="absolute left-2 top-2 z-[5]">
              <span className="exo-badge is-update">
                {progressPercent == null ? 'Downloading' : `${Math.round(progressPercent)}%`}
              </span>
            </div>
          )}
          {!isPlaying && !transferring && hasUpdate && (
            <div className="absolute left-2 top-2 z-[5]">
              <span className="exo-badge is-update">Update</span>
            </div>
          )}
          {!isPlaying && !transferring && !hasUpdate && queued && (
            <div className="absolute left-2 top-2 z-[5]">
              <span className="exo-badge">Queued</span>
            </div>
          )}
        </div>
        <div className="exo-card-meta">
          <div className={cn('exo-card-title', titleClass)}>{game.title}</div>
        </div>
      </button>
    </article>
  )
}
