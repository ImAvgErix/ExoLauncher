import { Star } from 'lucide-react'
import { resolvePrimaryAction, type Game } from '../lib/host'
import { CardMotion } from '../motion'
import { cn, storeDotColor, storeLabel } from '../lib/utils'
import { CoverArt, coverBg } from './CoverArt'

export { CoverArt, coverBg }

export function GameCard({
  game,
  selected,
  onSelect,
  onToggleFavorite,
  size = 'md',
  hidePin,
  disabled = false,
}: {
  game: Game
  selected: boolean
  onSelect: () => void
  onToggleFavorite?: () => void
  size?: 'md' | 'lg'
  hidePin?: boolean
  disabled?: boolean
}) {
  const installed = !!game.installed
  const primaryAction = resolvePrimaryAction(game)
  return (
    <CardMotion
      className={cn('group relative w-full text-left', size === 'lg' && 'w-[172px] shrink-0')}
    >
      <button
        type="button"
        data-game-id={game.id}
        onClick={onSelect}
        disabled={disabled}
        className="w-full rounded-xl text-left focus-visible:outline-2 focus-visible:outline-fg focus-visible:outline-offset-4"
        aria-label={game.title}
        aria-pressed={selected}
      >
        <div
          className={cn(
            'exo-cover relative aspect-[2/3]',
            selected && 'is-selected',
            !installed && 'is-not-installed',
          )}
          style={{ background: coverBg(game) }}
        >
          <CoverArt game={game} className="absolute inset-0 h-full w-full" />
          <div className="pointer-events-none absolute inset-x-0 bottom-0 z-[2] h-20 bg-gradient-to-t from-black/85 via-black/25 to-transparent" />
          {/* Badges are for things needing attention. Pinned is already obvious
              from the row the card sits in. */}
          <div className="absolute left-2 top-2 z-[2] flex gap-1">
            {primaryAction === 'update' && <span className="exo-badge is-warn">Update</span>}
            {primaryAction === 'install' && <span className="exo-badge">Install</span>}
          </div>
        </div>
        <div className={cn('mt-2.5 px-0.5', !installed && 'opacity-75')}>
          <div className="truncate text-[13px] font-medium tracking-tight text-fg outline-none [text-shadow:none]">
            {game.title}
          </div>
          <div className="mt-0.5 flex min-w-0 items-center gap-1.5 text-[11px] text-faint">
            <span className="h-1 w-1 rounded-full" style={{ background: storeDotColor(game.store) }} />
            <span className="truncate">{storeLabel(game.store)}</span>
            {!installed && <span>· Not installed</span>}
          </div>
        </div>
      </button>
      {installed && onToggleFavorite && !hidePin && (
        <button
          type="button"
          className="absolute right-2 top-2 z-10 rounded-full bg-black/60 p-1.5 text-muted opacity-70 transition-opacity group-hover:opacity-100 focus-visible:opacity-100"
          title={game.isFavorite ? 'Unpin' : 'Pin'}
          aria-label={game.isFavorite ? `Unpin ${game.title}` : `Pin ${game.title}`}
          aria-pressed={game.isFavorite}
          onClick={(e) => {
            e.stopPropagation()
            onToggleFavorite()
          }}
        >
          <Star size={12} className={game.isFavorite ? 'fill-current text-fg' : ''} />
        </button>
      )}
    </CardMotion>
  )
}
