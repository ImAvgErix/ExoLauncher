import { Star, StarFilled } from '../brand/icons'
import { resolvePrimaryAction, type Game } from '../lib/host'
import { cn, storeLabel, visibleInstallPercent } from '../lib/utils'
import { CoverArt } from './CoverArt'
import type { CSSProperties } from 'react'

export function GameCard({
  game,
  selected,
  onSelect,
  onActivate,
  onToggleFavorite,
  hidePin,
  disabled = false,
  transfer = null,
}: {
  game: Game
  selected: boolean
  onSelect: () => void
  /** Double-click / Enter play-or-stop for this exact card. */
  onActivate?: () => void
  onToggleFavorite?: () => void
  hidePin?: boolean
  disabled?: boolean
  transfer?: { percent: number | null } | null
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
  const stores = Array.from(
    new Set(
      (game.stores?.length ? game.stores : [game.store])
        .map((store) => store.trim().toLowerCase())
        .filter(Boolean),
    ),
  )
  const canActivate = !!onActivate && (isPlaying || primaryAction === 'play' || primaryAction === 'install' || primaryAction === 'update')
  return (
    <article
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
        onMouseDown={(event) => {
          if (event.button === 0) event.preventDefault()
        }}
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
            <div className="absolute inset-0 overflow-hidden">
              <CoverArt game={game} className="absolute inset-0 h-full w-full" />
            </div>
            <span className="exo-tile-shine" aria-hidden />
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
        </div>
        <div className="exo-card-meta">
          <div className="exo-card-title">{game.title}</div>
          <div className="exo-card-store" aria-label={stores.map(storeLabel).join(', ')}>
            {[
              ...stores.map(storeLabel),
              transferring
                ? progressPercent == null ? 'Downloading' : `${Math.round(progressPercent)}%`
                : !isPlaying && hasUpdate ? 'Update' : null,
              !transferring && !isPlaying && !hasUpdate && primaryAction === 'install'
                ? game.store === 'local' ? 'Install' : 'Download'
                : null,
            ].filter(Boolean).join(' · ')}
          </div>
        </div>
      </button>
      {installed && onToggleFavorite && !hidePin && (
        <button
          type="button"
          className={cn('exo-tile-pin', game.isFavorite && 'is-on')}
          title={game.isFavorite ? 'Unpin' : 'Pin'}
          aria-label={game.isFavorite ? `Unpin ${game.title}` : `Pin ${game.title}`}
          aria-pressed={game.isFavorite}
          onClick={(e) => {
            e.stopPropagation()
            onToggleFavorite()
          }}
        >
          {game.isFavorite ? <StarFilled size={12} /> : <Star size={12} />}
        </button>
      )}
    </article>
  )
}
