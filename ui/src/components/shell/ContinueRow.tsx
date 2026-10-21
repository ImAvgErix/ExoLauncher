import { memo, useRef, type CSSProperties } from 'react'
import { shelfAction, type Game, type InstallProgress } from '../../lib/host'
import { useGlassTone, useLens } from '../../lib/glass'
import { More, Play } from '../../brand/icons'
import { formatPlayed, formatRelativeLastPlayed, transferPercent } from '../../lib/utils'
import { HeroWash } from '../CoverArt'
import type { GameMenuAt } from './GameMenu'

/**
 * Continue (DESIGN.md › Library): the games you are actually playing, as big
 * landscape art with Play one click away. The first is the widest. The live state rides on the card:
 * the green glow while it runs, a ring while it downloads or updates.
 */
export const ContinueRow = memo(function ContinueRow({ games, transferFor, startingId, onOpen, onPlay, onMenu }: {
  games: Game[]
  transferFor: (game: Game) => InstallProgress | null
  startingId: string | null
  onOpen: (game: Game) => void
  onPlay: (game: Game) => void
  onMenu: (at: GameMenuAt) => void
}) {
  if (games.length === 0) return null
  return (
    <section className="exo-continue" aria-labelledby="exo-continue-title">
      <h2 className="exo-section-title" id="exo-continue-title">Continue</h2>
      <div className="exo-continue-row" data-count={games.length}>
        {games.map(game => <ContinueCard key={game.id} game={game} transfer={transferFor(game)} starting={game.id === startingId} onOpen={onOpen} onPlay={onPlay} onMenu={onMenu} />)}
      </div>
    </section>
  )
})

function ContinueCard({ game, transfer, starting, onOpen, onPlay, onMenu }: {
  game: Game
  transfer: InstallProgress | null
  starting: boolean
  onOpen: (game: Game) => void
  onPlay: (game: Game) => void
  onMenu: (at: GameMenuAt) => void
}) {
  const percent = transfer?.isActive ? transferPercent(transfer) : null
  const working = !!transfer?.isActive
  const more = useRef<HTMLButtonElement>(null)
  useLens(more, !working)
  useGlassTone(more, `continue-${game.id}`, !working)
  const shortcut = shelfAction(game)
  return (
    <article className={`exo-continue-card${game.isRunning ? ' is-playing' : ''}${working ? ' exo-motion-work' : ''}`}>
      <button type="button" className="exo-continue-open" data-vt-art aria-label={`Open ${game.title}`} onClick={() => onOpen(game)}
        onContextMenu={(event) => { event.preventDefault(); onMenu({ game, x: event.clientX, y: event.clientY, from: event.currentTarget }) }}>
        <span className="exo-continue-scrim"><HeroWash game={game} className="exo-continue-art" /></span>
      </button>
      <div className="exo-continue-info">
        <div className="exo-continue-text">
          <strong>{game.title}</strong>
          <span className={game.isRunning ? 'is-good' : undefined}>{stateLine(game, transfer, percent)}</span>
        </div>
        {working ? (
          <span className="exo-continue-ring" style={{ '--p': `${percent ?? 0}%` } as CSSProperties} role="img" aria-label={percent == null ? 'Working' : `${Math.floor(percent)}%`}>
            <span>{percent == null ? '' : Math.floor(percent)}</span>
          </span>
        ) : (
          <div className="exo-continue-actions">
            <button ref={more} type="button" className="exo-glass exo-continue-more" aria-label={`More for ${game.title}`} aria-haspopup="menu"
              onClick={(event) => {
                const box = event.currentTarget.getBoundingClientRect()
                onMenu({ game, x: box.left, y: box.bottom + 6, from: event.currentTarget })
              }}>
              <More size={16} />
            </button>
            {shortcut ? (
              <button type="button" className="exo-continue-play" disabled={starting} aria-label={`${starting ? 'Starting' : shortcut.label} ${game.title}`} onClick={() => onPlay(game)}>
                <Play size={14} />
                {starting ? 'Starting…' : shortcut.label}
              </button>
            ) : null}
          </div>
        )}
      </div>
    </article>
  )
}

function stateLine(game: Game, transfer: InstallProgress | null, percent: number | null): string {
  if (game.isRunning) return 'Playing'
  if (transfer?.isActive) {
    const verb = game.installed ? 'Updating' : 'Downloading'
    return percent == null ? verb : `${verb} · ${Math.floor(percent)}%`
  }
  const parts = [
    game.playtimeMinutes ? formatPlayed(game.playtimeMinutes) : null,
    game.lastPlayedUtc ? formatRelativeLastPlayed(game.lastPlayedUtc) : null,
    game.updateAvailable ? 'Update ready' : null,
  ].filter(Boolean)
  return parts.join(' · ')
}
