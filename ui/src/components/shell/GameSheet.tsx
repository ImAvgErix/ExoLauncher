import { useEffect, useState } from 'react'
import type { Game, InstallProgress } from '../../lib/host'
import { statusBelongsToSelection } from '../../lib/library'
import { GameOverlay } from '../../motion'
import { GamePage, type GamePageProps } from '../GamePage'

/** Presses closer together than this, and this close to the last one (a double click's reach), belong to the click streak that opened the sheet. */
const STREAK_MS = 500
const STREAK_REACH = 16

// Where the last press was (the press that opened the sheet, when its streak starts).
let lastPoint = { x: -1, y: -1 }
if (typeof window !== 'undefined') window.addEventListener('pointerdown', event => { lastPoint = { x: event.clientX, y: event.clientY } }, { capture: true, passive: true })

export type GameSheetProps = Omit<GamePageProps, 'selected' | 'statusMsg' | 'onStatus'> & {
  /** A press outside the page, or Escape, closes it: the page has no close button of its own. */
  onClose: () => void
  game: Game | null
  /** Status raised by the sheet always names the exact source it was raised for. */
  onStatusFor: (gameId: string, message: string | null, sticky?: boolean) => void
  open: boolean
  instant: boolean
  openedAt: number
  statusMsg: string | null
  statusGameId: string | null
  progress: InstallProgress | null
}

/**
 * Fixed-size game details with a scrim and one opacity transition. It always closes, a launch or install running or not:
 * the launch goes on behind it (Continue and the Bar show it), and a launch stuck waiting on a store's app (Riot asking
 * to sign in again) no longer kept the page up with every press outside it ignored.
 */
export function GameSheet({ game: current, open, instant, openedAt, statusMsg, statusGameId, onClose, onStatusFor, ...page }: GameSheetProps) {
  // The closing fade keeps showing the game that was open.
  const [last, setLast] = useState(current)
  if (current && current !== last) setLast(current)
  const game = current ?? last

  // Clicking a card again and again opens its sheet once: every press in that streak (each within half a second of the
  // last, where the last one was) lands on the sheet and presses nothing, however long it goes on, so only a deliberate
  // press of Play plays, and none closes it. A press anywhere else is meant: it ends the streak, and outside the page it
  // closes it (before, quick presses outside were taken for the streak too, and the page stayed open). Buttons act on
  // pointer up, so the whole press is held back, on the window, ahead of the press that closes what is open (lib/dismiss).
  useEffect(() => {
    if (!open || instant) return
    let last = openedAt
    let at = lastPoint
    let holding = false
    let over = false
    const down = (event: Event) => {
      const now = performance.now()
      const { clientX: x, clientY: y } = event as PointerEvent
      holding = !over && now - last < STREAK_MS && Math.hypot(x - at.x, y - at.y) <= STREAK_REACH
      if (!holding) { over = true; return }
      last = now
      at = { x, y }
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
          onClick={onClose}
        />
      }
    >
      {game ? (
        <GamePage
          {...page}
          selected={game}
          statusMsg={statusBelongsToSelection(statusGameId, game) ? statusMsg : null}
          onStatus={(message, sticky) => onStatusFor(game.id, message, sticky)}
        />
      ) : null}
    </GameOverlay>
  )
}
