import { resolvePrimaryAction, type Game, type InstallProgress } from '../lib/host'
import { cn, transferPercent, formatTransferPercent } from '../lib/utils'
import { useJustFinished } from '../lib/useJustFinished'
import { CoverArt } from './CoverArt'
import { memo, type CSSProperties, type MouseEvent } from 'react'
import type { GameMenuAt } from './shell/GameMenu'
import { CHECK_PATH, Check } from '../brand/icons'

type Transfer = Partial<InstallProgress> | null

type CardState = {
  installed: boolean
  hasUpdate: boolean
  isPlaying: boolean
  transferring: boolean
  phase: string
}

function cardState(game: Game, transfer: Transfer): CardState {
  const variants = game.variants ?? []
  return {
    installed: !!game.installed,
    hasUpdate: resolvePrimaryAction(game) === 'update' || !!game.updateAvailable || variants.some((v) => v.updateAvailable),
    isPlaying: !!(game.canStop || game.isRunning || variants.some((v) => v.canStop || v.isRunning)),
    transferring: !!transfer,
    phase: transfer?.phase === "installing" ? "Installing" : transfer?.phase === "finalizing" ? "Finalizing" : "Downloading",
  }
}

/** One badge at most. Playing beats Downloading beats Update beats Queued. */
function badgeFor(state: CardState, percent: number | null, queued: boolean): { text: string; tone: string } | null {
  if (state.isPlaying) return { text: 'Playing', tone: 'is-good' }
  if (state.transferring) return { text: percent == null ? state.phase : `${formatTransferPercent(percent)}%`, tone: 'is-update' }
  if (state.hasUpdate && state.installed) return { text: 'Update', tone: 'is-update' }
  if (queued) return { text: 'Queued', tone: '' }
  return null
}

/** The badge's word, in the badge's order, so the name says what the card shows. */
function ariaLabelFor(title: string, state: CardState, queued: boolean): string {
  if (state.isPlaying) return `${title} (playing)`
  if (state.transferring) return `${title} (${state.phase.toLowerCase()})`
  if (state.hasUpdate && state.installed) return `${title} (update)`
  if (queued) return `${title} (queued)`
  return title
}


export const GameCard = memo(function GameCard({
  game,
  selected,
  onSelect,
  preload = false,
  transfer = null,
  queued = false,
  tabIndex,
  onActiveGameChange,
  position,
  setSize,
  arriveIndex,
  onMenu,
  picked,
}: {
  game: Game
  selected: boolean
  /** A press: Select mode passes the click on, for Shift's range. */
  onSelect: (game: Game, event?: MouseEvent) => void
  /** Right-click: the card's menu (Play, Open folder). Without it, right-click opens the sheet. */
  onMenu?: (at: GameMenuAt) => void
  preload?: boolean
  transfer?: Transfer
  queued?: boolean
  tabIndex?: number
  onActiveGameChange?: (gameId: string) => void
  /** 1-based place in a windowed list, so assistive tech counts the whole shelf. */
  position?: number
  setSize?: number
  /** Set only while the grid first shows: this card's place in the arrival. */
  arriveIndex?: number
  /** Select mode: whether this card is picked (a press toggles it); null outside it. */
  picked?: boolean | null
}) {
  const state = cardState(game, transfer)
  const percent = transferPercent(transfer)
  const badge = badgeFor(state, percent, queued)
  // Done (DESIGN.md › Motion): a download or update that lands on an installed, current game.
  const finished = useJustFinished(state.transferring, state.installed && !state.hasUpdate)

  return (
    <article
      role={position != null ? 'listitem' : undefined}
      aria-posinset={position}
      aria-setsize={setSize}
      className={cn(
        'exo-tile group relative w-full',
        arriveIndex != null && 'exo-motion-arrive',
        selected && 'is-selected',
        picked && 'is-picked',
        !state.installed && 'is-dim',
        state.hasUpdate && !state.isPlaying && 'is-update',
        state.isPlaying && 'is-playing',
      )}
      style={arriveIndex != null ? { '--i': arriveIndex } as CSSProperties : undefined}
    >
      <button
        type="button"
        data-game-id={game.id}
        tabIndex={tabIndex}
        onFocus={onActiveGameChange ? () => onActiveGameChange(game.id) : undefined}
        onClick={(event) => {
          if (event.detail > 1 && picked == null) return
          onSelect(game, event)
        }}
        onContextMenu={(event) => {
          event.preventDefault()
          if (game.isAddPortable || game.id === 'local:add') return
          if (!onMenu) { onSelect(game); return }
          // The context-menu key reports no pointer: open at the card instead.
          const box = event.currentTarget.getBoundingClientRect()
          const keyboard = event.clientX === 0 && event.clientY === 0
          onMenu({ game, x: keyboard ? box.left + 12 : event.clientX, y: keyboard ? box.top + 12 : event.clientY, from: event.currentTarget })
        }}
        className="exo-tile-hit"
        aria-label={ariaLabelFor(game.title, state, queued)}
        {...pickAria(picked)}
      >
        <div className="exo-tile-frame" data-vt-art>
          <span className="exo-tile-media" aria-hidden="true">
            <CoverArt game={game} preload={preload} className="absolute inset-0 h-full w-full" />
          </span>
          <TileMotion working={state.transferring} finished={finished} />
          {state.transferring && (
            <span
              className={`exo-tile-progress${percent == null ? ' is-unknown' : ''}`}
              style={percent == null ? undefined : { '--progress': percent / 100 } as CSSProperties}
              aria-hidden
            />
          )}
          {badge && (
            <div className="absolute left-2 top-2 z-[5]">
              <span className={cn('exo-badge', badge.tone)}>{badge.text}</span>
            </div>
          )}
          <TileCheck picked={picked} />
        </div>
        <div className="exo-card-meta">
          <div className="exo-card-title">{game.title}</div>
        </div>
      </button>
    </article>
  )
})

/** Opens the game sheet; in Select mode it picks the game instead, a toggle. */
const pickAria = (picked: boolean | null | undefined) => picked == null ? { 'aria-haspopup': 'dialog' as const } : { 'aria-pressed': picked }

/** Select mode's ring on the cover, checked once picked. */
function TileCheck({ picked }: { picked: boolean | null | undefined }) {
  if (picked == null) return null
  return <span className="exo-tile-check" aria-hidden="true">{picked && <Check size={14} />}</span>
}

/** Motion's own layer (the frame's ::after is its hover and selection ring): work while it downloads, done when it lands. */
function TileMotion({ working, finished }: { working: boolean; finished: boolean }) {
  if (!working && !finished) return null
  return (
    <span className={cn('exo-tile-motion', working ? 'exo-motion-work' : 'exo-motion-done')} aria-hidden="true">
      {finished && <svg className="exo-motion-check" viewBox="0 0 24 24"><path d={CHECK_PATH} pathLength="1" /></svg>}
    </span>
  )
}
