import { useEffect, useState } from 'react'
import type { Game, InstallProgress } from '../../lib/host'
import { statusBelongsToSelection } from '../../lib/library'
import { GameOverlay } from '../../motion'
import { GamePage, type GamePageProps } from '../GamePage'

/** Presses closer together than this belong to the click streak that opened the sheet. */
const STREAK_MS = 500

export type GameSheetProps = Omit<GamePageProps, 'selected' | 'statusMsg' | 'closeDisabled' | 'onStatus'> & {
  game: Game | null
  /** Status raised by the sheet always names the exact source it was raised for. */
  onStatusFor: (gameId: string, message: string | null, sticky?: boolean) => void
  open: boolean
  instant: boolean
  openedAt: number
  /** A launch or install owns the shell: no closing until it settles. */
  locked: boolean
  statusMsg: string | null
  statusGameId: string | null
  progress: InstallProgress | null
}

/** Fixed-size game details with a scrim and one opacity transition. */
export function GameSheet({ game: current, open, instant, openedAt, locked, statusMsg, statusGameId, onClose, onStatusFor, ...page }: GameSheetProps) {
  // The closing fade keeps showing the game that was open.
  const [last, setLast] = useState(current)
  if (current && current !== last) setLast(current)
  const game = current ?? last

  // Clicking a card again and again opens its sheet once: every press in that streak (each within
  // half a second of the last) lands on the sheet and presses nothing, however long it goes on, so
  // only a deliberate press of Play plays, and none closes it. Buttons act on pointer up, so the whole press is held
  // back, on the window, ahead of the press that closes what is open (lib/dismiss).
  useEffect(() => {
    if (!open || instant) return
    let last = openedAt
    let holding = false
    let over = false
    const down = (event: Event) => {
      const now = performance.now()
      holding = !over && now - last < STREAK_MS
      if (!holding) { over = true; return }
      last = now
      event.preventDefault()
      event.stopPropagation()
    }
    const rest = (event: Event) => {
      if (!holding) return
      event.preventDefault()
      event.stopPropagation()
      if (event.type === 'click') holding = false
    }
    const events = ['pointerup', 'mousedown', 'mouseup', 'click', 'dblclick'] as const
    window.addEventListener('pointerdown', down, true)
    for (const name of events) window.addEventListener(name, rest, true)
    return () => {
      window.removeEventListener('pointerdown', down, true)
      for (const name of events) window.removeEventListener(name, rest, true)
    }
  }, [open, instant, openedAt])

  return (
    <GameOverlay
      open={open}
      instant={instant}
      label={game?.title ?? 'Game'}
      scrim={
        <button
          type="button"
          className="exo-game-overlay-scrim"
          aria-label="Close"
          tabIndex={-1}
          disabled={locked}
          onClick={onClose}
        />
      }
    >
      {game ? (
        <GamePage
          {...page}
          selected={game}
          statusMsg={statusBelongsToSelection(statusGameId, game) ? statusMsg : null}
          closeDisabled={locked}
          onClose={onClose}
          onStatus={(message, sticky) => onStatusFor(game.id, message, sticky)}
        />
      ) : null}
    </GameOverlay>
  )
}
